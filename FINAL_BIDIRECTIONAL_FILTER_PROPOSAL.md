# Final Proposal: Bidirectional Event-Emitting Filters

**Version**: 1.0 (Corrected)
**Status**: Ready for Implementation
**Breaking Changes**: Yes (v0 acceptable)

---

## Executive Summary

Extend the filter system to support **bidirectional event emission** using channel-based communication. This provides a standardized way for filters to emit events (permissions, progress, cost approvals, custom observability) and wait for responses during execution.

**Key Insight**: Bidirectional communication requires **concurrent execution** - events must be visible to handlers WHILE filters are blocked waiting for responses. Channels are the appropriate .NET primitive for this producer-consumer pattern.

---

## Problem Statement

### Current Situation

`IAiFunctionFilter` execution happens deep in the call stack:

```
RunAgenticLoopInternal()                    ← Emits events via yield return
  └─ ToolScheduler.ExecuteToolsAsync()
      └─ FunctionCallProcessor.ProcessFunctionCallsAsync()
          └─ FilterChain.BuildAiFunctionPipeline()
              └─ Filter.InvokeAsync()       ← CAN'T emit events to agent stream!
```

**Current workarounds**:
- `AGUIPermissionFilter`: Uses injected `IPermissionEventEmitter` (custom interface per filter type)
- `ConsolePermissionFilter`: Blocks on `Console.ReadLine()` (blocking I/O)
- Permission filters: Run BEFORE the pipeline via `PermissionManager`, not DURING
- Regular filters: Cannot emit events at all

### The Architectural Gap

1. No standard way for filters to emit events
2. Each filter type needs custom event emission mechanism
3. Events cannot flow from filters up to `RunAgenticLoopInternal`'s event stream
4. No pattern for bidirectional communication (request/response)

### Why Bidirectional Communication Requires Concurrency

For a permission filter to work:

```
Filter emits permission request event
  ↓
Filter BLOCKS waiting for user's approval/denial
  ↓ (meanwhile, concurrently)
Handler MUST SEE the event (while filter is blocked)
  ↓
Handler sends approval/denial response
  ↓
Filter receives response and unblocks
  ↓
Filter continues or terminates based on response
```

This is **producer-consumer concurrency** - the filter produces events while blocked, and the handler must consume them concurrently.

---

## Proposed Solution

### Core Design Decisions

1. **Channel-based communication**: Use `System.Threading.Channels` for concurrent event emission/response
2. **Agent-level response coordination**: Store response waiters at Agent level (not context level, which is ephemeral)
3. **Zero breaking changes to filter signature**: `IAiFunctionFilter` remains unchanged
4. **Background filter execution**: Execute filter pipeline in background task to enable event streaming
5. **Unified event collection**: All events (one-way and bidirectional) use same mechanism

---

## Implementation

### 1. Enhanced Agent Class

```csharp
public class Agent
{
    // Existing fields...

    /// <summary>
    /// Shared response coordination across all filter invocations.
    /// Maps requestId -> (TaskCompletionSource, CancellationTokenSource)
    /// Lifetime: Entire agent lifetime (not per-context)
    /// </summary>
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<InternalAgentEvent>, CancellationTokenSource)>
        _filterResponseWaiters = new();

    /// <summary>
    /// Sends a response to a filter waiting for a specific request.
    /// Called by external handlers (AGUI, Console, etc.) when user provides input.
    /// </summary>
    /// <param name="requestId">The unique identifier for the request</param>
    /// <param name="response">The response event to deliver</param>
    /// <exception cref="ArgumentNullException">If response is null</exception>
    public void SendFilterResponse(string requestId, InternalAgentEvent response)
    {
        if (response == null)
            throw new ArgumentNullException(nameof(response));

        if (_filterResponseWaiters.TryRemove(requestId, out var entry))
        {
            entry.Item1.TrySetResult(response);
            entry.Item2.Dispose();
        }
    }

    /// <summary>
    /// Internal method for filters to wait for responses.
    /// Called by AiFunctionContext.WaitForResponseAsync().
    /// </summary>
    internal async Task<T> WaitForFilterResponseAsync<T>(
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken) where T : InternalAgentEvent
    {
        var tcs = new TaskCompletionSource<InternalAgentEvent>();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        _filterResponseWaiters[requestId] = (tcs, cts);

        // Register cancellation/timeout cleanup
        cts.Token.Register(() =>
        {
            if (_filterResponseWaiters.TryRemove(requestId, out var entry))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    entry.Item1.TrySetCanceled(cancellationToken);
                }
                else
                {
                    entry.Item1.TrySetException(
                        new TimeoutException($"No response received for request '{requestId}' within {timeout}"));
                }
                entry.Item2.Dispose();
            }
        });

        try
        {
            var response = await tcs.Task;

            // Type safety check
            if (response is not T typedResponse)
            {
                throw new InvalidOperationException(
                    $"Expected response of type {typeof(T).Name}, but received {response.GetType().Name}");
            }

            return typedResponse;
        }
        finally
        {
            // Cleanup on success
            if (_filterResponseWaiters.TryRemove(requestId, out var entry))
            {
                entry.Item2.Dispose();
            }
        }
    }
}
```

