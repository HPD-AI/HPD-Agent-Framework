using Microsoft.Extensions.AI;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Agent facade that delegates to specialized components for clean separation of concerns.
/// Supports traditional chat, AGUI streaming protocols, and extended capabilities.
/// </summary>
public class Agent : IChatClient
{
    private readonly IChatClient _baseClient;
    private readonly string _name;
    private readonly ScopedFilterManager? _scopedFilterManager;
    private readonly int _maxFunctionCalls;

    // Microsoft.Extensions.AI compliance fields
    private readonly ChatClientMetadata _metadata;
    private readonly ErrorHandlingPolicy _errorPolicy;
    private string? _conversationId;

    // OpenTelemetry Activity Source for telemetry
    private static readonly ActivitySource ActivitySource = new("HPD.Agent");

    // Specialized component fields for delegation
    private readonly MessageProcessor _messageProcessor;
    private readonly FunctionCallProcessor _functionCallProcessor;
    private readonly AgentTurn _agentTurn;
    private readonly ToolScheduler _toolScheduler;
    private readonly AGUIEventHandler _aguiEventHandler;
    private readonly AGUIEventConverter _aguiConverter;
    private readonly PluginScopingManager _pluginScopingManager;
    private readonly IReadOnlyList<IAiFunctionFilter> _aiFunctionFilters;
    private readonly IReadOnlyList<IMessageTurnFilter> _messageTurnFilters;

    /// <summary>
    /// Agent configuration object containing all settings
    /// </summary>
    public AgentConfig? Config { get; private set; }

    /// <summary>
    /// Metadata about this chat client, compatible with Microsoft.Extensions.AI patterns
    /// </summary>
    public ChatClientMetadata Metadata => _metadata;

    /// <summary>
    /// Provider from the configuration
    /// </summary>
    public ChatProvider Provider => Config?.Provider?.Provider ?? ChatProvider.OpenAI;

    /// <summary>
    /// Model ID from the configuration
    /// </summary>
    public string? ModelId => Config?.Provider?.ModelName;


    /// <summary>
    /// Current conversation ID for tracking
    /// </summary>
    public string? ConversationId => _conversationId;

    /// <summary>
    /// Error handling policy for normalizing provider errors
    /// </summary>
    public ErrorHandlingPolicy ErrorPolicy => _errorPolicy;

    /// <summary>
    /// Extracts and merges ChatOptions from AgentRunOptions (for workflow compatibility).
    /// Preserves workflow-provided tools (e.g., handoff functions) while injecting conversation context.
    /// </summary>
    /// <param name="workflowOptions">Options from workflow (may contain handoff tools)</param>
    /// <param name="conversationContext">Additional context to inject (e.g., ConversationId, Project)</param>
    /// <returns>Merged ChatOptions ready for agent execution</returns>
    /// <summary>
    /// Initializes a new Agent instance from an AgentConfig object
    /// </summary>
    public Agent(
        AgentConfig config,
        IChatClient baseClient,
        ChatOptions? mergedOptions,
        List<IPromptFilter> promptFilters,
        ScopedFilterManager scopedFilterManager,
        IReadOnlyList<IPermissionFilter>? permissionFilters = null,
        IReadOnlyList<IAiFunctionFilter>? aiFunctionFilters = null,
        IReadOnlyList<IMessageTurnFilter>? messageTurnFilters = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = config.Name;
        _scopedFilterManager = scopedFilterManager ?? throw new ArgumentNullException(nameof(scopedFilterManager));
        _maxFunctionCalls = config.MaxAgenticIterations;

        // Initialize Microsoft.Extensions.AI compliance metadata
        _metadata = new ChatClientMetadata(
            providerName: config.Provider?.Provider.ToString()?.ToLowerInvariant(),
            providerUri: AgentBuilderHelpers.ResolveProviderUri(config.Provider),
            defaultModelId: config.Provider?.ModelName
        );

        // Initialize error handling policy
        _errorPolicy = new ErrorHandlingPolicy
        {
            NormalizeProviderErrors = config.ErrorHandling?.NormalizeErrors ?? true,
            IncludeProviderDetails = config.ErrorHandling?.IncludeProviderDetails ?? false,
            MaxRetries = config.ErrorHandling?.MaxRetries ?? 3
        };

        // Auto-detect and set provider error handler if not explicitly configured
        if (config.ErrorHandling != null && config.ErrorHandling.ProviderHandler == null)
        {
            config.ErrorHandling.ProviderHandler = CreateProviderHandler(config.Provider?.Provider);
        }

        // Fix: Store and use AI function filters
        _aiFunctionFilters = aiFunctionFilters ?? new List<IAiFunctionFilter>();
        _messageTurnFilters = messageTurnFilters ?? new List<IMessageTurnFilter>();

        // Create history reducer if configured
        var chatReducer = CreateChatReducer(config, baseClient);

        // Augment system instructions with plan mode guidance if enabled
        var systemInstructions = AugmentSystemInstructionsForPlanMode(config);

        _messageProcessor = new MessageProcessor(
            systemInstructions,
            mergedOptions ?? config.Provider?.DefaultChatOptions,
            promptFilters,
            chatReducer,
            config.HistoryReduction);
        _functionCallProcessor = new FunctionCallProcessor(scopedFilterManager, permissionFilters, _aiFunctionFilters, config.MaxAgenticIterations, config.ErrorHandling);
        _agentTurn = new AgentTurn(_baseClient);
        _pluginScopingManager = new PluginScopingManager();
        _toolScheduler = new ToolScheduler(_functionCallProcessor, config, _pluginScopingManager);
        _aguiConverter = new AGUIEventConverter();
        _aguiEventHandler = new AGUIEventHandler(this);
    }

    /// <summary>
    /// Agent name
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// System instructions/persona
    /// </summary>
    public string? SystemInstructions => Config?.SystemInstructions ?? _messageProcessor.SystemInstructions;

    /// <summary>
    /// Default chat options
    /// </summary>
    public ChatOptions? DefaultOptions => Config?.Provider?.DefaultChatOptions ?? _messageProcessor.DefaultOptions;

    /// <summary>
    /// Clears the execution plan for a specific conversation.
    /// Plans are conversation-scoped and isolated automatically via ConversationContext.
    /// This method is typically not needed but provided for explicit cleanup if desired.
    /// </summary>
    public void ClearPlan(string conversationId, AgentPlanManager planManager)
    {
        planManager?.ClearPlan(conversationId);
    }

    /// <summary>
    /// AIFuncton filters applied to tool calls in conversations (via ScopedFilterManager)
    /// </summary>
    public IReadOnlyList<IAiFunctionFilter> AIFunctionFilters => _aiFunctionFilters;

    /// <summary>
    /// Maximum number of function calls allowed in a single conversation turn
    /// </summary>
    public int MaxFunctionCalls => _maxFunctionCalls;

    /// <summary>
    /// Scoped filter manager for applying filters based on function/plugin scope
    /// </summary>
    public ScopedFilterManager? ScopedFilterManager => _scopedFilterManager;

    #region IChatClient Implementation

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("agent.chat_completion");
        var startTime = DateTimeOffset.UtcNow;

        // Set telemetry tags
        activity?.SetTag("agent.name", _name);
        activity?.SetTag("agent.provider", Provider.ToString());
        activity?.SetTag("agent.model", ModelId);

        // Track conversation ID
        if (options?.AdditionalProperties?.TryGetValue("ConversationId", out var convIdObj) == true &&
            convIdObj is string convId)
        {
            _conversationId = convId;
            activity?.SetTag("conversation.id", convId);
        }

