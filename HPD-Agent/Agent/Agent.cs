using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using HPD.Agent.Providers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

/// <summary>
/// Agent facade that delegates to specialized components for clean separation of concerns.
/// Supports traditional chat, AGUI streaming protocols, and extended capabilities.
/// AIAgent (stateless, and stateful with threads) for maximum compatibility.
/// </summary>
public class Agent : AIAgent
{
    private readonly IChatClient _baseClient;
    private readonly string _name;
    private readonly ScopedFilterManager? _scopedFilterManager;
    private readonly int _maxFunctionCalls;
    private readonly IProviderRegistry _providerRegistry;

    // Microsoft.Extensions.AI compliance fields
    private readonly ChatClientMetadata _metadata;
    private readonly ErrorHandlingPolicy _errorPolicy;
    private string? _conversationId;

    // OpenTelemetry Activity Source for telemetry
    private static readonly ActivitySource ActivitySource = new("HPD.Agent");

    // AsyncLocal storage for function invocation context (flows across async calls)
    private static readonly AsyncLocal<FunctionInvocationContext?> _currentFunctionContext = new();

    // AsyncLocal storage for root agent tracking in nested agent calls
    // Used for event bubbling from nested agents to their orchestrator
    // When an agent calls another agent (via AsAIFunction), this tracks the top-level orchestrator
    // Flows automatically through AsyncLocal propagation across nested async calls
    private static readonly AsyncLocal<Agent?> _rootAgent = new();

    // Specialized component fields for delegation
    private readonly MessageProcessor _messageProcessor;
    private readonly FunctionCallProcessor _functionCallProcessor;
    private readonly AgentTurn _agentTurn;
    private readonly ToolScheduler _toolScheduler;
    private readonly AGUIEventHandler _aguiEventHandler;
    private readonly AGUIEventConverter _aguiConverter;
    private readonly HPD_Agent.Scoping.UnifiedScopingManager _scopingManager;
    private readonly PermissionManager _permissionManager;
    private readonly BidirectionalEventCoordinator _eventCoordinator;
    private readonly IReadOnlyList<IAiFunctionFilter> _aiFunctionFilters;
    private readonly IReadOnlyList<IMessageTurnFilter> _messageTurnFilters;
    private readonly HPD.Agent.ErrorHandling.IProviderErrorHandler _providerErrorHandler;


    /// <summary>
    /// Agent configuration object containing all settings
    /// </summary>
    public AgentConfig? Config { get; private set; }

    /// <summary>
    /// Gets the current function invocation context (if a function is currently being invoked).
    /// This flows across async calls via AsyncLocal storage.
    /// Returns null if no function is currently executing.
    /// </summary>
    /// <remarks>
    /// Use this in plugins/filters to access metadata about the current function call:
    /// - Function name and description
    /// - Call ID for correlation
    /// - Agent name and iteration number
    /// - Arguments being passed
    /// 
    /// Example:
    /// <code>
    /// var ctx = Agent.CurrentFunctionContext;
    /// if (ctx != null)
    /// {
    ///     Console.WriteLine($"Function {ctx.FunctionName} called at iteration {ctx.Iteration}");
    /// }
    /// </code>
    /// </remarks>
    public static FunctionInvocationContext? CurrentFunctionContext
    {
        get => _currentFunctionContext.Value;
        internal set => _currentFunctionContext.Value = value;
    }

    /// <summary>
    /// Gets or sets the root agent in the current execution chain.
    /// Returns null if no root agent is set (single-agent execution).
    /// </summary>
    /// <remarks>
    /// This property enables event bubbling from nested agents to their orchestrator.
    /// When an agent calls another agent (via AsAIFunction), this tracks the top-level orchestrator.
    ///
    /// Flow example:
    /// <code>
    /// User → Orchestrator.RunAsync()
    ///   Agent.RootAgent = orchestrator  // Set by Orchestrator
    ///   ↓
    ///   Orchestrator calls: CodingAgent(query)
    ///     Agent.RootAgent is still orchestrator ✓ (AsyncLocal flows!)
    ///     ↓
    ///     CodingAgent.Emit(event)
    ///       → Writes to CodingAgent's channel
    ///       → ALSO writes to orchestrator's channel (bubbling!)
    /// </code>
    ///
    /// This is set automatically by RunAgenticLoopInternal when starting execution
    /// and is used by BidirectionalEventCoordinator for event bubbling.
    /// </remarks>
    public static Agent? RootAgent
    {
        get => _rootAgent.Value;
        internal set => _rootAgent.Value = value;
    }

    /// <summary>
    /// Metadata about this chat client, compatible with Microsoft.Extensions.AI patterns
    /// </summary>
    public ChatClientMetadata Metadata => _metadata;

    /// <summary>
    /// Provider from the configuration
    /// </summary>
    public string? ProviderKey => Config?.Provider?.ProviderKey;

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
    /// Internal access to event coordinator for context setup and nested agent configuration.
    /// </summary>
    internal BidirectionalEventCoordinator EventCoordinator => _eventCoordinator;

    /// <summary>
    /// Internal access to filter event channel writer for context setup.
    /// Delegates to the event coordinator.
    /// </summary>
    internal ChannelWriter<InternalAgentEvent> FilterEventWriter => _eventCoordinator.EventWriter;

    /// <summary>
    /// Internal access to filter event channel reader for RunAgenticLoopInternal.
    /// Delegates to the event coordinator.
    /// </summary>
    internal ChannelReader<InternalAgentEvent> FilterEventReader => _eventCoordinator.EventReader;

    /// <summary>
    /// Sends a response to a filter waiting for a specific request.
    /// Called by external handlers (AGUI, Console, etc.) when user provides input.
    /// Thread-safe: Can be called from any thread.
    /// Delegates to the event coordinator.
    /// </summary>
    /// <param name="requestId">The unique identifier for the request</param>
    /// <param name="response">The response event to deliver</param>
    /// <exception cref="ArgumentNullException">If response is null</exception>
    public void SendFilterResponse(string requestId, InternalAgentEvent response)
    {
        _eventCoordinator.SendResponse(requestId, response);
    }

    /// <summary>
    /// Internal method for filters to wait for responses.
    /// Called by AiFunctionContext.WaitForResponseAsync().
    /// Delegates to the event coordinator.
    /// </summary>
    internal async Task<T> WaitForFilterResponseAsync<T>(
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken) where T : InternalAgentEvent
    {
        return await _eventCoordinator.WaitForResponseAsync<T>(requestId, timeout, cancellationToken);
    }

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
        HPD.Agent.ErrorHandling.IProviderErrorHandler providerErrorHandler,
        IProviderRegistry providerRegistry, // New
        HPD_Agent.Skills.SkillScopingManager? skillScopingManager = null, // New: Optional skill scoping
        IReadOnlyList<IPermissionFilter>? permissionFilters = null,
        IReadOnlyList<IAiFunctionFilter>? aiFunctionFilters = null,
        IReadOnlyList<IMessageTurnFilter>? messageTurnFilters = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = config.Name ?? "Agent"; // Default to "Agent" to prevent null dictionary key exceptions
        _scopedFilterManager = scopedFilterManager ?? throw new ArgumentNullException(nameof(scopedFilterManager));
        _maxFunctionCalls = config.MaxAgenticIterations;
        _providerErrorHandler = providerErrorHandler;
        _providerRegistry = providerRegistry; // New

        // TEMPORARY: Inject the resolved handler into the config object
        // so that FunctionCallProcessor can access it without changing its signature yet.
        // This will be removed when FunctionCallProcessor is refactored.
        if (Config.ErrorHandling == null) Config.ErrorHandling = new ErrorHandlingConfig();
        Config.ErrorHandling.ProviderHandler = _providerErrorHandler;


        // Initialize Microsoft.Extensions.AI compliance metadata
        _metadata = new ChatClientMetadata(
            providerName: config.Provider?.ProviderKey,
            providerUri: null, 
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

        // Create bidirectional event coordinator for filter events and human-in-the-loop
        _eventCoordinator = new BidirectionalEventCoordinator();

        // Create permission manager
        _permissionManager = new PermissionManager(permissionFilters);

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
        _functionCallProcessor = new FunctionCallProcessor(
            this, // NEW: Pass agent reference for filter event coordination
            scopedFilterManager,
            _permissionManager,
            _aiFunctionFilters,
            config.MaxAgenticIterations,
            config.ErrorHandling,
            config.ServerConfiguredTools,
            config.AgenticLoop);  // NEW: Pass agentic loop config for TerminateOnUnknownCalls
        _agentTurn = new AgentTurn(_baseClient);

        // Initialize unified scoping manager
        var skills = skillScopingManager?.GetSkills() ?? Enumerable.Empty<HPD_Agent.Skills.SkillDefinition>();
        var initialTools = (mergedOptions ?? config.Provider?.DefaultChatOptions)?.Tools?
            .OfType<AIFunction>().ToList() ?? new List<AIFunction>();
        _scopingManager = new HPD_Agent.Scoping.UnifiedScopingManager(skills, initialTools, null);

        _toolScheduler = new ToolScheduler(this, _functionCallProcessor, _permissionManager, config, _scopingManager);
        _aguiConverter = new AGUIEventConverter();
        _aguiEventHandler = new AGUIEventHandler(this);
    }

    /// <summary>
    /// Agent name
    /// </summary>
    public override string Name => _name;

    /// <summary>
    /// System instructions/persona
    /// </summary>
    public string? SystemInstructions => Config?.SystemInstructions ?? _messageProcessor.SystemInstructions;

    /// <summary>
    /// Default chat options
    /// </summary>
    public ChatOptions? DefaultOptions => Config?.Provider?.DefaultChatOptions ?? _messageProcessor.DefaultOptions;

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

        // Use the new protocol-agnostic internal core
        var turnHistory = new List<ChatMessage>();
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var internalStream = RunAgenticLoopInternal(
            messages,
            chatOptions,
            documentPaths: null,
            turnHistory,
            historyCompletionSource,
            reductionCompletionSource,
            cancellationToken);

        // Adapt internal events to AGUI protocol
        var aguiStream = EventStreamAdapter.ToAGUI(internalStream, aguiInput.ThreadId, aguiInput.RunId, cancellationToken);

        // Wrap with error handling
        var errorHandledStream = EventStreamAdapter.WithErrorHandling(aguiStream, historyCompletionSource, cancellationToken);

        // Wrap the final history task to apply post-processing when complete
        var wrappedHistoryTask = historyCompletionSource.Task.ContinueWith(async task =>
        {
            Exception? invocationException = task.IsFaulted ? task.Exception?.InnerException : null;
            var history = task.IsCompletedSuccessfully ? await task : new List<ChatMessage>();

            // Apply post-invoke filters (for memory extraction, learning, etc.)
            try
            {
                await _messageProcessor.ApplyPostInvokeFiltersAsync(
                    messages.ToList(),
                    task.IsCompletedSuccessfully ? history : null,
                    invocationException,
                    chatOptions,
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
                    var userMessage = messages.LastOrDefault(m => m.Role == ChatRole.User) ?? new ChatMessage(ChatRole.User, string.Empty);
                    await ApplyMessageTurnFilters(userMessage, history, chatOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Log but don't fail the turn if filters fail
                }
            }

            return history;
        }, cancellationToken).Unwrap();

        // Return the result containing stream, history, and reduction metadata
        return new StreamingTurnResult(errorHandledStream, wrappedHistoryTask, reductionCompletionSource.Task);
    }



    /// <summary>
    /// Protocol-agnostic core agentic loop that emits internal events.
    /// This method contains all the agent logic without any protocol-specific concerns.
    /// Adapters convert internal events to protocol-specific formats (AGUI, IChatClient, etc.).
    /// </summary>
    private async IAsyncEnumerable<InternalAgentEvent> RunAgenticLoopInternal(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string[]? documentPaths,
        List<ChatMessage> turnHistory,
        TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletionSource,
        TaskCompletionSource<ReductionMetadata?> reductionCompletionSource,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Create orchestration activity to group all agent turns and function calls
        // This provides better observability in distributed tracing systems (OpenTelemetry)
        using var orchestrationActivity = ActivitySource.StartActivity(
            "agent.orchestration",
            ActivityKind.Internal);

        orchestrationActivity?.SetTag("agent.name", _name);
        orchestrationActivity?.SetTag("agent.max_iterations", _maxFunctionCalls);
        orchestrationActivity?.SetTag("agent.provider", ProviderKey);
        orchestrationActivity?.SetTag("agent.model", ModelId);

        // Track root agent for event bubbling across nested agent calls
        // If RootAgent is null, this is the top-level agent - set ourselves as root
        // If RootAgent is already set, we're nested - keep the existing root
        var previousRootAgent = RootAgent;
        RootAgent ??= this;

        // Process documents if provided (protocol-agnostic feature)
        if (documentPaths?.Length > 0)
        {
            messages = await AgentDocumentProcessor.ProcessDocumentsAsync(messages, documentPaths, Config, cancellationToken).ConfigureAwait(false);
        }

        // Create linked cancellation token for turn timeout
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Config?.AgenticLoop?.MaxTurnDuration is { } turnTimeout)
        {
            turnCts.CancelAfter(turnTimeout);
        }
        var effectiveCancellationToken = turnCts.Token;

        // Generate IDs for this message turn
        var messageTurnId = Guid.NewGuid().ToString();

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

        var messageId = Guid.NewGuid().ToString();

        // Track conversation ID consistently
        _conversationId = conversationId;