### 2. Enhanced AiFunctionContext

```csharp
public class AiFunctionContext : FunctionInvocationContext
{
    // Existing properties...

    /// <summary>
    /// Channel writer for emitting events during filter execution.
    /// Events written here are immediately visible to RunAgenticLoopInternal.
    /// </summary>
    internal ChannelWriter<InternalAgentEvent>? OutboundEvents { get; set; }

    /// <summary>
    /// Reference to the agent for response coordination.
    /// </summary>
    internal Agent? Agent { get; set; }

    /// <summary>
    /// Emits an event that will be yielded by RunAgenticLoopInternal.
    /// Events are delivered immediately (not batched).
    /// </summary>
    /// <param name="evt">The event to emit</param>
    /// <exception cref="ArgumentNullException">If event is null</exception>
    /// <exception cref="InvalidOperationException">If OutboundEvents channel is not configured</exception>
    public void Emit(InternalAgentEvent evt)
    {
        if (evt == null)
            throw new ArgumentNullException(nameof(evt));

        if (OutboundEvents == null)
            throw new InvalidOperationException("Event emission not configured for this context");

        // Non-blocking write (unbounded channel)
        if (!OutboundEvents.TryWrite(evt))
        {
            throw new InvalidOperationException("Failed to emit event - channel may be closed");
        }
    }

    /// <summary>
    /// Emits an event and returns immediately (async version for bounded channels if needed).
    /// </summary>
    public async Task EmitAsync(InternalAgentEvent evt, CancellationToken cancellationToken = default)
    {
        if (evt == null)
            throw new ArgumentNullException(nameof(evt));

        if (OutboundEvents == null)
            throw new InvalidOperationException("Event emission not configured for this context");

        await OutboundEvents.WriteAsync(evt, cancellationToken);
    }

    /// <summary>
    /// Waits for a response event with automatic timeout and cancellation handling.
    /// Used for request/response patterns in interactive filters (permissions, approvals, etc.)
    /// </summary>
    /// <typeparam name="T">Type of response event to wait for</typeparam>
    /// <param name="requestId">Unique identifier for this request</param>
    /// <param name="timeout">Maximum time to wait for response (default: 5 minutes)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The response event</returns>
    /// <exception cref="TimeoutException">Thrown if no response received within timeout</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancellation requested</exception>
    /// <exception cref="InvalidOperationException">Thrown if Agent reference not set or response type mismatch</exception>
    public async Task<T> WaitForResponseAsync<T>(
        string requestId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) where T : InternalAgentEvent
    {
        if (Agent == null)
            throw new InvalidOperationException("Agent reference not configured for this context");

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);

        return await Agent.WaitForFilterResponseAsync<T>(requestId, effectiveTimeout, cancellationToken);
    }
}
```

### 3. Modified ProcessFunctionCallsAsync