        try
        {
            // Use the unified streaming approach and collect all updates
            var allEvents = new List<BaseEvent>();

            var turnResult = await ExecuteStreamingTurnAsync(messages, options, null, cancellationToken).ConfigureAwait(false);

            // Consume the entire stream
            await foreach (var evt in turnResult.EventStream.WithCancellation(cancellationToken))
            {
                allEvents.Add(evt);
            }

            // Wait for final history and construct response
            var finalHistory = await turnResult.FinalHistory.ConfigureAwait(false);
            // Extract assistant messages from final history
            var assistantMessages = finalHistory.Where(m => m.Role == ChatRole.Assistant).ToList();
            var response = new ChatResponse(assistantMessages);

            // Record telemetry metrics
            var duration = DateTimeOffset.UtcNow - startTime;
            // Note: Token usage not available from constructed ChatResponse, would need provider integration

            activity?.SetTag("completion.duration_ms", duration.TotalMilliseconds);
            activity?.SetTag("completion.success", true);

            return response;
        }
        catch (Exception ex)
        {
            var duration = DateTimeOffset.UtcNow - startTime;
            activity?.SetTag("completion.duration_ms", duration.TotalMilliseconds);
            activity?.SetTag("completion.success", false);
            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetTag("error.message", ex.Message);
            
            // Add cancellation/timeout specific telemetry
            if (ex is OperationCanceledException)
            {
                activity?.SetTag("completion.canceled", true);
                if (Config?.AgenticLoop?.MaxTurnDuration.HasValue == true)
                {
                    activity?.SetTag("completion.timeout_ms", Config.AgenticLoop.MaxTurnDuration.Value.TotalMilliseconds);
                }
            }
            
            throw;
        }
    }



    public async Task<StreamingTurnResult> ExecuteStreamingTurnAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        string[]? documentPaths = null,
        CancellationToken cancellationToken = default)
    {
        // Process documents if provided
        if (documentPaths?.Length > 0)
        {
            messages = await AgentDocumentProcessor.ProcessDocumentsAsync(messages, documentPaths, Config, cancellationToken).ConfigureAwait(false);
        }

        var messagesList = messages.ToList();
        var userMessage = messagesList.LastOrDefault(m => m.Role == ChatRole.User)
            ?? new ChatMessage(ChatRole.User, string.Empty);

        // Create TaskCompletionSources for final history and reduction metadata
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnHistory = new List<ChatMessage>();

        // Create the streaming enumerable with error handling wrapper
        var coreStream = RunAgenticLoopCore(messagesList, options, turnHistory, historyCompletionSource, reductionCompletionSource, cancellationToken);
        var responseStream = WrapStreamWithErrorHandling(coreStream, historyCompletionSource, cancellationToken);

        // Wrap the final history task to apply post-processing when complete
        var wrappedHistoryTask = historyCompletionSource.Task.ContinueWith(async task =>
        {
            Exception? invocationException = task.IsFaulted ? task.Exception?.InnerException : null;
            var history = task.IsCompletedSuccessfully ? await task : new List<ChatMessage>();

            // Apply post-invoke filters (for memory extraction, learning, etc.)
            try
            {
                await _messageProcessor.ApplyPostInvokeFiltersAsync(
                    messagesList,
                    task.IsCompletedSuccessfully ? history : null,
                    invocationException,
                    options,
                    Config?.Name ?? "Agent",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Log but don't fail - post-processing is best-effort
            }

            // Apply message turn filters after the turn completes
            if (task.IsCompletedSuccessfully && _messageTurnFilters.Any())
            {
                try
                {
                    await ApplyMessageTurnFilters(userMessage, history, options, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Log but don't fail the turn if filters fail
                    // Silently continue if filters fail
                }
            }

            return history;
        }, cancellationToken).Unwrap();

        // Return the result containing stream, history, and reduction metadata
        return new StreamingTurnResult(responseStream, wrappedHistoryTask, reductionCompletionSource.Task);
    }

    /// <summary>
    /// Executes a streaming turn with AGUI protocol input.
    /// Converts RunAgentInput to Extensions.AI format internally and streams BaseEvent results.
    /// This eliminates the need for a separate AGUIEventHandler - AGUI support is built directly into Agent.
    /// </summary>
    /// <param name="aguiInput">The AGUI protocol input containing thread, messages, tools, and context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>StreamingTurnResult containing the BaseEvent stream and final conversation history</returns>
    public async Task<StreamingTurnResult> ExecuteStreamingTurnAsync(
        RunAgentInput aguiInput,
        CancellationToken cancellationToken = default)
    {
        // Convert AGUI input to Extensions.AI format using the shared converter instance
        // This preserves tool tracking state across calls
        var messages = _aguiConverter.ConvertToExtensionsAI(aguiInput);
        var chatOptions = _aguiConverter.ConvertToExtensionsAIChatOptions(
            aguiInput,
            Config?.Provider?.DefaultChatOptions,
            Config?.PluginScoping?.ScopeFrontendTools ?? false,
            Config?.PluginScoping?.MaxFunctionNamesInDescription ?? 10);

        // Add AGUI metadata to chat options for tracking
        chatOptions ??= new ChatOptions();
        chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        chatOptions.AdditionalProperties["ConversationId"] = aguiInput.ThreadId;
        chatOptions.AdditionalProperties["RunId"] = aguiInput.RunId;

        // Delegate to the existing Extensions.AI overload
        return await ExecuteStreamingTurnAsync(messages, chatOptions, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Use the raw streaming loop directly for ChatResponseUpdate compatibility
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await foreach (var update in RunAgenticLoopAsync(messages, options, historyCompletionSource, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Wraps the core event stream with error handling and emits structured error events.
    /// Since C# doesn't allow yield return in try-catch blocks, this wrapper catches
    /// exceptions during stream enumeration and converts them to error events.
    /// </summary>
    private async IAsyncEnumerable<BaseEvent> WrapStreamWithErrorHandling(
        IAsyncEnumerable<BaseEvent> innerStream,
        TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = innerStream.GetAsyncEnumerator(cancellationToken);
        Exception? caughtError = null;
        bool runFinishedEmitted = false;
        
        // Capture IDs from RunStartedEvent to use in error scenarios
        string? threadId = null;
        string? runId = null;

        try
        {
            while (true)
            {
                BaseEvent? currentEvent = default;
                bool hasNext = false;
                bool hadError = false;

                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (hasNext)
                    {
                        currentEvent = enumerator.Current;
                        
                        // Capture IDs from RunStartedEvent for error correlation
                        if (currentEvent is RunStartedEvent runStarted)
                        {
                            threadId = runStarted.ThreadId;
                            runId = runStarted.RunId;
                        }
                        else if (currentEvent is RunFinishedEvent)
                        {
                            runFinishedEmitted = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Capture the exception for error event emission
                    caughtError = ex;
                    hadError = true;

                    // Fault the history completion
                    historyCompletion.TrySetException(ex);
                }

                // Emit error event AFTER catch block (C# doesn't allow yield in catch)
                if (hadError && caughtError != null)
                {
                    // Friendlier error message for timeout/cancellation
                    var errorMessage = caughtError is OperationCanceledException
                        ? "Turn was canceled or timed out."
                        : caughtError.Message;
                    
                    yield return EventSerialization.CreateRunError(errorMessage);
                    break; // Exit the loop - stream is terminated
                }

                if (!hasNext)
                {
                    break;
                }

                yield return currentEvent!; // Non-null because hasNext was true
            }

            // If error occurred and RunFinished wasn't emitted, we should emit it now
            // This ensures consumers always get lifecycle closure even on error
            if (caughtError != null && !runFinishedEmitted)
            {
                // Use captured IDs if available, otherwise fallback to empty strings
                yield return EventSerialization.CreateRunFinished(
                    threadId ?? string.Empty, 
                    runId ?? string.Empty);
            }
        }
        finally
        {
            // Always dispose the enumerator
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Core agentic loop that handles streaming with tool execution
    /// </summary>
    private async IAsyncEnumerable<ChatResponseUpdate> RunAgenticLoopAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletionSource,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turnHistory = new List<ChatMessage>();
        
        // Create reduction completion source (not used in this overload but required by signature)
        var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Convert BaseEvent stream to ChatResponseUpdate stream for IChatClient compatibility
        await foreach (var baseEvent in RunAgenticLoopCore(messages, options, turnHistory, historyCompletionSource, reductionCompletionSource, cancellationToken))
        {
            // Only convert events that map to the chat protocol
            switch (baseEvent)
            {
                case TextMessageContentEvent textEvent:
                    yield return new ChatResponseUpdate
                    {
                        Contents = [new TextContent(textEvent.Delta)]
                    };
                    break;

                // StepStartedEvent is for UI observability only - ignore in chat adapter
                case StepStartedEvent:
                    break;

                // ToolCallStartEvent is for UI observability only - ignore in chat adapter  
                case ToolCallStartEvent:
                    break;

                case ToolCallEndEvent:
                    // ToolCallEndEvent is for UI notification only
                    break;
            }
        }
    }

    private async IAsyncEnumerable<BaseEvent> RunAgenticLoopCore(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        List<ChatMessage> turnHistory,
        TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletionSource,
        TaskCompletionSource<ReductionMetadata?> reductionCompletionSource,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Create linked cancellation token for turn timeout
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Config?.AgenticLoop?.MaxTurnDuration is { } turnTimeout)
        {
            turnCts.CancelAfter(turnTimeout);
        }
        var effectiveCancellationToken = turnCts.Token;

        // Generate IDs for this run
        var runId = Guid.NewGuid().ToString();

        // Extract conversation ID from options or generate new one
        string conversationId;
        if (options?.AdditionalProperties?.TryGetValue("ConversationId", out var convIdObj) == true && convIdObj is string convId)
        {
            conversationId = convId;
        }
        else
        {
            conversationId = Guid.NewGuid().ToString();
        }

        var threadId = conversationId; // Use conversation ID as thread ID
        var messageId = Guid.NewGuid().ToString();

        // Fix: Track conversation ID consistently
        _conversationId = conversationId;

        // Emit mandatory RunStarted event
        yield return EventSerialization.CreateRunStarted(threadId, runId);

        // Collect all response updates to build final history - cannot use try-catch with yield
        var responseUpdates = new List<ChatResponseUpdate>();

        // Prepare messages using MessageProcessor
        // Note: PrepareMessagesAsync now returns reduction metadata directly for thread-safety
        ReductionMetadata? reductionMetadata = null;
        IEnumerable<ChatMessage> effectiveMessages;
        ChatOptions? effectiveOptions;
        
        try
        {
            var prep = await _messageProcessor.PrepareMessagesAsync(
                messages, options, _name, effectiveCancellationToken).ConfigureAwait(false);
            (effectiveMessages, effectiveOptions, reductionMetadata) = prep;
        }
        catch
        {
            // Always complete the reduction task even on error to prevent hanging
            reductionCompletionSource.TrySetResult(null);
            throw;
        }

        // Set reduction metadata immediately (thread-safe - no shared mutable state)
        reductionCompletionSource.TrySetResult(reductionMetadata);

        var currentMessages = effectiveMessages.ToList();

        // Emit message start event at the beginning
        bool messageStarted = false;

        // Create agent run context for tracking across all function calls
        var agentRunContext = new AgentRunContext(runId, conversationId, _maxFunctionCalls);

        // Track consecutive same-function calls for circuit breaker (Gemini CLI-inspired)
        // Tracks function name + arguments hash to detect exact repetition
        var lastSignaturePerTool = new Dictionary<string, string>();
        var consecutiveCountPerTool = new Dictionary<string, int>();

        // Plugin scoping: Track expanded plugins (message-turn scoped, auto-collapses after this method exits)
        var expandedPlugins = new HashSet<string>();

        // Main agentic loop - use while loop to allow dynamic limit extension
        int iteration = 0;
        while (iteration < agentRunContext.MaxIterations)
        {
            agentRunContext.CurrentIteration = iteration;

            // Check if run has been terminated early (e.g., by error limit or tool request)
            if (agentRunContext.IsTerminated)
            {
                break;
            }

            var toolRequests = new List<FunctionCallContent>();
            var assistantContents = new List<AIContent>();
            bool streamFinished = false;
            bool hadAnyContent = false;

            // Track thinking/reasoning state PER ITERATION (reset for each turn/function call)
            // Using official AG-UI thinking events for better frontend compatibility
            bool reasoningStarted = false;
            bool reasoningMessageStarted = false;

            // Plugin scoping: Apply scoping to tools for this agent turn (only if enabled)
            var scopedOptions = effectiveOptions;
            if (Config?.PluginScoping?.Enabled == true &&
                effectiveOptions?.Tools != null && effectiveOptions.Tools.Count > 0)
            {
                // Extract AIFunctions from tools (filter out non-function tools)
                var aiFunctions = effectiveOptions.Tools.OfType<AIFunction>().ToList();

                // Apply scoping
                var scopedFunctions = _pluginScopingManager.GetToolsForAgentTurn(
                    aiFunctions,
                    expandedPlugins);

                // Create new options with scoped tools (cast back to AITool)
                scopedOptions = new ChatOptions
                {
                    ModelId = effectiveOptions.ModelId,
                    Tools = scopedFunctions.Cast<AITool>().ToList(),
                    ToolMode = effectiveOptions.ToolMode,
                    Temperature = effectiveOptions.Temperature,
                    MaxOutputTokens = effectiveOptions.MaxOutputTokens,
                    TopP = effectiveOptions.TopP,
                    FrequencyPenalty = effectiveOptions.FrequencyPenalty,
                    PresencePenalty = effectiveOptions.PresencePenalty,
                    StopSequences = effectiveOptions.StopSequences,
                    ResponseFormat = effectiveOptions.ResponseFormat,
                    AdditionalProperties = effectiveOptions.AdditionalProperties
                };
            }

            // Run turn and collect events
            await foreach (var update in _agentTurn.RunAsync(currentMessages, scopedOptions, effectiveCancellationToken))
            {
                // Store update for building final history
                responseUpdates.Add(update);

                // Track whether we received any content this iteration
                hadAnyContent = hadAnyContent || (update.Contents?.Count > 0) || update.FinishReason != null;

                // Process contents and emit appropriate BaseEvent objects
                if (update.Contents != null)
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                        {
                            // Emit reasoning start event ONLY on first reasoning chunk (official AG-UI REASONING event)
                            if (!reasoningStarted)
                            {
                                yield return EventSerialization.CreateReasoningStart(messageId);
                                reasoningStarted = true;
                            }

                            // Emit reasoning message start if not already started
                            if (!reasoningMessageStarted)
                            {
                                yield return EventSerialization.CreateReasoningMessageStart(messageId, "assistant");
                                reasoningMessageStarted = true;
                            }

                            // Emit reasoning content for each chunk (official AG-UI REASONING event)
                            yield return EventSerialization.CreateReasoningMessageContent(messageId, reasoning.Text);

                            // Add reasoning to assistantContents so it's preserved in conversation history
                            assistantContents.Add(reasoning);
                        }
                        else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                        {
                            // If we were in reasoning mode, finish the reasoning events
                            if (reasoningMessageStarted)
                            {
                                yield return EventSerialization.CreateReasoningMessageEnd(messageId);
                                reasoningMessageStarted = false;
                            }
                            if (reasoningStarted)
                            {
                                yield return EventSerialization.CreateReasoningEnd(messageId);
                                reasoningStarted = false;
                            }

                            // Emit message start if this is the first text content
                            if (!messageStarted)
                            {
                                yield return EventSerialization.CreateTextMessageStart(messageId, "assistant");
                                messageStarted = true;
                            }

                            // Regular text content - add to history
                            assistantContents.Add(textContent);

                            // Emit text content event
                            yield return EventSerialization.CreateTextMessageContent(messageId, textContent.Text);
                        }
                        else if (content is FunctionCallContent functionCall)
                        {
                            toolRequests.Add(functionCall);
                            assistantContents.Add(functionCall);
                        }
                    }
                }

                // Check for stream completion
                if (update.FinishReason != null)
                {
                    streamFinished = true;

                    // If reasoning is still active when stream ends, finish it
                    if (reasoningMessageStarted)
                    {
                        yield return EventSerialization.CreateReasoningMessageEnd(messageId);
                        reasoningMessageStarted = false;
                    }
                    if (reasoningStarted)
                    {
                        yield return EventSerialization.CreateReasoningEnd(messageId);
                        reasoningStarted = false;
                    }
                }
            }

            // If there are tool requests, execute them
            if (toolRequests.Count > 0)
            {
                // Fix: Ensure message start before first tool event
                if (!messageStarted)
                {
                    yield return EventSerialization.CreateTextMessageStart(messageId, "assistant");
                    messageStarted = true;
                }

                // Create assistant message with tool calls for current turn (includes reasoning for API)
                var assistantMessage = new ChatMessage(ChatRole.Assistant, assistantContents);
                currentMessages.Add(assistantMessage);

                // Create assistant message for history WITHOUT reasoning (save tokens in future turns)
                var historyContents = assistantContents.Where(c => c is not TextReasoningContent).ToList();
                var historyMessage = new ChatMessage(ChatRole.Assistant, historyContents);
                turnHistory.Add(historyMessage);

                // Emit tool call start events and track statistics
                foreach (var toolRequest in toolRequests)
                {
                    // Track tool call statistics
                    // Tool call telemetry now handled by Activity tags in GetResponseAsync

                    yield return EventSerialization.CreateToolCallStart(
                        toolRequest.CallId,
                        toolRequest.Name ?? string.Empty,
                        messageId);

                    // Emit tool call arguments event
                    if (toolRequest.Arguments != null && toolRequest.Arguments.Count > 0)
                    {
                        var argsJson = System.Text.Json.JsonSerializer.Serialize(
                            toolRequest.Arguments,
                            AGUIJsonContext.Default.DictionaryStringObject);

                        yield return EventSerialization.CreateToolCallArgs(toolRequest.CallId, argsJson);
                    }
                }

                // Execute tools (pass expandedPlugins for container expansion tracking)
                var toolResultMessage = await _toolScheduler.ExecuteToolsAsync(
                    currentMessages, toolRequests, effectiveOptions, agentRunContext, _name, expandedPlugins, effectiveCancellationToken).ConfigureAwait(false);

                // Add tool results to history
                currentMessages.Add(toolResultMessage);
                turnHistory.Add(toolResultMessage);

                // Check for errors in tool results and track consecutive errors
                bool hasErrors = false;
                foreach (var content in toolResultMessage.Contents)
                {
                    if (content is FunctionResultContent result)
                    {
                        yield return EventSerialization.CreateToolCallEnd(result.CallId);

                        // Emit official AG-UI TOOL_CALL_RESULT event
                        yield return EventSerialization.CreateToolCallResult(result.CallId, result.Result?.ToString() ?? "null");

                        // Check if this result represents an error
                        if (result.Exception != null ||
                            (result.Result?.ToString()?.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ?? false))
                        {
                            hasErrors = true;
                        }
                    }
                }

                // Circuit breaker: Check for consecutive same-function calls (Gemini CLI-inspired)
                // Tracks function signature (name + arguments) to detect exact repetition
                // Handles parallel tool calls by checking each one separately
                if (toolRequests.Count > 0 && Config?.AgenticLoop?.MaxConsecutiveFunctionCalls is { } maxConsecutive)
                {
                    // Check each tool request in the batch (handles parallel calls)
                    foreach (var toolRequest in toolRequests)
                    {
                        var currentSignature = GetFunctionSignature(toolRequest);
                        var toolName = toolRequest.Name ?? "_unknown";

                        if (lastSignaturePerTool.TryGetValue(toolName, out var lastSignature) && currentSignature == lastSignature)
                        {
                            consecutiveCountPerTool[toolName] = consecutiveCountPerTool.GetValueOrDefault(toolName, 0) + 1;
                        }
                        else
                        {
                            lastSignaturePerTool[toolName] = currentSignature;
                            consecutiveCountPerTool[toolName] = 1;
                        }

                        if (consecutiveCountPerTool[toolName] >= maxConsecutive)
                        {
                            var errorMessage = $"⚠️ Circuit breaker triggered: Function '{toolRequest.Name}' with same arguments called {consecutiveCountPerTool[toolName]} times consecutively. Stopping to prevent infinite loop.";
                            yield return EventSerialization.CreateTextMessageContent(messageId, errorMessage);

                            agentRunContext.IsTerminated = true;
                            agentRunContext.TerminationReason = $"Circuit breaker: '{toolRequest.Name}' with same arguments called {consecutiveCountPerTool[toolName]} times consecutively";
                            break;
                        }
                    }

                    // If circuit breaker triggered, exit the main loop
                    if (agentRunContext.IsTerminated)
                    {
                        break;
                    }
                }

                // Track consecutive errors across iterations
                if (hasErrors)
                {
                    agentRunContext.RecordError();

                    var maxConsecutiveErrors = Config?.ErrorHandling?.MaxRetries ?? 3;
                    if (agentRunContext.HasExceededErrorLimit(maxConsecutiveErrors))
                    {
                        // Emit error event and terminate
                        yield return EventSerialization.CreateTextMessageContent(
                            messageId,
                            $"⚠️ Maximum consecutive errors ({maxConsecutiveErrors}) exceeded. Stopping execution to prevent infinite error loop.");

                        agentRunContext.IsTerminated = true;
                        agentRunContext.TerminationReason = $"Exceeded maximum consecutive errors ({maxConsecutiveErrors})";
                        break;
                    }
                }
                else
                {
                    // Reset error count on successful iteration
                    agentRunContext.RecordSuccess();
                }

                // Fix: Do NOT add tool results to responseUpdates - they belong to tool role, not assistant
                // This prevents tool content from being mixed into the final assistant message

                // Update options for next iteration to allow the model to choose not to call tools
                effectiveOptions = effectiveOptions == null
                    ? new ChatOptions { ToolMode = ChatToolMode.Auto }
                    : new ChatOptions
                    {
                        Tools = effectiveOptions.Tools,
                        ToolMode = ChatToolMode.Auto,
                        AllowMultipleToolCalls = effectiveOptions.AllowMultipleToolCalls,
                        MaxOutputTokens = effectiveOptions.MaxOutputTokens,
                        Temperature = effectiveOptions.Temperature,
                        TopP = effectiveOptions.TopP,
                        FrequencyPenalty = effectiveOptions.FrequencyPenalty,
                        PresencePenalty = effectiveOptions.PresencePenalty,
                        ResponseFormat = effectiveOptions.ResponseFormat,
                        Seed = effectiveOptions.Seed,
                        StopSequences = effectiveOptions.StopSequences,
                        ModelId = effectiveOptions.ModelId,
                        AdditionalProperties = effectiveOptions.AdditionalProperties
                    };

                // Continue to next iteration
                iteration++;
            }
            else if (streamFinished)
            {
                // No tools called and stream finished - we're done
                if (assistantContents.Any())
                {
                    // Save to history WITHOUT reasoning (save tokens in future turns)
                    var historyContents = assistantContents.Where(c => c is not TextReasoningContent).ToList();
                    var historyMessage = new ChatMessage(ChatRole.Assistant, historyContents);
                    turnHistory.Add(historyMessage);
                }
                break;
            }
            else
            {
                // Guard: If stream ended (we're here) and there are no tools to run,
                // there's nothing left to do—exit regardless of whether content was received.
                // This prevents unnecessary LLM passes when providers omit FinishReason.
                if (toolRequests.Count == 0)
                {
                    break;
                }
                
                // Increment for next iteration to execute tools
                iteration++;
            }
        }

        // Build the complete history including the final assistant message
        if (responseUpdates.Any())
        {
            var finalResponse = ConstructChatResponseFromUpdates(responseUpdates);
            // Fix: Guard against empty messages collection and ensure message has content
            if (finalResponse.Messages.Count > 0)
            {
                var finalAssistantMessage = finalResponse.Messages[0];
                
                // Only add if the message has content and we don't already have it
                // Use canonical comparison to avoid false negatives with identical content in new instances
                if (finalAssistantMessage.Contents.Count > 0)
                {
                    var lastAssistant = turnHistory.LastOrDefault(m => m.Role == ChatRole.Assistant);
                    if (lastAssistant == null ||
                        CanonicalizeContents(lastAssistant.Contents) != CanonicalizeContents(finalAssistantMessage.Contents))
                    {
                        turnHistory.Add(finalAssistantMessage);
                    }
                }
            }
        }

        // Emit message end event if we started a message
        if (messageStarted)
        {
            yield return EventSerialization.CreateTextMessageEnd(messageId);
        }

        // Emit mandatory RunFinished event (errors handled at higher level)
        yield return EventSerialization.CreateRunFinished(threadId, runId);

        // Set the final complete history
        historyCompletionSource.SetResult(turnHistory);
    }

    /// <summary>
    /// Constructs a final ChatResponse from collected streaming updates
    /// </summary>
    private static ChatResponse ConstructChatResponseFromUpdates(List<ChatResponseUpdate> updates)
    {
        // Collect all content from the updates
        var allContents = new List<AIContent>();
        ChatFinishReason? finishReason = null;
        string? modelId = null;
        string? responseId = null;
        DateTimeOffset? createdAt = null;

        foreach (var update in updates)
        {
            if (update.Contents != null)
            {
                // Only include TextContent (exclude TextReasoningContent to save tokens in future turns)
                allContents.AddRange(update.Contents.Where(c => c is TextContent && c is not TextReasoningContent));
            }

            if (update.FinishReason != null)
                finishReason = update.FinishReason;

            if (update.ModelId != null)
                modelId = update.ModelId;

            if (update.ResponseId != null)
                responseId = update.ResponseId;

            if (update.CreatedAt != null)
                createdAt = update.CreatedAt;
        }

        // Don't create empty assistant messages
        if (allContents.Count == 0)
        {
            return new ChatResponse(Array.Empty<ChatMessage>())
            {
                FinishReason = finishReason,
                ModelId = modelId,
                CreatedAt = createdAt
            };
        }

        // Create a ChatMessage from the collected content
        var chatMessage = new ChatMessage(ChatRole.Assistant, allContents)
        {
            MessageId = responseId
        };

        return new ChatResponse(chatMessage)
        {
            FinishReason = finishReason,
            ModelId = modelId,
            CreatedAt = createdAt
        };
    }

    /// <inheritdoc />
    object? IChatClient.GetService(Type serviceType, object? serviceKey)
    {
        return serviceType switch
        {
            Type t when t == typeof(Agent) => this,
            Type t when t == typeof(IAGUIAgent) => _aguiEventHandler,
            Type t when t == typeof(ChatClientMetadata) => _metadata,
            Type t when t == typeof(ScopedFilterManager) => _scopedFilterManager,
            Type t when t == typeof(ErrorHandlingPolicy) => _errorPolicy,
            Type t when t == typeof(IChatClient) => _baseClient,
            _ => _baseClient.GetService(serviceType, serviceKey)
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _baseClient?.Dispose();
    }

    #endregion

    #region History Reduction

    /// <summary>
    /// Creates an IChatReducer based on the AgentConfig settings
    /// </summary>
    private static string? AugmentSystemInstructionsForPlanMode(AgentConfig config)
    {
        var baseInstructions = config.SystemInstructions;
        var planConfig = config.PlanMode;

        if (planConfig == null || !planConfig.Enabled)
        {
            return baseInstructions;
        }

        var planInstructions = planConfig.CustomInstructions ?? GetDefaultPlanModeInstructions();

        if (string.IsNullOrEmpty(baseInstructions))
        {
            return planInstructions;
        }

        return $"{baseInstructions}\n\n{planInstructions}";
    }

    private static string GetDefaultPlanModeInstructions()
    {
        return @"[PLAN MODE ENABLED]
You have access to plan management tools for complex multi-step tasks.

Available functions:
- create_plan(goal, steps[]): Create a new plan with a goal and initial steps
- update_plan_step(stepId, status, notes): Update step status (pending/in_progress/completed/blocked) and add notes
- add_plan_step(description, afterStepId): Add a new step when you discover additional work needed
- add_context_note(note): Record important discoveries, learnings, or context during execution
- complete_plan(): Mark the entire plan as complete when goal is achieved

Best practices:
- Create plans for tasks requiring 3+ steps, affecting multiple files, or with uncertain scope
- Update step status as you progress to maintain context across conversation turns
- Add context notes when discovering important information (e.g., ""Found auth uses JWT, not sessions"")
- Plans are conversation-scoped working memory - they help you maintain progress and avoid repeating failed approaches
- When a step is blocked, mark it as 'blocked' with notes explaining why, then continue with other steps if possible";
    }

    private static IChatReducer? CreateChatReducer(AgentConfig config, IChatClient baseClient)
    {
        var historyConfig = config.HistoryReduction;

        if (historyConfig == null || !historyConfig.Enabled)
        {
            return null;
        }

        return historyConfig.Strategy switch
        {
            HistoryReductionStrategy.MessageCounting =>
                new MessageCountingChatReducer(historyConfig.TargetMessageCount),

            HistoryReductionStrategy.Summarizing =>
                CreateSummarizingReducer(baseClient, historyConfig, config),

            _ => throw new ArgumentException($"Unknown history reduction strategy: {historyConfig.Strategy}")
        };
    }

    /// <summary>
    /// Creates a SummarizingChatReducer with custom configuration
    /// </summary>
    private static SummarizingChatReducer CreateSummarizingReducer(IChatClient baseClient, HistoryReductionConfig historyConfig, AgentConfig agentConfig)
    {
        // Determine which client to use for summarization
        IChatClient summarizerClient = baseClient; // Default to main client

        if (historyConfig.SummarizerProvider != null)
        {
            // Create a separate client for summarization
            summarizerClient = CreateClientForProvider(historyConfig.SummarizerProvider);
        }

        var reducer = new SummarizingChatReducer(
            summarizerClient,
            historyConfig.TargetMessageCount,
            historyConfig.SummarizationThreshold);

        if (!string.IsNullOrEmpty(historyConfig.CustomSummarizationPrompt))
        {
            reducer.SummarizationPrompt = historyConfig.CustomSummarizationPrompt;
        }

        return reducer;
    }

    /// <summary>
    /// Creates an IChatClient from a ProviderConfig for use by the summarizer.
    /// Delegates to AgentBuilderHelpers to reuse existing provider creation logic.
    /// </summary>
    private static IChatClient CreateClientForProvider(ProviderConfig providerConfig)
    {
        return AgentBuilderHelpers.CreateClientFromProviderConfig(providerConfig);
    }

    /// <summary>
    /// Auto-detects and creates the appropriate error handler based on the provider.
    /// Opinionated by default: automatically selects provider-specific handler.
    /// Returns GenericErrorHandler as fallback for unknown providers.
    /// </summary>
    private static HPD.Agent.ErrorHandling.IProviderErrorHandler CreateProviderHandler(ChatProvider? provider)
    {
        return provider switch
        {
            ChatProvider.OpenAI => new HPD.Agent.ErrorHandling.Providers.OpenAIErrorHandler(),
            ChatProvider.AzureOpenAI => new HPD.Agent.ErrorHandling.Providers.OpenAIErrorHandler(),
            // Future provider handlers will be added here as they are implemented:
            // ChatProvider.Anthropic => new AnthropicErrorHandler(),
            // ChatProvider.GoogleAI => new GoogleAIErrorHandler(),
            // ChatProvider.VertexAI => new GoogleAIErrorHandler(),
            _ => new HPD.Agent.ErrorHandling.GenericErrorHandler()
        };
    }

    #endregion

    #region Message Turn Filters

    /// <summary>
    /// Applies message turn filters after a complete turn (including all function calls) finishes.
    /// 
    /// IMPORTANT: Message turn filters are READ-ONLY for observation and logging purposes.
    /// Mutations made by filters to the MessageTurnFilterContext are NOT persisted back to conversation history.
    /// 
    /// Use cases for message turn filters:
    /// - Telemetry and logging of completed turns
    /// - Monitoring agent behavior and function call patterns
    /// - Triggering side effects based on conversation events
    /// 
    /// If you need to mutate conversation history, use:
    /// - IPromptFilter (pre-turn) to modify messages before LLM execution
    /// - IAiFunctionFilter (during-turn) to intercept and modify tool execution
    /// - Conversation APIs directly to append/modify persisted messages
    /// </summary>
    private async Task ApplyMessageTurnFilters(
        ChatMessage userMessage,
        IReadOnlyList<ChatMessage> finalHistory,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        if (!_messageTurnFilters.Any())
        {
            return;
        }

        // Extract assistant messages from final history as the response
        var assistantMessages = finalHistory.Where(m => m.Role == ChatRole.Assistant).ToList();
        if (!assistantMessages.Any())
        {
            return; // No response to filter
        }

        var response = new ChatResponse(assistantMessages);

        // Collect agent function call metadata
        var agentFunctionCalls = CollectAgentFunctionCalls(finalHistory);

        // Create filter context (convert AdditionalProperties to Dictionary if available)
        Dictionary<string, object>? metadata = null;
        if (options?.AdditionalProperties != null)
        {
            metadata = new Dictionary<string, object>(options.AdditionalProperties!);
        }

        var context = new MessageTurnFilterContext(
            _conversationId ?? Guid.NewGuid().ToString(),
            userMessage,
            response,
            agentFunctionCalls,
            metadata,
            options,
            cancellationToken);

        // Build reverse pipeline
        Func<MessageTurnFilterContext, Task> pipeline = _ => Task.CompletedTask;
        foreach (var filter in _messageTurnFilters.AsEnumerable().Reverse())
        {
            var next = pipeline;
            pipeline = ctx => filter.InvokeAsync(ctx, next);
        }

        await pipeline(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Collects function call metadata from the message history
    /// </summary>
    private Dictionary<string, List<string>> CollectAgentFunctionCalls(IReadOnlyList<ChatMessage> history)
    {
        var metadata = new Dictionary<string, List<string>>();

        foreach (var message in history)
        {
            if (message.Role != ChatRole.Assistant)
                continue;

            var functionCalls = message.Contents
                .OfType<FunctionCallContent>()
                .Select(fc => fc.Name)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            if (functionCalls.Any())
            {
                // Append to existing list instead of overwriting
                if (!metadata.TryGetValue(_name, out var existingList))
                {
                    metadata[_name] = functionCalls;
                }
                else
                {
                    // Add unique function calls to avoid duplicates
                    existingList.AddRange(functionCalls.Where(fc => !existingList.Contains(fc)));
                }
            }
        }

        return metadata;
    }

    #endregion

    /// <summary>
    /// Serializes any AG-UI event to JSON using the correct polymorphic serialization.
    /// </summary>
    /// <param name="aguiEvent">The AG-UI event to serialize</param>
    /// <returns>JSON string with proper polymorphic serialization</returns>
    public string SerializeEvent(BaseEvent aguiEvent)
    {
        return EventSerialization.SerializeEvent(aguiEvent);
    }

    #region History Reduction Metadata

    // Reduction metadata is now properly handled via StreamingTurnResult.ReductionTask.
    // This ensures the metadata is available when the turn completes and PrepareMessagesAsync
    // has finished executing. The task-based approach eliminates the timing bug where
    // metadata was captured before the reduction actually occurred.

    #endregion

    #region Circuit Breaker Helper

    /// <summary>
    /// Generates a unique signature for a function call based on name and arguments.
    /// Inspired by Gemini CLI's loop detection approach.
    /// Uses SHA256 hash of "functionName:jsonArguments" to detect exact repetition.
    /// </summary>
    /// <param name="toolCall">The function call to generate a signature for</param>
    /// <returns>SHA256 hash string representing the function signature</returns>
    private static string GetFunctionSignature(FunctionCallContent toolCall)
    {
        // Sort arguments by key for deterministic serialization
        // This prevents semantically identical args with different key order from hashing differently
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();
        var ordered = args.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                          .ToDictionary(kv => kv.Key, kv => kv.Value);

        // Serialize sorted arguments to JSON (using source-generated context for AOT)
        var argsJson = System.Text.Json.JsonSerializer.Serialize(
            ordered,
            AGUIJsonContext.Default.DictionaryStringObject);

        // Combine function name and arguments
        var combined = $"{toolCall.Name}:{argsJson}";

        // Generate SHA256 hash for efficient comparison
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// Creates a canonical string representation of message contents for comparison.
    /// Avoids false negatives when comparing semantically identical content in different instances.
    /// Covers all major content types to prevent duplicate message appending.
    /// </summary>
    private static string CanonicalizeContents(IList<AIContent> contents)
    {
        var sb = new StringBuilder();
        foreach (var c in contents)
        {
            switch (c)
            {
                case TextReasoningContent r:
                    // Check reasoning first since it derives from TextContent
                    sb.Append("|R:").Append(r.Text);
                    break;
                case TextContent t:
                    sb.Append("|T:").Append(t.Text);
                    break;
                case FunctionCallContent fc:
                    sb.Append("|F:").Append(fc.Name).Append(":").Append(fc.CallId).Append(":");
                    sb.Append(System.Text.Json.JsonSerializer.Serialize(
                        fc.Arguments ?? new Dictionary<string, object?>(),
                        AGUIJsonContext.Default.DictionaryStringObject));
                    break;
                case FunctionResultContent fr:
                    sb.Append("|FR:").Append(fr.CallId).Append(":");
                    sb.Append(fr.Result?.ToString() ?? "null");
                    if (fr.Exception != null)
                    {
                        sb.Append(":EX:").Append(fr.Exception.Message);
                    }
                    break;
                case DataContent data:
                    // DataContent covers images, audio, and generic data
                    sb.Append("|D:").Append(data.MediaType ?? "unknown").Append(":");
                    if (data.Data.Length > 0)
                    {
                        // Use SHA-256 for deterministic, collision-resistant hashing
                        // (GetHashCode() is not stable across processes)
                        sb.Append(HashDataBytes(data.Data));
                    }
                    else if (data.Uri != null)
                    {
                        sb.Append(data.Uri.ToString());
                    }
                    break;
                // Future-proof: capture type name for any unknown content types
                default:
                    sb.Append("|U:").Append(c.GetType().Name);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Computes a deterministic SHA-256 hash of byte data for content comparison.
    /// Stable across processes and prevents collisions better than GetHashCode().
    /// </summary>
    private static string HashDataBytes(ReadOnlyMemory<byte> data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data.ToArray());
        return Convert.ToHexString(hash);
    }

    #endregion

}


#region AGUI Event Handling

/// <summary>
/// Thin wrapper that implements IAGUIAgent interface for backward compatibility.
/// Delegates directly to Agent.ExecuteStreamingTurnAsync(RunAgentInput) overload.
/// For new code, prefer calling Agent.ExecuteStreamingTurnAsync(RunAgentInput) directly.
///
/// MIGRATION NOTE: This is a temporary adapter for the custom AGUI protocol implementation.
/// When the official AGUIDotnet library is released with AOT support:
/// 1. This class will be updated to implement AGUIDotnet.Agent.IAGUIAgent
/// 2. Or may be deprecated in favor of the official library's implementation
/// 3. The underlying Agent.ExecuteStreamingTurnAsync(RunAgentInput) will remain stable
///
/// Current implementation provides AGUI protocol compatibility without external dependencies.
/// </summary>
public class AGUIEventHandler : IAGUIAgent
{
    private readonly Agent _agent;

    public AGUIEventHandler(Agent agent)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    /// <summary>
    /// Runs the agent in AGUI streaming mode, emitting events to the provided channel.
    /// This is now a thin wrapper around Agent.ExecuteStreamingTurnAsync(RunAgentInput).
    /// </summary>
    public async Task RunAsync(
        RunAgentInput input,
        ChannelWriter<BaseEvent> events,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Delegate to the new Agent overload that handles AGUI input directly
            var streamResult = await _agent.ExecuteStreamingTurnAsync(input, cancellationToken).ConfigureAwait(false);

            // Forward events to the channel
            await foreach (var baseEvent in streamResult.EventStream.WithCancellation(cancellationToken))
            {
                await events.WriteAsync(baseEvent, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Always complete the channel, even on error
            events.Complete();
        }
    }
}

#endregion

#region FunctionCallProcessor

/// <summary>
/// Handles all function calling logic, including multi-turn execution and filter pipelines.
/// </summary>
public class FunctionCallProcessor
{
    private readonly ScopedFilterManager? _scopedFilterManager;
    private readonly IReadOnlyList<IPermissionFilter> _permissionFilters;
    private readonly IReadOnlyList<IAiFunctionFilter> _aiFunctionFilters;
    private readonly int _maxFunctionCalls;
    private readonly ErrorHandlingConfig? _errorHandlingConfig;

    public FunctionCallProcessor(
        ScopedFilterManager? scopedFilterManager,
        IReadOnlyList<IPermissionFilter>? permissionFilters,
        IReadOnlyList<IAiFunctionFilter>? aiFunctionFilters,
        int maxFunctionCalls,
        ErrorHandlingConfig? errorHandlingConfig = null)
    {
        _scopedFilterManager = scopedFilterManager;
        _permissionFilters = permissionFilters ?? new List<IPermissionFilter>();
        _aiFunctionFilters = aiFunctionFilters ?? new List<IAiFunctionFilter>();
        _maxFunctionCalls = maxFunctionCalls;
        _errorHandlingConfig = errorHandlingConfig;
    }


    /// <summary>
    /// Checks if a function call requires and has permission to execute.
    /// Returns true if approved (or no permission needed), false if denied.
    /// </summary>
    public async Task<bool> CheckPermissionAsync(
        FunctionCallContent functionCall,
        ChatOptions? options,
        AgentRunContext agentRunContext,
        string? agentName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(functionCall.Name))
            return false;

        var toolCallRequest = new ToolCallRequest
        {
            FunctionName = functionCall.Name,
            Arguments = functionCall.Arguments ?? new Dictionary<string, object?>()
        };

        var context = new AiFunctionContext(toolCallRequest)
        {
            Function = FindFunction(toolCallRequest.FunctionName, options?.Tools),
            RunContext = agentRunContext,
            AgentName = agentName
        };

        // Pass the CallId through metadata for permission tracking
        context.Metadata["CallId"] = functionCall.CallId;

        // If function not found, deny
        if (context.Function == null)
            return false;

        // Check if the function requires permission
        if (context.Function is not HPDAIFunctionFactory.HPDAIFunction hpdFunction ||
            !hpdFunction.HPDOptions.RequiresPermission)
        {
            return true; // No permission needed, auto-approve
        }

        // Run ONLY permission filters (no execution)
        Func<AiFunctionContext, Task> noOpNext = async (ctx) => { await Task.CompletedTask; };
        var pipeline = noOpNext;

        // Build permission filter pipeline
        foreach (var permissionFilter in _permissionFilters.Reverse())
        {
            var previous = pipeline;
            pipeline = ctx => permissionFilter.InvokeAsync(ctx, previous);
        }

        // Execute permission check
        await pipeline(context).ConfigureAwait(false);

        // Return approval status
        return !context.IsTerminated;
    }

    /// <summary>
    /// Processes the function calls and returns the messages to add to the conversation.
    /// If skipPermissionFilters is true, permission filters are not re-run (used when parallel
    /// execution has already checked permissions in CheckPermissionAsync).
    /// </summary>
    public async Task<IList<ChatMessage>> ProcessFunctionCallsAsync(
        List<ChatMessage> messages,
        ChatOptions? options,
        List<FunctionCallContent> functionCallContents,
        AgentRunContext agentRunContext,
        string? agentName,
        CancellationToken cancellationToken,
        bool skipPermissionFilters = false)
    {
        var resultMessages = new List<ChatMessage>();

        // Process each function call through the filter pipeline
        foreach (var functionCall in functionCallContents)
        {
            // Skip functions without names (safety check)
            if (string.IsNullOrEmpty(functionCall.Name))
                continue;

            var toolCallRequest = new ToolCallRequest
            {
                FunctionName = functionCall.Name,
                Arguments = functionCall.Arguments ?? new Dictionary<string, object?>()
            };

            var context = new AiFunctionContext(toolCallRequest)
            {
                Function = FindFunction(toolCallRequest.FunctionName, options?.Tools),
                RunContext = agentRunContext,
                AgentName = agentName
            };

            // Pass the CallId through metadata for permission tracking
            context.Metadata["CallId"] = functionCall.CallId;

            // The final step in the pipeline is the actual function invocation with retry logic.
            Func<AiFunctionContext, Task> finalInvoke = async (ctx) =>
            {
                if (ctx.Function is null)
                {
                    ctx.Result = $"Function '{ctx.ToolCallRequest.FunctionName}' not found.";
                    return;
                }

                await ExecuteWithRetryAsync(ctx, cancellationToken).ConfigureAwait(false);
            };

            var pipeline = finalInvoke;

            // Get scoped filters for this function
            var scopedFilters = _scopedFilterManager?.GetApplicableFilters(functionCall.Name)
                                ?? Enumerable.Empty<IAiFunctionFilter>();

            // Combine scoped filters with general AI function filters
            var allStandardFilters = _aiFunctionFilters.Concat(scopedFilters);

            // Wrap all standard filters first.
            foreach (var filter in allStandardFilters.Reverse())
            {
                var previous = pipeline;
                pipeline = ctx => filter.InvokeAsync(ctx, previous);
            }

            // Wrap permission filters last, so they run FIRST.
            // Skip if already checked by parallel execution (prevents double execution)
            if (!skipPermissionFilters)
            {
                foreach (var permissionFilter in _permissionFilters.Reverse())
                {
                    var previous = pipeline;
                    pipeline = ctx => permissionFilter.InvokeAsync(ctx, previous);
                }
            }

            // Execute the full pipeline.
            await pipeline(context).ConfigureAwait(false);

            // Mark function as completed in run context
            agentRunContext.CompleteFunction(functionCall.Name);

            var functionResult = new FunctionResultContent(functionCall.CallId, context.Result);
            var functionMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { functionResult });
            resultMessages.Add(functionMessage);
        }

        return resultMessages;
    }

    /// <summary>
    /// Executes a function with provider-aware retry logic and timeout enforcement.
    /// Uses provider-specific error handlers to parse errors and determine retry delays.
    /// Implements intelligent backoff: API-specified > Provider-specific > Exponential with jitter.
    /// </summary>
    private async Task ExecuteWithRetryAsync(AiFunctionContext context, CancellationToken cancellationToken)
    {
        var maxRetries = _errorHandlingConfig?.MaxRetries ?? 3;
        var retryDelay = _errorHandlingConfig?.RetryDelay ?? TimeSpan.FromSeconds(1);
        var functionTimeout = _errorHandlingConfig?.SingleFunctionTimeout;
        var providerHandler = _errorHandlingConfig?.ProviderHandler;
        var customRetryStrategy = _errorHandlingConfig?.CustomRetryStrategy;

        Exception? lastException = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Create linked cancellation token for function timeout
                using var functionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (functionTimeout.HasValue)
                {
                    functionCts.CancelAfter(functionTimeout.Value);
                }

                var args = new AIFunctionArguments(context.ToolCallRequest.Arguments);
                context.Result = await context.Function!.InvokeAsync(args, functionCts.Token).ConfigureAwait(false);
                return; // Success - exit retry loop
            }
            catch (OperationCanceledException) when (functionTimeout.HasValue && !cancellationToken.IsCancellationRequested)
            {
                // Function-specific timeout (not the overall cancellation)
                context.Result = $"Function '{context.ToolCallRequest.FunctionName}' timed out after {functionTimeout.Value.TotalSeconds} seconds.";
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;

                // Don't retry if we've exhausted attempts
                if (attempt >= maxRetries)
                {
                    context.Result = $"Error invoking function '{context.ToolCallRequest.FunctionName}' after {maxRetries + 1} attempts: {ex.Message}";
                    return;
                }

                // PRIORITY 1: Use custom retry strategy if provided
                TimeSpan? delay = null;
                if (customRetryStrategy != null)
                {
                    delay = await customRetryStrategy(ex, attempt, cancellationToken).ConfigureAwait(false);
                    if (!delay.HasValue)
                    {
                        // Custom strategy says don't retry
                        context.Result = $"Error invoking function '{context.ToolCallRequest.FunctionName}': {ex.Message} (retry declined by custom strategy)";
                        return;
                    }
                }

                // PRIORITY 2: Use provider-aware error handling if available
                if (delay == null && providerHandler != null)
                {
                    var errorDetails = providerHandler.ParseError(ex);
                    if (errorDetails != null)
                    {
                        // Check if per-category retry limits apply
                        var categoryMaxRetries = _errorHandlingConfig?.MaxRetriesByCategory?.GetValueOrDefault(errorDetails.Category) ?? maxRetries;
                        if (attempt >= categoryMaxRetries)
                        {
                            context.Result = $"Error invoking function '{context.ToolCallRequest.FunctionName}' after {categoryMaxRetries + 1} attempts ({errorDetails.Category}): {ex.Message}";
                            return;
                        }

                        // Get provider-calculated delay
                        var maxDelayFromConfig = _errorHandlingConfig?.MaxRetryDelay ?? TimeSpan.FromSeconds(30);
                        var backoffMultiplier = _errorHandlingConfig?.BackoffMultiplier ?? 2.0;
                        delay = providerHandler.GetRetryDelay(errorDetails, attempt, retryDelay, backoffMultiplier, maxDelayFromConfig);

                        if (!delay.HasValue)
                        {
                            // Provider says this error is not retryable (e.g., 400 client error, quota exceeded)
                            context.Result = $"Error invoking function '{context.ToolCallRequest.FunctionName}': {ex.Message} ({errorDetails.Category}, non-retryable)";
                            return;
                        }
                    }
                }

                // PRIORITY 3: Fallback to exponential backoff with jitter
                if (delay == null)
                {
                    var baseMs = retryDelay.TotalMilliseconds;
                    var maxDelayMs = baseMs * Math.Pow(2, attempt); // 1x, 2x, 4x, 8x...
                    var jitteredDelayMs = Random.Shared.NextDouble() * maxDelayMs;
                    delay = TimeSpan.FromMilliseconds(jitteredDelayMs);
                }

                // Apply configured max delay cap
                var maxDelayCap = _errorHandlingConfig?.MaxRetryDelay ?? TimeSpan.FromSeconds(30);
                if (delay.Value > maxDelayCap)
                {
                    delay = maxDelayCap;
                }

                await Task.Delay(delay.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Helper method to find a function by name in the tools collection
    /// </summary>
    private AIFunction? FindFunction(string functionName, IList<AITool>? tools)
    {
        if (tools == null) return null;
        return tools.OfType<AIFunction>().FirstOrDefault(f => f.Name == functionName);
    }

    /// <summary>
    /// Prepares the various chat message lists after a response from the inner client and before invoking functions
    /// </summary>
}

#endregion 

#region MessageProcessor

/// <summary>
/// Handles all pre-processing of chat messages and options before sending to the LLM.
/// </summary>
public class MessageProcessor
{
    private readonly IReadOnlyList<IPromptFilter> _promptFilters;
    private readonly string? _systemInstructions;
    private readonly ChatOptions? _defaultOptions;
    private readonly IChatReducer? _chatReducer;
    private readonly HistoryReductionConfig? _reductionConfig;

    public MessageProcessor(
        string? systemInstructions,
        ChatOptions? defaultOptions,
        IReadOnlyList<IPromptFilter> promptFilters,
        IChatReducer? chatReducer,
        HistoryReductionConfig? reductionConfig)
    {
        _systemInstructions = systemInstructions;
        _defaultOptions = defaultOptions;
        _promptFilters = promptFilters ?? new List<IPromptFilter>();
        _chatReducer = chatReducer;
        _reductionConfig = reductionConfig;
    }

    /// <summary>
    /// Gets the system instructions configured for this processor.
    /// </summary>
    public string? SystemInstructions => _systemInstructions;

    /// <summary>
    /// Gets the default chat options configured for this processor.
    /// </summary>
    public ChatOptions? DefaultOptions => _defaultOptions;

    /// <summary>
    /// Prepares the final list of messages and chat options for the LLM call.
    /// Returns reduction metadata directly for thread-safety (no shared mutable state).
    /// </summary>
    public async Task<(IEnumerable<ChatMessage> messages, ChatOptions? options, ReductionMetadata? reduction)> PrepareMessagesAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string agentName,
        CancellationToken cancellationToken)
    {
        var effectiveMessages = PrependSystemInstructions(messages);
        var effectiveOptions = MergeOptions(options);

        // Apply cache-aware history reduction if configured
        if (_chatReducer != null)
        {
            var messagesList = effectiveMessages.ToList();

            // Check for existing summary marker (cache optimization)
            var lastSummaryIndex = messagesList.FindLastIndex(m =>
                m.AdditionalProperties?.ContainsKey(HistoryReductionConfig.SummaryMetadataKey) == true);

            bool shouldReduce = ShouldTriggerReduction(messagesList, lastSummaryIndex);

            ReductionMetadata? reductionMetadata = null;

            if (shouldReduce)
            {
                var reduced = await _chatReducer.ReduceAsync(effectiveMessages, cancellationToken).ConfigureAwait(false);

                if (reduced != null)
                {
                    var reducedList = reduced.ToList();

                    // Extract summary message
                    var summaryMsg = reducedList.FirstOrDefault(m =>
                        m.AdditionalProperties?.ContainsKey(HistoryReductionConfig.SummaryMetadataKey) == true);

                    if (summaryMsg != null)
                    {
                        // Calculate how many messages were removed
                        int removedCount = messagesList.Count - reducedList.Count + 1; // +1 for summary itself

                        // Return metadata directly for thread-safety
                        reductionMetadata = new ReductionMetadata
                        {
                            SummaryMessage = summaryMsg,
                            MessagesRemovedCount = removedCount
                        };
                    }

                    effectiveMessages = reducedList;
                }
            }

            effectiveMessages = await ApplyPromptFiltersAsync(effectiveMessages, effectiveOptions, agentName, cancellationToken).ConfigureAwait(false);

            return (effectiveMessages, effectiveOptions, reductionMetadata);
        }

        effectiveMessages = await ApplyPromptFiltersAsync(effectiveMessages, effectiveOptions, agentName, cancellationToken).ConfigureAwait(false);

        return (effectiveMessages, effectiveOptions, null);
    }

    /// <summary>
    /// Determines if history reduction should be triggered based on configured thresholds.
    /// Implements priority system: Percentage > Absolute Tokens > Message Count.
    /// </summary>
    private bool ShouldTriggerReduction(List<ChatMessage> messagesList, int lastSummaryIndex)
    {
        if (_reductionConfig == null) return false;

        // PRIORITY 1: Percentage-based (when configured)
        if (_reductionConfig.TokenBudgetTriggerPercentage.HasValue && _reductionConfig.ContextWindowSize.HasValue)
        {
            return ShouldReduceByPercentage(messagesList, lastSummaryIndex);
        }

        // PRIORITY 2: Absolute token budget (when configured)
        if (_reductionConfig.MaxTokenBudget.HasValue)
        {
            return ShouldReduceByTokens(messagesList, lastSummaryIndex);
        }

        // PRIORITY 3: Message-based (default, existing logic)
        return ShouldReduceByMessages(messagesList, lastSummaryIndex);
    }

    /// <summary>
    /// Checks if reduction should be triggered based on percentage of context window.
    /// Gemini CLI-inspired approach with user-specified context window size.
    /// </summary>
    private bool ShouldReduceByPercentage(List<ChatMessage> messagesList, int lastSummaryIndex)
    {
        var contextWindow = _reductionConfig!.ContextWindowSize!.Value;
        var triggerPercentage = _reductionConfig.TokenBudgetTriggerPercentage!.Value;
        var triggerThreshold = (int)(contextWindow * triggerPercentage);

        if (lastSummaryIndex >= 0)
        {
            // Count tokens AFTER last summary (incremental token tracking)
            var messagesAfterSummary = messagesList.Skip(lastSummaryIndex + 1);
            var tokensAfterSummary = messagesAfterSummary.CalculateTotalTokens();
            return tokensAfterSummary > triggerThreshold;
        }
        else
        {
            // Count all message tokens (first reduction)
            var totalTokens = messagesList.CalculateTotalTokens();
            return totalTokens > triggerThreshold;
        }
    }

    /// <summary>
    /// Checks if reduction should be triggered based on absolute token budget.
    /// Uses existing MaxTokenBudget configuration.
    /// </summary>
    private bool ShouldReduceByTokens(List<ChatMessage> messagesList, int lastSummaryIndex)
    {
        var maxBudget = _reductionConfig!.MaxTokenBudget!.Value;
        var threshold = _reductionConfig.TokenBudgetThreshold;

        if (lastSummaryIndex >= 0)
        {
            var messagesAfterSummary = messagesList.Skip(lastSummaryIndex + 1);
            var tokensAfterSummary = messagesAfterSummary.CalculateTotalTokens();
            return tokensAfterSummary > (maxBudget + threshold);
        }
        else
        {
            var totalTokens = messagesList.CalculateTotalTokens();
            return totalTokens > (maxBudget + threshold);
        }
    }

    /// <summary>
    /// Checks if reduction should be triggered based on message count.
    /// Preserves existing behavior for backward compatibility.
    /// </summary>
    private bool ShouldReduceByMessages(List<ChatMessage> messagesList, int lastSummaryIndex)
    {
        var targetCount = _reductionConfig!.TargetMessageCount;
        var threshold = _reductionConfig.SummarizationThreshold ?? 5;

        if (lastSummaryIndex >= 0)
        {
            // Summary found - only count messages AFTER the summary
            var messagesAfterSummary = messagesList.Count - lastSummaryIndex - 1;
            return messagesAfterSummary > (targetCount + threshold);
        }
        else
        {
            // No summary found - check total message count
            return messagesList.Count > (targetCount + threshold);
        }
    }

    /// <summary>
    /// Prepends system instructions to the message list if configured.
    /// </summary>
    private IEnumerable<ChatMessage> PrependSystemInstructions(IEnumerable<ChatMessage> messages)
    {
        if (string.IsNullOrEmpty(_systemInstructions))
            return messages;

        var messagesList = messages.ToList();

        // Check if there's already a system message
        if (messagesList.Any(m => m.Role == ChatRole.System))
            return messagesList;

        // Prepend system instruction
        var systemMessage = new ChatMessage(ChatRole.System, _systemInstructions);
        return new[] { systemMessage }.Concat(messagesList);
    }

    /// <summary>
    /// Merges provided options with default options.
    /// </summary>
    private ChatOptions? MergeOptions(ChatOptions? providedOptions)
    {
        if (_defaultOptions == null)
            return providedOptions;

        if (providedOptions == null)
            return _defaultOptions;

        // Merge options - provided options take precedence
        return new ChatOptions
        {
            // Fix: Proper tools merging - keep defaults when provided list is null or empty
            Tools = (providedOptions.Tools is { Count: > 0 })
                ? providedOptions.Tools
                : _defaultOptions.Tools,
            ToolMode = providedOptions.ToolMode ?? _defaultOptions.ToolMode,
            AllowMultipleToolCalls = providedOptions.AllowMultipleToolCalls ?? _defaultOptions.AllowMultipleToolCalls,
            MaxOutputTokens = providedOptions.MaxOutputTokens ?? _defaultOptions.MaxOutputTokens,
            Temperature = providedOptions.Temperature ?? _defaultOptions.Temperature,
            TopP = providedOptions.TopP ?? _defaultOptions.TopP,
            FrequencyPenalty = providedOptions.FrequencyPenalty ?? _defaultOptions.FrequencyPenalty,
            PresencePenalty = providedOptions.PresencePenalty ?? _defaultOptions.PresencePenalty,
            ResponseFormat = providedOptions.ResponseFormat ?? _defaultOptions.ResponseFormat,
            Seed = providedOptions.Seed ?? _defaultOptions.Seed,
            StopSequences = providedOptions.StopSequences ?? _defaultOptions.StopSequences,
            ModelId = providedOptions.ModelId ?? _defaultOptions.ModelId,
            AdditionalProperties = MergeDictionaries(_defaultOptions.AdditionalProperties, providedOptions.AdditionalProperties)
        };
    }

    /// <summary>
    /// Applies the registered prompt filters pipeline.
    /// </summary>
    private async Task<IEnumerable<ChatMessage>> ApplyPromptFiltersAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string agentName,
        CancellationToken cancellationToken)
    {
        if (!_promptFilters.Any())
        {
            return messages;
        }

        // Create filter context
        var context = new PromptFilterContext(messages, options, agentName, cancellationToken);

        // Transfer additional properties to filter context
        if (options?.AdditionalProperties != null)
        {
            foreach (var kvp in options.AdditionalProperties)
            {
                context.Properties[kvp.Key] = kvp.Value!;
            }
        }

        // Core next delegate returns current messages
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> pipeline = ctx => Task.FromResult(ctx.Messages);

        // Wrap filters in reverse order
        foreach (var filter in _promptFilters.AsEnumerable().Reverse())
        {
            var next = pipeline;
            pipeline = ctx => filter.InvokeAsync(ctx, next);
        }
        return await pipeline(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies post-invocation filters to process results, extract memories, etc.
    /// </summary>
    /// <param name="requestMessages">The messages sent to the LLM (after pre-processing)</param>
    /// <param name="responseMessages">The messages returned by the LLM, or null if failed</param>
    /// <param name="exception">Exception that occurred, or null if successful</param>
    /// <param name="options">The chat options used for the invocation</param>
    /// <param name="agentName">The agent name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task ApplyPostInvokeFiltersAsync(
        IEnumerable<ChatMessage> requestMessages,
        IEnumerable<ChatMessage>? responseMessages,
        Exception? exception,
        ChatOptions? options,
        string agentName,
        CancellationToken cancellationToken)
    {
        if (!_promptFilters.Any())
        {
            return;
        }

        // Create properties dictionary from options
        var properties = new Dictionary<string, object>();
        if (options?.AdditionalProperties != null)
        {
            foreach (var kvp in options.AdditionalProperties)
            {
                properties[kvp.Key] = kvp.Value!;
            }
        }

        // Create post-invoke context
        var context = new PostInvokeContext(
            requestMessages,
            responseMessages,
            exception,
            properties,
            agentName,
            options);

        // Call PostInvokeAsync on all filters (in order, not reversed)
        foreach (var filter in _promptFilters)
        {
            try
            {
                await filter.PostInvokeAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Log but don't fail - post-processing is best-effort
                // Individual filter failures shouldn't break the response
            }
        }
    }

    /// <summary>
    /// Merges two dictionaries, with the second taking precedence.
    /// </summary>
    private static AdditionalPropertiesDictionary? MergeDictionaries(
        AdditionalPropertiesDictionary? first,
        AdditionalPropertiesDictionary? second)
    {
        if (first == null) return second;
        if (second == null) return first;

        var merged = new AdditionalPropertiesDictionary(first);
        foreach (var kvp in second)
        {
            merged[kvp.Key] = kvp.Value;
        }
        return merged;
    }
}

#endregion

#region AgentTurn
/// <summary>
/// Manages a single streaming call to the LLM and translates the raw output into TurnEvents
/// </summary>
public class AgentTurn
{
    private readonly IChatClient _baseClient;

    /// <summary>
    /// Initializes a new instance of AgentTurn
    /// </summary>
    /// <param name="baseClient">The underlying chat client to use for LLM calls</param>
    public AgentTurn(IChatClient baseClient)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
    }

    /// <summary>
    /// Runs a single turn with the LLM and yields ChatResponseUpdates representing the response
    /// </summary>
    /// <param name="messages">The conversation history to send to the LLM</param>
    /// <param name="options">Optional chat options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of ChatResponseUpdates representing the LLM's response</returns>
    public async IAsyncEnumerable<ChatResponseUpdate> RunAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in RunAsyncCore(messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> RunAsyncCore(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Get the streaming response from the base client
        var stream = _baseClient.GetStreamingResponseAsync(messages, options, cancellationToken);

        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        Exception? errorToYield = null;

        try
        {
            enumerator = stream.GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errorToYield = ex;
                    break;
                }

                if (!hasNext)
                    break;

                var update = enumerator.Current;

                // Yield the update directly
                yield return update;
            }
        }
        finally
        {
            if (enumerator != null)
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore disposal errors
                }
            }
        }

        // If there was an error, throw it after cleanup
        if (errorToYield != null)
        {
            throw errorToYield;
        }
    }
}

#endregion

#region Streaming Turn Result

/// <summary>
/// Metadata about history reduction that occurred during a turn.
/// Contains information needed by Conversation to apply the reduction to storage.
/// </summary>
public record ReductionMetadata
{
    /// <summary>
    /// The summary message that should be inserted into conversation history.
    /// Contains the __summary__ marker in AdditionalProperties.
    /// </summary>
    public ChatMessage? SummaryMessage { get; init; }

    /// <summary>
    /// Number of messages that were removed during reduction.
    /// Conversation uses this to know how many messages to remove from storage.
    /// </summary>
    public int MessagesRemovedCount { get; init; }
}

/// <summary>
/// Updated to stream BaseEvent instead of ChatResponseUpdate.
/// This is a breaking change for v0.
/// </summary>
public class StreamingTurnResult
{
    /// <summary>
    /// BREAKING: Change from ChatResponseUpdate to BaseEvent
    /// </summary>
    public IAsyncEnumerable<BaseEvent> EventStream { get; }

    /// <summary>
    /// Task that completes with the final turn history once streaming is done
    /// </summary>
    public Task<IReadOnlyList<ChatMessage>> FinalHistory { get; }

    /// <summary>
    /// Task that completes with reduction metadata if history reduction occurred during this turn.
    /// Null if no reduction was performed.
    /// </summary>
    public Task<ReductionMetadata?> ReductionTask { get; }

    /// <summary>
    /// Initializes a new instance of StreamingTurnResult
    /// </summary>
    /// <param name="eventStream">The stream of BaseEvents</param>
    /// <param name="finalHistory">Task that provides the final turn history</param>
    /// <param name="reductionTask">Task that provides the reduction metadata</param>
    public StreamingTurnResult(
        IAsyncEnumerable<BaseEvent> eventStream,
        Task<IReadOnlyList<ChatMessage>> finalHistory,
        Task<ReductionMetadata?> reductionTask)
    {
        EventStream = eventStream ?? throw new ArgumentNullException(nameof(eventStream));
        FinalHistory = finalHistory ?? throw new ArgumentNullException(nameof(finalHistory));
        ReductionTask = reductionTask ?? Task.FromResult<ReductionMetadata?>(null);
    }
}


#endregion

#region Tool Scheduler

/// <summary>
/// Responsible for executing tools and running the associated IAiFunctionFilter pipeline
/// </summary>
public class ToolScheduler
{
    private readonly FunctionCallProcessor _functionCallProcessor;
    private readonly AgentConfig? _config;
    private readonly PluginScopingManager _pluginScopingManager;

    /// <summary>
    /// Initializes a new instance of ToolScheduler
    /// </summary>
    /// <param name="functionCallProcessor">The function call processor to use for tool execution</param>
    /// <param name="config">Agent configuration for execution settings</param>
    /// <param name="pluginScopingManager">Plugin scoping manager for container detection</param>
    public ToolScheduler(FunctionCallProcessor functionCallProcessor, AgentConfig? config, PluginScopingManager pluginScopingManager)
    {
        _functionCallProcessor = functionCallProcessor ?? throw new ArgumentNullException(nameof(functionCallProcessor));
        _config = config;
        _pluginScopingManager = pluginScopingManager ?? throw new ArgumentNullException(nameof(pluginScopingManager));
    }

    /// <summary>
    /// Executes the requested tools in parallel and returns the tool response message
    /// </summary>
    /// <param name="currentHistory">The current conversation history</param>
    /// <param name="toolRequests">The tool call requests to execute</param>
    /// <param name="options">Optional chat options containing tool definitions</param>
    /// <param name="agentRunContext">Agent run context for cross-call tracking</param>
    /// <param name="agentName">The name of the agent executing the tools</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A chat message containing the tool execution results</returns>
    public async Task<ChatMessage> ExecuteToolsAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentRunContext agentRunContext,
        string? agentName,
        HashSet<string> expandedPlugins,
        CancellationToken cancellationToken)
    {
        // For single tool calls, use sequential execution (no parallelization overhead)
        if (toolRequests.Count <= 1)
        {
            return await ExecuteSequentiallyAsync(currentHistory, toolRequests, options, agentRunContext, agentName, expandedPlugins, cancellationToken).ConfigureAwait(false);
        }

        // For multiple tool calls, execute in parallel for better performance
        return await ExecuteInParallelAsync(currentHistory, toolRequests, options, agentRunContext, agentName, expandedPlugins, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes tools sequentially (used for single tools or as fallback)
    /// </summary>
    private async Task<ChatMessage> ExecuteSequentiallyAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentRunContext agentRunContext,
        string? agentName,
        HashSet<string> expandedPlugins,
        CancellationToken cancellationToken)
    {
        var allContents = new List<AIContent>();

        // Track which tool requests are containers for expansion after invocation
        var containerExpansions = new Dictionary<string, string>(); // callId -> pluginName
        foreach (var toolRequest in toolRequests)
        {
            // Find the function in the options to check if it's a container
            var function = options?.Tools?.OfType<AIFunction>()
                .FirstOrDefault(f => f.Name == toolRequest.Name);

            if (function != null && _pluginScopingManager.IsContainer(function))
            {
                // Track this container for expansion after invocation
                var pluginName = function.AdditionalProperties
                    ?.TryGetValue("PluginName", out var value) == true && value is string pn
                    ? pn
                    : toolRequest.Name;

                containerExpansions[toolRequest.CallId] = pluginName;
            }
        }

        // Process ALL tools (containers + regular) through the existing processor
        var resultMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(
            currentHistory, options, toolRequests, agentRunContext, agentName, cancellationToken);

        // Combine results and mark containers as expanded
        foreach (var message in resultMessages)
        {
            foreach (var content in message.Contents)
            {
                allContents.Add(content);

                // If this result is for a container, mark the plugin as expanded
                if (content is FunctionResultContent functionResult &&
                    containerExpansions.TryGetValue(functionResult.CallId, out var pluginName))
                {
                    expandedPlugins.Add(pluginName);
                }
            }
        }

        return new ChatMessage(ChatRole.Tool, allContents);
    }

    /// <summary>
    /// Executes tools in parallel for improved performance with multiple independent tools
    /// </summary>
    private async Task<ChatMessage> ExecuteInParallelAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentRunContext agentRunContext,
        string? agentName,
        HashSet<string> expandedPlugins,
        CancellationToken cancellationToken)
    {
        // THREE-PHASE EXECUTION (inspired by Gemini CLI's CoreToolScheduler with plugin scoping)
        // Phase 0: Separate container expansions from regular tools
        // Phase 1: Check permissions for ALL tools SEQUENTIALLY (prevents race conditions)
        // Phase 2: Execute ALL approved tools in PARALLEL (with optional throttling)

        var allContents = new List<AIContent>();

        // PHASE 0: Identify containers and track them for expansion after invocation
        var containerExpansions = new Dictionary<string, string>(); // callId -> pluginName
        foreach (var toolRequest in toolRequests)
        {
            // Find the function in the options to check if it's a container
            var function = options?.Tools?.OfType<AIFunction>()
                .FirstOrDefault(f => f.Name == toolRequest.Name);

            if (function != null && _pluginScopingManager.IsContainer(function))
            {
                // Track this container for expansion after invocation
                var pluginName = function.AdditionalProperties
                    ?.TryGetValue("PluginName", out var value) == true && value is string pn
                    ? pn
                    : toolRequest.Name;

                containerExpansions[toolRequest.CallId] = pluginName;
            }
        }

        // All tool requests (containers + regular) go through normal invocation pipeline
        var approvedTools = new List<FunctionCallContent>();
        var deniedTools = new List<FunctionCallContent>();

        // PHASE 1: Permission checking (sequential to prevent race conditions)
        foreach (var toolRequest in toolRequests)
        {
            var approved = await _functionCallProcessor.CheckPermissionAsync(
                toolRequest, options, agentRunContext, agentName, cancellationToken);

            if (approved)
            {
                approvedTools.Add(toolRequest);
            }
            else
            {
                deniedTools.Add(toolRequest);
            }
        }

        // PHASE 2: Execute approved tools in parallel with optional throttling
        var maxParallel = _config?.AgenticLoop?.MaxParallelFunctions ?? int.MaxValue;
        using var semaphore = new SemaphoreSlim(maxParallel);

        var executionTasks = approvedTools.Select(async toolRequest =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Execute each approved tool call through the processor
                // Skip permission filters since we already checked them in Phase 1
                var singleToolList = new List<FunctionCallContent> { toolRequest };
                var resultMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(
                    currentHistory, options, singleToolList, agentRunContext, agentName, cancellationToken, 
                    skipPermissionFilters: true).ConfigureAwait(false);

                return (Success: true, Messages: resultMessages, Error: (Exception?)null, ToolRequest: toolRequest);
            }
            catch (Exception ex)
            {
                // Capture any errors for aggregation
                return (Success: false, Messages: new List<ChatMessage>(), Error: ex, ToolRequest: toolRequest);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        // Wait for all approved tasks to complete
        var results = await Task.WhenAll(executionTasks).ConfigureAwait(false);

        // Aggregate results and handle errors (allContents already initialized with container results)
        var errors = new List<Exception>();

        // Add results from approved tools
        foreach (var result in results)
        {
            if (result.Success)
            {
                foreach (var message in result.Messages)
                {
                    allContents.AddRange(message.Contents);
                }

                // If this was a container, mark the plugin as expanded
                if (containerExpansions.TryGetValue(result.ToolRequest.CallId, out var pluginName))
                {
                    expandedPlugins.Add(pluginName);
                }
            }
            else if (result.Error != null)
            {
                errors.Add(result.Error);
            }
        }

        // Add denied tool results with proper error messages
        foreach (var deniedTool in deniedTools)
        {
            var functionResult = new FunctionResultContent(
                deniedTool.CallId,
                $"Execution of '{deniedTool.Name}' was denied by permissions/policy."
            );
            allContents.Add(functionResult);
        }

        // If there were any execution errors, include them in the response
        if (errors.Count > 0)
        {
            var errorMessage = $"Some tool executions failed: {string.Join("; ", errors.Select(e => e.Message))}";
            allContents.Add(new TextContent($"⚠️ Tool Execution Errors: {errorMessage}"));
        }

        return new ChatMessage(ChatRole.Tool, allContents);
    }
}

#endregion

#region Document Processing Helper

/// <summary>
/// Helper methods for document processing in Agent
/// </summary>
internal static class AgentDocumentProcessor
{
    /// <summary>
    /// Processes documents and modifies messages to include document content
    /// </summary>
    public static async Task<IEnumerable<ChatMessage>> ProcessDocumentsAsync(
        IEnumerable<ChatMessage> messages,
        string[] documentPaths,
        AgentConfig? config,
        CancellationToken cancellationToken)
    {
        if (documentPaths == null || documentPaths.Length == 0)
        {
            return messages;
        }

        // Get document handling configuration
        var docConfig = config?.DocumentHandling;
        var strategy = docConfig?.Strategy ?? ConversationDocumentHandling.FullTextInjection;

        // Only FullTextInjection is supported for now
        if (strategy != ConversationDocumentHandling.FullTextInjection)
        {
            throw new NotImplementedException($"Document handling strategy '{strategy}' is not yet implemented. Only FullTextInjection is currently supported.");
        }

        // Process document uploads
        var textExtractor = new HPD_Agent.TextExtraction.TextExtractionUtility();
        var uploads = await ConversationDocumentHelper.ProcessUploadsAsync(documentPaths, textExtractor, cancellationToken).ConfigureAwait(false);

        // Modify the last user message with document content
        return ModifyLastUserMessageWithDocuments(messages, uploads, docConfig?.DocumentTagFormat);
    }

    /// <summary>
    /// Modifies the last user message to include document content
    /// </summary>
    private static IEnumerable<ChatMessage> ModifyLastUserMessageWithDocuments(
        IEnumerable<ChatMessage> messages,
        ConversationDocumentUpload[] uploads,
        string? customTagFormat = null)
    {
        var messagesList = messages.ToList();
        if (messagesList.Count == 0 || uploads.Length == 0)
        {
            return messagesList;
        }

        // Find the last user message
        var lastUserMessageIndex = -1;
        for (int i = messagesList.Count - 1; i >= 0; i--)
        {
            if (messagesList[i].Role == ChatRole.User)
            {
                lastUserMessageIndex = i;
                break;
            }
        }

        if (lastUserMessageIndex == -1)
        {
            return messagesList;
        }

        var lastUserMessage = messagesList[lastUserMessageIndex];
        var originalText = ExtractTextFromMessage(lastUserMessage);

        // Format message with documents
        var formattedMessage = ConversationDocumentHelper.FormatMessageWithDocuments(
            originalText, uploads, customTagFormat);

        // Append document content to existing contents instead of replacing
        // This preserves images, audio, and other non-text content
        var newContents = lastUserMessage.Contents.ToList();
        newContents.Add(new TextContent(formattedMessage));

        // Preserve AdditionalProperties if present
        var newMessage = new ChatMessage(ChatRole.User, newContents);
        if (lastUserMessage.AdditionalProperties != null)
        {
            newMessage.AdditionalProperties = new AdditionalPropertiesDictionary(lastUserMessage.AdditionalProperties);
        }
        
        messagesList[lastUserMessageIndex] = newMessage;

        return messagesList;
    }

    /// <summary>
    /// Extracts text content from a ChatMessage
    /// </summary>
    private static string ExtractTextFromMessage(ChatMessage message)
    {
        var textContents = message.Contents
            .OfType<TextContent>()
            .Select(tc => tc.Text)
            .Where(text => !string.IsNullOrEmpty(text));

        return string.Join(" ", textContents);
    }
}

#endregion