        try
        {
            // Emit MESSAGE TURN started event
            yield return new InternalMessageTurnStartedEvent(messageTurnId, conversationId);

            // Collect all response updates to build final history
            var responseUpdates = new List<ChatResponseUpdate>();

        // Prepare messages using MessageProcessor
        ReductionMetadata? reductionMetadata = null;
        IEnumerable<ChatMessage> effectiveMessages;
        ChatOptions? effectiveOptions;

        try
        {
            var prep = await _messageProcessor.PrepareMessagesAsync(
                messages, options, _name, effectiveCancellationToken).ConfigureAwait(false);
            (effectiveMessages, effectiveOptions, reductionMetadata) = prep;
        }
        catch (Exception ex)
        {
            // FIX #8: ALWAYS complete both tasks to prevent hanging callers
            // Following Microsoft's pattern: guarantee completion on all paths
            reductionCompletionSource.TrySetException(ex);
            historyCompletionSource.TrySetException(ex);
            throw;
        }

        // Set reduction metadata immediately
        reductionCompletionSource.TrySetResult(reductionMetadata);

        var currentMessages = effectiveMessages.ToList();

        // Track LAST message ID for cleanup outside loop (not per-iteration state)
        string? lastAssistantMessageId = null;

        // Create agent run context for tracking across all function calls
        var agentRunContext = new AgentRunContext(messageTurnId, conversationId, _maxFunctionCalls);

        // Track consecutive same-function calls for circuit breaker
        var lastSignaturePerTool = new Dictionary<string, string>();
        var consecutiveCountPerTool = new Dictionary<string, int>();

        // Plugin scoping: Track expanded plugins (message-turn scoped)
        var expandedPlugins = new HashSet<string>();

        // Skill scoping: Track expanded skills (message-turn scoped)
        var expandedSkills = new HashSet<string>();

        // Auto-expand skills marked with AutoExpand = true will be handled by UnifiedScopingManager

        // ConversationId history optimization (inspired by Microsoft's FunctionInvokingChatClient)
        // When the inner client manages history server-side (indicated by returning a ConversationId),
        // we only send NEW messages rather than the full history, significantly reducing token costs.
        // Beneficial for: OpenAI Assistants API, Anthropic threads, Azure AI services with conversation tracking
        bool innerClientTracksHistory = false; // whether the service returned a ConversationId in last iteration
        int messagesSentToInnerClient = 0; // how many messages from currentMessages we've sent to the inner client

        // Main agentic loop - use while loop to allow dynamic limit extension
        int iteration = 0;
        while (iteration < agentRunContext.MaxIterations)
        {
            agentRunContext.CurrentIteration = iteration;

            // Check if run has been terminated early
            if (agentRunContext.IsTerminated)
            {
                break;
            }

            // FIX #4: Generate NEW message ID per agent turn (not per message turn)
            // Following Microsoft's pattern: new ID for each assistant segment
            // Prevents protocol violations where multiple responses share same ID
            var assistantMessageId = Guid.NewGuid().ToString();
            lastAssistantMessageId = assistantMessageId; // Track for cleanup outside loop

            // Emit AGENT TURN started event
            yield return new InternalAgentTurnStartedEvent(iteration);

            // Yield any filter events that accumulated before iteration start
            while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
            {
                yield return filterEvt;
            }

            var toolRequests = new List<FunctionCallContent>();
            var assistantContents = new List<AIContent>();
            bool streamFinished = false;

            // Track thinking/reasoning/message state PER AGENT TURN (reset for each iteration)
            // CRITICAL: messageStarted MUST reset per iteration to emit TextMessageStart for each assistant message
            bool messageStarted = false;
            bool reasoningStarted = false;
            bool reasoningMessageStarted = false;

            // Plugin & Skill scoping: Apply scoping to tools for this agent turn (only if enabled)
            var scopedOptions = effectiveOptions;
            if (Config?.PluginScoping?.Enabled == true &&
                effectiveOptions?.Tools is { Count: > 0 })
            {
                // PERFORMANCE: Single-pass extraction using manual loop
                // Replaces: OfType<AIFunction>().ToList() + Cast<AITool>().ToList()
                var aiFunctions = new List<AIFunction>(effectiveOptions.Tools.Count);
                for (int i = 0; i < effectiveOptions.Tools.Count; i++)
                {
                    if (effectiveOptions.Tools[i] is AIFunction af)
                        aiFunctions.Add(af);
                }

                // Get plugin-scoped functions (containers + non-plugin + expanded plugin functions)
                // Use unified scoping manager that handles both plugins and skills
                var scopedFunctions = _scopingManager.GetToolsForAgentTurn(aiFunctions, expandedPlugins, expandedSkills);

                // Manual cast to AITool list (still better than LINQ Cast + ToList)
                var scopedTools = new List<AITool>(scopedFunctions.Count);
                for (int i = 0; i < scopedFunctions.Count; i++)
                {
                    scopedTools.Add(scopedFunctions[i]);
                }

                scopedOptions = new ChatOptions
                {
                    ModelId = effectiveOptions.ModelId,
                    Tools = scopedTools,
                    ToolMode = effectiveOptions.ToolMode,
                    Temperature = effectiveOptions.Temperature,
                    MaxOutputTokens = effectiveOptions.MaxOutputTokens,
                    TopP = effectiveOptions.TopP,
                    FrequencyPenalty = effectiveOptions.FrequencyPenalty,
                    PresencePenalty = effectiveOptions.PresencePenalty,
                    StopSequences = effectiveOptions.StopSequences,
                    ResponseFormat = effectiveOptions.ResponseFormat,
                    AdditionalProperties = effectiveOptions.AdditionalProperties,
                    ConversationId = innerClientTracksHistory ? conversationId : effectiveOptions.ConversationId
                };
            }
            else if (innerClientTracksHistory && (scopedOptions == null || scopedOptions.ConversationId != conversationId))
            {
                // Need to add ConversationId to options (when not doing plugin scoping)
                scopedOptions = scopedOptions == null 
                    ? new ChatOptions { ConversationId = conversationId }
                    : new ChatOptions
                    {
                        ModelId = scopedOptions.ModelId,
                        Tools = scopedOptions.Tools,
                        ToolMode = scopedOptions.ToolMode,
                        Temperature = scopedOptions.Temperature,
                        MaxOutputTokens = scopedOptions.MaxOutputTokens,
                        TopP = scopedOptions.TopP,
                        FrequencyPenalty = scopedOptions.FrequencyPenalty,
                        PresencePenalty = scopedOptions.PresencePenalty,
                        StopSequences = scopedOptions.StopSequences,
                        ResponseFormat = scopedOptions.ResponseFormat,
                        AdditionalProperties = scopedOptions.AdditionalProperties,
                        ConversationId = conversationId
                    };
            }

            // ConversationId history optimization: Determine which messages to send
            // If the service tracks history (has ConversationId), only send NEW messages
            IEnumerable<ChatMessage> messagesToSend;
            if (innerClientTracksHistory && iteration > 0)
            {
                // Service manages history server-side, only send messages added since last iteration
                messagesToSend = currentMessages.Skip(messagesSentToInnerClient);
            }
            else
            {
                // Either first iteration or service doesn't track history - send all messages
                messagesToSend = currentMessages;
            }

            // Track how many messages we're sending for next iteration
            int messageCountBeforeThisTurn = currentMessages.Count;

            // Run agent turn (single LLM call)
            await foreach (var update in _agentTurn.RunAsync(messagesToSend, scopedOptions, effectiveCancellationToken))
            {
                // Store update for building final history
                responseUpdates.Add(update);

                // Process contents and emit internal events
                if (update.Contents != null)
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                        {
                            // Emit reasoning start event ONLY on first reasoning chunk
                            if (!reasoningStarted)
                            {
                                yield return new InternalReasoningStartEvent(assistantMessageId);
                                reasoningStarted = true;
                            }

                            if (!reasoningMessageStarted)
                            {
                                yield return new InternalReasoningMessageStartEvent(assistantMessageId, "assistant");
                                reasoningMessageStarted = true;
                            }

                            // Emit reasoning delta
                            yield return new InternalReasoningDeltaEvent(reasoning.Text, assistantMessageId);

                            // Add reasoning to assistantContents for history
                            assistantContents.Add(reasoning);
                        }
                        else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                        {
                            // If we were in reasoning mode, finish the reasoning events
                            if (reasoningMessageStarted)
                            {
                                yield return new InternalReasoningMessageEndEvent(assistantMessageId);
                                reasoningMessageStarted = false;
                            }
                            if (reasoningStarted)
                            {
                                yield return new InternalReasoningEndEvent(assistantMessageId);
                                reasoningStarted = false;
                            }

                            // Emit message start if this is the first text content (for THIS agent turn)
                            if (!messageStarted)
                            {
                                yield return new InternalTextMessageStartEvent(assistantMessageId, "assistant");
                                messageStarted = true;
                            }

                            // Regular text content - add to history
                            assistantContents.Add(textContent);

                            // Emit text delta event
                            yield return new InternalTextDeltaEvent(textContent.Text, assistantMessageId);
                        }
                        else if (content is FunctionCallContent functionCall)
                        {
                            // Emit message start if this is the first content (for THIS agent turn)
                            if (!messageStarted)
                            {
                                yield return new InternalTextMessageStartEvent(assistantMessageId, "assistant");
                                messageStarted = true;
                            }

                            // ✨ TRUE STREAMING: Emit function call events immediately
                            yield return new InternalToolCallStartEvent(
                                functionCall.CallId,
                                functionCall.Name ?? string.Empty,
                                assistantMessageId);

                            // Emit tool call arguments immediately
                            if (functionCall.Arguments != null && functionCall.Arguments.Count > 0)
                            {
                                var argsJson = System.Text.Json.JsonSerializer.Serialize(
                                    functionCall.Arguments,
                                    AGUIJsonContext.Default.DictionaryStringObject);

                                yield return new InternalToolCallArgsEvent(functionCall.CallId, argsJson);
                            }

                            // Still buffer for execution (unchanged)
                            toolRequests.Add(functionCall);
                            assistantContents.Add(functionCall);
                        }
                    }
                }

                // Periodically yield filter events during LLM streaming
                while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
                {
                    yield return filterEvt;
                }

                // Check for stream completion
                if (update.FinishReason != null)
                {
                    streamFinished = true;

                    // If reasoning is still active when stream ends, finish it
                    if (reasoningMessageStarted)
                    {
                        yield return new InternalReasoningMessageEndEvent(assistantMessageId);
                        reasoningMessageStarted = false;
                    }
                    if (reasoningStarted)
                    {
                        yield return new InternalReasoningEndEvent(assistantMessageId);
                        reasoningStarted = false;
                    }
                }
            }

            // Capture ConversationId from the agent turn response (if available)
            // This enables server-side history optimization for OpenAI Assistants, Anthropic threads, etc.
            if (_agentTurn.LastResponseConversationId != null)
            {
                // Service returned a ConversationId - it's managing history server-side
                conversationId = _agentTurn.LastResponseConversationId;
                
                if (!innerClientTracksHistory)
                {
                    // First time seeing a ConversationId - switch to optimized mode
                    innerClientTracksHistory = true;
                }
                
                // Update our tracking of how many messages we've sent
                // This will be used in the next iteration to only send NEW messages
                messagesSentToInnerClient = messageCountBeforeThisTurn;
            }
            else if (innerClientTracksHistory)
            {
                // Edge case: Service STOPPED returning ConversationId mid-conversation
                // This is rare but possible - need to reset and send full history next time
                innerClientTracksHistory = false;
                messagesSentToInnerClient = 0;
            }

            // Emit AGENT TURN finished event
            yield return new InternalAgentTurnFinishedEvent(iteration);

            // Close the message if we started one in this iteration
            if (messageStarted)
            {
                yield return new InternalTextMessageEndEvent(assistantMessageId);
            }

            // If there are tool requests, execute them
            if (toolRequests.Count > 0)
            {
                // Create assistant message with tool calls
                var assistantMessage = new ChatMessage(ChatRole.Assistant, assistantContents);
                currentMessages.Add(assistantMessage);

                // Create assistant message for history WITHOUT reasoning (save tokens)
                var historyContents = assistantContents.Where(c => c is not TextReasoningContent).ToList();
                
                // FIX Bug #2: Only add to history if there's meaningful TEXT content (non-empty, non-whitespace)
                // FunctionCallContent alone doesn't need a separate assistant message - it's just metadata
                var hasNonEmptyText = historyContents
                    .OfType<TextContent>()
                    .Any(t => !string.IsNullOrWhiteSpace(t.Text));
                
                if (hasNonEmptyText)
                {
                    var historyMessage = new ChatMessage(ChatRole.Assistant, historyContents);
                    turnHistory.Add(historyMessage);
                }

                // Note: Tool call start/args events are now emitted immediately during streaming (see above)
                // This ensures true streaming with zero latency for function call visibility

                // Yield filter events before tool execution
                while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
                {
                    yield return filterEvt;
                }

                // Execute tools with periodic event draining to prevent deadlock
                // This allows permission events to be yielded WHILE waiting for approval
                var executeTask = _toolScheduler.ExecuteToolsAsync(
                    currentMessages, toolRequests, effectiveOptions, agentRunContext, _name, expandedPlugins, expandedSkills, effectiveCancellationToken);

                // Poll for filter events while tool execution is in progress
                // This is CRITICAL for bidirectional filters (permissions, etc.)
                while (!executeTask.IsCompleted)
                {
                    // Wait for either task completion or a short delay
                    var delayTask = Task.Delay(10, effectiveCancellationToken);
                    await Task.WhenAny(executeTask, delayTask).ConfigureAwait(false);

                    // Yield any events that accumulated during execution
                    while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
                    {
                        yield return filterEvt;
                    }
                }

                // Get the result (this won't block since task is complete)
                var toolResultMessage = await executeTask.ConfigureAwait(false);

                // Final drain - yield any remaining events after tool execution
                while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
                {
                    yield return filterEvt;
                }

                // Filter out container expansion results from persistent history
                // Container expansions are only relevant within the current message turn
                // since expansion state resets after each message turn (expandedPlugins is local variable)
                // Without filtering, expansion messages accumulate in history but become stale/useless
                var nonContainerResults = new List<AIContent>();
                foreach (var content in toolResultMessage.Contents)
                {
                    if (content is FunctionResultContent result)
                    {
                        // Check if this result is from a container function
                        var isContainerResult = toolRequests.Any(tr =>
                            tr.CallId == result.CallId &&
                            effectiveOptions?.Tools?.OfType<AIFunction>()
                                .FirstOrDefault(t => t.Name == tr.Name)
                                ?.AdditionalProperties?.TryGetValue("IsContainer", out var isContainer) == true &&
                            isContainer is bool isCont && isCont);

                        if (!isContainerResult)
                        {
                            nonContainerResults.Add(content);
                        }
                    }
                    else
                    {
                        nonContainerResults.Add(content);
                    }
                }

                // Add filtered results to persistent history (excluding container expansions)
                // This keeps history clean and avoids accumulating stale expansion messages
                if (nonContainerResults.Count > 0)
                {
                    var filteredMessage = new ChatMessage(ChatRole.Tool, nonContainerResults);
                    currentMessages.Add(filteredMessage);
                }

                // Add ALL results (including container expansions) to turn history
                // The LLM needs to see container expansions within the current turn to know what functions are available
                turnHistory.Add(toolResultMessage);

                // Check for errors in tool results
                bool hasErrors = false;
                foreach (var content in toolResultMessage.Contents)
                {
                    if (content is FunctionResultContent result)
                    {
                        yield return new InternalToolCallEndEvent(result.CallId);

                        // Emit tool call result event
                        yield return new InternalToolCallResultEvent(result.CallId, result.Result?.ToString() ?? "null");

                        // Check if this result represents an error
                        if (result.Exception != null ||
                            (result.Result?.ToString()?.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ?? false))
                        {
                            hasErrors = true;
                        }
                    }
                }

                // Circuit breaker: Check for consecutive same-function calls
                if (toolRequests.Count > 0 && Config?.AgenticLoop?.MaxConsecutiveFunctionCalls is { } maxConsecutive)
                {
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
                            yield return new InternalTextDeltaEvent(errorMessage, assistantMessageId);

                            agentRunContext.IsTerminated = true;
                            agentRunContext.TerminationReason = $"Circuit breaker: '{toolRequest.Name}' with same arguments called {consecutiveCountPerTool[toolName]} times consecutively";
                            break;
                        }
                    }

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
                        yield return new InternalTextDeltaEvent(
                            $"⚠️ Maximum consecutive errors ({maxConsecutiveErrors}) exceeded. Stopping execution to prevent infinite error loop.",
                            assistantMessageId);

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

                // FIX #7: Clone AdditionalProperties to prevent cross-iteration mutation
                // Following Microsoft's pattern: avoid aliasing mutable dictionaries
                var clonedProperties = effectiveOptions?.AdditionalProperties is null
                    ? null
                    : new AdditionalPropertiesDictionary(effectiveOptions.AdditionalProperties);