```csharp
/// <summary>
/// Result of processing function calls, including messages and events.
/// </summary>
public record ProcessFunctionResult(
    List<ChatMessage> Messages,
    List<InternalAgentEvent> Events);

public class FunctionCallProcessor
{
    private readonly Agent _agent; // NEW: Reference to agent for response coordination

    public FunctionCallProcessor(
        Agent agent,
        ScopedFilterManager? scopedFilterManager,
        PermissionManager permissionManager,
        IReadOnlyList<IAiFunctionFilter>? aiFunctionFilters,
        int maxFunctionCalls,
        ErrorHandlingConfig? errorHandlingConfig = null,
        IList<AITool>? serverConfiguredTools = null,
        AgenticLoopConfig? agenticLoopConfig = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _scopedFilterManager = scopedFilterManager;
        _permissionManager = permissionManager ?? throw new ArgumentNullException(nameof(permissionManager));
        _aiFunctionFilters = aiFunctionFilters ?? new List<IAiFunctionFilter>();
        _maxFunctionCalls = maxFunctionCalls;
        _errorHandlingConfig = errorHandlingConfig;
        _serverConfiguredTools = serverConfiguredTools;
        _agenticLoopConfig = agenticLoopConfig;
    }

    public async Task<ProcessFunctionResult> ProcessFunctionCallsAsync(
        List<ChatMessage> messages,
        ChatOptions? options,
        List<FunctionCallContent> functionCallContents,
        AgentRunContext agentRunContext,
        string? agentName,
        CancellationToken cancellationToken)
    {
        var resultMessages = new List<ChatMessage>();
        var allEvents = new List<InternalAgentEvent>();

        // Build function map per execution (Microsoft pattern for thread-safety)
        var functionMap = BuildFunctionMap(_serverConfiguredTools, options?.Tools);

        // Process each function call through the filter pipeline
        foreach (var functionCall in functionCallContents)
        {
            // Skip functions without names
            if (string.IsNullOrEmpty(functionCall.Name))
                continue;

            var toolCallRequest = new ToolCallRequest
            {
                FunctionName = functionCall.Name,
                Arguments = functionCall.Arguments ?? new Dictionary<string, object?>()
            };

            // Create outbound channel for this function's filter execution
            var outboundChannel = Channel.CreateUnbounded<InternalAgentEvent>(
                new UnboundedChannelOptions
                {
                    SingleWriter = false, // Multiple filters may emit
                    SingleReader = true,  // Only this method reads
                    AllowSynchronousContinuations = false // Performance & safety
                });

            var context = new AiFunctionContext(toolCallRequest)
            {
                Function = FindFunctionInMap(functionCall.Name, functionMap),
                RunContext = agentRunContext,
                AgentName = agentName,
                OutboundEvents = outboundChannel.Writer,
                Agent = _agent
            };

            context.Metadata["CallId"] = functionCall.CallId;

            // Check if function is unknown and TerminateOnUnknownCalls is enabled
            if (context.Function == null && _agenticLoopConfig?.TerminateOnUnknownCalls == true)
            {
                context.IsTerminated = true;
                agentRunContext.IsTerminated = true;
                agentRunContext.TerminationReason = $"Unknown function requested: '{functionCall.Name}'";
                break;
            }

            // Check permissions BEFORE filter pipeline
            var permissionResult = await _permissionManager.CheckPermissionAsync(
                functionCall,
                context.Function,
                agentRunContext,
                agentName,
                cancellationToken).ConfigureAwait(false);

            if (!permissionResult.IsApproved)
            {
                context.Result = permissionResult.DenialReason ?? "Permission denied";
                context.IsTerminated = true;

                agentRunContext.CompleteFunction(functionCall.Name);

                var denialResult = new FunctionResultContent(functionCall.CallId, context.Result);
                var denialMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { denialResult });
                resultMessages.Add(denialMessage);

                // Close channel and collect any events that were emitted during permission check
                outboundChannel.Writer.Complete();
                await foreach (var evt in outboundChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    allEvents.Add(evt);
                }

                continue;
            }

            // Permission approved - execute filter pipeline
            Func<AiFunctionContext, Task> finalInvoke = async (ctx) =>
            {
                if (ctx.Function is null)
                {
                    ctx.Result = $"Function '{ctx.ToolCallRequest.FunctionName}' not found.";
                    return;
                }
                await ExecuteWithRetryAsync(ctx, cancellationToken).ConfigureAwait(false);
            };

            var scopedFilters = _scopedFilterManager?.GetApplicableFilters(functionCall.Name)
                                ?? Enumerable.Empty<IAiFunctionFilter>();
            var allStandardFilters = _aiFunctionFilters.Concat(scopedFilters);

            // Build filter pipeline
            var pipeline = FilterChain.BuildAiFunctionPipeline(allStandardFilters, finalInvoke);

            // Execute pipeline in background task to enable concurrent event collection
            var pipelineTask = Task.Run(async () =>
            {
                try
                {
                    await pipeline(context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Emit error event
                    context.Emit(new InternalFilterErrorEvent(
                        "FilterPipeline",
                        $"Error in filter pipeline: {ex.Message}",
                        ex));
                    throw;
                }
                finally
                {
                    // Signal completion
                    outboundChannel.Writer.Complete();
                }
            }, cancellationToken);

            // Collect events as they're emitted (concurrent with filter execution)
            await foreach (var evt in outboundChannel.Reader.ReadAllAsync(cancellationToken))
            {
                allEvents.Add(evt);
            }

            // Wait for pipeline to complete
            try
            {
                await pipelineTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Error already emitted as event, continue to next function
                context.IsTerminated = true;
            }

            // Mark function as completed
            agentRunContext.CompleteFunction(functionCall.Name);

            var functionResult = new FunctionResultContent(functionCall.CallId, context.Result);
            var functionMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { functionResult });
            resultMessages.Add(functionMessage);
        }

        return new ProcessFunctionResult(resultMessages, allEvents);
    }

    // ... existing ExecuteWithRetryAsync and other methods unchanged ...
}
```

### 4. Modified ToolScheduler

