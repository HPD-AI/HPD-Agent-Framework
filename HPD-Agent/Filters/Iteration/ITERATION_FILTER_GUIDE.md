# Iteration Filter Guide

**Status**: Implemented (Lifecycle Pattern)
**Version**: 2.0
**Last Updated**: 2025-01-27

---

## Overview

**Iteration Filters** run **before and after each LLM call** within the agentic loop, providing fine-grained control over agent execution. They complement **Prompt Filters** (which run once per message turn) by enabling dynamic, iteration-aware modifications.

### Key Capabilities

- ✅ Access to full agent state (including tool results from previous iterations)
- ✅ Dynamic instruction modification per iteration
- ✅ Explicit pre AND post LLM call hooks via lifecycle methods
- ✅ Iteration-aware guidance and context enhancement
- ✅ Signal-based communication with the agent loop
- ✅ Response caching support via `SkipLLMCall` flag

### Architecture Note

**Iteration filters use a lifecycle pattern** (`BeforeIterationAsync`/`AfterIterationAsync`) instead of the middleware pattern used by other filter types. This is because the LLM call uses `yield return` for streaming, which cannot be wrapped in a lambda expression. See [`ITERATION_FILTER_LIFECYCLE_PATTERN.md`](/Proposals/ITERATION_FILTER_LIFECYCLE_PATTERN.md) for technical details.

---

## When to Use Iteration Filters

| Use Case | Filter Type | Reason |
|----------|-------------|--------|
| Dynamic skill instruction injection | **Iteration Filter** | Skills activated mid-turn need instructions in next LLM call |
| Iteration-aware guidance | **Iteration Filter** | Different instructions for iteration 0 vs iteration 5 |
| Tool result context enhancement | **Iteration Filter** | React to tool execution results |
| Error recovery guidance | **Iteration Filter** | Adjust instructions based on consecutive failures |
| Observability & logging | **Iteration Filter** | Track each LLM call with timing |
| Initial RAG/memory retrieval | Prompt Filter | Heavy operations run once per turn |
| Final result processing | Message Turn Filter | Process completed turns |

---

## Architecture

### Execution Flow

```
MESSAGE TURN (once per user message)
├─ IPromptFilter → PrepareTurnAsync
│  └─ Heavy operations (RAG, memory retrieval)
│
└─ AGENTIC LOOP (multiple iterations)
   ├─ Iteration 0:
   │  ├─ IIterationFilter.BeforeIterationAsync → Modify context
   │  ├─ [LLM Call - Streaming]
   │  ├─ IIterationFilter.AfterIterationAsync → Inspect response
   │  └─ [Tool Execution]
   │
   └─ Iteration 1:
      ├─ IIterationFilter.BeforeIterationAsync → Modify context
      ├─ [LLM Call - Streaming]
      ├─ IIterationFilter.AfterIterationAsync → Inspect response
      └─ Done (no tool calls)
```

### Core Interface

```csharp
internal interface IIterationFilter
{
    /// <summary>
    /// Called BEFORE the LLM call begins.
    /// Filters can modify messages/options to inject dynamic context.
    /// Can skip the LLM call by setting context.SkipLLMCall = true.
    /// </summary>
    Task BeforeIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called AFTER the LLM call completes (streaming finished).
    /// Filters can inspect the response and signal state changes.
    /// Response, ToolCalls, and Exception properties are populated at this point.
    /// </summary>
    Task AfterIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken);
}
```

**Why Two Methods?** The lifecycle pattern provides explicit before/after hooks because the LLM call uses `yield return` for streaming, which cannot be wrapped in a middleware lambda expression.

### Context Object

```csharp
public class IterationFilterContext
{
    // METADATA
    public int Iteration { get; init; }              // Current iteration (0-based)
    public string AgentName { get; init; }           // Agent name
    public CancellationToken CancellationToken { get; init; }

    // INPUT (Mutable in BeforeIterationAsync)
    public IList<ChatMessage> Messages { get; set; }  // Messages to send to LLM
    public ChatOptions? Options { get; set; }         // Chat options (includes Instructions)

    // STATE (Read-only snapshot)
    public AgentLoopState State { get; init; }       // Full agent state

    // OUTPUT (Populated before AfterIterationAsync)
    public ChatMessage? Response { get; set; }       // LLM response
    public IReadOnlyList<FunctionCallContent> ToolCalls { get; set; }  // Tool calls requested
    public Exception? Exception { get; set; }        // Exception if failed

    // CONTROL
    public bool SkipLLMCall { get; set; }           // Skip LLM invocation
    public Dictionary<string, object> Properties { get; init; }  // Inter-filter communication

    // HELPERS
    public bool IsFirstIteration => Iteration == 0;
    public bool IsSuccess => Exception == null && Response != null;
    public bool IsFinalIteration => IsSuccess && !ToolCalls.Any();
}
```