                // Update options for next iteration
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
                        AdditionalProperties = clonedProperties
                    };

                // FIX: Do NOT increment here - moved to single location at loop end
            }
            else if (streamFinished)
            {
                // No tools called and stream finished - we're done
                // FIX Bug #1: Do NOT add message to turnHistory here
                // The final message will be added after the loop from ConstructChatResponseFromUpdates
                // This prevents duplicate final assistant messages
                break;
            }
            else
            {
                // Guard: If stream ended and there are no tools to run, exit
                if (toolRequests.Count == 0)
                {
                    break;
                }

                // FIX: Do NOT increment here - moved to single location at loop end
            }

            // FIX: Increment exactly once per loop iteration in a single place
            // This ensures circuit breaker and error limits work correctly
            iteration++;
        }

        // Build the complete history including the final assistant message
        // FIX Bug #1 & #2: Only add final message if NO tools were called in the entire turn
        // When tools are called, assistant messages are already added to turnHistory inside the loop (line ~945)
        // ConstructChatResponseFromUpdates merges ALL updates into ONE message, causing duplicates/wrong content
        // So we only use it when there were NO iterations with tool calls (simple text-only response)
        var hadAnyToolCalls = turnHistory.Any(m => m.Role == ChatRole.Tool);
        
        if (!hadAnyToolCalls && responseUpdates.Any())
        {
            var finalResponse = ConstructChatResponseFromUpdates(responseUpdates);
            if (finalResponse.Messages.Count > 0)
            {
                var finalAssistantMessage = finalResponse.Messages[0];

                // Only add final message if it has meaningful content
                if (finalAssistantMessage.Contents.Count > 0)
                {
                    turnHistory.Add(finalAssistantMessage);
                }
            }
        }

        // Note: TextMessageEnd is now emitted INSIDE the loop after each agent turn
        // This ensures proper event pairing per AGUI spec

        // Final drain of filter events after loop
        while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
        {
            yield return filterEvt;
        }

        // Emit MESSAGE TURN finished event
        yield return new InternalMessageTurnFinishedEvent(messageTurnId, conversationId);

        // Record orchestration telemetry metrics
        orchestrationActivity?.SetTag("agent.total_iterations", iteration);
        orchestrationActivity?.SetTag("agent.total_function_calls", agentRunContext.CompletedFunctions.Count);
        orchestrationActivity?.SetTag("agent.termination_reason", agentRunContext.TerminationReason ?? "completed");
        orchestrationActivity?.SetTag("agent.was_terminated", agentRunContext.IsTerminated);
        
        if (reductionMetadata != null)
        {
            orchestrationActivity?.SetTag("agent.history_reduction_occurred", true);
            orchestrationActivity?.SetTag("agent.history_messages_removed", reductionMetadata.MessagesRemovedCount);
        }

            // FIX #8: ALWAYS complete history task to prevent hanging
            // This is guaranteed to be called even if exceptions occur during enumeration
            historyCompletionSource.TrySetResult(turnHistory);
        }
        finally
        {
            // Restore previous root agent (important for nested calls)
            // This ensures AsyncLocal state is properly cleaned up
            RootAgent = previousRootAgent;
        }
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

    private IChatReducer? CreateChatReducer(AgentConfig config, IChatClient baseClient)
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
    private SummarizingChatReducer CreateSummarizingReducer(IChatClient baseClient, HistoryReductionConfig historyConfig, AgentConfig agentConfig)
    {
        // Determine which client to use for summarization
        IChatClient summarizerClient = baseClient; // Default to main client

        if (historyConfig.SummarizerProvider != null)
        {
            var providerConfig = historyConfig.SummarizerProvider;
            var providerKey = providerConfig.ProviderKey;
            
            if (!string.IsNullOrEmpty(providerKey) && _providerRegistry.GetProvider(providerKey) is { } providerFeatures)
            {
                try
                {
                    summarizerClient = providerFeatures.CreateChatClient(providerConfig, null);
                }
                catch (Exception)
                {
                    // Log warning about failing to create summarizer client, will use base client
                }
            }
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

        // Build and execute the message turn filter pipeline using FilterChain
        Func<MessageTurnFilterContext, Task> finalAction = _ => Task.CompletedTask;
        var pipeline = FilterChain.BuildMessageTurnPipeline(_messageTurnFilters, finalAction);
        await pipeline(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Collects function call metadata from the message history.
    /// PERFORMANCE: Replaces 5 LINQ operations with direct iteration (5-10x faster).
    /// </summary>
    private Dictionary<string, List<string>> CollectAgentFunctionCalls(IReadOnlyList<ChatMessage> history)
    {
        var metadata = new Dictionary<string, List<string>>();

        foreach (var message in history)
        {
            if (message.Role != ChatRole.Assistant)
                continue;

            // Manual iteration instead of LINQ chain (OfType + Select + Where + ToList + Any)
            List<string>? functionCalls = null;
            for (int i = 0; i < message.Contents.Count; i++)
            {
                if (message.Contents[i] is FunctionCallContent fc && 
                    !string.IsNullOrEmpty(fc.Name))
                {
                    (functionCalls ??= []).Add(fc.Name);
                }
            }

            if (functionCalls is { Count: > 0 })
            {
                // Append to existing list instead of overwriting
                if (!metadata.TryGetValue(_name, out var existingList))
                {
                    metadata[_name] = functionCalls;
                }
                else
                {
                    // Add unique function calls to avoid duplicates (manual loop instead of LINQ)
                    foreach (var fc in functionCalls)
                    {
                        if (!existingList.Contains(fc))
                            existingList.Add(fc);
                    }
                }
            }
        }

        return metadata;
    }

    #endregion

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
    /// Extracts only text content (TextContent and TextReasoningContent) from a message for comparison.
    /// Used for dedupe logic to avoid false negatives when function calls are present in one message but not the other.
    /// </summary>
    private static string ExtractTextOnlyContent(IList<AIContent> contents)
    {
        var sb = new StringBuilder();
        foreach (var c in contents)
        {
            switch (c)
            {
                case TextReasoningContent r:
                    sb.Append(r.Text);
                    break;
                case TextContent t:
                    sb.Append(t.Text);
                    break;
                // Ignore function calls, function results, and data content for text comparison
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

    #region Thread-Aware Public API

    /// <summary>
    /// Creates a new conversation thread.
    /// </summary>
    /// <returns>A new ConversationThread instance</returns>
    public ConversationThread CreateThread()
    {
        return new ConversationThread();
    }

    /// <summary>
    /// Creates a new conversation thread within a project context.
    /// The project reference is stored in the thread's metadata.
    /// </summary>
    /// <param name="project">The project to associate with this thread</param>
    /// <returns>A new ConversationThread with project metadata</returns>
    public ConversationThread CreateThread(Project project)
    {
        var thread = new ConversationThread();
        thread.AddMetadata("Project", project);
        return thread;
    }

    /// <summary>
    /// Runs the agent with messages and an explicit thread for state management (non-streaming).
    /// This is the primary method for agent execution.
    /// </summary>
    /// <param name="messages">Messages to process</param>
    /// <param name="thread">Thread for conversation state</param>
    /// <param name="options">Optional chat options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Chat response with final messages</returns>
    public override async Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Create thread if not provided, cast to ConversationThread
        var conversationThread = (thread as ConversationThread) ?? new ConversationThread();

        // Convert AgentRunOptions to ChatOptions (for now, create empty ChatOptions)
        var chatOptions = options != null ? new ChatOptions() : null;

        // Add workflow messages to thread state
        foreach (var msg in messages)
        {
            if (!conversationThread.Messages.Contains(msg))
            {
                await conversationThread.AddMessageAsync(msg, cancellationToken);
            }
        }

        // Create ConversationExecutionContext for AsyncLocal context
        var executionContext = new ConversationExecutionContext(conversationThread.Id)
        {
            AgentName = this.Name
        };

        // Set AsyncLocal context for plugins (e.g., PlanMode) to access
        ConversationContext.Set(executionContext);

        IReadOnlyList<ChatMessage> finalHistory;
        try
        {
            // ✅ OPTIMIZATION: Call RunAgenticLoopInternal directly (no AGUI conversion!)
            var turnHistory = new List<ChatMessage>();
            var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var internalStream = RunAgenticLoopInternal(
                conversationThread.Messages,  // ✅ ChatMessage[] - NO CONVERSION!
                chatOptions,
                documentPaths: null,
                turnHistory,
                historyCompletionSource,
                reductionCompletionSource,
                cancellationToken);

            // Consume stream (non-streaming path)
            await foreach (var _ in internalStream.WithCancellation(cancellationToken))
            {
                // Just consume events
            }

            // Get final history and reduction
            finalHistory = await historyCompletionSource.Task;
            var reductionMetadata = await reductionCompletionSource.Task;

            // Apply post-invoke filters (for memory extraction, learning, etc.)
            try
            {
                await _messageProcessor.ApplyPostInvokeFiltersAsync(
                    conversationThread.Messages.ToList(),
                    finalHistory.ToList(),
                    null, // no exception
                    chatOptions,
                    Config?.Name ?? "Agent",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Log but don't fail - post-processing is best-effort
            }

            // Apply message turn filters after the turn completes
            if (_messageTurnFilters.Any())
            {
                try
                {
                    var userMessage = conversationThread.Messages.LastOrDefault(m => m.Role == ChatRole.User) ?? new ChatMessage(ChatRole.User, string.Empty);
                    await ApplyMessageTurnFilters(userMessage, finalHistory, chatOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Log but don't fail the turn if filters fail
                }
            }

            // Apply reduction
            if (reductionMetadata != null && reductionMetadata.SummaryMessage != null)
            {
                await conversationThread.ApplyReductionAsync(reductionMetadata.SummaryMessage, reductionMetadata.MessagesRemovedCount, cancellationToken);
            }
        }
        finally
        {
            // Clear context to prevent leaks
            ConversationContext.Clear();
        }

        // Update thread with final messages
        foreach (var msg in finalHistory)
        {
            if (!conversationThread.Messages.Contains(msg))
            {
                await conversationThread.AddMessageAsync(msg, cancellationToken);
            }
        }

        // Convert to AgentRunResponse
        var response = new AgentRunResponse();
        foreach (var msg in finalHistory)
        {
            response.Messages.Add(msg);
        }

        return response;
    }

    /// <summary>
    /// Runs the agent with messages and an explicit thread for state management (streaming).
    /// This is the primary method for streaming agent execution.
    /// </summary>
    /// <param name="messages">Messages to process</param>
    /// <param name="thread">Thread for conversation state</param>
    /// <param name="options">Optional chat options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Streaming agent run response updates (extended with HPD-specific event data)</returns>
    public override async IAsyncEnumerable<ExtendedAgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Create thread if not provided, cast to ConversationThread
        var conversationThread = (thread as ConversationThread) ?? new ConversationThread();

        // Convert AgentRunOptions to ChatOptions (for now, create empty ChatOptions)
        var chatOptions = options != null ? new ChatOptions() : null;

        // Add workflow messages to thread state
        foreach (var msg in messages)
        {
            if (!conversationThread.Messages.Contains(msg))
            {
                await conversationThread.AddMessageAsync(msg, cancellationToken);
            }
        }

        // Create ConversationExecutionContext for AsyncLocal context
        var executionContext = new ConversationExecutionContext(conversationThread.Id)
        {
            AgentName = this.Name
        };

        // Set AsyncLocal context for plugins (e.g., PlanMode) to access
        ConversationContext.Set(executionContext);

        IReadOnlyList<ChatMessage> finalHistory;
        try
        {
            // ✅ OPTIMIZATION: Call RunAgenticLoopInternal directly (no AGUI conversion!)
            var turnHistory = new List<ChatMessage>();
            var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var internalStream = RunAgenticLoopInternal(
                conversationThread.Messages,  // ✅ ChatMessage[] - NO CONVERSION!
                chatOptions,
                documentPaths: null,
                turnHistory,
                historyCompletionSource,
                reductionCompletionSource,
                cancellationToken);

            // ✅ Use EventStreamAdapter pattern for protocol conversion
            var agentsAIStream = EventStreamAdapter.ToAgentsAI(internalStream, conversationThread.Id, this.Name, cancellationToken);

            await foreach (var update in agentsAIStream)
            {
                yield return update;
            }

            // Get final history and reduction
            finalHistory = await historyCompletionSource.Task;
            var reductionMetadata = await reductionCompletionSource.Task;

            // Apply post-invoke filters (for memory extraction, learning, etc.)
            try
            {
                await _messageProcessor.ApplyPostInvokeFiltersAsync(
                    conversationThread.Messages.ToList(),
                    finalHistory.ToList(),
                    null, // no exception
                    chatOptions,
                    Config?.Name ?? "Agent",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Log but don't fail - post-processing is best-effort
            }

            // Apply message turn filters after the turn completes
            if (_messageTurnFilters.Any())
            {
                try
                {
                    var userMessage = conversationThread.Messages.LastOrDefault(m => m.Role == ChatRole.User) ?? new ChatMessage(ChatRole.User, string.Empty);
                    await ApplyMessageTurnFilters(userMessage, finalHistory, chatOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Log but don't fail the turn if filters fail
                }
            }

            // Apply reduction
            if (reductionMetadata != null && reductionMetadata.SummaryMessage != null)
            {
                await conversationThread.ApplyReductionAsync(reductionMetadata.SummaryMessage, reductionMetadata.MessagesRemovedCount, cancellationToken);
            }
        }
        finally
        {
            // Clear context to prevent leaks
            ConversationContext.Clear();
        }

        // Update thread with final messages
        foreach (var msg in finalHistory)
        {
            if (!conversationThread.Messages.Contains(msg))
            {
                await conversationThread.AddMessageAsync(msg, cancellationToken);
            }
        }
    }


    /// <summary>
    /// Runs the agent with AGUI protocol input (streaming).
    /// Returns full BaseEvent stream for AGUI frontend compatibility.
    /// This method is for direct AGUI protocol integration (e.g., frontend communication).
    /// </summary>
    /// <param name="aguiInput">AGUI protocol input containing thread, messages, tools, and context</param>
    /// <param name="thread">Conversation thread for state management</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Full stream of BaseEvent (AGUI protocol events)</returns>
    public async IAsyncEnumerable<BaseEvent> RunStreamingAGUIAsync(
        RunAgentInput aguiInput,
        ConversationThread thread,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("agent.turn");
        var startTime = DateTimeOffset.UtcNow;

        activity?.SetTag("agent.id", thread.Id);
        activity?.SetTag("agent.input_format", "agui");
        activity?.SetTag("agent.thread_id", aguiInput.ThreadId);
        activity?.SetTag("agent.run_id", aguiInput.RunId);

        // Add the new user message from aguiInput to thread
        var newUserMessage = aguiInput.Messages.LastOrDefault(m => m.Role == "user");
        if (newUserMessage != null)
        {
            await thread.AddMessageAsync(new ChatMessage(ChatRole.User, newUserMessage.Content ?? ""), cancellationToken);
        }

        // ✅ OPTIMIZATION: Skip AGUI message conversion, call RunAgenticLoopInternal directly
        // Build ChatOptions from AGUI tools (only convert tools, not messages!)
        var chatOptions = _aguiConverter.ConvertToExtensionsAIChatOptions(
            aguiInput,
            Config?.Provider?.DefaultChatOptions,
            Config?.PluginScoping?.ScopeFrontendTools ?? false,
            Config?.PluginScoping?.MaxFunctionNamesInDescription ?? 10);

        chatOptions ??= new ChatOptions();
        chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        chatOptions.AdditionalProperties["ConversationId"] = aguiInput.ThreadId;
        chatOptions.AdditionalProperties["RunId"] = aguiInput.RunId;

        // Call RunAgenticLoopInternal directly with Extensions.AI format (no message conversion!)
        var turnHistory = new List<ChatMessage>();
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var internalStream = RunAgenticLoopInternal(
            thread.Messages,  // ✅ ChatMessage[] - NO CONVERSION!
            chatOptions,
            documentPaths: null,
            turnHistory,
            historyCompletionSource,
            reductionCompletionSource,
            cancellationToken);

        // Adapt internal events to AGUI protocol
        var aguiStream = EventStreamAdapter.ToAGUI(internalStream, aguiInput.ThreadId, aguiInput.RunId, cancellationToken);

        // Wrap with error handling
        var errorHandledStream = EventStreamAdapter.WithErrorHandling(aguiStream, historyCompletionSource, cancellationToken);

        // Stream ALL BaseEvent events (no filtering for AGUI protocol)
        await foreach (var evt in errorHandledStream.WithCancellation(cancellationToken))
        {
            yield return evt;
        }

        // Wait for final history and check for reduction
        var finalHistory = await historyCompletionSource.Task;
        var reductionMetadata = await reductionCompletionSource.Task;

        // Apply post-invoke filters (for memory extraction, learning, etc.)
        try
        {
            await _messageProcessor.ApplyPostInvokeFiltersAsync(
                thread.Messages.ToList(),
                finalHistory.ToList(),
                null, // no exception
                chatOptions,
                Config?.Name ?? "Agent",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Log but don't fail - post-processing is best-effort
        }

        // Apply message turn filters after the turn completes
        if (_messageTurnFilters.Any())
        {
            try
            {
                var userMessage = thread.Messages.LastOrDefault(m => m.Role == ChatRole.User) ?? new ChatMessage(ChatRole.User, string.Empty);
                await ApplyMessageTurnFilters(userMessage, finalHistory, chatOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Log but don't fail the turn if filters fail
            }
        }

        // Apply reduction BEFORE adding new messages
        if (reductionMetadata != null && reductionMetadata.SummaryMessage != null)
        {
            await thread.ApplyReductionAsync(reductionMetadata.SummaryMessage, reductionMetadata.MessagesRemovedCount, cancellationToken);
        }

        // Update thread
        foreach (var msg in finalHistory)
        {
            if (!thread.Messages.Contains(msg))
            {
                await thread.AddMessageAsync(msg, cancellationToken);
            }
        }

        var duration = DateTimeOffset.UtcNow - startTime;
        activity?.SetTag("agent.duration_ms", duration.TotalMilliseconds);
        activity?.SetTag("agent.success", true);
    }

    #endregion

    #region AIAgent Abstract Method Implementations

    /// <summary>
    /// Creates a new conversation thread compatible with this agent.
    /// </summary>
    /// <returns>A new ConversationThread instance.</returns>
    public override AgentThread GetNewThread()
    {
        return new ConversationThread();
    }

    /// <summary>
    /// Deserializes a conversation thread from its JSON representation.
    /// </summary>
    /// <param name="serializedThread">The JSON element containing the serialized thread state.</param>
    /// <param name="jsonSerializerOptions">Optional JSON serialization options.</param>
    /// <returns>A restored ConversationThread instance.</returns>
    public override AgentThread DeserializeThread(System.Text.Json.JsonElement serializedThread, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<ConversationThreadSnapshot>(serializedThread, jsonSerializerOptions);
        if (snapshot == null)
        {
            throw new System.Text.Json.JsonException("Failed to deserialize ConversationThreadSnapshot from JSON.");
        }
        return ConversationThread.Deserialize(snapshot);
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
    private readonly Agent _agent; // NEW: Reference to agent for filter event coordination
    private readonly ScopedFilterManager? _scopedFilterManager;
    private readonly PermissionManager _permissionManager;
    private readonly IReadOnlyList<IAiFunctionFilter> _aiFunctionFilters;
    private readonly int _maxFunctionCalls;
    private readonly ErrorHandlingConfig? _errorHandlingConfig;
    private readonly IList<AITool>? _serverConfiguredTools;
    private readonly AgenticLoopConfig? _agenticLoopConfig;

    public FunctionCallProcessor(
        Agent agent, // NEW: Added parameter
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


    /// <summary>
    /// Processes the function calls and returns the messages to add to the conversation.
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

        // Build function map per execution (Microsoft pattern for thread-safety)
        // This avoids shared mutable state and stale cache issues
        // Merge server-configured tools with request tools (request tools take precedence)
        var functionMap = BuildFunctionMap(_serverConfiguredTools, options?.Tools);

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
                Function = FindFunctionInMap(functionCall.Name, functionMap),
                RunContext = agentRunContext,
                AgentName = agentName,
                // NEW: Point to Agent's shared channel for event emission
                OutboundEvents = _agent.FilterEventWriter,
                Agent = _agent
            };

            // Pass the CallId through metadata for tracking
            context.Metadata["CallId"] = functionCall.CallId;

            // Check if function is unknown and TerminateOnUnknownCalls is enabled
            if (context.Function == null && _agenticLoopConfig?.TerminateOnUnknownCalls == true)
            {
                // Terminate the loop - don't process this or any remaining functions
                // The function call will be returned to the caller for handling (e.g., multi-agent handoff)
                context.IsTerminated = true;
                agentRunContext.IsTerminated = true;
                agentRunContext.TerminationReason = $"Unknown function requested: '{functionCall.Name}'";
                
                // Don't add any result message - let the caller handle the unknown function
                break;
            }

            // Check permissions using PermissionManager BEFORE building execution pipeline
            var permissionResult = await _permissionManager.CheckPermissionAsync(
                functionCall,
                context.Function,
                agentRunContext,
                agentName,
                _agent,
                cancellationToken).ConfigureAwait(false);

            // If permission denied, record the denial and skip execution
            if (!permissionResult.IsApproved)
            {
                context.Result = permissionResult.DenialReason ?? "Permission denied";
                context.IsTerminated = true;
                
                // Mark function as completed (even though denied)
                agentRunContext.CompleteFunction(functionCall.Name);

                var denialResult = new FunctionResultContent(functionCall.CallId, context.Result);
                var denialMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { denialResult });
                resultMessages.Add(denialMessage);
                continue; // Skip to next function call
            }

            // Permission approved - proceed with execution pipeline
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

            // Get scoped filters for this function
            var scopedFilters = _scopedFilterManager?.GetApplicableFilters(functionCall.Name)
                                ?? Enumerable.Empty<IAiFunctionFilter>();

            // Combine scoped filters with general AI function filters
            var allStandardFilters = _aiFunctionFilters.Concat(scopedFilters);

            // Build and execute the filter pipeline using FilterChain
            var pipeline = FilterChain.BuildAiFunctionPipeline(allStandardFilters, finalInvoke);

            // Execute pipeline SYNCHRONOUSLY (no Task.Run!)
            // Events flow directly to shared channel, drained by background task
            try
            {
                await pipeline(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Emit error event before handling
                context.OutboundEvents?.TryWrite(new InternalFilterErrorEvent(
                    "FilterPipeline",
                    $"Error in filter pipeline: {ex.Message}",
                    ex));

                // Mark context as terminated
                context.IsTerminated = true;
                context.Result = $"Error executing function '{functionCall.Name}': {ex.Message}";
            }

            // Mark function as completed in run context (even if failed - prevents infinite loops)
            agentRunContext.CompleteFunction(functionCall.Name);

            var functionResult = new FunctionResultContent(functionCall.CallId, context.Result);
            var functionMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { functionResult });
            resultMessages.Add(functionMessage);
        }

        return resultMessages;
    }

    /// <summary>
    /// Executes a function with provider-aware retry logic and timeout enforcement.
    /// Delegates to FunctionRetryExecutor for consistent retry behavior.
    /// </summary>
    private async Task ExecuteWithRetryAsync(AiFunctionContext context, CancellationToken cancellationToken)
    {
        if (context.Function is null)
        {
            context.Result = $"Function '{context.ToolCallRequest.FunctionName}' not found.";
            return;
        }

        // Set AsyncLocal function invocation context for ambient access
        // Store the full AiFunctionContext so plugins can access Emit() and WaitForResponseAsync()
        // for human-in-the-loop interactions (permissions, clarifications, etc.)
        Agent.CurrentFunctionContext = context;

        var retryExecutor = new FunctionRetryExecutor(_errorHandlingConfig);
        
        try
        {
            context.Result = await retryExecutor.ExecuteWithRetryAsync(
                context.Function,
                context.ToolCallRequest.Arguments,
                context.ToolCallRequest.FunctionName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // Function-specific timeout
            context.Result = FormatErrorForLLM(ex, context.ToolCallRequest.FunctionName);
        }
        catch (Exception ex)
        {
            // All retries exhausted or non-retryable error
            context.Result = FormatErrorForLLM(ex, context.ToolCallRequest.FunctionName);
        }
        finally
        {
            // Always clear the context after function completes
            Agent.CurrentFunctionContext = null;
        }
    }

    /// <summary>
    /// Formats an exception for inclusion in function results sent to the LLM.
    /// Respects IncludeDetailedErrorsInChat security setting.
    /// </summary>
    private string FormatErrorForLLM(Exception exception, string functionName)
    {
        if (_errorHandlingConfig?.IncludeDetailedErrorsInChat == true)
        {
            // Include full exception details (potential security risk)
            return $"Error invoking function '{functionName}': {exception.Message}";
        }
        else
        {
            // Generic error message (safe for LLM consumption)
            // Full exception still available via FunctionResultContent.Exception
            return $"Error: Function '{functionName}' failed.";
        }
    }

    /// <summary>
    /// Builds a dictionary map of function name to AIFunction from multiple tool sources.
    /// Built per-execution to avoid shared mutable state and cache staleness.
    /// </summary>
    /// <param name="serverConfiguredTools">Tools configured server-side (lower priority)</param>
    /// <param name="requestTools">Tools provided in the request (higher priority, can override)</param>
    /// <returns>Dictionary mapping function names to AIFunction instances, or null if no functions</returns>
    private static Dictionary<string, AIFunction>? BuildFunctionMap(
        IList<AITool>? serverConfiguredTools,
        IList<AITool>? requestTools)
    {
        if (serverConfiguredTools is not { Count: > 0 } && 
            requestTools is not { Count: > 0 })
        {
            return null;
        }

        var map = new Dictionary<string, AIFunction>(StringComparer.Ordinal);
        
        // Add server-configured tools first (lower priority)
        if (serverConfiguredTools is { Count: > 0 })
        {
            for (int i = 0; i < serverConfiguredTools.Count; i++)
            {
                if (serverConfiguredTools[i] is AIFunction af)
                {
                    map[af.Name] = af;
                }
            }
        }
        
        // Add request tools second (higher priority, can override server-configured)
        if (requestTools is { Count: > 0 })
        {
            for (int i = 0; i < requestTools.Count; i++)
            {
                if (requestTools[i] is AIFunction af)
                {
                    map[af.Name] = af; // Overwrites if exists
                }
            }
        }
        
        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// Finds a function by name in the pre-built function map.
    /// O(1) dictionary lookup.
    /// </summary>
    /// <param name="functionName">The name of the function to find</param>
    /// <param name="functionMap">Pre-built map of function names to AIFunction instances</param>
    /// <returns>The AIFunction if found, null otherwise</returns>
    private static AIFunction? FindFunctionInMap(string functionName, Dictionary<string, AIFunction>? functionMap)
    {
        if (functionMap == null)
        {
            return null;
        }
        
        functionMap.TryGetValue(functionName, out var function);
        return function;
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

        // Build and execute the prompt filter pipeline using FilterChain
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> finalAction = ctx => Task.FromResult(ctx.Messages);
        var pipeline = FilterChain.BuildPromptPipeline(_promptFilters, finalAction);
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
    /// The ConversationId from the most recent response (if the service manages history server-side).
    /// Null if the service doesn't track conversation history.
    /// </summary>
    public string? LastResponseConversationId { get; private set; }

    /// <summary>
    /// Initializes a new instance of AgentTurn
    /// </summary>
    /// <param name="baseClient">The underlying chat client to use for LLM calls</param>
    public AgentTurn(IChatClient baseClient)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
    }

    /// <summary>
    /// Runs a single turn with the LLM and yields ChatResponseUpdates representing the response.
    /// Captures ConversationId from the response for server-side history tracking optimization.
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
        // Reset ConversationId at start of new turn
        LastResponseConversationId = null;

        await foreach (var update in RunAsyncCore(messages, options, cancellationToken))
        {
            // Capture ConversationId from first update that has one
            if (LastResponseConversationId == null && update.ConversationId != null)
            {
                LastResponseConversationId = update.ConversationId;
            }

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
    private readonly Agent _agent;
    private readonly FunctionCallProcessor _functionCallProcessor;
    private readonly PermissionManager _permissionManager;
    private readonly AgentConfig? _config;
    private readonly HPD_Agent.Scoping.UnifiedScopingManager _scopingManager;

    /// <summary>
    /// Initializes a new instance of ToolScheduler
    /// </summary>
    /// <param name="agent">The agent instance for event emission</param>
    /// <param name="functionCallProcessor">The function call processor to use for tool execution</param>
    /// <param name="permissionManager">The permission manager for authorization checks</param>
    /// <param name="config">Agent configuration for execution settings</param>
    /// <param name="pluginScopingManager">Plugin scoping manager for container detection</param>
    /// <param name="skillScopingManager">Optional skill scoping manager for skill container detection</param>
    public ToolScheduler(
        Agent agent,
        FunctionCallProcessor functionCallProcessor,
        PermissionManager permissionManager,
        AgentConfig? config,
        HPD_Agent.Scoping.UnifiedScopingManager scopingManager)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _functionCallProcessor = functionCallProcessor ?? throw new ArgumentNullException(nameof(functionCallProcessor));
        _permissionManager = permissionManager ?? throw new ArgumentNullException(nameof(permissionManager));
        _config = config;
        _scopingManager = scopingManager ?? throw new ArgumentNullException(nameof(scopingManager));
    }

    /// <summary>
    /// Executes the requested tools in parallel and returns the tool response message
    /// </summary>
    /// <param name="currentHistory">The current conversation history</param>
    /// <param name="toolRequests">The tool call requests to execute</param>
    /// <param name="options">Optional chat options containing tool definitions</param>
    /// <param name="agentRunContext">Agent run context for cross-call tracking</param>
    /// <param name="agentName">The name of the agent executing the tools</param>
    /// <param name="expandedPlugins">Set of expanded plugin names (message-turn scoped)</param>
    /// <param name="expandedSkills">Set of expanded skill names (message-turn scoped)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A chat message containing the tool execution results</returns>
    public async Task<ChatMessage> ExecuteToolsAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentRunContext agentRunContext,
        string? agentName,
        HashSet<string> expandedPlugins,
        HashSet<string> expandedSkills,
        CancellationToken cancellationToken)
    {
        // For single tool calls, use sequential execution (no parallelization overhead)
        if (toolRequests.Count <= 1)
        {
            return await ExecuteSequentiallyAsync(currentHistory, toolRequests, options, agentRunContext, agentName, expandedPlugins, expandedSkills, cancellationToken).ConfigureAwait(false);
        }

        // For multiple tool calls, execute in parallel for better performance
        return await ExecuteInParallelAsync(currentHistory, toolRequests, options, agentRunContext, agentName, expandedPlugins, expandedSkills, cancellationToken).ConfigureAwait(false);
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
        HashSet<string> expandedSkills,
        CancellationToken cancellationToken)
    {
        var allContents = new List<AIContent>();

        // Track which tool requests are containers for expansion after invocation
        var pluginContainerExpansions = new Dictionary<string, string>(); // callId -> pluginName
        var skillContainerExpansions = new Dictionary<string, string>(); // callId -> skillName

        foreach (var toolRequest in toolRequests)
        {
            // Find the function in the options to check if it's a container
            // PERFORMANCE: Use optimized helper instead of LINQ (O(n) direct vs O(n) with overhead)
            var function = FindFunctionInTools(toolRequest.Name, options?.Tools);

            if (function != null)
            {
                // Check if it's a plugin container
                if (function.AdditionalProperties?.TryGetValue("IsContainer", out var isCont) == true && isCont is bool isC && isC)
                {
                    var pluginName = function.AdditionalProperties
                        ?.TryGetValue("PluginName", out var value) == true && value is string pn
                        ? pn
                        : toolRequest.Name;

                    pluginContainerExpansions[toolRequest.CallId] = pluginName;
                }
                // Check if it's a skill container
                // Skill container expansion tracking removed - handled by UnifiedScopingManager
                // else if (_scopingManager.IsSkillContainer(function))
                // {
                //     var skillName = function.Name;
                //     skillContainerExpansions[toolRequest.CallId] = skillName;
                // }
            }
        }

        // Process ALL tools (containers + regular) through the existing processor
        var resultMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(
            currentHistory, options, toolRequests, agentRunContext, agentName, cancellationToken).ConfigureAwait(false);

        // Combine results and mark containers as expanded
        foreach (var message in resultMessages)
        {
            foreach (var content in message.Contents)
            {
                allContents.Add(content);

                // If this result is for a plugin container, mark the plugin as expanded
                if (content is FunctionResultContent functionResult)
                {
                    if (pluginContainerExpansions.TryGetValue(functionResult.CallId, out var pluginName))
                    {
                        expandedPlugins.Add(pluginName);
                    }
                    // If this result is for a skill container, mark the skill as expanded
                    else if (skillContainerExpansions.TryGetValue(functionResult.CallId, out var skillName))
                    {
                        expandedSkills.Add(skillName);
                    }
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
        HashSet<string> expandedSkills,
        CancellationToken cancellationToken)
    {
        // THREE-PHASE EXECUTION (inspired by Gemini CLI's CoreToolScheduler with plugin & skill scoping)
        // Phase 0: Separate container expansions from regular tools
        // Phase 1: Check permissions for ALL tools SEQUENTIALLY (prevents race conditions)
        // Phase 2: Execute ALL approved tools in PARALLEL (with optional throttling)

        var allContents = new List<AIContent>();

        // PHASE 0: Identify containers and track them for expansion after invocation
        var pluginContainerExpansions = new Dictionary<string, string>(); // callId -> pluginName
        var skillContainerExpansions = new Dictionary<string, string>(); // callId -> skillName

        foreach (var toolRequest in toolRequests)
        {
            // Find the function in the options to check if it's a container
            // PERFORMANCE: Use optimized helper instead of LINQ (O(n) direct vs O(n) with overhead)
            var function = FindFunctionInTools(toolRequest.Name, options?.Tools);

            if (function != null)
            {
                // Check if it's a plugin container
                if (function.AdditionalProperties?.TryGetValue("IsContainer", out var isCont) == true && isCont is bool isC && isC)
                {
                    var pluginName = function.AdditionalProperties
                        ?.TryGetValue("PluginName", out var value) == true && value is string pn
                        ? pn
                        : toolRequest.Name;

                    pluginContainerExpansions[toolRequest.CallId] = pluginName;
                }
                // Check if it's a skill container
                // Skill container expansion tracking removed - handled by UnifiedScopingManager
                // else if (_scopingManager.IsSkillContainer(function))
                // {
                //     var skillName = function.Name;
                //     skillContainerExpansions[toolRequest.CallId] = skillName;
                // }
            }
        }

        // PHASE 1: Permission checking (sequential to prevent race conditions)
        var permissionResult = await _permissionManager.CheckPermissionsAsync(
            toolRequests, options, agentRunContext, agentName, _agent, cancellationToken).ConfigureAwait(false);

        var approvedTools = permissionResult.Approved;
        var deniedTools = permissionResult.Denied;

        // PHASE 2: Execute approved tools in parallel with optional throttling
        // SAFETY: Default to bounded parallelism (following Microsoft's conservative approach)
        // Unlike their boolean flag, we use a numeric limit for finer control
        var maxParallel = _config?.AgenticLoop?.MaxParallelFunctions ?? Environment.ProcessorCount * 4;
        using var semaphore = new SemaphoreSlim(maxParallel);

        var executionTasks = approvedTools.Select(async toolRequest =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Execute each approved tool call through the processor
                var singleToolList = new List<FunctionCallContent> { toolRequest };
                var resultMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(
                    currentHistory, options, singleToolList, agentRunContext, agentName, cancellationToken).ConfigureAwait(false);

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

        // Aggregate results and handle errors
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

                // If this was a plugin container, mark the plugin as expanded
                if (pluginContainerExpansions.TryGetValue(result.ToolRequest.CallId, out var pluginName))
                {
                    expandedPlugins.Add(pluginName);
                }
                // If this was a skill container, mark the skill as expanded
                else if (skillContainerExpansions.TryGetValue(result.ToolRequest.CallId, out var skillName))
                {
                    expandedSkills.Add(skillName);
                }
            }
            else if (result.Error != null)
            {
                errors.Add(result.Error);
            }
        }

        // Add denied tool results with proper error messages
        foreach (var deniedEntry in deniedTools)
        {
            var functionResult = new FunctionResultContent(
                deniedEntry.Tool.CallId,
                $"Execution of '{deniedEntry.Tool.Name}' was denied: {deniedEntry.Reason}"
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

    /// <summary>
    /// Fast O(1) function lookup helper for ToolScheduler.
    /// Uses manual iteration
    /// PERFORMANCE: Replaces OfType + FirstOrDefault LINQ chain with direct iteration.
    /// </summary>
    /// <param name="functionName">The name of the function to find</param>
    /// <param name="tools">The list of available tools</param>
    /// <returns>The AIFunction if found, null otherwise</returns>
    private static AIFunction? FindFunctionInTools(string functionName, IList<AITool>? tools)
    {
        if (tools is not { Count: > 0 }) return null;
        
        for (int i = 0; i < tools.Count; i++)
        {
            if (tools[i] is AIFunction af && af.Name == functionName)
                return af;
        }
        return null;
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

#region Permission Management

/// <summary>
/// Strongly-typed permission result (replaces bool return)
/// </summary>
public record PermissionResult(
    bool IsApproved, 
    string? DenialReason = null,
    bool IsAutoApproved = false)
{
    public static PermissionResult Approved() => new(true);
    public static PermissionResult AutoApproved() => new(true, IsAutoApproved: true);
    public static PermissionResult Denied(string reason) => new(false, reason);
}

/// <summary>
/// Result of batch permission checking
/// </summary>
public record PermissionBatchResult(
    List<FunctionCallContent> Approved,
    List<(FunctionCallContent Tool, string Reason)> Denied);

/// <summary>
/// Centralized manager for all permission-related logic.
/// Eliminates duplication and provides single source of truth for permission decisions.
/// </summary>
public class PermissionManager
{
    private readonly IReadOnlyList<IPermissionFilter> _permissionFilters;
    
    public PermissionManager(IReadOnlyList<IPermissionFilter>? permissionFilters)
    {
        _permissionFilters = permissionFilters ?? Array.Empty<IPermissionFilter>();
    }
    
    /// <summary>
    /// Checks if a function requires permission (metadata check only)
    /// </summary>
    public bool RequiresPermission(AIFunction function)
    {
        return function is HPDAIFunctionFactory.HPDAIFunction hpdFunction 
            && hpdFunction.HPDOptions.RequiresPermission;
    }
    
    /// <summary>
    /// Executes permission check for a single function call
    /// </summary>
    public async Task<PermissionResult> CheckPermissionAsync(
        FunctionCallContent functionCall,
        AIFunction? function,
        AgentRunContext agentRunContext,
        string? agentName,
        Agent agent,
        CancellationToken cancellationToken)
    {
        // Null checks
        if (string.IsNullOrEmpty(functionCall.Name))
            return PermissionResult.Denied("Function name is empty");
            
        if (function == null)
            return PermissionResult.Denied($"Function '{functionCall.Name}' not found");
        
        // Check if permission is required
        if (!RequiresPermission(function))
            return PermissionResult.AutoApproved();
        
        // If no permission filters are configured, auto-deny functions requiring permission
        // This is a safety measure - functions with [RequiresPermission] should not auto-approve
        if (_permissionFilters.Count == 0)
            return PermissionResult.Denied("No permission filter configured for function requiring permission");
        
        // Build and execute permission filter pipeline
        var context = new AiFunctionContext(new ToolCallRequest
        {
            FunctionName = functionCall.Name,
            Arguments = functionCall.Arguments ?? new Dictionary<string, object?>()
        })
        {
            Function = function,
            RunContext = agentRunContext,
            AgentName = agentName,
            // ✅ FIX: Set OutboundEvents and Agent for permission event emission
            OutboundEvents = agent.FilterEventWriter,
            Agent = agent
        };

        context.Metadata["CallId"] = functionCall.CallId;
        
        await ExecutePermissionPipeline(context, cancellationToken).ConfigureAwait(false);
        
        // If filter terminated the context, permission was denied
        if (context.IsTerminated)
        {
            var denialReason = context.Result?.ToString() ?? "Permission denied by filter";
            return PermissionResult.Denied(denialReason);
        }
        
        // If filter did not terminate, permission was approved
        return PermissionResult.Approved();
    }
    
    /// <summary>
    /// Batch permission check for multiple tools (optimized for parallel execution)
    /// FIX #14: Build O(1) function map to avoid O(n×m) LINQ scans
    /// Following Microsoft's CreateToolsMap pattern
    /// </summary>
    public async Task<PermissionBatchResult> CheckPermissionsAsync(
        IEnumerable<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentRunContext agentRunContext,
        string? agentName,
        Agent agent,
        CancellationToken cancellationToken)
    {
        var approved = new List<FunctionCallContent>();
        var denied = new List<(FunctionCallContent Tool, string Reason)>();
        
        // Build function map ONCE per batch (not per request)
        var functionMap = BuildFunctionMapFromTools(options?.Tools);
        
        foreach (var toolRequest in toolRequests)
        {
            // O(1) lookup instead of O(n) LINQ scan
            AIFunction? function = null;
            functionMap?.TryGetValue(toolRequest.Name ?? string.Empty, out function);
            
            var result = await CheckPermissionAsync(
                toolRequest, function, agentRunContext, agentName, agent, cancellationToken).ConfigureAwait(false);
            
            if (result.IsApproved)
                approved.Add(toolRequest);
            else
                denied.Add((toolRequest, result.DenialReason ?? "Unknown"));
        }
        
        return new PermissionBatchResult(approved, denied);
    }
    
    /// <summary>
    /// Builds a function name-to-AIFunction map from tools list.
    /// Replicates Microsoft's CreateToolsMap pattern for O(1) lookups.
    /// </summary>
    private static Dictionary<string, AIFunction>? BuildFunctionMapFromTools(IList<AITool>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return null;
        }

        var map = new Dictionary<string, AIFunction>(tools.Count, StringComparer.Ordinal);
        
        for (int i = 0; i < tools.Count; i++)
        {
            if (tools[i] is AIFunction af)
            {
                map[af.Name] = af;
            }
        }
        
        return map.Count > 0 ? map : null;
    }
    
    /// <summary>
    /// Executes the permission filter pipeline (single responsibility)
    /// </summary>
    private async Task ExecutePermissionPipeline(
        AiFunctionContext context, 
        CancellationToken cancellationToken)
    {
        // Build and execute the permission filter pipeline using FilterChain
        Func<AiFunctionContext, Task> finalAction = _ => Task.CompletedTask;
        var pipeline = FilterChain.BuildPermissionPipeline(_permissionFilters, finalAction);
        await pipeline(context).ConfigureAwait(false);
    }
}

#endregion

#region Event Stream Adapters

/// <summary>
/// Adapts protocol-agnostic internal agent events to specific protocol formats.
/// Eliminates duplication of event adaptation logic across the Agent codebase.
/// 
/// Supported protocols:
/// - AGUI: Full event streaming with Run/Step/Tool lifecycle
/// - IChatClient: Simplified content-only streaming (Microsoft.Extensions.AI)
/// - Error handling wrapper for all protocols
/// </summary>
internal static class EventStreamAdapter
{
    /// <summary>
    /// Adapts internal events to AGUI protocol format.
    /// Maps internal events to AGUI lifecycle events (Run, Step, Tool, Content).
    /// </summary>
    public static async IAsyncEnumerable<BaseEvent> ToAGUI(
        IAsyncEnumerable<InternalAgentEvent> internalStream,
        string threadId,
        string runId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var internalEvent in internalStream.WithCancellation(cancellationToken))
        {
            BaseEvent? aguiEvent = internalEvent switch
            {
                // MESSAGE TURN → RUN events
                InternalMessageTurnStartedEvent => EventSerialization.CreateRunStarted(threadId, runId),
                InternalMessageTurnFinishedEvent => EventSerialization.CreateRunFinished(threadId, runId),
                InternalMessageTurnErrorEvent e => EventSerialization.CreateRunError(e.Message),

                // AGENT TURN → STEP events
                InternalAgentTurnStartedEvent e => EventSerialization.CreateStepStarted(
                    stepId: $"step_{e.Iteration}",
                    stepName: $"Iteration {e.Iteration}",
                    description: null),
                InternalAgentTurnFinishedEvent e => EventSerialization.CreateStepFinished(
                    stepId: $"step_{e.Iteration}",
                    stepName: $"Iteration {e.Iteration}",
                    result: null),

                // TEXT CONTENT events
                InternalTextMessageStartEvent e => EventSerialization.CreateTextMessageStart(e.MessageId, e.Role),
                InternalTextDeltaEvent e => EventSerialization.CreateTextMessageContent(e.MessageId, e.Text),
                InternalTextMessageEndEvent e => EventSerialization.CreateTextMessageEnd(e.MessageId),

                // REASONING events
                InternalReasoningStartEvent e => EventSerialization.CreateReasoningStart(e.MessageId),
                InternalReasoningMessageStartEvent e => EventSerialization.CreateReasoningMessageStart(e.MessageId, e.Role),
                InternalReasoningDeltaEvent e => EventSerialization.CreateReasoningMessageContent(e.MessageId, e.Text),
                InternalReasoningMessageEndEvent e => EventSerialization.CreateReasoningMessageEnd(e.MessageId),
                InternalReasoningEndEvent e => EventSerialization.CreateReasoningEnd(e.MessageId),

                // TOOL events
                InternalToolCallStartEvent e => EventSerialization.CreateToolCallStart(e.CallId, e.Name, e.MessageId),
                InternalToolCallArgsEvent e => EventSerialization.CreateToolCallArgs(e.CallId, e.ArgsJson),
                InternalToolCallEndEvent e => EventSerialization.CreateToolCallEnd(e.CallId),
                InternalToolCallResultEvent e => EventSerialization.CreateToolCallResult(e.CallId, e.Result),

                _ => null // Unknown event type
            };

            if (aguiEvent != null)
            {
                yield return aguiEvent;
            }
        }
    }


    /// <summary>
    /// Adapts internal events to Microsoft.Agents.AI protocol (ExtendedAgentRunResponseUpdate).
    /// Converts internal events to the Agents.AI protocol format, preserving all HPD-specific event data.
    /// Returns ExtendedAgentRunResponseUpdate which includes turn boundaries, permissions, filters, etc.
    /// </summary>
    public static async IAsyncEnumerable<ExtendedAgentRunResponseUpdate> ToAgentsAI(
        IAsyncEnumerable<InternalAgentEvent> internalStream,
        string threadId,
        string agentName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var internalEvent in internalStream.WithCancellation(cancellationToken))
        {
            ExtendedAgentRunResponseUpdate? update = internalEvent switch
            {
                // Text content
                InternalTextDeltaEvent text => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(text.Text)],
                    CreatedAt = DateTimeOffset.UtcNow,
                    MessageId = text.MessageId,
                    OriginalInternalEvent = text
                },

                // Reasoning content (for o1, DeepSeek-R1, etc.)
                InternalReasoningDeltaEvent reasoning => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    Role = ChatRole.Assistant,
                    Contents = [new TextReasoningContent(reasoning.Text)],
                    CreatedAt = DateTimeOffset.UtcNow,
                    MessageId = reasoning.MessageId,
                    OriginalInternalEvent = reasoning
                },

                // Tool call start
                InternalToolCallStartEvent toolCall => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent(toolCall.CallId, toolCall.Name)],
                    CreatedAt = DateTimeOffset.UtcNow,
                    MessageId = toolCall.MessageId,
                    OriginalInternalEvent = toolCall
                },

                // Tool call arguments
                InternalToolCallArgsEvent toolArgs => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ToolData = new ToolCallData
                    {
                        CallId = toolArgs.CallId,
                        ArgsJson = toolArgs.ArgsJson
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalToolCallArgsEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = toolArgs
                },

                // Tool call end
                InternalToolCallEndEvent toolEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ToolData = new ToolCallData
                    {
                        CallId = toolEnd.CallId,
                        IsToolEnd = true
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalToolCallEndEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = toolEnd
                },

                // Tool call result
                InternalToolCallResultEvent toolResult => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    Role = ChatRole.Tool,
                    Contents = [new FunctionResultContent(toolResult.CallId, toolResult.Result)],
                    CreatedAt = DateTimeOffset.UtcNow,
                    OriginalInternalEvent = toolResult
                },

                // Message turn started
                InternalMessageTurnStartedEvent msgTurnStart => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    TurnBoundary = new TurnBoundaryData
                    {
                        Type = TurnBoundaryType.MessageTurnStart,
                        MessageTurnId = msgTurnStart.MessageTurnId,
                        ConversationId = msgTurnStart.ConversationId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalMessageTurnStartedEvent),
                        MessageTurnId = msgTurnStart.MessageTurnId,
                        ConversationId = msgTurnStart.ConversationId,
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = msgTurnStart
                },

                // Message turn finished
                InternalMessageTurnFinishedEvent msgTurnEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    TurnBoundary = new TurnBoundaryData
                    {
                        Type = TurnBoundaryType.MessageTurnEnd,
                        MessageTurnId = msgTurnEnd.MessageTurnId,
                        ConversationId = msgTurnEnd.ConversationId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalMessageTurnFinishedEvent),
                        MessageTurnId = msgTurnEnd.MessageTurnId,
                        ConversationId = msgTurnEnd.ConversationId,
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = msgTurnEnd
                },

                // Message turn error
                InternalMessageTurnErrorEvent msgTurnError => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ErrorData = new ErrorEventData
                    {
                        Message = msgTurnError.Message,
                        Exception = msgTurnError.Exception,
                        ExceptionType = msgTurnError.Exception?.GetType().Name,
                        StackTrace = msgTurnError.Exception?.StackTrace
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalMessageTurnErrorEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = msgTurnError
                },

                // Agent turn started
                InternalAgentTurnStartedEvent agentTurnStart => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    TurnBoundary = new TurnBoundaryData
                    {
                        Type = TurnBoundaryType.AgentTurnStart,
                        Iteration = agentTurnStart.Iteration
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalAgentTurnStartedEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = agentTurnStart
                },

                // Agent turn finished
                InternalAgentTurnFinishedEvent agentTurnEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    TurnBoundary = new TurnBoundaryData
                    {
                        Type = TurnBoundaryType.AgentTurnEnd,
                        Iteration = agentTurnEnd.Iteration
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalAgentTurnFinishedEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = agentTurnEnd
                },

                // Text message start
                InternalTextMessageStartEvent textStart => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.TextMessageStart,
                        MessageId = textStart.MessageId,
                        Role = textStart.Role
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalTextMessageStartEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = textStart
                },

                // Text message end
                InternalTextMessageEndEvent textEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.TextMessageEnd,
                        MessageId = textEnd.MessageId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalTextMessageEndEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = textEnd
                },

                // Reasoning start
                InternalReasoningStartEvent reasoningStart => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.ReasoningStart,
                        MessageId = reasoningStart.MessageId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalReasoningStartEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = reasoningStart
                },

                // Reasoning message start
                InternalReasoningMessageStartEvent reasoningMsgStart => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.ReasoningMessageStart,
                        MessageId = reasoningMsgStart.MessageId,
                        Role = reasoningMsgStart.Role
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalReasoningMessageStartEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = reasoningMsgStart
                },

                // Reasoning message end
                InternalReasoningMessageEndEvent reasoningMsgEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.ReasoningMessageEnd,
                        MessageId = reasoningMsgEnd.MessageId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalReasoningMessageEndEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = reasoningMsgEnd
                },

                // Reasoning end
                InternalReasoningEndEvent reasoningEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.ReasoningEnd,
                        MessageId = reasoningEnd.MessageId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalReasoningEndEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = reasoningEnd
                },

                // Permission request
                InternalPermissionRequestEvent permReq => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    PermissionData = new PermissionEventData
                    {
                        Type = PermissionEventType.Request,
                        PermissionId = permReq.PermissionId,
                        FunctionName = permReq.FunctionName,
                        Description = permReq.Description,
                        CallId = permReq.CallId,
                        Arguments = permReq.Arguments
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalPermissionRequestEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = permReq
                },

                // Permission response
                InternalPermissionResponseEvent permResp => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    PermissionData = new PermissionEventData
                    {
                        Type = PermissionEventType.Response,
                        PermissionId = permResp.PermissionId,
                        Approved = permResp.Approved,
                        Reason = permResp.Reason,
                        Choice = permResp.Choice
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalPermissionResponseEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = permResp
                },

                // Permission approved
                InternalPermissionApprovedEvent permApproved => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    PermissionData = new PermissionEventData
                    {
                        Type = PermissionEventType.Approved,
                        PermissionId = permApproved.PermissionId,
                        Approved = true
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalPermissionApprovedEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = permApproved
                },

                // Permission denied
                InternalPermissionDeniedEvent permDenied => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    PermissionData = new PermissionEventData
                    {
                        Type = PermissionEventType.Denied,
                        PermissionId = permDenied.PermissionId,
                        Approved = false,
                        Reason = permDenied.Reason
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalPermissionDeniedEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = permDenied
                },

                // Continuation request
                InternalContinuationRequestEvent contReq => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ContinuationData = new ContinuationEventData
                    {
                        Type = ContinuationEventType.Request,
                        ContinuationId = contReq.ContinuationId,
                        CurrentIteration = contReq.CurrentIteration,
                        MaxIterations = contReq.MaxIterations
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalContinuationRequestEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = contReq
                },

                // Continuation response
                InternalContinuationResponseEvent contResp => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ContinuationData = new ContinuationEventData
                    {
                        Type = ContinuationEventType.Response,
                        ContinuationId = contResp.ContinuationId,
                        Approved = contResp.Approved,
                        ExtensionAmount = contResp.ExtensionAmount
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalContinuationResponseEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = contResp
                },

                // Clarification request
                InternalClarificationRequestEvent clarReq => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ClarificationData = new ClarificationEventData
                    {
                        Type = ClarificationEventType.Request,
                        RequestId = clarReq.RequestId,
                        AgentName = clarReq.AgentName,
                        Question = clarReq.Question,
                        Options = clarReq.Options
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalClarificationRequestEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = clarReq
                },

                // Clarification response
                InternalClarificationResponseEvent clarResp => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ClarificationData = new ClarificationEventData
                    {
                        Type = ClarificationEventType.Response,
                        RequestId = clarResp.RequestId,
                        Question = clarResp.Question,
                        Answer = clarResp.Answer
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalClarificationResponseEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = clarResp
                },

                // Filter progress
                InternalFilterProgressEvent filterProgress => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    FilterData = new FilterEventData
                    {
                        Type = FilterEventType.Progress,
                        SourceName = filterProgress.SourceName,
                        ProgressMessage = filterProgress.Message,
                        PercentComplete = filterProgress.PercentComplete
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalFilterProgressEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = filterProgress
                },

                // Filter error
                InternalFilterErrorEvent filterError => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    FilterData = new FilterEventData
                    {
                        Type = FilterEventType.Error,
                        SourceName = filterError.SourceName,
                        ErrorMessage = filterError.ErrorMessage,
                        Exception = filterError.Exception
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalFilterErrorEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = filterError
                },

                // Unknown event types - return null to filter out
                _ => null
            };

            if (update != null)
            {
                yield return update;
            }
        }
    }

    /// <summary>
    /// Wraps an event stream with error handling.
    /// Catches exceptions during enumeration and converts them to structured error events.
    /// Works around C#'s limitation of no yield return in try-catch blocks.
    /// </summary>
    public static async IAsyncEnumerable<BaseEvent> WithErrorHandling(
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
                    caughtError = ex;
                    hadError = true;
                    historyCompletion.TrySetException(ex);
                }

                // Emit error event AFTER catch block (C# doesn't allow yield in catch)
                if (hadError && caughtError != null)
                {
                    var errorMessage = caughtError is OperationCanceledException
                        ? "Turn was canceled or timed out."
                        : caughtError.Message;
                    
                    yield return EventSerialization.CreateRunError(errorMessage);
                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                yield return currentEvent!;
            }

            // If error occurred and RunFinished wasn't emitted, emit it now for lifecycle closure
            if (caughtError != null && !runFinishedEmitted)
            {
                yield return EventSerialization.CreateRunFinished(
                    threadId ?? string.Empty, 
                    runId ?? string.Empty);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}