```csharp
public class ToolScheduler
{
    /// <summary>
    /// Result of tool execution, including messages and filter events.
    /// </summary>
    public record ToolExecutionResult(
        ChatMessage Message,
        List<InternalAgentEvent> Events);

    public async Task<ToolExecutionResult> ExecuteToolsAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentRunContext agentRunContext,
        string? agentName,
        HashSet<string> expandedPlugins,
        HashSet<string> expandedSkills,
        CancellationToken cancellationToken)
    {
        var allContents = new List<AIContent>();
        var allEvents = new List<InternalAgentEvent>();

        // ... existing plugin/skill scoping logic ...

        // Execute tools through processor
        var result = await _functionCallProcessor.ProcessFunctionCallsAsync(
            currentHistory, options, toolRequests, agentRunContext, agentName, cancellationToken);

        // Collect events from processor
        allEvents.AddRange(result.Events);

        // Add result messages to contents
        foreach (var message in result.Messages)
        {
            allContents.AddRange(message.Contents);
        }

        var toolResultMessage = new ChatMessage(ChatRole.Tool, allContents);
        return new ToolExecutionResult(toolResultMessage, allEvents);
    }
}
```

### 5. Modified RunAgenticLoopInternal

```csharp
private async IAsyncEnumerable<InternalAgentEvent> RunAgenticLoopInternal(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    string[]? documentPaths,
    List<ChatMessage> turnHistory,
    TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletionSource,
    TaskCompletionSource<ReductionMetadata?> reductionCompletionSource,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    // ... existing setup ...

    yield return new InternalMessageTurnStartedEvent(messageTurnId, conversationId);

    for (int iteration = 0; iteration < maxIterations; iteration++)
    {
        yield return new InternalAgentTurnStartedEvent(iteration);

        // ... LLM streaming logic (text, reasoning, tool call events) ...

        // Execute tools and collect events
        var toolResult = await _toolScheduler.ExecuteToolsAsync(
            currentMessages, toolRequests, effectiveOptions, agentRunContext,
            _name, expandedPlugins, expandedSkills, effectiveCancellationToken);

        // ✅ Yield filter events immediately
        foreach (var evt in toolResult.Events)
        {
            yield return evt;
        }

        // Then yield tool execution results
        foreach (var content in toolResult.Message.Contents)
        {
            if (content is FunctionResultContent result)
            {
                yield return new InternalToolCallEndEvent(result.CallId);
                yield return new InternalToolCallResultEvent(result.CallId, result.Result?.ToString() ?? "null");
            }
        }

        // Add to history
        currentMessages.Add(toolResult.Message);
        turnHistory.Add(toolResult.Message);

        yield return new InternalAgentTurnFinishedEvent(iteration);

        // ... existing loop continuation logic ...
    }

    yield return new InternalMessageTurnFinishedEvent(messageTurnId, conversationId);
}
```

---

## Event Types

### Permission Events

```csharp
/// <summary>
/// Filter requests permission to execute a function.
/// Handler should prompt user and send InternalPermissionResponseEvent.
/// </summary>
public record InternalPermissionRequestEvent(
    string PermissionId,
    string FunctionName,
    string? Description,
    string CallId,
    IDictionary<string, object?>? Arguments) : InternalAgentEvent;

/// <summary>
/// Response to permission request.
/// Sent by external handler (AGUI, Console) back to waiting filter.
/// </summary>
public record InternalPermissionResponseEvent(
    string PermissionId,
    bool Approved,
    string? Reason = null,
    PermissionChoice Choice = PermissionChoice.AllowOnce) : InternalAgentEvent;

/// <summary>
/// Emitted after permission is approved (for observability).
/// </summary>
public record InternalPermissionApprovedEvent(
    string PermissionId) : InternalAgentEvent;

/// <summary>
/// Emitted after permission is denied (for observability).
/// </summary>
public record InternalPermissionDeniedEvent(
    string PermissionId,
    string Reason) : InternalAgentEvent;
```

### Continuation Events

```csharp
/// <summary>
/// Filter requests permission to continue beyond max iterations.
/// </summary>
public record InternalContinuationRequestEvent(
    string ContinuationId,
    int CurrentIteration,
    int MaxIterations) : InternalAgentEvent;

/// <summary>
/// Response to continuation request.
/// </summary>
public record InternalContinuationResponseEvent(
    string ContinuationId,
    bool Approved,
    int ExtensionAmount = 0) : InternalAgentEvent;
```

### Generic Observability Events

```csharp
/// <summary>
/// Filter reports progress (one-way, no response needed).
/// </summary>
public record InternalFilterProgressEvent(
    string FilterName,
    string Message,
    int? PercentComplete = null) : InternalAgentEvent;

/// <summary>
/// Filter reports an error (one-way, no response needed).
/// </summary>
public record InternalFilterErrorEvent(
    string FilterName,
    string ErrorMessage,
    Exception? Exception = null) : InternalAgentEvent;
```

### Custom Events (User Extensibility)

```csharp
/// <summary>
/// Generic custom event for user-defined scenarios.
/// Users can also create their own event types by deriving from InternalAgentEvent.
/// </summary>
public record InternalCustomFilterEvent(
    string FilterName,
    string EventType,
    IDictionary<string, object?> Data) : InternalAgentEvent;
```

---

## Usage Examples

### Example 1: Simple One-Way Event Emission