---

## Built-in Filters

### 1. SkillInstructionIterationFilter

**Purpose**: Injects active skill instructions before each LLM call.

**Problem Solved**: Skills activated during iteration 0 need their instructions visible in iteration 1+.

**Auto-registered**: Yes (when skills are present)

**Example**:
```csharp
Turn 1: "Activate trading skill and buy AAPL"
  Iteration 0:
    - Filter: No active skills yet
    - LLM: Returns activate_skill("trading")
    - Execute: Skill activated → State.ActiveSkillInstructions += {"trading": "..."}

  Iteration 1: 🔥 KEY MOMENT
    - Filter: Detects State.ActiveSkillInstructions["trading"]
    - Filter: Injects trading instructions into ChatOptions.Instructions
    - LLM: NOW SEES trading instructions, knows how to buy stocks!
    - LLM: Returns buy_stock(symbol="AAPL", quantity=10)
```

### 2. IterationLoggingFilter

**Purpose**: Logs detailed information about each iteration for observability.

**Auto-registered**: Yes (when logger is available)

**Log Levels**:
- `Information`: Iteration start/completion with timing
- `Debug`: Message counts, tool counts
- `Trace`: Instruction previews

**Example Output**:
```
🔄 Iteration 0 starting - Agent: MyAgent
✅ Iteration 0 completed in 1234ms - Tool calls: 2
🔄 Iteration 1 starting - Agent: MyAgent
✅ Iteration 1 completed in 567ms - Tool calls: 0
🏁 Final iteration detected - Agent will respond to user
```

---

## Writing Custom Filters

### Basic Pattern

```csharp
internal class MyIterationFilter : IIterationFilter
{
    public Task BeforeIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        // Modify context before LLM call
        if (context.Iteration > 0)
        {
            context.Options.Instructions += "\nAnalyze tool results.";
        }

        return Task.CompletedTask;
    }

    public Task AfterIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        // React to LLM response
        if (context.IsFinalIteration)
        {
            Console.WriteLine("Final iteration!");
        }

        return Task.CompletedTask;
    }
}
```

**Key Points:**
- `BeforeIterationAsync`: Modify `Messages` and `Options` to influence the LLM call
- `AfterIterationAsync`: Inspect `Response`, `ToolCalls`, and `Exception` to react to results
- Both methods run sequentially for all filters (not in a pipeline)

### Example: Iteration Guidance Filter

```csharp
internal class IterationGuidanceFilter : IIterationFilter
{
    private readonly int _maxIterations;

    public IterationGuidanceFilter(int maxIterations = 20)
    {
        _maxIterations = maxIterations;
    }

    public Task BeforeIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        if (context.Options == null)
            return Task.CompletedTask;

        // Add iteration-specific guidance
        if (context.IsFirstIteration)
        {
            context.Options.Instructions +=
                "\n\n📍 ITERATION GUIDANCE: This is your first turn. " +
                "Identify what tools you need and call them to gather information.";
        }
        else if (context.Iteration >= 1)
        {
            var toolResultCount = context.Messages
                .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                .Count();

            context.Options.Instructions +=
                $"\n\n📍 ITERATION GUIDANCE: You have {toolResultCount} tool result(s). " +
                "Analyze the data and synthesize your response.";
        }

        // Warn if approaching iteration limit
        if (context.Iteration >= _maxIterations - 3)
        {
            var remaining = _maxIterations - context.Iteration;
            context.Options.Instructions +=
                $"\n\n⚠️ WARNING: Only {remaining} iteration(s) remaining. " +
                "Wrap up your analysis and provide a conclusion.";
        }

        return Task.CompletedTask;
    }

    public Task AfterIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        // Could track iteration history, emit analytics, etc.
        return Task.CompletedTask;
    }
}
```

### Example: Response Caching Filter