#endregion
#region

/// <summary>
/// Manages bidirectional event coordination for request/response patterns.
/// Used by filters (permissions, clarifications) and supports nested agent communication.
/// Thread-safe for concurrent event emission and response coordination.
/// </summary>
/// <remarks>
/// This coordinator provides the infrastructure for:
/// - Event emission from filters to handlers (one-way communication)
/// - Request/response patterns (bidirectional communication)
/// - Event bubbling in nested agent scenarios (parent coordinator support)
///
/// Lifecycle:
/// - Created once per Agent instance
/// - Lives for entire Agent lifetime
/// - Disposed when Agent is disposed
///
/// Thread-safety:
/// - All public methods are thread-safe
/// - Multiple filters can emit concurrently
/// - Event channel supports multiple producers, single consumer
/// </remarks>
public class BidirectionalEventCoordinator : IDisposable
{
    /// <summary>
    /// Shared event channel for all events.
    /// Unbounded to prevent blocking during event emission.
    /// Thread-safe: Multiple producers (filters), single consumer (background drainer).
    /// </summary>
    private readonly Channel<InternalAgentEvent> _eventChannel;

    /// <summary>
    /// Response coordination for bidirectional patterns.
    /// Maps requestId -> (TaskCompletionSource, CancellationTokenSource)
    /// Thread-safe: ConcurrentDictionary handles concurrent access.
    /// </summary>
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<InternalAgentEvent>, CancellationTokenSource)>
        _responseWaiters = new();

    /// <summary>
    /// Parent coordinator for event bubbling in nested agent scenarios.
    /// Set via SetParent() when an agent is used as a tool by another agent.
    /// Events emitted to this coordinator will also bubble to the parent.
    /// </summary>
    private BidirectionalEventCoordinator? _parentCoordinator;

    /// <summary>
    /// Creates a new bidirectional event coordinator.
    /// </summary>
    public BidirectionalEventCoordinator()
    {
        _eventChannel = Channel.CreateUnbounded<InternalAgentEvent>(new UnboundedChannelOptions
        {
            SingleWriter = false,  // Multiple filters can emit concurrently
            SingleReader = true,   // Only background drainer reads
            AllowSynchronousContinuations = false  // Performance & safety
        });
    }

    /// <summary>
    /// Gets the channel writer for event emission.
    /// Used by filters and contexts to emit events directly to the channel.
    /// </summary>
    /// <remarks>
    /// Note: For most use cases, prefer Emit() method over direct channel access
    /// as it handles event bubbling to parent coordinators.
    /// </remarks>
    public ChannelWriter<InternalAgentEvent> EventWriter => _eventChannel.Writer;

    /// <summary>
    /// Gets the channel reader for event consumption.
    /// Used by the agent's background drainer to read events.
    /// </summary>
    public ChannelReader<InternalAgentEvent> EventReader => _eventChannel.Reader;

    /// <summary>
    /// Sets the parent coordinator for event bubbling in nested agent scenarios.
    /// When a parent is set, all events emitted via Emit() will bubble to the parent.
    /// </summary>
    /// <param name="parent">The parent coordinator to bubble events to</param>
    /// <exception cref="ArgumentNullException">If parent is null</exception>
    /// <remarks>
    /// Use this when an agent is being used as a tool by another agent (via AsAIFunction).
    /// This enables events from nested agents to be visible to the orchestrator.
    ///
    /// Example:
    /// <code>
    /// var orchestratorAgent = new Agent(...);
    /// var codingAgent = new Agent(...);
    ///
    /// // When setting up CodingAgent as a tool
    /// codingAgent.EventCoordinator.SetParent(orchestratorAgent.EventCoordinator);
    /// </code>
    /// </remarks>
    public void SetParent(BidirectionalEventCoordinator parent)
    {
        _parentCoordinator = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    /// <summary>
    /// Emits an event to this coordinator and bubbles to parent (if set).
    /// Thread-safe: Can be called from any thread.
    /// </summary>
    /// <param name="evt">The event to emit</param>
    /// <exception cref="ArgumentNullException">If event is null</exception>
    /// <remarks>
    /// This is the preferred way to emit events as it handles parent bubbling automatically.
    ///
    /// Event flow:
    /// 1. Event is written to local channel (visible to this agent's background drainer)
    /// 2. If parent coordinator is set, event is recursively emitted to parent (bubbling)
    ///
    /// For nested agents:
    /// - Events bubble up the chain until reaching the root orchestrator
    /// - Each level's event loop sees the event
    /// - Handlers at any level can process the event
    /// </remarks>
    public void Emit(InternalAgentEvent evt)
    {
        if (evt == null)
            throw new ArgumentNullException(nameof(evt));

        // Emit to local channel
        _eventChannel.Writer.TryWrite(evt);

        // Bubble to parent coordinator (if nested agent)
        // This creates a chain: NestedAgent -> Orchestrator -> RootOrchestrator
        _parentCoordinator?.Emit(evt);
    }

    /// <summary>
    /// Sends a response to a filter waiting for a specific request.
    /// Called by external handlers (AGUI, Console, etc.) when user provides input.
    /// Thread-safe: Can be called from any thread.
    /// </summary>
    /// <param name="requestId">The unique identifier for the request</param>
    /// <param name="response">The response event to deliver</param>
    /// <exception cref="ArgumentNullException">If response is null</exception>
    /// <remarks>
    /// If requestId is not found (e.g., timeout already occurred), the call is silently ignored.
    /// This is intentional to avoid race conditions between timeout and response.
    ///
    /// Example:
    /// <code>
    /// // In handler
    /// await foreach (var evt in agent.RunStreamingAsync(...))
    /// {
    ///     if (evt is InternalPermissionRequestEvent permReq)
    ///     {
    ///         var approved = PromptUser(permReq);
    ///         coordinator.SendResponse(permReq.PermissionId,
    ///             new InternalPermissionResponseEvent(permReq.PermissionId, approved));
    ///     }
    /// }
    /// </code>
    /// </remarks>
    public void SendResponse(string requestId, InternalAgentEvent response)
    {
        if (response == null)
            throw new ArgumentNullException(nameof(response));

        if (_responseWaiters.TryRemove(requestId, out var entry))
        {
            entry.Item1.TrySetResult(response);
            entry.Item2.Dispose();
        }
        // Note: If requestId not found, silently ignore (response may have timed out)
    }

    /// <summary>
    /// Wait for a response to a previously emitted request event.
    /// Blocks until response received, timeout expires, or cancellation requested.
    /// </summary>
    /// <typeparam name="T">Expected response event type</typeparam>
    /// <param name="requestId">Unique identifier matching the request event</param>
    /// <param name="timeout">Maximum time to wait for response</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The typed response event</returns>
    /// <exception cref="TimeoutException">No response received within timeout</exception>
    /// <exception cref="OperationCanceledException">Operation was cancelled</exception>
    /// <exception cref="InvalidOperationException">Response type mismatch</exception>
    /// <remarks>
    /// This method is used by filters that need bidirectional communication:
    /// 1. Filter emits request event (e.g., InternalPermissionRequestEvent)
    /// 2. Filter calls WaitForResponseAsync() - BLOCKS HERE
    /// 3. Handler receives request event (via agent's event loop)
    /// 4. User provides input
    /// 5. Handler calls SendResponse()
    /// 6. Filter receives response and continues
    ///
    /// Important: The filter is blocked during step 2-5, but events still flow
    /// because of the polling mechanism in RunAgenticLoopInternal.
    ///
    /// Timeout vs. Cancellation:
    /// - TimeoutException: No response received within the specified timeout
    /// - OperationCanceledException: External cancellation (e.g., user stopped agent)
    ///
    /// Example:
    /// <code>
    /// // In filter
    /// var requestId = Guid.NewGuid().ToString();
    /// coordinator.Emit(new InternalPermissionRequestEvent(requestId, ...));
    ///
    /// try
    /// {
    ///     var response = await coordinator.WaitForResponseAsync&lt;InternalPermissionResponseEvent&gt;(
    ///         requestId,
    ///         TimeSpan.FromMinutes(5),
    ///         cancellationToken);
    ///
    ///     if (response.Approved)
    ///         await next(context);
    ///     else
    ///         context.IsTerminated = true;
    /// }
    /// catch (TimeoutException)
    /// {
    ///     context.Result = "Permission request timed out";
    ///     context.IsTerminated = true;
    /// }
    /// </code>
    /// </remarks>
    public async Task<T> WaitForResponseAsync<T>(
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken) where T : InternalAgentEvent
    {
        var tcs = new TaskCompletionSource<InternalAgentEvent>();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        _responseWaiters[requestId] = (tcs, cts);

        // Register cancellation/timeout cleanup
        // IMPORTANT: Distinguishes between timeout and external cancellation
        cts.Token.Register(() =>
        {
            if (_responseWaiters.TryRemove(requestId, out var entry))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // External cancellation (user stopped agent)
                    entry.Item1.TrySetCanceled(cancellationToken);
                }
                else
                {
                    // Timeout (no response received in time)
                    entry.Item1.TrySetException(
                        new TimeoutException($"No response received for request '{requestId}' within {timeout}"));
                }
                entry.Item2.Dispose();
            }
        });

        try
        {
            var response = await tcs.Task;

            // Type safety check with clear error message
            if (response is not T typedResponse)
            {
                throw new InvalidOperationException(
                    $"Expected response of type {typeof(T).Name}, but received {response.GetType().Name}");
            }

            return typedResponse;
        }
        finally
        {
            // Cleanup on success (timeout/cancellation cleanup handled by registration above)
            if (_responseWaiters.TryRemove(requestId, out var entry))
            {
                entry.Item2.Dispose();
            }
        }
    }

    /// <summary>
    /// Disposes the coordinator, completing the event channel and cancelling all pending waiters.
    /// Should be called when the agent is being disposed.
    /// </summary>
    /// <remarks>
    /// Cleanup sequence:
    /// 1. Complete the event channel (no more events can be emitted)
    /// 2. Cancel all pending response waiters
    /// 3. Dispose all cancellation token sources
    /// 4. Clear the waiters dictionary
    ///
    /// This ensures clean shutdown even if there are pending bidirectional requests.
    /// </remarks>
    public void Dispose()
    {
        // Complete the channel first
        _eventChannel.Writer.Complete();

        // Cancel all pending waiters
        foreach (var waiter in _responseWaiters.Values)
        {
            waiter.Item1.TrySetCanceled();
            waiter.Item2.Dispose();
        }
        _responseWaiters.Clear();
    }
}