```csharp
public class ProgressLoggingFilter : IAiFunctionFilter
{
    public async Task InvokeAsync(AiFunctionContext context, Func<AiFunctionContext, Task> next)
    {
        // Emit progress start (one-way, no response needed)
        context.Emit(new InternalFilterProgressEvent(
            "ProgressLoggingFilter",
            $"Starting execution of {context.ToolCallRequest.FunctionName}",
            PercentComplete: 0));

        var sw = Stopwatch.StartNew();

        try
        {
            await next(context);

            // Emit progress complete
            context.Emit(new InternalFilterProgressEvent(
                "ProgressLoggingFilter",
                $"Completed {context.ToolCallRequest.FunctionName} in {sw.ElapsedMilliseconds}ms",
                PercentComplete: 100));
        }
        catch (Exception ex)
        {
            // Emit error
            context.Emit(new InternalFilterErrorEvent(
                "ProgressLoggingFilter",
                $"Error in {context.ToolCallRequest.FunctionName}: {ex.Message}",
                ex));
            throw;
        }
    }
}
```

**Agent usage**:
```csharp
var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4", apiKey)
    .WithPlugin<FileSystemPlugin>()
    .WithFilter(new ProgressLoggingFilter())
    .Build();

// Events automatically flow to stream
await foreach (var evt in agent.RunStreamingAsync(thread, options))
{
    switch (evt)
    {
        case InternalFilterProgressEvent progress:
            Console.WriteLine($"[{progress.FilterName}] {progress.Message}");
            break;

        case InternalTextDeltaEvent text:
            Console.Write(text.Text);
            break;
    }
}
```

### Example 2: Bidirectional Permission Filter

```csharp
public class UnifiedPermissionFilter : IPermissionFilter
{
    private readonly IPermissionStorage? _storage;

    public UnifiedPermissionFilter(IPermissionStorage? storage = null)
    {
        _storage = storage;
    }

    public async Task InvokeAsync(AiFunctionContext context, Func<AiFunctionContext, Task> next)
    {
        // Check if permission required
        if (context.Function is not HPDAIFunctionFactory.HPDAIFunction hpdFunction ||
            !hpdFunction.HPDOptions.RequiresPermission)
        {
            await next(context);
            return;
        }

        var functionName = context.ToolCallRequest.FunctionName;
        var conversationId = context.RunContext?.ConversationId ?? string.Empty;

        // Check storage for cached decision
        var storedChoice = await _storage?.GetStoredPermissionAsync(functionName, conversationId, null);
        if (storedChoice == PermissionChoice.AlwaysAllow)
        {
            await next(context);
            return;
        }
        if (storedChoice == PermissionChoice.AlwaysDeny)
        {
            context.Result = "Permission denied by stored preference";
            context.IsTerminated = true;
            return;
        }

        // Emit permission request event
        var permissionId = Guid.NewGuid().ToString();
        context.Emit(new InternalPermissionRequestEvent(
            permissionId,
            functionName,
            context.Function.Description,
            context.Metadata["CallId"]?.ToString() ?? "",
            context.ToolCallRequest.Arguments));

        // Wait for response from external handler (BLOCKS HERE)
        InternalPermissionResponseEvent response;
        try
        {
            response = await context.WaitForResponseAsync<InternalPermissionResponseEvent>(
                permissionId,
                timeout: TimeSpan.FromMinutes(5));
        }
        catch (TimeoutException)
        {
            context.Emit(new InternalPermissionDeniedEvent(
                permissionId,
                "Permission request timed out"));
            context.Result = "Permission request timed out";
            context.IsTerminated = true;
            return;
        }
        catch (OperationCanceledException)
        {
            context.Emit(new InternalPermissionDeniedEvent(
                permissionId,
                "Permission request cancelled"));
            context.Result = "Permission request cancelled";
            context.IsTerminated = true;
            return;
        }

        // Emit result event
        if (response.Approved)
        {
            context.Emit(new InternalPermissionApprovedEvent(permissionId));

            // Store persistent choice if needed
            if (response.Choice != PermissionChoice.AllowOnce)
            {
                await _storage?.StorePermissionAsync(
                    functionName,
                    response.Choice,
                    PermissionScope.Conversation,
                    conversationId,
                    null);
            }

            // Continue execution
            await next(context);
        }
        else
        {
            context.Emit(new InternalPermissionDeniedEvent(
                permissionId,
                response.Reason ?? "Permission denied"));
            context.Result = response.Reason ?? "Permission denied";
            context.IsTerminated = true;
        }
    }
}
```

### Example 3: External Handler (AGUI)