```csharp
internal class IterationCachingFilter : IIterationFilter
{
    private readonly IMemoryCache _cache;

    public IterationCachingFilter(IMemoryCache cache)
    {
        _cache = cache;
    }

    public Task BeforeIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        // Generate cache key from messages
        var cacheKey = GenerateCacheKey(context.Messages, context.Options);

        // Try to get cached response
        if (_cache.TryGetValue(cacheKey, out CachedResponse? cached))
        {
            // Cache hit! Skip LLM call
            context.SkipLLMCall = true;
            context.Response = cached.Message;
            context.ToolCalls = cached.ToolCalls;

            Console.WriteLine($"✅ Cache hit for iteration {context.Iteration}");
        }

        return Task.CompletedTask;
    }

    public Task AfterIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        // Cache the response (if successful and not from cache)
        if (!context.SkipLLMCall && context.IsSuccess && context.Response != null)
        {
            var cacheKey = GenerateCacheKey(context.Messages, context.Options);
            _cache.Set(cacheKey, new CachedResponse
            {
                Message = context.Response,
                ToolCalls = context.ToolCalls.ToList()
            }, TimeSpan.FromMinutes(10));
        }

        return Task.CompletedTask;
    }

    private string GenerateCacheKey(IList<ChatMessage> messages, ChatOptions? options)
    {
        var messagesJson = JsonSerializer.Serialize(messages);
        var instructions = options?.Instructions ?? "";
        return $"{messagesJson.GetHashCode()}_{instructions.GetHashCode()}";
    }
}
```

**Note:** When `SkipLLMCall = true` is set in `BeforeIterationAsync`, the LLM call is skipped and the `AfterIterationAsync` phase immediately executes with the provided `Response` and `ToolCalls`.

---

## Registration

### Automatic Registration

Built-in filters are automatically registered in `AgentBuilder.BuildCoreAgent()`:

```csharp
// Logging filter (if logger available)
if (_logger != null)
{
    _iterationFilters.Add(new IterationLoggingFilter(iterationLogger));
}

// Skill instruction filter (if skills registered)
if (_pluginManager.GetPluginRegistrations().Any())
{
    _iterationFilters.Add(new SkillInstructionIterationFilter());
}
```

### Manual Registration (Internal API)

```csharp
var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4", apiKey)
    .WithIterationFilter(new MyCustomFilter())  // Internal API
    .Build();
```

---

## Signal-Based Communication

Filters can signal actions to the agent loop via the `Properties` dictionary:

```csharp
// Filter signals intent
context.Properties["ShouldClearActiveSkills"] = true;

// Agent loop processes signal
if (context.Properties.TryGetValue("ShouldClearActiveSkills", out var clear))
{
    state = state with { ActiveSkillInstructions = Empty };
}
```

**Common Signals**:
- `ShouldClearActiveSkills` - Clear active skill instructions
- Custom signals can be added as needed

---

## Bidirectional Event Communication

Iteration filters support **bidirectional event communication** for interactive patterns like permission requests, user confirmations, and clarifications. This allows filters to pause execution, emit events to external handlers, and wait for responses.

### Event Coordination API

The `IterationFilterContext` provides two methods for bidirectional communication:

```csharp
public class IterationFilterContext
{
    /// <summary>
    /// Emits an event to the agent's event stream for external handling.
    /// </summary>
    public void Emit(InternalAgentEvent evt);

    /// <summary>
    /// Waits for a response event from external handlers (blocking operation).
    /// </summary>
    public async Task<T> WaitForResponseAsync<T>(
        string requestId,
        TimeSpan? timeout = null) where T : InternalAgentEvent;
}
```

### Example: Continuation Permission Filter

The `ContinuationPermissionIterationFilter` uses bidirectional events to request permission when the iteration limit is reached:

```csharp
internal class ContinuationPermissionIterationFilter : IIterationFilter
{
    private readonly int _maxIterations;
    private readonly int _extensionAmount;
    private int _currentExtendedLimit;

    public ContinuationPermissionIterationFilter(int maxIterations, int extensionAmount = 3)
    {
        _maxIterations = maxIterations;
        _extensionAmount = extensionAmount;
        _currentExtendedLimit = maxIterations;
    }

    public async Task BeforeIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        // Check if we've hit the iteration limit
        if (context.Iteration >= _currentExtendedLimit)
        {
            var shouldContinue = await RequestContinuationPermissionAsync(context);

            if (!shouldContinue)
            {
                // Terminate execution by skipping LLM call
                context.SkipLLMCall = true;
                context.Response = new ChatMessage(
                    ChatRole.Assistant,
                    "Execution terminated: Maximum iteration limit reached. " +
                    "The agent has exceeded the allowed number of iterations.");
                context.ToolCalls = Array.Empty<FunctionCallContent>();
                context.Properties["IsTerminated"] = true;
            }
        }
    }

    public Task AfterIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task<bool> RequestContinuationPermissionAsync(IterationFilterContext context)
    {
        var continuationId = Guid.NewGuid().ToString();

        // Emit request event to external handler (UI, console, web, etc.)
        context.Emit(new InternalContinuationRequestEvent(
            continuationId,
            "ContinuationPermissionFilter",
            context.Iteration + 1,  // Display as 1-based
            _currentExtendedLimit));

        // Wait for response (blocks until user responds or timeout)
        InternalContinuationResponseEvent response;
        try
        {
            response = await context.WaitForResponseAsync<InternalContinuationResponseEvent>(
                continuationId,
                timeout: TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            return false;  // Default to deny on timeout
        }
        catch (OperationCanceledException)
        {
            return false;  // Default to deny on cancellation
        }

        // If approved, extend the limit
        if (response.Approved)
        {
            var extension = response.ExtensionAmount > 0
                ? response.ExtensionAmount
                : _extensionAmount;

            _currentExtendedLimit += extension;
            return true;
        }

        return false;
    }
}
```