#endregion
#region Function Retry Executor

/// <summary>
/// Handles intelligent retry logic for function execution with provider-aware error handling.
/// Consolidates retry logic that was previously duplicated in FunctionCallProcessor.
/// 
/// Features:
/// - Provider-specific error parsing and retry delays
/// - Intelligent backoff: API-specified > Provider-specific > Exponential with jitter
/// - Timeout enforcement per function call
/// - Category-based retry limits
/// </summary>
internal class FunctionRetryExecutor
{
    private readonly ErrorHandlingConfig? _errorConfig;

    public FunctionRetryExecutor(ErrorHandlingConfig? errorConfig)
    {
        _errorConfig = errorConfig;
    }

    /// <summary>
    /// Executes a function with provider-aware retry logic and timeout enforcement.
    /// </summary>
    /// <param name="function">The AIFunction to invoke</param>
    /// <param name="arguments">The arguments to pass to the function</param>
    /// <param name="functionName">The function name (for error messages)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function result as an object</returns>
    public async Task<object?> ExecuteWithRetryAsync(
        AIFunction function,
        IDictionary<string, object?> arguments,
        string functionName,
        CancellationToken cancellationToken)
    {
        var maxRetries = _errorConfig?.MaxRetries ?? 3;
        var retryDelay = _errorConfig?.RetryDelay ?? TimeSpan.FromSeconds(1);
        var functionTimeout = _errorConfig?.SingleFunctionTimeout;
        var providerHandler = _errorConfig?.ProviderHandler;
        var customRetryStrategy = _errorConfig?.CustomRetryStrategy;

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

                var args = new AIFunctionArguments(arguments);
                return await function.InvokeAsync(args, functionCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (functionTimeout.HasValue && !cancellationToken.IsCancellationRequested)
            {
                // Function-specific timeout (not the overall cancellation)
                throw new TimeoutException($"Function '{functionName}' timed out after {functionTimeout.Value.TotalSeconds} seconds.");
            }
            catch (Exception ex)
            {
                lastException = ex;

                // Don't retry if we've exhausted attempts
                if (attempt >= maxRetries)
                {
                    throw new InvalidOperationException(
                        $"Error invoking function '{functionName}' after {maxRetries + 1} attempts: {ex.Message}",
                        ex);
                }

                // Calculate retry delay using priority system
                var delay = await CalculateRetryDelay(
                    ex,
                    attempt,
                    retryDelay,
                    customRetryStrategy,
                    providerHandler,
                    maxRetries,
                    functionName,
                    cancellationToken).ConfigureAwait(false);

                if (!delay.HasValue)
                {
                    // Strategy says don't retry
                    throw new InvalidOperationException(
                        $"Error invoking function '{functionName}': {ex.Message} (non-retryable)",
                        ex);
                }

                await Task.Delay(delay.Value, cancellationToken).ConfigureAwait(false);
            }
        }

        // Should never reach here, but include for completeness
        throw new InvalidOperationException(
            $"Error invoking function '{functionName}': {lastException?.Message ?? "Unknown error"}",
            lastException);
    }