```csharp
public class AGUIEventHandler
{
    private readonly Agent _agent;

    public AGUIEventHandler(Agent agent)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    public async Task HandleEventStreamAsync(
        IAsyncEnumerable<InternalAgentEvent> eventStream,
        Func<BaseEvent, Task> emitToFrontend)
    {
        await foreach (var evt in eventStream)
        {
            switch (evt)
            {
                case InternalPermissionRequestEvent permReq:
                    // Convert to AGUI event and send to frontend
                    await emitToFrontend(new FunctionPermissionRequestEvent
                    {
                        Type = "custom",
                        PermissionId = permReq.PermissionId,
                        FunctionName = permReq.FunctionName,
                        FunctionDescription = permReq.Description,
                        Arguments = new Dictionary<string, object?>(permReq.Arguments ?? new Dictionary<string, object?>())
                    });
                    break;

                case InternalPermissionApprovedEvent approved:
                    await emitToFrontend(new PermissionApprovedEvent
                    {
                        Type = "custom",
                        PermissionId = approved.PermissionId
                    });
                    break;

                case InternalPermissionDeniedEvent denied:
                    await emitToFrontend(new PermissionDeniedEvent
                    {
                        Type = "custom",
                        PermissionId = denied.PermissionId,
                        Reason = denied.Reason
                    });
                    break;

                case InternalTextDeltaEvent text:
                    await emitToFrontend(CreateTextDelta(text));
                    break;

                // ... other event conversions ...
            }
        }
    }

    // Called when frontend sends permission response
    public void HandlePermissionResponse(PermissionResponsePayload response)
    {
        // Send response to waiting filter via agent
        _agent.SendFilterResponse(response.PermissionId, new InternalPermissionResponseEvent(
            response.PermissionId,
            response.Approved,
            response.Approved ? null : "User denied",
            response.RememberChoice
                ? (response.Approved ? PermissionChoice.AlwaysAllow : PermissionChoice.AlwaysDeny)
                : PermissionChoice.AllowOnce));
    }
}
```

### Example 4: External Handler (Console)

```csharp
public class ConsoleEventHandler
{
    private readonly Agent _agent;

    public ConsoleEventHandler(Agent agent)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    public async Task HandleEventStreamAsync(IAsyncEnumerable<InternalAgentEvent> eventStream)
    {
        await foreach (var evt in eventStream)
        {
            switch (evt)
            {
                case InternalPermissionRequestEvent permReq:
                    // Prompt user on console (runs in background thread)
                    _ = Task.Run(async () =>
                    {
                        var decision = await PromptUserAsync(permReq);

                        // Send response back to agent
                        _agent.SendFilterResponse(permReq.PermissionId, new InternalPermissionResponseEvent(
                            permReq.PermissionId,
                            decision.Approved,
                            decision.Reason,
                            decision.Choice));
                    });
                    break;

                case InternalTextDeltaEvent text:
                    Console.Write(text.Text);
                    break;

                case InternalFilterProgressEvent progress:
                    Console.WriteLine($"\n[{progress.FilterName}] {progress.Message}");
                    break;
            }
        }
    }

    private async Task<(bool Approved, string? Reason, PermissionChoice Choice)> PromptUserAsync(
        InternalPermissionRequestEvent request)
    {
        return await Task.Run(() =>
        {
            Console.WriteLine($"\n[PERMISSION REQUIRED]");
            Console.WriteLine($"Function: {request.FunctionName}");
            Console.WriteLine($"Description: {request.Description}");
            Console.WriteLine("\nChoose:");
            Console.WriteLine("  [A] Allow once");
            Console.WriteLine("  [D] Deny once");
            Console.WriteLine("  [Y] Always allow");
            Console.WriteLine("  [N] Never allow");
            Console.Write("Choice: ");

            var input = Console.ReadLine()?.ToUpperInvariant();

            return input switch
            {
                "A" => (true, null, PermissionChoice.AllowOnce),
                "D" => (false, "User denied", PermissionChoice.DenyOnce),
                "Y" => (true, null, PermissionChoice.AlwaysAllow),
                "N" => (false, "User denied permanently", PermissionChoice.AlwaysDeny),
                _ => (false, "Invalid input", PermissionChoice.DenyOnce)
            };
        });
    }
}
```

### Example 5: Custom User-Defined Filter with Custom Events

```csharp
// User defines custom event types
public record DatabaseQueryStartEvent(
    string QueryId,
    string Query,
    TimeSpan EstimatedDuration) : InternalAgentEvent;

public record DatabaseQueryCompleteEvent(
    string QueryId,
    int RowCount,
    TimeSpan ActualDuration) : InternalAgentEvent;

// User creates custom filter
public class DatabaseObservabilityFilter : IAiFunctionFilter
{
    public async Task InvokeAsync(AiFunctionContext context, Func<AiFunctionContext, Task> next)
    {
        if (context.ToolCallRequest.FunctionName == "QueryDatabase")
        {
            var queryId = Guid.NewGuid().ToString();
            var query = context.ToolCallRequest.Arguments.TryGetValue("query", out var q)
                ? q?.ToString() ?? ""
                : "";

            // Emit query start event
            context.Emit(new DatabaseQueryStartEvent(
                queryId,
                query,
                EstimatedDuration: TimeSpan.FromSeconds(2)));

            var sw = Stopwatch.StartNew();

            // Execute query
            await next(context);

            sw.Stop();

            // Emit query result event
            var rowCount = ParseRowCount(context.Result);
            context.Emit(new DatabaseQueryCompleteEvent(
                queryId,
                rowCount,
                sw.Elapsed));
        }
        else
        {
            await next(context);
        }
    }

    private int ParseRowCount(object? result)
    {
        // Parse result to extract row count
        return 0;
    }
}
```