### Key Concepts

**Request/Response Pattern**:
1. **Generate unique ID**: Each request needs a unique identifier for matching responses
2. **Emit request event**: Send event to external handlers via `context.Emit()`
3. **Wait for response**: Block execution with `context.WaitForResponseAsync<T>()`
4. **Handle response**: Process the response and adjust filter behavior

**Event Types**:
- Events must inherit from `InternalAgentEvent`
- Request events contain the data needed for the handler to make a decision
- Response events contain the user's decision and any additional data

**Timeout Handling**:
- Always specify a reasonable timeout (default: 5 minutes)
- Handle `TimeoutException` gracefully (usually deny/cancel)
- Handle `OperationCanceledException` if the operation is cancelled

**Control Flow**:
- `SkipLLMCall = true` prevents the LLM call from executing
- Populate `Response` and `ToolCalls` when skipping to provide proper context
- Use `Properties` dictionary to signal termination state

### Use Cases

**Continuation Permission** (Built-in):
- Request user permission when iteration limit is reached
- Allow user to extend the limit or terminate execution
- Prevents runaway agent loops

**Custom Confirmations**:
- Request approval before expensive operations
- Ask for clarification on ambiguous instructions
- Confirm destructive actions

**Interactive Guidance**:
- Prompt user for additional context mid-execution
- Request parameter values that can't be inferred
- Allow user intervention at specific points

### Integration with External Handlers

External handlers (UI, console, web) subscribe to the agent's event stream and respond:

```csharp
// External handler subscribes to events
await foreach (var evt in agent.RunAsync("Do something complex"))
{
    if (evt is InternalContinuationRequestEvent request)
    {
        // Show prompt to user
        var approved = await PromptUserForContinuation(request);

        // Send response back to filter
        agent.EventCoordinator.Emit(new InternalContinuationResponseEvent(
            request.RequestId,
            approved,
            extensionAmount: 5));
    }
}
```

---

## Performance Guidelines

### DO ✅
- Keep filters fast (< 1ms per filter)
- Inspect state (cheap, immutable)
- Modify strings (cheap)
- Add simple conditionals
- Use early returns to skip unnecessary work

### DON'T ❌
- Make API calls (use prompt filters for that)
- Query databases (use prompt filters for that)
- Perform heavy computations
- Block on I/O

**Rule of Thumb**: If an operation is "heavy," it belongs in a prompt filter (once per message turn), not an iteration filter (multiple times per turn).

---

## Comparison: Prompt Filters vs Iteration Filters

| Aspect | IPromptFilter | IIterationFilter |
|--------|---------------|------------------|
| **Execution Frequency** | Once per message turn | Every LLM call (multiple per turn) |
| **Runs In** | `PrepareTurnAsync` | `RunAgenticLoopInternal` |
| **Timing** | Before agentic loop starts | Before each LLM call in loop |
| **Access to Tool Results** | ❌ No | ✅ Yes (from previous iterations) |
| **Iteration Number** | ❌ N/A | ✅ `context.Iteration` |
| **Agent State Access** | ❌ Limited | ✅ Full `AgentLoopState` |
| **Modify Instructions** | ✅ Initial setup only | ✅ Dynamic per iteration |
| **See LLM Response** | ❌ No (via separate PostInvoke) | ✅ Yes (in `AfterIterationAsync`) |
| **Detect Final Iteration** | ❌ N/A | ✅ `context.IsFinalIteration` |
| **Use Case** | Initial context injection | Iterative guidance + response processing |
| **Example** | RAG, memory retrieval | Skill instructions, iteration guidance |
| **Performance Impact** | Once (acceptable for heavy ops) | Multiple (must be lightweight) |