    /// <summary>
    /// Calculates retry delay using priority system:
    /// PRIORITY 1: Custom retry strategy
    /// PRIORITY 2: Provider-aware error handling
    /// PRIORITY 3: Exponential backoff with jitter
    /// </summary>
    private async Task<TimeSpan?> CalculateRetryDelay(
        Exception exception,
        int attempt,
        TimeSpan baseRetryDelay,
        Func<Exception, int, CancellationToken, Task<TimeSpan?>>? customStrategy,
        HPD.Agent.ErrorHandling.IProviderErrorHandler? providerHandler,
        int maxRetries,
        string functionName,
        CancellationToken cancellationToken)
    {
        // PRIORITY 1: Use custom retry strategy if provided
        if (customStrategy != null)
        {
            var customDelay = await customStrategy(exception, attempt, cancellationToken).ConfigureAwait(false);
            if (!customDelay.HasValue)
            {
                return null; // Custom strategy says don't retry
            }
            return ApplyMaxDelayCap(customDelay.Value);
        }

        // PRIORITY 2: Use provider-aware error handling if available
        if (providerHandler != null)
        {
            var errorDetails = providerHandler.ParseError(exception);
            if (errorDetails != null)
            {
                // Check if per-category retry limits apply
                var categoryMaxRetries = _errorConfig?.MaxRetriesByCategory?.GetValueOrDefault(errorDetails.Category) ?? maxRetries;
                if (attempt >= categoryMaxRetries)
                {
                    return null; // Exceeded category-specific retry limit
                }

                // Get provider-calculated delay
                var maxDelayFromConfig = _errorConfig?.MaxRetryDelay ?? TimeSpan.FromSeconds(30);
                var backoffMultiplier = _errorConfig?.BackoffMultiplier ?? 2.0;
                var providerDelay = providerHandler.GetRetryDelay(
                    errorDetails,
                    attempt,
                    baseRetryDelay,
                    backoffMultiplier,
                    maxDelayFromConfig);

                if (!providerDelay.HasValue)
                {
                    return null; // Provider says this error is not retryable
                }

                return ApplyMaxDelayCap(providerDelay.Value);
            }
        }

        // PRIORITY 3: Fallback to exponential backoff with jitter
        var baseMs = baseRetryDelay.TotalMilliseconds;
        var maxDelayMs = baseMs * Math.Pow(2, attempt); // 1x, 2x, 4x, 8x...
        var jitteredDelayMs = Random.Shared.NextDouble() * maxDelayMs;
        var delay = TimeSpan.FromMilliseconds(jitteredDelayMs);

        return ApplyMaxDelayCap(delay);
    }