**User's event handler**:
```csharp
await foreach (var evt in agent.RunStreamingAsync(thread, options))
{
    switch (evt)
    {
        case DatabaseQueryStartEvent queryStart:
            Console.WriteLine($"[DB] Starting query: {queryStart.Query}");
            Console.WriteLine($"[DB] Estimated duration: {queryStart.EstimatedDuration}");
            break;

        case DatabaseQueryCompleteEvent queryComplete:
            Console.WriteLine($"[DB] Query complete: {queryComplete.RowCount} rows in {queryComplete.ActualDuration}");
            break;

        case InternalTextDeltaEvent text:
            Console.Write(text.Text);
            break;
    }
}
```

---

## Migration Strategy

### Phase 1: Add Foundation (Breaking - v0.x → v1.0)

**Changes**:
1. Add `OutboundEvents` and `Agent` properties to `AiFunctionContext`
2. Add `_filterResponseWaiters`, `SendFilterResponse()`, and `WaitForFilterResponseAsync()` to `Agent`
3. Add `Emit()`, `EmitAsync()`, and `WaitForResponseAsync()` methods to `AiFunctionContext`
4. Change `ProcessFunctionCallsAsync` return type to `ProcessFunctionResult(Messages, Events)`
5. Change `ToolScheduler.ExecuteToolsAsync` return type to `ToolExecutionResult(Message, Events)`
6. Modify `ProcessFunctionCallsAsync` to execute filters in background with channel-based event collection
7. Modify `RunAgenticLoopInternal` to yield collected events
8. Add new event types (permission, continuation, progress, error, custom)
9. Update `FunctionCallProcessor` constructor to accept `Agent` parameter

**Breaking changes**:
- ✅ `ProcessFunctionCallsAsync` signature change (internal API)
- ✅ `ToolScheduler.ExecuteToolsAsync` signature change (internal API)
- ✅ `FunctionCallProcessor` constructor signature change (internal API)
- ✅ Filter execution model change (filters run in background task)