---

## Troubleshooting

### Filter Not Running

**Problem**: Filter registered but not executing.

**Solution**: Check that:
1. Filter is added to `_iterationFilters` list in AgentBuilder
2. AgentCore constructor receives the filters
3. Both `BeforeIterationAsync()` and `AfterIterationAsync()` are implemented
4. Filters are being called in `RunAgenticLoopInternal` (lines 985-997, 1166-1171)

### State Changes Not Persisting

**Problem**: Modifications to `context.State` are lost.

**Solution**: `State` is immutable (record type). Use `context.Properties` to signal changes:
```csharp
// ❌ WRONG: Direct modification doesn't work
context.State.ActiveSkillInstructions = newValue;

// ✅ CORRECT: Signal via Properties
context.Properties["ShouldClearActiveSkills"] = true;
```

### Performance Issues

**Problem**: Agent execution is slow.

**Solution**:
1. Profile your filters - they should be < 1ms each
2. Move heavy operations to prompt filters
3. Use conditional execution to skip unnecessary work
4. Cache computed values

---

## Future Enhancements

### Potential Extensions (Not Implemented)

1. **Filter Scoping**:
   ```csharp
   builder.WithIterationFilter(new MyFilter(), scope: filter =>
       filter.Iteration > 0); // Only iterations 1+
   ```

2. **Filter Priority**:
   ```csharp
   builder.WithIterationFilter(new Filter1(), priority: 100);
   builder.WithIterationFilter(new Filter2(), priority: 50); // Runs first
   ```

3. **Lifecycle Hooks**:
   ```csharp
   interface IIterationFilter
   {
       Task OnMessageTurnStartAsync();  // Before first iteration
       Task InvokeAsync(context, next); // Each iteration
       Task OnMessageTurnEndAsync();    // After last iteration
   }
   ```

---

## Why Lifecycle Pattern Instead of Middleware?

Iteration filters use a **lifecycle pattern** (`BeforeIterationAsync`/`AfterIterationAsync`) instead of the middleware pattern used by other filter types. Here's why:

### The Technical Constraint

The LLM call uses **streaming with `yield return`** to emit events in real-time:

```csharp
await foreach (var update in _agentTurn.RunAsync(messages, options, ct))
{
    yield return new InternalTextDeltaEvent(textContent.Text);
    // ... more event emissions
}
```

**Problem**: C# **does not allow `yield return` inside lambda expressions**. This means we cannot wrap the LLM streaming call in a middleware pattern like:

```csharp
// ❌ DOESN'T COMPILE - yield return in lambda forbidden
await filter.InvokeAsync(context, async ctx => {
    await foreach (var update in _agentTurn.RunAsync(...))
    {
        yield return new TextDeltaEvent(...);  // ❌ Compiler error!
    }
});
```

### The Solution

The lifecycle pattern provides **explicit before/after hooks** that are called outside the streaming iterator:

```csharp
// ✅ WORKS - No yield return in lambdas
await filter.BeforeIterationAsync(context, ct);  // Modify context

await foreach (var update in _agentTurn.RunAsync(...))  // Stream events
{
    yield return new TextDeltaEvent(...);  // ✅ OK in iterator method
}

await filter.AfterIterationAsync(context, ct);  // Inspect response
```

### Benefits

- ✅ **Honest semantics** - Methods do exactly what their names say
- ✅ **Clear execution** - Each method runs exactly once per iteration
- ✅ **Simple mental model** - No middleware confusion
- ✅ **Streaming preserved** - Real-time event emission unchanged
- ✅ **Same capabilities** - All the functionality of middleware pattern

For full technical details, see [`ITERATION_FILTER_LIFECYCLE_PATTERN.md`](/Proposals/ITERATION_FILTER_LIFECYCLE_PATTERN.md).

---

## References

- **Lifecycle Pattern Proposal**: `/Proposals/ITERATION_FILTER_LIFECYCLE_PATTERN.md`
- **Architecture Proposal**: `/Proposals/ITERATION_FILTER_ARCHITECTURE.md`
- **Implementation**: `/HPD-Agent/Filters/Iteration/`
- **Related Filters**: `/HPD-Agent/Filters/PromptFiltering/`
- **AgentCore Integration**: `/HPD-Agent/Agent/AgentCore.cs` (lines 967-1177)