    /// <summary>
    /// Applies configured maximum delay cap
    /// </summary>
    private TimeSpan ApplyMaxDelayCap(TimeSpan delay)
    {
        var maxDelay = _errorConfig?.MaxRetryDelay ?? TimeSpan.FromSeconds(30);
        return delay > maxDelay ? maxDelay : delay;
    }
}

#endregion

#region Filter Chain

/// <summary>
/// Builds and executes filter chains with proper ordering and pipeline execution.
/// Eliminates duplication of filter pipeline construction across the Agent codebase.
/// 
/// Usage Pattern:
/// 1. Define a final action (core logic)
/// 2. Build pipeline with filters (automatically reversed for correct execution order)
/// 3. Execute the built pipeline
/// 
/// Supports all filter types: IAiFunctionFilter, IPromptFilter, IPermissionFilter, IMessageTurnFilter
/// </summary>
internal static class FilterChain
{
    /// <summary>
    /// Builds an IAiFunctionFilter pipeline.
    /// Filters are applied in REVERSE order so they execute in the order provided.
    /// </summary>
    /// <param name="filters">The filters to apply, in the order they should execute</param>
    /// <param name="finalAction">The final action to execute after all filters</param>
    /// <returns>A function that executes the complete pipeline</returns>
    public static Func<AiFunctionContext, Task> BuildAiFunctionPipeline(
        IEnumerable<IAiFunctionFilter> filters,
        Func<AiFunctionContext, Task> finalAction)
    {
        if (finalAction == null)
            throw new ArgumentNullException(nameof(finalAction));

        var pipeline = finalAction;

        // Wrap in reverse order so filters execute in the order provided
        foreach (var filter in filters.Reverse())
        {
            var previous = pipeline;
            pipeline = ctx => filter.InvokeAsync(ctx, previous);
        }

        return pipeline;
    }

    /// <summary>
    /// Builds an IPromptFilter pipeline with result transformation.
    /// Filters can modify messages and return transformed results.
    /// </summary>
    /// <param name="filters">The filters to apply, in the order they should execute</param>
    /// <param name="finalAction">The final action that returns the messages</param>
    /// <returns>A function that executes the complete pipeline and returns messages</returns>
    public static Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> BuildPromptPipeline(
        IEnumerable<IPromptFilter> filters,
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> finalAction)
    {
        if (finalAction == null)
            throw new ArgumentNullException(nameof(finalAction));

        var pipeline = finalAction;

        // Wrap in reverse order so filters execute in the order provided
        foreach (var filter in filters.Reverse())
        {
            var previous = pipeline;
            pipeline = ctx => filter.InvokeAsync(ctx, previous);
        }

        return pipeline;
    }

    /// <summary>
    /// Builds an IPermissionFilter pipeline.
    /// Used specifically for permission checking before function execution.
    /// </summary>
    /// <param name="filters">The filters to apply, in the order they should execute</param>
    /// <param name="finalAction">The final action (typically a no-op for permission checks)</param>
    /// <returns>A function that executes the complete pipeline</returns>
    public static Func<AiFunctionContext, Task> BuildPermissionPipeline(
        IEnumerable<IPermissionFilter> filters,
        Func<AiFunctionContext, Task> finalAction)
    {
        if (finalAction == null)
            throw new ArgumentNullException(nameof(finalAction));

        var pipeline = finalAction;

        // Wrap in reverse order so filters execute in the order provided
        foreach (var filter in filters.Reverse())
        {
            var previous = pipeline;
            pipeline = ctx => filter.InvokeAsync(ctx, previous);
        }

        return pipeline;
    }

    /// <summary>
    /// Builds an IMessageTurnFilter pipeline.
    /// Used for post-turn observation and telemetry.
    /// </summary>
    /// <param name="filters">The filters to apply, in the order they should execute</param>
    /// <param name="finalAction">The final action (typically a no-op for observation)</param>
    /// <returns>A function that executes the complete pipeline</returns>
    public static Func<MessageTurnFilterContext, Task> BuildMessageTurnPipeline(
        IEnumerable<IMessageTurnFilter> filters,
        Func<MessageTurnFilterContext, Task> finalAction)
    {
        if (finalAction == null)
            throw new ArgumentNullException(nameof(finalAction));

        var pipeline = finalAction;

        // Wrap in reverse order so filters execute in the order provided
        foreach (var filter in filters.Reverse())
        {
            var previous = pipeline;
            pipeline = ctx => filter.InvokeAsync(ctx, previous);
        }

        return pipeline;
    }
}

#endregion

#region Internal Events
/// <summary>
/// Protocol-agnostic internal events emitted by the agent core.
/// These events represent what actually happened during agent execution,
/// independent of any specific protocol (AGUI, IChatClient, etc.).
///
/// KEY CONCEPTS:
/// - MESSAGE TURN: The entire user interaction (user sends message → agent responds)
///   May contain multiple agent turns if tools are called
/// - AGENT TURN: A single call to the LLM (one iteration in the agentic loop)
///   Multiple agent turns happen within one message turn when using tools
///
/// Adapters convert these to protocol-specific formats:
/// - AGUI: MessageTurn → Run, AgentTurn → Step
/// - IChatClient: Only cares about final result, ignores turn boundaries
/// </summary>
public abstract record InternalAgentEvent;

#region Message Turn Events (Entire User Interaction)

/// <summary>
/// Emitted when a message turn starts (user sends message, agent begins processing)
/// This represents the START of the entire multi-step agent execution.
/// </summary>
public record InternalMessageTurnStartedEvent(string MessageTurnId, string ConversationId) : InternalAgentEvent;

/// <summary>
/// Emitted when a message turn completes successfully
/// This represents the END of the entire agent execution for this user message.
/// </summary>
public record InternalMessageTurnFinishedEvent(string MessageTurnId, string ConversationId) : InternalAgentEvent;

/// <summary>
/// Emitted when an error occurs during message turn execution
/// </summary>
public record InternalMessageTurnErrorEvent(string Message, Exception? Exception = null) : InternalAgentEvent;

#endregion

#region Agent Turn Events (Single LLM Call Within Message Turn)

/// <summary>
/// Emitted when an agent turn starts (single LLM call within the agentic loop)
/// An agent turn represents one iteration where the LLM processes messages and responds.
/// Multiple agent turns may occur in one message turn when tools are called.
/// </summary>
public record InternalAgentTurnStartedEvent(int Iteration) : InternalAgentEvent;

/// <summary>
/// Emitted when an agent turn completes
/// </summary>
public record InternalAgentTurnFinishedEvent(int Iteration) : InternalAgentEvent;

#endregion

#region Content Events (Within an Agent Turn)

/// <summary>
/// Emitted when the agent starts producing text content
/// </summary>
public record InternalTextMessageStartEvent(string MessageId, string Role) : InternalAgentEvent;

/// <summary>
/// Emitted when the agent produces text content (streaming delta)
/// </summary>
public record InternalTextDeltaEvent(string Text, string MessageId) : InternalAgentEvent;

/// <summary>
/// Emitted when the agent finishes producing text content
/// </summary>
public record InternalTextMessageEndEvent(string MessageId) : InternalAgentEvent;

#endregion

#region Reasoning Events (For reasoning-capable models like o1, DeepSeek-R1)

/// <summary>
/// Emitted when the agent starts reasoning/thinking
/// </summary>
public record InternalReasoningStartEvent(string MessageId) : InternalAgentEvent;

/// <summary>
/// Emitted when the agent starts a reasoning message
/// </summary>
public record InternalReasoningMessageStartEvent(string MessageId, string Role) : InternalAgentEvent;

/// <summary>
/// Emitted when the agent produces reasoning/thinking content (streaming delta)
/// </summary>
public record InternalReasoningDeltaEvent(string Text, string MessageId) : InternalAgentEvent;

/// <summary>
/// Emitted when the agent finishes a reasoning message
/// </summary>
public record InternalReasoningMessageEndEvent(string MessageId) : InternalAgentEvent;

/// <summary>
/// Emitted when the agent finishes reasoning
/// </summary>
public record InternalReasoningEndEvent(string MessageId) : InternalAgentEvent;

#endregion

#region Tool Events

/// <summary>
/// Emitted when the agent requests a tool call
/// </summary>
public record InternalToolCallStartEvent(
    string CallId,
    string Name,
    string MessageId) : InternalAgentEvent;

/// <summary>
/// Emitted when a tool call's arguments are fully available
/// </summary>
public record InternalToolCallArgsEvent(string CallId, string ArgsJson) : InternalAgentEvent;

/// <summary>
/// Emitted when a tool call completes execution
/// </summary>
public record InternalToolCallEndEvent(string CallId) : InternalAgentEvent;

/// <summary>
/// Emitted when a tool call result is available
/// </summary>
public record InternalToolCallResultEvent(
    string CallId,
    string Result) : InternalAgentEvent;

#endregion

#region Filter Events

/// <summary>
/// Marker interface for events that support bidirectional communication.
/// Events implementing this interface can:
/// - Be emitted during execution
/// - Bubble to parent agents via AsyncLocal
/// - Wait for responses using WaitForResponseAsync
/// </summary>
public interface IBidirectionalEvent
{
    /// <summary>
    /// Name of the filter that emitted this event.
    /// </summary>
    string SourceName { get; }
}