**Backwards compatibility**:
- ✅ `IAiFunctionFilter` interface unchanged
- ✅ Existing filters that don't use `Emit()` continue working (channel created but unused)
- ✅ All existing tests should pass (filters just don't emit events)

### Phase 2: Create UnifiedPermissionFilter

**Implementation**:
1. Implement `UnifiedPermissionFilter` using `context.Emit()` and `context.WaitForResponseAsync()`
2. Update AGUI handler to convert permission events
3. Create console handler for testing
4. Keep old filters for backwards compatibility (mark deprecated)

### Phase 3: Documentation

**Deliverables**:
1. Document filter event emission pattern
2. Provide example filters (progress, validation, observability)
3. Update quickstart guides
4. Migration guide for custom filters
5. API reference for new event types

### Phase 4: Deprecation (v2.0)

**Future breaking changes**:
1. Remove deprecated filters (`AGUIPermissionFilter`, `ConsolePermissionFilter`)
2. Remove `IPermissionEventEmitter` interface (no longer needed)
3. Bump major version

---

## Benefits

### 1. Standardized Event Emission

**Before**: Each filter type needs custom solution
```csharp
// AGUIPermissionFilter
private readonly IPermissionEventEmitter _emitter;
await _emitter.EmitAsync(new FunctionPermissionRequestEvent { ... });

// ConsolePermissionFilter
Console.WriteLine("[PERMISSION REQUIRED]");
var response = Console.ReadLine();

// Regular filters
// No way to emit events at all
```

**After**: All filters use same pattern
```csharp
context.Emit(new InternalPermissionRequestEvent { ... });
```

### 2. Protocol Independence

**Before**: Separate filter implementation per protocol
- `AGUIPermissionFilter`
- `ConsolePermissionFilter`
- `WebPermissionFilter` (doesn't exist yet)
- `DiscordPermissionFilter` (doesn't exist yet)

**After**: Single filter, protocol adapters handle conversion
```csharp
// One filter
public class UnifiedPermissionFilter : IPermissionFilter { ... }

// Multiple adapters
AGUIEventHandler.ConvertToAGUI(InternalPermissionRequestEvent)
ConsoleEventHandler.HandleConsole(InternalPermissionRequestEvent)
WebEventHandler.ConvertToSSE(InternalPermissionRequestEvent)
```

### 3. User Extensibility

**Before**: No way for users to create custom filters with events

**After**: Users define custom events and filters
```csharp
public record MyCustomEvent(...) : InternalAgentEvent;

public class MyCustomFilter : IAiFunctionFilter
{
    public async Task InvokeAsync(...)
    {
        context.Emit(new MyCustomEvent(...));
        await next(context);
    }
}
```

### 4. Real-Time Event Delivery

Events are visible to handlers **while filters are executing** (not after completion):
- Permission requests reach handlers immediately
- Handlers can respond while filter is blocked
- No deadlocks, no timeouts

### 5. Concurrent Execution

Filters can perform bidirectional communication:
- Emit request event
- Block waiting for response
- Handler sees event concurrently
- Handler sends response
- Filter resumes execution

### 6. Simple Testing

```csharp
[Fact]
public async Task Filter_EmitsEvents()
{
    // Create channel for testing
    var channel = Channel.CreateUnbounded<InternalAgentEvent>();
    var events = new List<InternalAgentEvent>();

    // Collect events in background
    var collectTask = Task.Run(async () =>
    {
        await foreach (var evt in channel.Reader.ReadAllAsync())
        {
            events.Add(evt);
        }
    });

    var context = new AiFunctionContext(...)
    {
        OutboundEvents = channel.Writer,
        Agent = mockAgent
    };

    var filter = new MyFilter();
    await filter.InvokeAsync(context, _ => Task.CompletedTask);

    channel.Writer.Complete();
    await collectTask;

    Assert.Equal(2, events.Count);
    Assert.IsType<MyStartEvent>(events[0]);
    Assert.IsType<MyEndEvent>(events[1]);
}
```

---

## Performance Analysis

### Channel Allocation Overhead

**Per filter invocation**:
- Create `Channel<InternalAgentEvent>`: ~100-200 bytes
- Background `Task.Run()`: ~200 bytes (task object + state machine)
- Total overhead: ~300-400 bytes per function call

**Context**:
- Typical agent run: 5-10 function calls
- Total overhead: 1.5-4 KB
- LLM call response: 1-10 MB
- LLM call duration: 500ms-2s

**Verdict**: Overhead is **0.04-0.4%** of typical memory usage and irrelevant compared to LLM latency.

### CPU Overhead

- Channel write/read: ~20-50ns per event
- Task scheduling: ~100-200ns per filter
- Total: Negligible compared to LLM API call (500ms-2s)

### Comparison to Alternatives

| Approach | Memory Overhead | CPU Overhead | Complexity |
|----------|----------------|--------------|------------|
| **Channels** | ~300 bytes | ~150ns | Low |
| Polling | 0 bytes | High (10ms loops) | Low |
| Callbacks | 0 bytes | ~50ns | High |
| CPS | ~200 bytes | ~100ns | Very High |

---

## Success Criteria

✅ Single `IAiFunctionFilter` interface (unchanged)
✅ All filters can emit events via `context.Emit()`
✅ Bidirectional communication via `context.WaitForResponseAsync()`
✅ Users can create custom event types (derive from `InternalAgentEvent`)
✅ Foundation is event-type-agnostic (no hardcoded event type checking)
✅ Events delivered in real-time (during filter execution, not after)
✅ No deadlocks for bidirectional filters
✅ Protocol adapters handle event conversion (AGUI, Console, IChatClient)
✅ Minimal performance overhead (<1% of LLM operations)
✅ Simple testing (inject channels, assert on events)
✅ Clear migration path (existing filters continue working)

---

## Implementation Timeline

**Phase 1** (Foundation): 2-3 days
- Modify `AiFunctionContext`, `Agent`, `ProcessFunctionCallsAsync`
- Update `ToolScheduler` and `RunAgenticLoopInternal`
- Add event types
- Write unit tests

**Phase 2** (UnifiedPermissionFilter + handlers): 1-2 days
- Implement `UnifiedPermissionFilter`
- Update AGUI handler
- Create console handler
- Integration tests

**Phase 3** (Documentation + examples): 1 day
- API docs
- Usage examples
- Migration guide
- Quickstart updates

**Total**: 4-6 days

---

## Final Recommendation

**APPROVE AND IMPLEMENT** this proposal because:

1. **Solves the architectural gap**: Filters can now emit events and communicate bidirectionally
2. **Uses proven patterns**: Channels are the standard .NET solution for producer-consumer scenarios
3. **Maintains simplicity**: `IAiFunctionFilter` interface unchanged, existing filters continue working
4. **Enables full extensibility**: Users can create custom events and filters without framework changes
5. **Minimal overhead**: ~300 bytes per filter call is negligible in LLM context
6. **No deadlocks**: Concurrent execution prevents blocking issues
7. **Protocol-agnostic**: Single filter implementation works across all protocols
8. **Clear migration path**: Non-breaking addition in v0, deprecate old filters in v1

This design provides a robust foundation for filter event emission that will serve the framework for years to come.
