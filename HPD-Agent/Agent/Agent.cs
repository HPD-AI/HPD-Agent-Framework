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
    private readonly CapabilityManager _capabilityManager;
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

        // Fix: Store and use AI function filters
        _aiFunctionFilters = aiFunctionFilters ?? new List<IAiFunctionFilter>();
        _messageTurnFilters = messageTurnFilters ?? new List<IMessageTurnFilter>();

        // Create history reducer if configured
        var chatReducer = CreateChatReducer(config, baseClient);

        // Augment system instructions with plan mode guidance if enabled
        var systemInstructions = AugmentSystemInstructionsForPlanMode(config);

        _messageProcessor = new MessageProcessor(systemInstructions, mergedOptions ?? config.Provider?.DefaultChatOptions, promptFilters, chatReducer);
        _functionCallProcessor = new FunctionCallProcessor(scopedFilterManager, permissionFilters, _aiFunctionFilters, config.MaxAgenticIterations, config.ErrorHandling);
        _agentTurn = new AgentTurn(_baseClient);
        _toolScheduler = new ToolScheduler(_functionCallProcessor, config);
        _aguiEventHandler = new AGUIEventHandler(this, _baseClient, _messageProcessor, _name, config);
        _capabilityManager = new CapabilityManager();
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


    #region Capability Management

    /// <summary>
    /// Gets a capability by name and type
    /// </summary>
    /// <typeparam name="T">The type of capability to retrieve</typeparam>
    /// <param name="name">The name of the capability</param>
    /// <returns>The capability instance if found, otherwise null</returns>
    public T? GetCapability<T>(string name) where T : class
        => _capabilityManager.GetCapability<T>(name);

    /// <summary>
    /// Adds or updates a capability
    /// </summary>
    /// <param name="name">The name of the capability</param>
    /// <param name="capability">The capability instance</param>
    public void AddCapability(string name, object capability)
        => _capabilityManager.AddCapability(name, capability);

    /// <summary>
    /// Removes a capability by name
    /// </summary>
    /// <param name="name">The name of the capability to remove</param>
    /// <returns>True if the capability was removed, false if it wasn't found</returns>
    public bool RemoveCapability(string name)
        => _capabilityManager.RemoveCapability(name);


    #endregion

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

            var turnResult = await ExecuteStreamingTurnAsync(messages, options, null, cancellationToken);

            // Consume the entire stream
            await foreach (var evt in turnResult.EventStream.WithCancellation(cancellationToken))
            {
                allEvents.Add(evt);
            }

            // Wait for final history and construct response
            var finalHistory = await turnResult.FinalHistory;
            // Extract assistant messages from final history
            var assistantMessages = finalHistory.Where(m => m.Role == ChatRole.Assistant).ToList();
            var response = new ChatResponse(assistantMessages);

            // Record telemetry metrics
            var duration = DateTimeOffset.UtcNow - startTime;
            var tokensUsed = (int)(response.Usage?.TotalTokenCount ?? 0);

            activity?.SetTag("completion.tokens_used", tokensUsed);
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
            messages = await AgentDocumentProcessor.ProcessDocumentsAsync(messages, documentPaths, Config, cancellationToken);
        }

        var messagesList = messages.ToList();
        var userMessage = messagesList.LastOrDefault(m => m.Role == ChatRole.User)
            ?? new ChatMessage(ChatRole.User, string.Empty);

        // Create a TaskCompletionSource for the final history
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>();
        var turnHistory = new List<ChatMessage>();

        // Create the streaming enumerable - use BaseEvent directly without conversion
        var responseStream = RunAgenticLoopCore(messagesList, options, turnHistory, historyCompletionSource, cancellationToken);

        // Capture reduction metadata from MessageProcessor (set during PrepareMessagesAsync in RunAgenticLoopCore)
        ReductionMetadata? reductionMetadata = null;
        if (_messageProcessor.LastReductionMetadata.HasValue)
        {
            var metadata = _messageProcessor.LastReductionMetadata.Value;
            reductionMetadata = new ReductionMetadata
            {
                SummaryMessage = metadata.SummaryMessage,
                MessagesRemovedCount = metadata.RemovedCount
            };
        }

        // Wrap the final history task to apply message turn filters when complete
        var wrappedHistoryTask = historyCompletionSource.Task.ContinueWith(async task =>
        {
            var history = await task;

            // Apply message turn filters after the turn completes
            if (task.IsCompletedSuccessfully && _messageTurnFilters.Any())
            {
                try
                {
                    await ApplyMessageTurnFilters(userMessage, history, options, cancellationToken);
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
        return new StreamingTurnResult(responseStream, wrappedHistoryTask)
        {
            Reduction = reductionMetadata
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Use the raw streaming loop directly for ChatResponseUpdate compatibility
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>();
        await foreach (var update in RunAgenticLoopAsync(messages, options, historyCompletionSource, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return update;
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

        // Convert BaseEvent stream to ChatResponseUpdate stream for IChatClient compatibility
        await foreach (var baseEvent in RunAgenticLoopCore(messages, options, turnHistory, historyCompletionSource, cancellationToken))
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
        var (effectiveMessages, effectiveOptions) = await _messageProcessor.PrepareMessagesAsync(
            messages, options, _name, effectiveCancellationToken);

        // Note: Reduction metadata is now captured in ExecuteStreamingTurnAsync
        // and returned via StreamingTurnResult.Reduction property

        var currentMessages = effectiveMessages.ToList();

        // Emit message start event at the beginning
        bool messageStarted = false;

        // Create agent run context for tracking across all function calls
        var agentRunContext = new AgentRunContext(runId, conversationId, _maxFunctionCalls);

        // Track consecutive same-function calls for circuit breaker (Gemini CLI-inspired)
        // Tracks function name + arguments hash to detect exact repetition
        string? lastFunctionSignature = null;
        int consecutiveSameSignatureCount = 0;

        // Main agentic loop - use while loop to allow dynamic limit extension
        int iteration = 0;
        while (iteration <= agentRunContext.MaxIterations)
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

            // Track reasoning step state PER ITERATION (reset for each turn/function call)
            bool reasoningStepStarted = false;
            string? reasoningStepId = null;

            // Run turn and collect events
            await foreach (var update in _agentTurn.RunAsync(currentMessages, effectiveOptions, effectiveCancellationToken))
            {
                // Store update for building final history
                responseUpdates.Add(update);

                // Process contents and emit appropriate BaseEvent objects
                if (update.Contents != null)
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                        {
                            // Emit step started event ONLY on first reasoning chunk
                            if (!reasoningStepStarted)
                            {
                                reasoningStepId = Guid.NewGuid().ToString();
                                yield return EventSerialization.CreateStepStarted(reasoningStepId, "Reasoning");
                                reasoningStepStarted = true;
                            }

                            // Emit reasoning content for each chunk
                            yield return EventSerialization.CreateReasoningContent(messageId, reasoning.Text);

                            // Add reasoning to assistantContents so it's preserved in conversation history
                            assistantContents.Add(reasoning);
                        }
                        else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                        {
                            // If we were in reasoning mode, finish the reasoning step
                            if (reasoningStepStarted && reasoningStepId != null)
                            {
                                yield return EventSerialization.CreateStepFinished(reasoningStepId, "Reasoning");
                                reasoningStepStarted = false;
                                reasoningStepId = null;
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

                    // If reasoning step is still active when stream ends, finish it
                    if (reasoningStepStarted && reasoningStepId != null)
                    {
                        yield return EventSerialization.CreateStepFinished(reasoningStepId, "Reasoning");
                        reasoningStepStarted = false;
                        reasoningStepId = null;
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

                // Execute tools
                var toolResultMessage = await _toolScheduler.ExecuteToolsAsync(
                    currentMessages, toolRequests, effectiveOptions, agentRunContext, _name, effectiveCancellationToken);

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

                        // Emit custom tool result event for dev visibility and debugging
                        var matchingTool = toolRequests.FirstOrDefault(t => t.CallId == result.CallId);
                        var toolName = matchingTool?.Name ?? "unknown";
                        yield return EventSerialization.CreateToolResult(messageId, result.CallId, toolName, result.Result ?? "null");

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
                // Handles parallel tool calls by checking each one
                if (toolRequests.Count > 0 && Config?.AgenticLoop?.MaxConsecutiveFunctionCalls is { } maxConsecutive)
                {
                    // Check each tool request in the batch (handles parallel calls)
                    foreach (var toolRequest in toolRequests)
                    {
                        var currentSignature = GetFunctionSignature(toolRequest);

                        if (currentSignature == lastFunctionSignature)
                        {
                            consecutiveSameSignatureCount++;
                        }
                        else
                        {
                            lastFunctionSignature = currentSignature;
                            consecutiveSameSignatureCount = 1;
                        }

                        if (consecutiveSameSignatureCount >= maxConsecutive)
                        {
                            var errorMessage = $"⚠️ Circuit breaker triggered: Function '{toolRequest.Name}' with same arguments called {consecutiveSameSignatureCount} times consecutively. Stopping to prevent infinite loop.";
                            yield return EventSerialization.CreateTextMessageContent(messageId, errorMessage);

                            agentRunContext.IsTerminated = true;
                            agentRunContext.TerminationReason = $"Circuit breaker: '{toolRequest.Name}' with same arguments called {consecutiveSameSignatureCount} times consecutively";
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
                // Increment for next iteration even if no tools/stream finish
                iteration++;
            }
        }

        // Build the complete history including the final assistant message
        if (responseUpdates.Any())
        {
            var finalResponse = ConstructChatResponseFromUpdates(responseUpdates);
            // Fix: Guard against empty messages collection
            if (finalResponse.Messages.Count > 0)
            {
                var finalAssistantMessage = finalResponse.Messages[0];

                // Only add if we don't already have this assistant message
                // (in case it was already added during tool execution)
                if (!turnHistory.Any() || turnHistory.Last().Role != ChatRole.Assistant ||
                    !turnHistory.Last().Contents.SequenceEqual(finalAssistantMessage.Contents))
                {
                    turnHistory.Add(finalAssistantMessage);
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
            Type t when t == typeof(CapabilityManager) => _capabilityManager,
            Type t when t == typeof(AgentConfig) => Config,
            Type t when t == typeof(ScopedFilterManager) => _scopedFilterManager,
            Type t when t == typeof(ErrorHandlingPolicy) => _errorPolicy,
            Type t when t.IsInstanceOfType(_baseClient) => _baseClient,
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
- get_current_plan(): View the current plan and its status
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

    #endregion

    #region Message Turn Filters

    /// <summary>
    /// Applies message turn filters after a complete turn (including all function calls) finishes
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

        await pipeline(context);
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
                metadata[_name] = functionCalls;
            }
        }

        return metadata;
    }

    #endregion

    #region AGUI Delegation Methods

    /// <summary>
    /// Runs the agent in AGUI streaming mode, emitting events to the provided channel
    /// </summary>
    public async Task RunAsync(
        RunAgentInput input,
        ChannelWriter<BaseEvent> events,
        CancellationToken cancellationToken = default)
    {
        await _aguiEventHandler.RunAsync(input, events, cancellationToken);
    }



    #endregion



    /// <summary>
    /// Serializes any AG-UI event to JSON using the correct polymorphic serialization.
    /// This delegates to the AGUIEventHandler for processing.
    /// </summary>
    /// <param name="aguiEvent">The AG-UI event to serialize</param>
    /// <returns>JSON string with proper polymorphic serialization</returns>
    public string SerializeEvent(BaseEvent aguiEvent)
    {
        return _aguiEventHandler.SerializeEvent(aguiEvent);
    }

    #region History Reduction Metadata

    // NOTE: Reduction metadata is now returned via StreamingTurnResult.Reduction property
    // instead of being stored in agent instance fields. This makes Agent stateless and
    // allows it to be safely reused across multiple conversations.
    //
    // Old API (removed):
    // - public IReadOnlyDictionary<string, object> GetReductionMetadata()
    // - public void ClearReductionMetadata()
    //
    // New pattern:
    // - StreamingTurnResult.Reduction contains ReductionMetadata if reduction occurred
    // - Conversation reads metadata from result object instead of calling agent method

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
        // Serialize arguments to JSON for consistent comparison (using source-generated context for AOT)
        var argsJson = System.Text.Json.JsonSerializer.Serialize(
            toolCall.Arguments ?? new Dictionary<string, object?>(),
            AGUIJsonContext.Default.DictionaryStringObject);

        // Combine function name and arguments
        var combined = $"{toolCall.Name}:{argsJson}";

        // Generate SHA256 hash for efficient comparison
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hashBytes);
    }

    #endregion

}


#region AGUI Event Handling

/// <summary>
/// Handles all AGUI (Agent-GUI) protocol logic including event conversion, streaming, and WebSocket communication.
/// Implements the IAGUIAgent interface and provides SSE and WebSocket streaming capabilities.
/// </summary>
public class AGUIEventHandler : IAGUIAgent
{
    private readonly Agent _agent;
    private readonly IChatClient _baseClient;
    private readonly MessageProcessor _messageProcessor;
    private readonly string _agentName;
    private readonly AgentConfig? _config;
    private readonly AGUIEventConverter _eventConverter;

    public AGUIEventHandler(
        Agent agent,
        IChatClient baseClient,
        MessageProcessor messageProcessor,
        string agentName,
        AgentConfig? config = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _messageProcessor = messageProcessor ?? throw new ArgumentNullException(nameof(messageProcessor));
        _agentName = agentName ?? throw new ArgumentNullException(nameof(agentName));
        _config = config;
        _eventConverter = new AGUIEventConverter();
    }

    /// <summary>
    /// Runs the agent in AGUI streaming mode, emitting events to the provided channel
    /// </summary>
    public async Task RunAsync(
        RunAgentInput input,
        ChannelWriter<BaseEvent> events,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Emit run started event
            await events.WriteAsync(AGUIEventConverter.LifecycleEvents.CreateRunStarted(input), cancellationToken);

            // Convert AGUI input to Extensions.AI format
            var messages = _eventConverter.ConvertToExtensionsAI(input);
            var chatOptions = _eventConverter.ConvertToExtensionsAIChatOptions(input, _config?.Provider?.DefaultChatOptions);

            // Fix: Remove duplicate message start/end events - let the inner loop handle all message boundaries
            // Use the agent's native BaseEvent stream directly
            var streamResult = await _agent.ExecuteStreamingTurnAsync(messages, chatOptions, null, cancellationToken);
            await foreach (var baseEvent in streamResult.EventStream.WithCancellation(cancellationToken))
            {
                // Write native events directly to the channel
                await events.WriteAsync(baseEvent, cancellationToken);
            }

            // Emit run finished event
            await events.WriteAsync(AGUIEventConverter.LifecycleEvents.CreateRunFinished(input), cancellationToken);
        }
        catch (Exception ex)
        {
            // Emit error event
            await events.WriteAsync(AGUIEventConverter.LifecycleEvents.CreateRunError(input, ex), cancellationToken);
            throw;
        }
        finally
        {
            // Complete the channel
            events.Complete();
        }
    }




    /// <summary>
    /// Serializes any AG-UI event to JSON using the correct polymorphic serialization.
    /// Implements the functionality previously in EventHelpers.SerializeEvent.
    /// </summary>
    /// <param name="aguiEvent">The AG-UI event to serialize</param>
    /// <returns>JSON string with proper polymorphic serialization</returns>
    public string SerializeEvent(BaseEvent aguiEvent)
    {
        return EventSerialization.SerializeEvent(aguiEvent);
    }
}

#endregion

#region Capability Manager

/// <summary>
/// Manages the agent's extended capabilities, such as Audio and MCP.
/// </summary>
public class CapabilityManager
{
    private readonly Dictionary<string, object> _capabilities = new();

    /// <summary>
    /// Gets a capability by name and type.
    /// </summary>
    /// <typeparam name="T">The type of capability to retrieve.</typeparam>
    /// <param name="name">The name of the capability.</param>
    /// <returns>The capability instance if found, otherwise null.</returns>
    public T? GetCapability<T>(string name) where T : class
        => _capabilities.TryGetValue(name, out var capability) ? capability as T : null;

    /// <summary>
    /// Adds or updates a capability.
    /// </summary>
    /// <param name="name">The name of the capability.</param>
    /// <param name="capability">The capability instance.</param>
    public void AddCapability(string name, object capability)
        => _capabilities[name] = capability ?? throw new ArgumentNullException(nameof(capability));

    /// <summary>
    /// Removes a capability by name.
    /// </summary>
    /// <param name="name">The name of the capability to remove.</param>
    /// <returns>True if the capability was removed, false if it wasn't found.</returns>
    public bool RemoveCapability(string name)
        => _capabilities.Remove(name);
}

#endregion

#region 

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
        await pipeline(context);

        // Return approval status
        return !context.IsTerminated;
    }

    /// <summary>
    /// Processes the function calls and returns the messages to add to the conversation
    /// </summary>
    public async Task<IList<ChatMessage>> ProcessFunctionCallsAsync(
        List<ChatMessage> messages,
        ChatOptions? options,
        List<FunctionCallContent> functionCallContents,
        AgentRunContext agentRunContext,
        string? agentName,
        CancellationToken cancellationToken)
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

                await ExecuteWithRetryAsync(ctx, cancellationToken);
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
            // Permission filters are naturally part of the pipeline now.
            foreach (var permissionFilter in _permissionFilters.Reverse())
            {
                var previous = pipeline;
                pipeline = ctx => permissionFilter.InvokeAsync(ctx, previous);
            }

            // Execute the full pipeline.
            await pipeline(context);

            // Mark function as completed in run context
            agentRunContext.CompleteFunction(functionCall.Name);

            var functionResult = new FunctionResultContent(functionCall.CallId, context.Result);
            var functionMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { functionResult });
            resultMessages.Add(functionMessage);
        }

        return resultMessages;
    }

    /// <summary>
    /// Executes a function with retry logic and timeout enforcement.
    /// Implements exponential backoff for transient failures.
    /// </summary>
    private async Task ExecuteWithRetryAsync(AiFunctionContext context, CancellationToken cancellationToken)
    {
        var maxRetries = _errorHandlingConfig?.MaxRetries ?? 3;
        var retryDelay = _errorHandlingConfig?.RetryDelay ?? TimeSpan.FromSeconds(1);
        var functionTimeout = _errorHandlingConfig?.SingleFunctionTimeout;

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
                context.Result = await context.Function!.InvokeAsync(args, functionCts.Token);
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

                // Exponential backoff: delay increases with each attempt
                var delay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * (attempt + 1));
                await Task.Delay(delay, cancellationToken);
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

    /// <summary>
    /// Reduction metadata from the last PrepareMessages call.
    /// Null if no reduction occurred.
    /// </summary>
    public (ChatMessage? SummaryMessage, int RemovedCount)? LastReductionMetadata { get; private set; }

    public MessageProcessor(string? systemInstructions, ChatOptions? defaultOptions, IReadOnlyList<IPromptFilter> promptFilters, IChatReducer? chatReducer = null)
    {
        _systemInstructions = systemInstructions;
        _defaultOptions = defaultOptions;
        _promptFilters = promptFilters ?? new List<IPromptFilter>();
        _chatReducer = chatReducer;
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
    /// </summary>
    public async Task<(IEnumerable<ChatMessage> messages, ChatOptions? options)> PrepareMessagesAsync(
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

            bool shouldReduce = false;

            if (lastSummaryIndex >= 0)
            {
                // Summary found - only count messages AFTER the summary
                var messagesAfterSummary = messagesList.Count - lastSummaryIndex - 1;

                // For now, use hardcoded threshold values
                // TODO: Extract from reducer configuration
                int targetCount = 20;
                int threshold = 5;

                shouldReduce = messagesAfterSummary > (targetCount + threshold);
            }
            else
            {
                // No summary found - check total message count
                int targetCount = 20;
                int threshold = 5;

                shouldReduce = messagesList.Count > (targetCount + threshold);
            }

            if (shouldReduce)
            {
                var reduced = await _chatReducer.ReduceAsync(effectiveMessages, cancellationToken);

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

                        // Store metadata for Agent to access
                        LastReductionMetadata = (summaryMsg, removedCount);
                    }

                    effectiveMessages = reducedList;
                }
            }
            else
            {
                // Clear metadata if no reduction occurred
                LastReductionMetadata = null;
            }
        }

        effectiveMessages = await ApplyPromptFiltersAsync(effectiveMessages, effectiveOptions, agentName, cancellationToken);

        return (effectiveMessages, effectiveOptions);
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
        return await pipeline(context);
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
    /// Reduction metadata if history reduction occurred during this turn.
    /// Null if no reduction was performed.
    /// </summary>
    public ReductionMetadata? Reduction { get; init; }

    /// <summary>
    /// Initializes a new instance of StreamingTurnResult
    /// </summary>
    /// <param name="eventStream">The stream of BaseEvents</param>
    /// <param name="finalHistory">Task that provides the final turn history</param>
    public StreamingTurnResult(
        IAsyncEnumerable<BaseEvent> eventStream,
        Task<IReadOnlyList<ChatMessage>> finalHistory)
    {
        EventStream = eventStream ?? throw new ArgumentNullException(nameof(eventStream));
        FinalHistory = finalHistory ?? throw new ArgumentNullException(nameof(finalHistory));
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

    /// <summary>
    /// Initializes a new instance of ToolScheduler
    /// </summary>
    /// <param name="functionCallProcessor">The function call processor to use for tool execution</param>
    /// <param name="config">Agent configuration for execution settings</param>
    public ToolScheduler(FunctionCallProcessor functionCallProcessor, AgentConfig? config)
    {
        _functionCallProcessor = functionCallProcessor ?? throw new ArgumentNullException(nameof(functionCallProcessor));
        _config = config;
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
        CancellationToken cancellationToken)
    {
        // For single tool calls, use sequential execution (no parallelization overhead)
        if (toolRequests.Count <= 1)
        {
            return await ExecuteSequentiallyAsync(currentHistory, toolRequests, options, agentRunContext, agentName, cancellationToken);
        }

        // For multiple tool calls, execute in parallel for better performance
        return await ExecuteInParallelAsync(currentHistory, toolRequests, options, agentRunContext, agentName, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        // Use the existing FunctionCallProcessor to execute the tools sequentially
        var resultMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(
            currentHistory, options, toolRequests, agentRunContext, agentName, cancellationToken);

        // Combine all tool results into a single message
        var allContents = new List<AIContent>();
        foreach (var message in resultMessages)
        {
            allContents.AddRange(message.Contents);
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
        CancellationToken cancellationToken)
    {
        // TWO-PHASE EXECUTION (inspired by Gemini CLI's CoreToolScheduler)
        // Phase 1: Check permissions for ALL tools SEQUENTIALLY (prevents race conditions)
        // Phase 2: Execute ALL approved tools in PARALLEL (with optional throttling)

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
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                // Execute each approved tool call through the processor
                var singleToolList = new List<FunctionCallContent> { toolRequest };
                var resultMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(
                    currentHistory, options, singleToolList, agentRunContext, agentName, cancellationToken);

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
        var results = await Task.WhenAll(executionTasks);

        // Aggregate results and handle errors
        var allContents = new List<AIContent>();
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
                $"Execution of '{deniedTool.Name}' was denied by the user."
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
        var uploads = await ConversationDocumentHelper.ProcessUploadsAsync(documentPaths, textExtractor, cancellationToken);

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

        // Create new message with updated content
        var newMessage = new ChatMessage(ChatRole.User, formattedMessage);
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