/// <summary>
/// Marker interface for permission-related filter events.
/// Permission events are a specialized subset of filter events
/// that require user interaction and approval workflows.
/// </summary>
public interface IPermissionEvent : IBidirectionalEvent
{
    /// <summary>
    /// Unique identifier for this permission interaction.
    /// Used to correlate requests and responses.
    /// </summary>
    string PermissionId { get; }
}

/// <summary>
/// Filter requests permission to execute a function.
/// Handler should prompt user and send InternalPermissionResponseEvent.
/// </summary>
public record InternalPermissionRequestEvent(
    string PermissionId,
    string SourceName,
    string FunctionName,
    string? Description,
    string CallId,
    IDictionary<string, object?>? Arguments) : InternalAgentEvent, IPermissionEvent;

/// <summary>
/// Response to permission request.
/// Sent by external handler (AGUI, Console) back to waiting filter.
/// </summary>
public record InternalPermissionResponseEvent(
    string PermissionId,
    string SourceName,
    bool Approved,
    string? Reason = null,
    PermissionChoice Choice = PermissionChoice.Ask) : InternalAgentEvent, IPermissionEvent;

/// <summary>
/// Emitted after permission is approved (for observability).
/// </summary>
public record InternalPermissionApprovedEvent(
    string PermissionId,
    string SourceName) : InternalAgentEvent, IPermissionEvent;

/// <summary>
/// Emitted after permission is denied (for observability).
/// </summary>
public record InternalPermissionDeniedEvent(
    string PermissionId,
    string SourceName,
    string Reason) : InternalAgentEvent, IPermissionEvent;

/// <summary>
/// Filter requests permission to continue beyond max iterations.
/// </summary>
public record InternalContinuationRequestEvent(
    string ContinuationId,
    string SourceName,
    int CurrentIteration,
    int MaxIterations) : InternalAgentEvent, IPermissionEvent
{
    /// <summary>
    /// Explicit interface implementation for IPermissionEvent.PermissionId
    /// Maps ContinuationId to PermissionId for consistency.
    /// </summary>
    string IPermissionEvent.PermissionId => ContinuationId;
}

/// <summary>
/// Response to continuation request.
/// </summary>
public record InternalContinuationResponseEvent(
    string ContinuationId,
    string SourceName,
    bool Approved,
    int ExtensionAmount = 0) : InternalAgentEvent, IPermissionEvent
{
    /// <summary>
    /// Explicit interface implementation for IPermissionEvent.PermissionId
    /// Maps ContinuationId to PermissionId for consistency.
    /// </summary>
    string IPermissionEvent.PermissionId => ContinuationId;
}

/// <summary>
/// Marker interface for clarification-related events.
/// Clarification events enable agents/plugins to ask the user for additional information
/// during execution, supporting human-in-the-loop workflows beyond just permissions.
/// </summary>
public interface IClarificationEvent : IBidirectionalEvent
{
    /// <summary>
    /// Unique identifier for this clarification interaction.
    /// Used to correlate requests and responses.
    /// </summary>
    string RequestId { get; }

    /// <summary>
    /// The question being asked to the user.
    /// </summary>
    string Question { get; }
}

/// <summary>
/// Agent/plugin requests user clarification or additional input.
/// Handler should prompt user and send InternalClarificationResponseEvent.
/// </summary>
public record InternalClarificationRequestEvent(
    string RequestId,
    string SourceName,
    string Question,
    string? AgentName = null,
    string[]? Options = null) : InternalAgentEvent, IClarificationEvent;

/// <summary>
/// Response to clarification request.
/// Sent by external handler (AGUI, Console) back to waiting agent/plugin.
/// </summary>
public record InternalClarificationResponseEvent(
    string RequestId,
    string SourceName,
    string Question,
    string Answer) : InternalAgentEvent, IClarificationEvent;

/// <summary>
/// Filter reports progress (one-way, no response needed).
/// </summary>
public record InternalFilterProgressEvent(
    string SourceName,
    string Message,
    int? PercentComplete = null) : InternalAgentEvent, IBidirectionalEvent;

/// <summary>
/// Filter reports an error (one-way, no response needed).
/// </summary>
public record InternalFilterErrorEvent(
    string SourceName,
    string ErrorMessage,
    Exception? Exception = null) : InternalAgentEvent, IBidirectionalEvent;

#endregion

#endregion

#region Extended Agent Response Classes

/// <summary>
/// Extended version of AgentRunResponseUpdate that preserves all internal event data.
/// This allows streaming consumers to access rich event metadata that would otherwise
/// be filtered out when converting to the standard Microsoft.Agents.AI protocol.
/// </summary>
/// <remarks>
/// The base AgentRunResponseUpdate only supports basic content types (text, tool calls, etc.).
/// HPD-Agent emits many additional event types for turn boundaries, permissions, filters, etc.
/// This extended class preserves that data while remaining compatible with the base protocol.
/// </remarks>
public class ExtendedAgentRunResponseUpdate : AgentRunResponseUpdate
{
    /// <summary>
    /// Metadata about the event itself (type, conversation ID, turn ID)
    /// </summary>
    public EventMetadata? EventMetadata { get; set; }

    /// <summary>
    /// Turn boundary information (start/end of agent turns and message turns)
    /// </summary>
    public TurnBoundaryData? TurnBoundary { get; set; }

    /// <summary>
    /// Message boundary information (start/end of text/reasoning messages)
    /// </summary>
    public MessageBoundaryData? MessageBoundary { get; set; }

    /// <summary>
    /// Tool call details (arguments JSON, completion markers)
    /// </summary>
    public ToolCallData? ToolData { get; set; }

    /// <summary>
    /// Permission-related event data (requests, approvals, denials)
    /// </summary>
    public PermissionEventData? PermissionData { get; set; }

    /// <summary>
    /// Continuation-related event data (iteration limit requests)
    /// </summary>
    public ContinuationEventData? ContinuationData { get; set; }

    /// <summary>
    /// Clarification-related event data (requests and responses)
    /// </summary>
    public ClarificationEventData? ClarificationData { get; set; }

    /// <summary>
    /// Filter-related event data (progress, errors, custom events)
    /// </summary>
    public FilterEventData? FilterData { get; set; }

    /// <summary>
    /// Error information (from InternalMessageTurnErrorEvent)
    /// </summary>
    public ErrorEventData? ErrorData { get; set; }

    /// <summary>
    /// The original internal event that generated this update (for debugging/diagnostics)
    /// </summary>
    [JsonIgnore]
    public InternalAgentEvent? OriginalInternalEvent { get; set; }

    /// <summary>
    /// Helper property to check if this update represents a turn boundary
    /// </summary>
    [JsonIgnore]
    public bool IsTurnBoundary => TurnBoundary != null;

    /// <summary>
    /// Helper property to check if this update represents a message boundary
    /// </summary>
    [JsonIgnore]
    public bool IsMessageBoundary => MessageBoundary != null;

    /// <summary>
    /// Helper property to check if this update contains permission data
    /// </summary>
    [JsonIgnore]
    public bool IsPermissionEvent => PermissionData != null;

    /// <summary>
    /// Helper property to check if this update contains filter data
    /// </summary>
    [JsonIgnore]
    public bool IsFilterEvent => FilterData != null;

    /// <summary>
    /// Helper property to check if this update contains error data
    /// </summary>
    [JsonIgnore]
    public bool IsErrorEvent => ErrorData != null;
}

/// <summary>
/// Metadata about the event itself
/// </summary>
public class EventMetadata
{
    /// <summary>
    /// The type of internal event that generated this update
    /// </summary>
    public string? EventType { get; set; }

    /// <summary>
    /// The conversation ID (for message turn events)
    /// </summary>
    public string? ConversationId { get; set; }

    /// <summary>
    /// The message turn ID (for message turn events)
    /// </summary>
    public string? MessageTurnId { get; set; }

    /// <summary>
    /// Timestamp when the event occurred
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Turn boundary information
/// </summary>
public class TurnBoundaryData
{
    /// <summary>
    /// Type of turn boundary (MessageTurnStart, MessageTurnEnd, AgentTurnStart, AgentTurnEnd)
    /// </summary>
    public TurnBoundaryType Type { get; set; }

    /// <summary>
    /// The iteration number (for agent turn events)
    /// </summary>
    public int? Iteration { get; set; }

    /// <summary>
    /// The message turn ID (for message turn events)
    /// </summary>
    public string? MessageTurnId { get; set; }

    /// <summary>
    /// The conversation ID (for message turn events)
    /// </summary>
    public string? ConversationId { get; set; }
}

/// <summary>
/// Types of turn boundaries
/// </summary>
public enum TurnBoundaryType
{
    MessageTurnStart,
    MessageTurnEnd,
    AgentTurnStart,
    AgentTurnEnd
}

/// <summary>
/// Message boundary information
/// </summary>
public class MessageBoundaryData
{
    /// <summary>
    /// Type of message boundary (TextStart, TextEnd, ReasoningStart, ReasoningEnd, etc.)
    /// </summary>
    public MessageBoundaryType Type { get; set; }

    /// <summary>
    /// The message ID
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>
    /// The role of the message (for start events)
    /// </summary>
    public string? Role { get; set; }
}

/// <summary>
/// Types of message boundaries
/// </summary>
public enum MessageBoundaryType
{
    TextMessageStart,
    TextMessageEnd,
    ReasoningStart,
    ReasoningMessageStart,
    ReasoningMessageEnd,
    ReasoningEnd
}

/// <summary>
/// Tool call details
/// </summary>
public class ToolCallData
{
    /// <summary>
    /// The tool call ID
    /// </summary>
    public string? CallId { get; set; }

    /// <summary>
    /// The tool arguments as JSON (from InternalToolCallArgsEvent)
    /// </summary>
    public string? ArgsJson { get; set; }

    /// <summary>
    /// Whether this represents a tool call end event
    /// </summary>
    public bool IsToolEnd { get; set; }
}

/// <summary>
/// Permission event data
/// </summary>
public class PermissionEventData
{
    /// <summary>
    /// Type of permission event
    /// </summary>
    public PermissionEventType Type { get; set; }

    /// <summary>
    /// The permission ID
    /// </summary>
    public string? PermissionId { get; set; }

    /// <summary>
    /// The function name requiring permission
    /// </summary>
    public string? FunctionName { get; set; }

    /// <summary>
    /// Description of what the permission is for
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The tool call ID
    /// </summary>
    public string? CallId { get; set; }

    /// <summary>
    /// The function arguments
    /// </summary>
    public IDictionary<string, object?>? Arguments { get; set; }

    /// <summary>
    /// Whether permission was approved (for response/approved/denied events)
    /// </summary>
    public bool? Approved { get; set; }

    /// <summary>
    /// Reason for approval/denial
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// The permission choice (Ask, Allow, Deny)
    /// </summary>
    public PermissionChoice? Choice { get; set; }
}

/// <summary>
/// Types of permission events
/// </summary>
public enum PermissionEventType
{
    Request,
    Response,
    Approved,
    Denied
}

/// <summary>
/// Clarification event data for UI handlers
/// </summary>
public class ClarificationEventData
{
    /// <summary>
    /// Type of clarification event
    /// </summary>
    public ClarificationEventType Type { get; set; }

    /// <summary>
    /// The unique request ID
    /// </summary>
    public string? RequestId { get; set; }

    /// <summary>
    /// The name of the agent asking for clarification
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// The question being asked
    /// </summary>
    public string? Question { get; set; }

    /// <summary>
    /// Optional list of suggested answers/options
    /// </summary>
    public string[]? Options { get; set; }

    /// <summary>
    /// The user's answer (for response events)
    /// </summary>
    public string? Answer { get; set; }
}

/// <summary>
/// Types of clarification events
/// </summary>
public enum ClarificationEventType
{
    Request,
    Response
}

/// <summary>
/// Continuation event data
/// </summary>
public class ContinuationEventData
{
    /// <summary>
    /// Type of continuation event
    /// </summary>
    public ContinuationEventType Type { get; set; }

    /// <summary>
    /// The continuation ID
    /// </summary>
    public string? ContinuationId { get; set; }

    /// <summary>
    /// Current iteration number
    /// </summary>
    public int? CurrentIteration { get; set; }

    /// <summary>
    /// Maximum iterations allowed
    /// </summary>
    public int? MaxIterations { get; set; }

    /// <summary>
    /// Whether continuation was approved
    /// </summary>
    public bool? Approved { get; set; }

    /// <summary>
    /// How many additional iterations were granted
    /// </summary>
    public int? ExtensionAmount { get; set; }
}

/// <summary>
/// Types of continuation events
/// </summary>
public enum ContinuationEventType
{
    Request,
    Response
}

/// <summary>
/// Filter event data
/// </summary>
public class FilterEventData
{
    /// <summary>
    /// Type of filter event
    /// </summary>
    public FilterEventType Type { get; set; }

    /// <summary>
    /// The filter name
    /// </summary>
    public string? SourceName { get; set; }

    /// <summary>
    /// Progress message (for progress events)
    /// </summary>
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// Percent complete (0-100, for progress events)
    /// </summary>
    public int? PercentComplete { get; set; }

    /// <summary>
    /// Error message (for error events)
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Exception details (for error events)
    /// </summary>
    [JsonIgnore]
    public Exception? Exception { get; set; }

    /// <summary>
    /// Custom event type (for custom events)
    /// </summary>
    public string? CustomEventType { get; set; }

    /// <summary>
    /// Custom event data (for custom events)
    /// </summary>
    public IDictionary<string, object?>? CustomData { get; set; }
}

/// <summary>
/// Types of filter events
/// </summary>
public enum FilterEventType
{
    Progress,
    Error,
    Custom
}

/// <summary>
/// Error event data
/// </summary>
public class ErrorEventData
{
    /// <summary>
    /// Error message
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Exception details
    /// </summary>
    [JsonIgnore]
    public Exception? Exception { get; set; }

    /// <summary>
    /// Exception type name
    /// </summary>
    public string? ExceptionType { get; set; }

    /// <summary>
    /// Exception stack trace
    /// </summary>
    public string? StackTrace { get; set; }
}

#endregion

#region Function Invocation Context

/// <summary>
/// Provides ambient context about the currently executing function.
/// Flows across async calls via AsyncLocal storage in Agent class.
/// </summary>
/// <remarks>
/// This enables plugins and filters to access function invocation metadata
/// without explicit parameter passing. Inspired by Microsoft.Extensions.AI's
/// FunctionInvokingChatClient pattern but adapted for HPD-Agent's architecture.
/// 
/// Use cases:
/// - Logging which agent/iteration called the function
/// - Cancellation propagation based on iteration limits
/// - Telemetry correlation via CallId
/// - Security auditing (know which agent invoked sensitive functions)
/// </remarks>
public class FunctionInvocationContext
{
    /// <summary>
    /// The function being invoked.
    /// </summary>
    public AIFunction Function { get; init; } = null!;

    /// <summary>
    /// Name of the function being invoked.
    /// </summary>
    public string FunctionName => Function?.Name ?? string.Empty;

    /// <summary>
    /// Description of the function being invoked.
    /// </summary>
    public string? FunctionDescription => Function?.Description;

    /// <summary>
    /// Arguments being passed to the function.
    /// </summary>
    public IDictionary<string, object?> Arguments { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// Unique identifier for this function call (for correlation).
    /// </summary>
    public string CallId { get; init; } = string.Empty;

    /// <summary>
    /// Name of the agent that initiated this function call.
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Current iteration number in the agent's execution loop.
    /// </summary>
    public int Iteration { get; init; }

    /// <summary>
    /// Total number of function calls made in this agent run so far.
    /// </summary>
    public int TotalFunctionCallsInRun { get; init; }

    /// <summary>
    /// The agent run context (if available).
    /// </summary>
    public AgentRunContext? RunContext { get; init; }

    /// <summary>
    /// Extensible metadata dictionary for custom data.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>
    /// Gets a string representation for logging/debugging.
    /// </summary>
    public override string ToString() =>
        $"Function: {FunctionName}, CallId: {CallId}, Agent: {AgentName ?? "Unknown"}, Iteration: {Iteration}";
}

#endregion