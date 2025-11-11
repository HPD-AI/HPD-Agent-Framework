using Microsoft.Extensions.AI;
using System.Threading;
using System.Threading.Channels;
using HPD.Agent.Internal.Filters;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using HPD.Agent.Providers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using HPD.Agent.Conversation.Checkpointing;

namespace HPD.Agent;

/// <summary>
/// Protocol-agnostic agent execution engine.
/// Provides a pure, composable core for building AI agents without framework dependencies.
/// Delegates to specialized components for clean separation of concerns.
/// INTERNAL: Use HPD.Agent.Microsoft.Agent or HPD.Agent.AGUI.Agent for protocol-specific APIs.
/// 
/// <strong>Concurrency Model: Single-Turn</strong>
/// This Agent instance is designed for single concurrent execution. Multiple calls to RunAsync()
/// on the same instance will serialize - each call acquires a semaphore that only one caller can hold at a time.
/// This prevents race conditions on shared state (_conversationId, _eventCoordinator, etc.).
/// 
/// For parallel agent execution, create multiple Agent instances or use an object pool pattern.
/// Example:
/// <code>
/// // ❌ DON'T: Concurrent calls on same instance
/// var results = await Task.WhenAll(
///     agent.RunAsync(messages1).ToListAsync(),
///     agent.RunAsync(messages2).ToListAsync()
/// );
/// 
/// // ✅ DO: Create separate instances for parallelism
/// var agent1 = new Agent(...);
/// var agent2 = new Agent(...);
/// var results = await Task.WhenAll(
///     agent1.RunAsync(messages1).ToListAsync(),
///     agent2.RunAsync(messages2).ToListAsync()
/// );
/// </code>
/// </summary>
internal sealed class Agent
{
    private readonly IChatClient _baseClient;
    private readonly string _name;
    private readonly ScopedFilterManager? _scopedFilterManager;
    private readonly int _maxFunctionCalls;
    private readonly IServiceProvider? _serviceProvider; // Used for middleware dependency injection

    // Microsoft.Extensions.AI compliance fields
    private readonly ChatClientMetadata _metadata;
    private readonly ErrorHandlingPolicy _errorPolicy;
    private string? _conversationId;
    
    // Concurrency control: Enforces single-turn execution per Agent instance
    // Multiple concurrent RunAsync calls on the same instance will serialize to prevent
    // race conditions on _conversationId and shared state in _eventCoordinator
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    // OpenTelemetry Activity Source for telemetry
    private static readonly ActivitySource ActivitySource = new("HPD.Agent");

    // AsyncLocal storage for function invocation context (flows across async calls)
    // Stores the full FunctionInvocationContext with all orchestration capabilities
    private static readonly AsyncLocal<FunctionInvocationContext?> _currentFunctionContext = new();

    // AsyncLocal storage for root agent tracking in nested agent calls
    // Used for event bubbling from nested agents to their orchestrator
    // When an agent calls another agent (via AsAIFunction), this tracks the top-level orchestrator
    // Flows automatically through AsyncLocal propagation across nested async calls
    private static readonly AsyncLocal<Agent?> _rootAgent = new();

    // AsyncLocal storage for current conversation thread (flows across async calls)
    // Provides access to thread context (project, documents, etc.) throughout the agent execution
    private static readonly AsyncLocal<ConversationThread?> _currentThread = new();

    // Specialized component fields for delegation
    private readonly MessageProcessor _messageProcessor;
    private readonly FunctionCallProcessor _functionCallProcessor;
    private readonly AgentTurn _agentTurn;
    private readonly ToolScheduler _toolScheduler;
    private readonly HPD_Agent.Scoping.UnifiedScopingManager _scopingManager;
    private readonly PermissionManager _permissionManager;
    private readonly BidirectionalEventCoordinator _eventCoordinator;
    private readonly IReadOnlyList<IAiFunctionFilter> _aiFunctionFilters;
    private readonly IReadOnlyList<IMessageTurnFilter> _messageTurnFilters;
    private readonly HPD.Agent.ErrorHandling.IProviderErrorHandler _providerErrorHandler;

    // Observability components (following component delegation pattern)
    private readonly AgentTelemetryService? _telemetryService;
    private readonly AgentLoggingService? _loggingService;

    // Caching infrastructure (inline pattern - wraps stream lifecycle)
    private readonly IDistributedCache? _cache;
    private readonly CachingConfig? _cachingConfig;
    private readonly JsonSerializerOptions _jsonOptions;

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
    /// Gets or sets the current conversation thread in the execution context.
    /// This flows across async calls and provides access to thread context throughout agent execution.
    /// </summary>
    /// <remarks>
    /// This property enables filters and other components to access thread-specific context
    /// such as project information, conversation history, and thread metadata.
    /// </remarks>
    public static ConversationThread? CurrentThread
    {
        get => _currentThread.Value;
        internal set => _currentThread.Value = value;
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
    /// Called by external handlers when user provides input.
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
    /// <param name="conversationContext">Additional context to inject (e.g., ConversationId)</param>
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
        HPD_Agent.Skills.SkillScopingManager? skillScopingManager = null,
        IReadOnlyList<IPermissionFilter>? permissionFilters = null,
        IReadOnlyList<IAiFunctionFilter>? aiFunctionFilters = null,
        IReadOnlyList<IMessageTurnFilter>? messageTurnFilters = null,
        IServiceProvider? serviceProvider = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = config.Name ?? "Agent"; // Default to "Agent" to prevent null dictionary key exceptions
        _scopedFilterManager = scopedFilterManager ?? throw new ArgumentNullException(nameof(scopedFilterManager));
        _maxFunctionCalls = config.MaxAgenticIterations;
        _providerErrorHandler = providerErrorHandler;
        // Used for middleware dependency injection (e.g., ILoggerFactory)
        _serviceProvider = serviceProvider;

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
        _agentTurn = new AgentTurn(
            _baseClient,
            config.ConfigureOptions,
            config.ChatClientMiddleware,
            serviceProvider);

        // Initialize unified scoping manager
        var skills = skillScopingManager?.GetSkills() ?? Enumerable.Empty<HPD_Agent.Skills.SkillDefinition>();
        var initialTools = (mergedOptions ?? config.Provider?.DefaultChatOptions)?.Tools?
            .OfType<AIFunction>().ToList() ?? new List<AIFunction>();
        _scopingManager = new HPD_Agent.Scoping.UnifiedScopingManager(skills, initialTools, null);

        _toolScheduler = new ToolScheduler(this, _functionCallProcessor, _permissionManager, config, _scopingManager);

        // ═══════════════════════════════════════════════════════
        // INITIALIZE OBSERVABILITY SERVICES
        // ═══════════════════════════════════════════════════════

        // Resolve optional dependencies from service provider
        var loggerFactory = serviceProvider?.GetService(typeof(ILoggerFactory))
            as ILoggerFactory;
        _cache = serviceProvider?.GetService(typeof(IDistributedCache)) as IDistributedCache;
        _jsonOptions = global::Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions;
        _cachingConfig = config.Caching;

        // Create telemetry service if enabled
        if (config.Telemetry?.Enabled ?? true)
        {
            _telemetryService = new AgentTelemetryService(config.Telemetry ?? new TelemetryConfig());
        }

        // Create logging service if enabled and logger available
        if (loggerFactory != null && (config.Logging?.Enabled ?? true))
        {
            var logger = loggerFactory.CreateLogger<Agent>();
            _loggingService = new AgentLoggingService(logger, config.Logging ?? new LoggingConfig());
        }
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

    #region internal loop
    /// <summary>
    /// Protocol-agnostic core agentic loop that emits internal events.
    /// This method contains all the agent logic without any protocol-specific concerns.
    /// Adapters convert internal events to protocol-specific formats as needed.
    /// 
    /// ARCHITECTURE:
    /// - Uses AgentDecisionEngine (pure, testable) for all decision logic
    /// - Executes decisions INLINE to preserve real-time streaming
    /// - State managed via immutable AgentLoopState for testability
    /// 
    /// This hybrid approach delivers:
    /// - Testable decision logic (unit tests in microseconds)
    /// - Real-time streaming (no buffering overhead)
    /// - Clear separation of concerns (decisions vs execution)
    /// </summary>
    private async IAsyncEnumerable<InternalAgentEvent> RunAgenticLoopInternal(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string[]? documentPaths,
        List<ChatMessage> turnHistory,
        TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletionSource,
        TaskCompletionSource<ReductionMetadata?> reductionCompletionSource,
        ConversationThread? thread = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // ═══════════════════════════════════════════════════════
        // OBSERVABILITY: Track orchestration start time
        // ═══════════════════════════════════════════════════════
        var orchestrationStartTime = DateTime.UtcNow;

        // Create orchestration activity to group all agent turns and function calls
        using var orchestrationActivity = ActivitySource.StartActivity(
            "agent.orchestration",
            ActivityKind.Internal);

        orchestrationActivity?.SetTag("agent.name", _name);
        orchestrationActivity?.SetTag("agent.max_iterations", _maxFunctionCalls);
        orchestrationActivity?.SetTag("agent.provider", ProviderKey);
        orchestrationActivity?.SetTag("agent.model", ModelId);

        // Track root agent for event bubbling across nested agent calls
        var previousRootAgent = RootAgent;
        RootAgent ??= this;

        // Process documents if provided
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

        _conversationId = conversationId;

        try
        {
            // Emit MESSAGE TURN started event
            yield return new InternalMessageTurnStartedEvent(messageTurnId, conversationId);

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
                reductionCompletionSource.TrySetException(ex);
                historyCompletionSource.TrySetException(ex);
                throw;
            }

            // Set reduction metadata immediately
            reductionCompletionSource.TrySetResult(reductionMetadata);

            // ═══════════════════════════════════════════════════════
            // CREATE OR RESTORE IMMUTABLE STATE & DECISION ENGINE
            // ═══════════════════════════════════════════════════════

            // Build configuration first (needed for resume logging)
            var config = BuildDecisionConfiguration(effectiveOptions);
            var decisionEngine = new AgentDecisionEngine();

            AgentLoopState state;

            if (thread?.ExecutionState is { } executionState)
            {
                // ✅ RESUME: Restore from checkpoint
                state = executionState;

                // ═══════════════════════════════════════════════════════
                // RESTORE PENDING WRITES (partial failure recovery)
                // ═══════════════════════════════════════════════════════
                if (Config?.EnablePendingWrites == true &&
                    Config?.Checkpointer != null &&
                    state.ETag != null)
                {
                    try
                    {
                        var pendingWrites = await Config.Checkpointer.LoadPendingWritesAsync(
                            thread.Id,
                            state.ETag,
                            effectiveCancellationToken).ConfigureAwait(false);

                        // Record telemetry
                        _telemetryService?.RecordPendingWritesLoad(pendingWrites.Count, thread.Id);

                        if (pendingWrites.Count > 0)
                        {
                            // Restore pending writes to AgentLoopState
                            state = state with { PendingWrites = pendingWrites.ToImmutableList() };
                        }
                    }
                    catch (Exception)
                    {
                        // Swallow errors - pending writes are an optimization
                        // If loading fails, the system will just re-execute the functions
                    }
                }

                // Log resume (use specific logging method if available)
                try
                {
                    _loggingService?.LogIterationStart(_name, state.Iteration, config.MaxIterations);
                }
                catch (Exception)
                {
                    // Observability errors shouldn't break agent execution
                }

                // Emit state snapshot for observability
                yield return new InternalStateSnapshotEvent(
                    CurrentIteration: state.Iteration,
                    MaxIterations: config.MaxIterations,
                    IsTerminated: state.IsTerminated,
                    TerminationReason: state.TerminationReason,
                    ConsecutiveErrorCount: state.ConsecutiveFailures,
                    CompletedFunctions: new List<string>(state.CompletedFunctions));
            }
            else
            {
                // ✅ FRESH RUN: Initialize new state
                state = AgentLoopState.Initial(effectiveMessages.ToList(), messageTurnId, conversationId, this.Name);
            }

            ChatResponse? lastResponse = null;

            // Track expanded plugins/skills (message-turn scoped)
            // Note: These are now tracked in state.ExpandedPlugins and state.ExpandedSkills

            // Collect all response updates to build final history
            var responseUpdates = new List<ChatResponseUpdate>();

            // ═══════════════════════════════════════════════════════
            // OBSERVABILITY: Start telemetry and logging
            // ═══════════════════════════════════════════════════════
            Activity? telemetryActivity = null;
            try
            {
                telemetryActivity = _telemetryService?.StartOrchestration(
                    _name,
                    effectiveOptions?.ModelId ?? ModelId,
                    ProviderKey,
                    config.MaxIterations,
                    effectiveMessages,
                    effectiveOptions,
                    state);

                // Note: Basic orchestration start logging removed - use Microsoft's LoggingChatClient instead
            }
            catch (Exception)
            {
                // Observability errors shouldn't break agent execution
            }

            // ═══════════════════════════════════════════════════════
            // MAIN AGENTIC LOOP (Hybrid: Pure Decisions + Inline Execution)
            // ═══════════════════════════════════════════════════════

            while (!state.IsTerminated && state.Iteration < config.MaxIterations)
            {
                // Generate message ID for this iteration
                var assistantMessageId = Guid.NewGuid().ToString();

                // Emit iteration start
                yield return new InternalAgentTurnStartedEvent(state.Iteration);

                // Emit state snapshot for testing/debugging
                yield return new InternalStateSnapshotEvent(
                    CurrentIteration: state.Iteration,
                    MaxIterations: _maxFunctionCalls,
                    IsTerminated: state.IsTerminated,
                    TerminationReason: state.TerminationReason,
                    ConsecutiveErrorCount: state.ConsecutiveFailures,
                    CompletedFunctions: new List<string>(state.CompletedFunctions));

                // Drain filter events before decision
                while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
                    yield return filterEvt;

                // ═══════════════════════════════════════════════════
                // FUNCTIONAL CORE: Pure Decision (No I/O)
                // ═══════════════════════════════════════════════════

                var decision = decisionEngine.DecideNextAction(state, lastResponse, config);

                // ═══════════════════════════════════════════════════
                // OBSERVABILITY: Log and record decision
                // ═══════════════════════════════════════════════════
                try
                {
                    _loggingService?.LogIterationStart(_name, state.Iteration, config.MaxIterations);
                    _loggingService?.LogDecision(_name, decision, state);
                    _telemetryService?.RecordDecision(telemetryActivity, decision, state, _name, config);

                    // If circuit breaker triggered, log and record it specifically
                    if (decision is AgentDecision.Terminate terminate &&
                        terminate.Reason.Contains("Circuit breaker", StringComparison.OrdinalIgnoreCase))
                    {
                        // Extract function name from termination reason (format: "Circuit breaker triggered: function 'X' called...")
                        var match = System.Text.RegularExpressions.Regex.Match(
                            terminate.Reason,
                            @"function '([^']+)' called (\d+)");
                        if (match.Success)
                        {
                            var functionName = match.Groups[1].Value;
                            var count = int.Parse(match.Groups[2].Value);
                            _loggingService?.LogCircuitBreakerTriggered(_name, functionName, count);
                            _telemetryService?.RecordCircuitBreakerTrigger(
                                _name,
                                functionName,
                                count,
                                config.MaxConsecutiveFunctionCalls ?? int.MaxValue);
                        }
                    }
                }
                catch (Exception)
                {
                    // Observability errors shouldn't break execution
                }

                // Drain filter events after decision-making, before execution
                // CRITICAL: Ensures events emitted during decision logic are yielded before LLM streaming starts
                while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
                    yield return filterEvt;

                // ═══════════════════════════════════════════════════════════════
                // ARCHITECTURAL DECISION: Inline Execution for Zero-Latency Streaming
                // ═══════════════════════════════════════════════════════════════
                //
                // LLM calls and tool execution happen INLINE (not extracted to methods)
                // to preserve real-time streaming. Extracting would add 200-3000ms latency
                // due to buffering events before returning them.
                //
                // Why inline?
                // - Zero latency: Events yielded immediately as they arrive from LLM
                // - True streaming: No buffering overhead between method boundaries
                // - Testability: Decision logic extracted to AgentDecisionEngine (pure & fast)
                //
                // Trade-offs:
                // - Longer main method (~800 lines vs ~200 if extracted)
                // - Execution logic tested via integration tests (not unit tests)
                //
                // See: Proposals/Urgent/TESTABILITY_REFACTORING_PROPOSAL.md
                //      Section: "Event Streaming Architecture Deep Dive" for full analysis
                // ═══════════════════════════════════════════════════════════════

                // ═══════════════════════════════════════════════════
                // IMPERATIVE SHELL: Execute Decision INLINE with Real-time Streaming
                // ═══════════════════════════════════════════════════

                if (decision is AgentDecision.CallLLM)
                {
                    // ═══════════════════════════════════════════════════════
                    // EXECUTE LLM CALL (INLINE - NOT EXTRACTED)
                    // ═══════════════════════════════════════════════════════

                    // History optimization: Only send new messages if server manages history
                    IEnumerable<ChatMessage> messagesToSend;
                    if (state.InnerClientTracksHistory && state.Iteration > 0)
                    {
                        messagesToSend = state.CurrentMessages.Skip(state.MessagesSentToInnerClient);
                    }
                    else
                    {
                        messagesToSend = state.CurrentMessages;
                    }

                    // Apply plugin scoping if enabled
                    var scopedOptions = effectiveOptions;
                    if (Config?.PluginScoping?.Enabled == true && effectiveOptions?.Tools != null && effectiveOptions.Tools.Count > 0)
                    {
                        scopedOptions = ApplyPluginScoping(effectiveOptions, state.ExpandedPlugins, state.ExpandedSkills);
                    }
                    else if (state.InnerClientTracksHistory && scopedOptions != null && scopedOptions.ConversationId != _conversationId)
                    {
                        scopedOptions = new ChatOptions
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
                            ConversationId = _conversationId
                        };
                    }

                    // Streaming state
                    var assistantContents = new List<AIContent>();
                    var toolRequests = new List<FunctionCallContent>();
                    bool messageStarted = false;
                    bool reasoningStarted = false;
                    bool reasoningMessageStarted = false;

                    // Stream LLM response with IMMEDIATE event yielding
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

                                    yield return new InternalReasoningDeltaEvent(reasoning.Text, assistantMessageId);
                                    assistantContents.Add(reasoning);
                                }
                                else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                                {
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

                                    if (!messageStarted)
                                    {
                                        yield return new InternalTextMessageStartEvent(assistantMessageId, "assistant");
                                        messageStarted = true;
                                    }

                                    assistantContents.Add(textContent);
                                    yield return new InternalTextDeltaEvent(textContent.Text, assistantMessageId);
                                }
                                else if (content is FunctionCallContent functionCall)
                                {
                                    if (!messageStarted)
                                    {
                                        yield return new InternalTextMessageStartEvent(assistantMessageId, "assistant");
                                        messageStarted = true;
                                    }

                                    yield return new InternalToolCallStartEvent(
                                        functionCall.CallId,
                                        functionCall.Name ?? string.Empty,
                                        assistantMessageId);

                                    if (functionCall.Arguments != null && functionCall.Arguments.Count > 0)
                                    {
                                        var argsJson = System.Text.Json.JsonSerializer.Serialize(
                                            functionCall.Arguments,
                                            AGUIJsonContext.Default.DictionaryStringObject);

                                        yield return new InternalToolCallArgsEvent(functionCall.CallId, argsJson);
                                    }

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

                    // Capture ConversationId from the agent turn response
                    if (_agentTurn.LastResponseConversationId != null)
                    {
                        _conversationId = _agentTurn.LastResponseConversationId;
                    }
                    else if (state.InnerClientTracksHistory)
                    {
                        // Service stopped returning ConversationId - disable tracking
                        state = state.DisableHistoryTracking();
                    }

                    // Close the message if we started one
                    if (messageStarted)
                    {
                        yield return new InternalTextMessageEndEvent(assistantMessageId);
                    }

                    // If there are tool requests, execute them immediately
                    if (toolRequests.Count > 0)
                    {
                        // Create assistant message with tool calls
                        var assistantMessage = new ChatMessage(ChatRole.Assistant, assistantContents);
                        var currentMessages = state.CurrentMessages.ToList();
                        currentMessages.Add(assistantMessage);

                        // ✅ FIXED: Update state immediately after modifying messages
                        state = state.WithMessages(currentMessages);

                        // ✅ FIXED: Update history tracking with count AFTER adding assistant message
                        if (_agentTurn.LastResponseConversationId != null)
                        {
                            state = state.EnableHistoryTracking(currentMessages.Count);
                        }

                        // Create assistant message for history WITHOUT reasoning
                        var historyContents = assistantContents.Where(c => c is not TextReasoningContent).ToList();
                        var hasNonEmptyText = historyContents
                            .OfType<TextContent>()
                            .Any(t => !string.IsNullOrWhiteSpace(t.Text));

                        if (hasNonEmptyText)
                        {
                            var historyMessage = new ChatMessage(ChatRole.Assistant, historyContents);
                            turnHistory.Add(historyMessage);
                        }

                        // ═══════════════════════════════════════════════════════
                        // TOOL EXECUTION (Inline - NOT via decision engine)
                        // ═══════════════════════════════════════════════════════

                        // Apply plugin scoping if enabled
                        var effectiveOptionsForTools = effectiveOptions;
                        if (Config?.PluginScoping?.Enabled == true && effectiveOptions?.Tools != null && effectiveOptions.Tools.Count > 0)
                        {
                            effectiveOptionsForTools = ApplyPluginScoping(effectiveOptions, state.ExpandedPlugins, state.ExpandedSkills);
                        }

                        // Yield filter events before tool execution
                        while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
                        {
                            yield return filterEvt;
                        }

                        // ═══════════════════════════════════════════════════════
                        // CIRCUIT BREAKER: Check BEFORE execution (prevent the call)
                        // ═══════════════════════════════════════════════════════
                        if (Config?.AgenticLoop?.MaxConsecutiveFunctionCalls is { } maxConsecutiveCalls)
                        {
                            bool circuitBreakerTriggered = false;
                            
                            foreach (var toolRequest in toolRequests)
                            {
                                var signature = ComputeFunctionSignatureFromContent(toolRequest);
                                var toolName = toolRequest.Name ?? "_unknown";
                                
                                // Calculate what the count WOULD BE if we execute this tool
                                var lastSig = state.LastSignaturePerTool.GetValueOrDefault(toolName);
                                var isIdentical = !string.IsNullOrEmpty(lastSig) && signature == lastSig;
                                
                                var countAfterExecution = isIdentical 
                                    ? state.ConsecutiveCountPerTool.GetValueOrDefault(toolName, 0) + 1
                                    : 1;

                                // Check if executing this tool would exceed the limit
                                if (countAfterExecution >= maxConsecutiveCalls)
                                {
                                    var errorMessage = $"⚠️ Circuit breaker triggered: Function '{toolRequest.Name}' " +
                                        $"with same arguments would be called {countAfterExecution} times consecutively. " +
                                        $"Stopping to prevent infinite loop.";
                                    
                                    yield return new InternalTextDeltaEvent(errorMessage, assistantMessageId);
                                    
                                    var terminationReason = $"Circuit breaker: '{toolRequest.Name}' " +
                                        $"with same arguments would be called {countAfterExecution} times consecutively";
                                    state = state.Terminate(terminationReason);
                                    circuitBreakerTriggered = true;
                                    break;
                                }
                            }

                            if (circuitBreakerTriggered)
                            {
                                break; // Exit the main loop WITHOUT executing tools
                            }
                        }

                        // Execute tools with event polling (CRITICAL for permissions)
                        var executeTask = _toolScheduler.ExecuteToolsAsync(
                            currentMessages,
                            toolRequests,
                            effectiveOptionsForTools,
                            state,
                            effectiveCancellationToken);

                        // Poll for filter events while tool execution is in progress
                        while (!executeTask.IsCompleted)
                        {
                            var delayTask = Task.Delay(10, effectiveCancellationToken);
                            await Task.WhenAny(executeTask, delayTask).ConfigureAwait(false);

                            while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
                            {
                                yield return filterEvt;
                            }
                        }

                        var (toolResultMessage, pluginExpansions, skillExpansions, successfulFunctions) = await executeTask.ConfigureAwait(false);

                        // Final drain
                        while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
                        {
                            yield return filterEvt;
                        }

                        // ═══════════════════════════════════════════════════════
                        // PENDING WRITES (save successful function results immediately)
                        // ═══════════════════════════════════════════════════════
                        if (Config?.EnablePendingWrites == true &&
                            Config?.Checkpointer != null &&
                            thread != null &&
                            state.ETag != null)
                        {
                            SavePendingWritesFireAndForget(toolResultMessage, state, Config.Checkpointer, thread.Id);
                        }

                        // ═══════════════════════════════════════════════════════
                        // ERROR TRACKING (AFTER tool execution, BEFORE container updates)
                        // ✅ FIXED: Enhanced error detection to reduce false positives
                        bool hasErrors = toolResultMessage.Contents
                            .OfType<FunctionResultContent>()
                            .Any(r => 
                            {
                                // Primary signal: Exception present
                                if (r.Exception != null) return true;
                                
                                // Secondary signal: Result contains error indicators
                                var resultStr = r.Result?.ToString();
                                if (string.IsNullOrEmpty(resultStr)) return false;
                                
                                // Check for definitive error patterns (case-insensitive)
                                return resultStr.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                                       resultStr.StartsWith("Failed:", StringComparison.OrdinalIgnoreCase) ||
                                       // More precise exception matching - look for error context
                                       resultStr.Contains("exception occurred", StringComparison.OrdinalIgnoreCase) ||
                                       resultStr.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase) ||
                                       resultStr.Contains("exception was thrown", StringComparison.OrdinalIgnoreCase) ||
                                       // More precise rate limit matching - look for error context
                                       resultStr.Contains("rate limit exceeded", StringComparison.OrdinalIgnoreCase) ||
                                       resultStr.Contains("rate limited", StringComparison.OrdinalIgnoreCase) ||
                                       resultStr.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase) ||
                                       resultStr.Contains("quota reached", StringComparison.OrdinalIgnoreCase);
                            });

                        if (hasErrors)
                        {
                            state = state.WithFailure();

                            var maxConsecutiveErrors = Config?.ErrorHandling?.MaxRetries ?? 3;
                            if (state.ConsecutiveFailures >= maxConsecutiveErrors)
                            {
                                var errorMessage = $"⚠️ Maximum consecutive errors ({maxConsecutiveErrors}) exceeded. " +
                                    "Stopping execution to prevent infinite error loop.";
                                
                                yield return new InternalTextDeltaEvent(errorMessage, assistantMessageId);

                                var terminationReason = $"Exceeded maximum consecutive errors ({maxConsecutiveErrors})";
                                state = state.Terminate(terminationReason);
                                break;  // ✅ EXIT LOOP
                            }
                        }
                        else
                        {
                            state = state.WithSuccess();
                        }

                        // ═══════════════════════════════════════════════════════
                        // UPDATE STATE WITH CONTAINER EXPANSIONS
                        // ═══════════════════════════════════════════════════════
                        foreach (var pluginName in pluginExpansions)
                        {
                            state = state.WithExpandedPlugin(pluginName);
                        }
                        foreach (var skillName in skillExpansions)
                        {
                            state = state.WithExpandedSkill(skillName);
                        }

                        // ═══════════════════════════════════════════════════════
                        // UPDATE STATE WITH COMPLETED FUNCTIONS  
                        // ═══════════════════════════════════════════════════════
                        foreach (var functionName in successfulFunctions)
                        {
                            state = state.CompleteFunction(functionName);
                        }



                        // ═══════════════════════════════════════════════════════
                        // FILTER CONTAINER EXPANSIONS
                        // ═══════════════════════════════════════════════════════
                        var nonContainerResults = FilterContainerResults(
                            toolResultMessage.Contents,
                            toolRequests,
                            effectiveOptionsForTools);

                        // Add filtered results to persistent history
                        if (nonContainerResults.Count > 0)
                        {
                            var filteredMessage = new ChatMessage(ChatRole.Tool, nonContainerResults);
                            currentMessages.Add(filteredMessage);
                        }

                        // Add ALL results (including container expansions) to turn history
                        turnHistory.Add(toolResultMessage);

                        // ═══════════════════════════════════════════════════════
                        // EMIT TOOL RESULT EVENTS
                        // ═══════════════════════════════════════════════════════
                        foreach (var content in toolResultMessage.Contents)
                        {
                            if (content is FunctionResultContent result)
                            {
                                yield return new InternalToolCallEndEvent(result.CallId);
                                yield return new InternalToolCallResultEvent(result.CallId, result.Result?.ToString() ?? "null");
                            }
                        }

                        // ═══════════════════════════════════════════════════════
                        // CIRCUIT BREAKER: Update state after execution
                        // ═══════════════════════════════════════════════════════
                        foreach (var toolRequest in toolRequests)
                        {
                            var signature = ComputeFunctionSignatureFromContent(toolRequest);
                            state = state.RecordToolCall(toolRequest.Name ?? "_unknown", signature);
                        }

                        // Note: Actual circuit breaker CHECK happens BEFORE execution (above)
                        // This just updates the state for the next iteration

                        // Update state with new messages
                        state = state.WithMessages(currentMessages);

                        // Build ChatResponse for decision engine (after execution)
                        lastResponse = new ChatResponse(currentMessages.Where(m => m.Role == ChatRole.Assistant).ToList());
                        
                        // ✅ FIXED: Clear responseUpdates AFTER building the response
                        responseUpdates.Clear();
                    }
                    else
                    {
                        // No tools called - we're done
                        var finalResponse = ConstructChatResponseFromUpdates(responseUpdates);
                        lastResponse = finalResponse;
                        
                        // ✅ FIXED: Clear responseUpdates AFTER constructing final response
                        responseUpdates.Clear();
                        
                        // ✅ FIXED: Update history tracking if we have ConversationId (no assistant message added in this path)
                        if (_agentTurn.LastResponseConversationId != null)
                        {
                            // For non-tool responses, use current message count (no new assistant message added)
                            state = state.EnableHistoryTracking(state.CurrentMessages.Count);
                        }
                        
                        state = state.Terminate("Completed successfully");
                    }
                }
                else if (decision is AgentDecision.Complete complete)
                {
                    // Completion - extract final message if needed
                    lastResponse = complete.FinalResponse;
                    state = state.Terminate("Completed successfully");
                }
                else if (decision is AgentDecision.Terminate terminate)
                {
                    state = state.Terminate(terminate.Reason);
                }
                else
                {
                    throw new InvalidOperationException($"Unknown decision type: {decision.GetType().Name}");
                }

                // Emit iteration end
                yield return new InternalAgentTurnFinishedEvent(state.Iteration);

                // Advance to next iteration
                state = state.NextIteration();

                // ═══════════════════════════════════════════════════════
                // ✅ NEW: CHECKPOINT AFTER EACH ITERATION (if configured)
                // ═══════════════════════════════════════════════════════

                if (thread != null &&
                    Config?.CheckpointFrequency == CheckpointFrequency.PerIteration &&
                    Config?.Checkpointer != null)
                {
                    // Fire-and-forget async checkpoint (non-blocking!)
                    // Update metadata to reflect loop checkpoint
                    var checkpointState = state with
                    {
                        Metadata = new CheckpointMetadata
                        {
                            Source = CheckpointSource.Loop,
                            Step = state.Iteration
                        }
                    };

                    _ = Task.Run(async () =>
                    {
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            thread.ExecutionState = checkpointState;
                            await Config.Checkpointer.SaveThreadAsync(thread, CancellationToken.None);

                            // Cleanup pending writes after successful checkpoint (fire-and-forget)
                            if (Config.EnablePendingWrites && checkpointState.ETag != null)
                            {
                                var iteration = checkpointState.Iteration;
                                var threadId = thread.Id;
                                var telemetry = _telemetryService;

                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await Config.Checkpointer.DeletePendingWritesAsync(
                                            threadId,
                                            checkpointState.ETag,
                                            CancellationToken.None);

                                        // Record telemetry
                                        telemetry?.RecordPendingWritesDelete(threadId, iteration);
                                    }
                                    catch
                                    {
                                        // Swallow errors - cleanup is best-effort
                                    }
                                });
                            }

                            stopwatch.Stop();
                            _telemetryService?.RecordCheckpointSuccess(stopwatch.Elapsed, thread.Id, state.Iteration);
                        }
                        catch (Exception ex)
                        {
                            // Checkpoint failures are non-fatal (fire-and-forget)
                            // Agent execution continues even if checkpoint fails
                            _loggingService?.LogCheckpointFailure(ex, thread.Id, state.Iteration);
                            _telemetryService?.RecordCheckpointFailure(ex, thread.Id, state.Iteration);
                        }
                    }, CancellationToken.None);
                }
            }

            // ═══════════════════════════════════════════════════════
            // FINALIZATION
            // ═══════════════════════════════════════════════════════

            // Check if we exited because max iterations was reached
            if (state.Iteration >= _maxFunctionCalls && !state.IsTerminated)
            {
                var terminationMessageId = $"msg_{Guid.NewGuid()}";
                var terminationMessage = (Config?.Messages ?? new AgentMessagesConfig()).FormatMaxIterationsReached(_maxFunctionCalls);

                yield return new InternalTextMessageStartEvent(terminationMessageId, "assistant");
                yield return new InternalTextDeltaEvent(terminationMessage, terminationMessageId);
                yield return new InternalTextMessageEndEvent(terminationMessageId);

                turnHistory.Add(new ChatMessage(ChatRole.Assistant, terminationMessage));

                var terminationReason = $"Maximum iterations reached ({_maxFunctionCalls})";
                state = state.Terminate(terminationReason);
            }

            // Build the complete history including the final assistant message
            var hadAnyToolCalls = turnHistory.Any(m => m.Role == ChatRole.Tool);

            if (!hadAnyToolCalls && responseUpdates.Any())
            {
                var finalResponse = ConstructChatResponseFromUpdates(responseUpdates);
                if (finalResponse.Messages.Count > 0)
                {
                    var finalAssistantMessage = finalResponse.Messages[0];

                    if (finalAssistantMessage.Contents.Count > 0)
                    {
                        turnHistory.Add(finalAssistantMessage);
                    }
                }
            }

            // Final drain of filter events after loop
            while (_eventCoordinator.EventReader.TryRead(out var filterEvt))
                yield return filterEvt;

            // Emit MESSAGE TURN finished event
            yield return new InternalMessageTurnFinishedEvent(messageTurnId, conversationId);

            // Record orchestration telemetry metrics
            orchestrationActivity?.SetTag("agent.total_iterations", state.Iteration);
            orchestrationActivity?.SetTag("agent.total_function_calls", state.CompletedFunctions.Count);
            orchestrationActivity?.SetTag("agent.termination_reason", state.TerminationReason ?? "completed");
            orchestrationActivity?.SetTag("agent.was_terminated", state.IsTerminated);

            if (reductionMetadata != null)
            {
                orchestrationActivity?.SetTag("agent.history_reduction_occurred", true);
                orchestrationActivity?.SetTag("agent.history_messages_removed", reductionMetadata.MessagesRemovedCount);
            }

            // ═══════════════════════════════════════════════════════
            // OBSERVABILITY: Record completion metrics
            // ═══════════════════════════════════════════════════════
            try
            {
                // Note: Token usage, duration, and finish reason are now tracked by Microsoft's OpenTelemetryChatClient

                _loggingService?.LogCompletion(_name, state.Iteration);
                _telemetryService?.RecordCompletion(
                    telemetryActivity,
                    state,
                    effectiveOptions?.ModelId ?? ModelId);

                telemetryActivity?.Dispose();
            }
            catch (Exception)
            {
                // Observability errors shouldn't break execution
            }

            // ═══════════════════════════════════════════════════════
            // ✅ NEW: FINAL CHECKPOINT (if configured)
            // ═══════════════════════════════════════════════════════

            if (thread != null && Config?.Checkpointer != null)
            {
                var finalState = state with
                {
                    Metadata = new CheckpointMetadata
                    {
                        Source = CheckpointSource.Loop,
                        Step = state.Iteration
                    }
                };

                thread.ExecutionState = finalState;
                await Config.Checkpointer.SaveThreadAsync(thread, cancellationToken);

                // Cleanup pending writes after successful final checkpoint (fire-and-forget)
                if (Config.EnablePendingWrites && finalState.ETag != null)
                {
                    var iteration = finalState.Iteration;
                    var threadId = thread.Id;
                    var telemetry = _telemetryService;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Config.Checkpointer.DeletePendingWritesAsync(
                                threadId,
                                finalState.ETag,
                                CancellationToken.None);

                            // Record telemetry
                            telemetry?.RecordPendingWritesDelete(threadId, iteration);
                        }
                        catch
                        {
                            // Swallow errors - cleanup is best-effort
                        }
                    });
                }
            }

            historyCompletionSource.TrySetResult(turnHistory);
        }
        finally
        {
            RootAgent = previousRootAgent;
        }
    }

    /// <summary>
    /// Applies plugin scoping to limit which tools are visible to the LLM.
    /// Preserves expanded plugins/skills from previous iterations.
    /// </summary>
    private ChatOptions? ApplyPluginScoping(
        ChatOptions? options,
        ImmutableHashSet<string> expandedPlugins,
        ImmutableHashSet<string> expandedSkills)
    {
        if (options?.Tools == null || Config?.PluginScoping == null)
            return options;

        // PERFORMANCE: Single-pass extraction using manual loop
        var aiFunctions = new List<AIFunction>(options.Tools.Count);
        for (int i = 0; i < options.Tools.Count; i++)
        {
            if (options.Tools[i] is AIFunction af)
                aiFunctions.Add(af);
        }

        // Get plugin-scoped functions
        var scopedFunctions = _scopingManager.GetToolsForAgentTurn(
            aiFunctions,
            expandedPlugins,
            expandedSkills);

        // Manual cast to AITool list
        var scopedTools = new List<AITool>(scopedFunctions.Count);
        for (int i = 0; i < scopedFunctions.Count; i++)
        {
            scopedTools.Add(scopedFunctions[i]);
        }

        return new ChatOptions
        {
            ModelId = options.ModelId,
            Tools = scopedTools,
            ToolMode = options.ToolMode,
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            TopP = options.TopP,
            TopK = options.TopK,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            StopSequences = options.StopSequences,
            ResponseFormat = options.ResponseFormat,
            Seed = options.Seed,
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            Instructions = options.Instructions,
            RawRepresentationFactory = options.RawRepresentationFactory,
            AdditionalProperties = options.AdditionalProperties,
            ConversationId = _conversationId
        };
    }

    /// <summary>
    /// Filters out container expansion results from history.
    /// Container expansions are temporary - only relevant within the current turn.
    /// Persistent history should NOT contain "ExpandPlugin" or "ExpandSkill" results.
    /// </summary>
    /// <param name="contents">All tool result contents</param>
    /// <param name="toolRequests">Original tool call requests</param>
    /// <param name="options">Chat options containing tool metadata</param>
    /// <returns>Filtered contents (non-container results only)</returns>
    private static List<AIContent> FilterContainerResults(
        IList<AIContent> contents,
        IList<FunctionCallContent> toolRequests,
        ChatOptions? options)
    {
        var nonContainerResults = new List<AIContent>(contents.Count);

        foreach (var content in contents)
        {
            if (content is FunctionResultContent result)
            {
                // Check if this result is from a container function
                var isContainerResult = IsContainerResult(result, toolRequests, options);

                if (!isContainerResult)
                {
                    nonContainerResults.Add(content);
                }
            }
            else
            {
                // Non-function-result content always passes through
                nonContainerResults.Add(content);
            }
        }

        return nonContainerResults;
    }

    /// <summary>
    /// Checks if a function result is from a container expansion.
    /// </summary>
    /// <param name="result">The function result to check</param>
    /// <param name="toolRequests">Original tool call requests</param>
    /// <param name="options">Chat options containing tool metadata</param>
    /// <returns>True if this result is from a container function</returns>
    private static bool IsContainerResult(
        FunctionResultContent result,
        IList<FunctionCallContent> toolRequests,
        ChatOptions? options)
    {
        return toolRequests.Any(tr =>
            tr.CallId == result.CallId &&
            options?.Tools?.OfType<AIFunction>()
                .FirstOrDefault(t => t.Name == tr.Name)
                ?.AdditionalProperties?.TryGetValue("IsContainer", out var isContainer) == true &&
            isContainer is bool isCont && isCont);
    }

    /// <summary>
    /// Checks if a function result is successful (no exception, no error message).
    /// </summary>
    private static bool IsFunctionResultSuccessful(FunctionResultContent result)
    {
        // Exception present = failure
        if (result.Exception != null)
            return false;

        // Check if result looks like an error message
        var resultStr = result.Result?.ToString();
        return !IsLikelyErrorString(resultStr);
    }

    /// <summary>
    /// Heuristic to detect error strings in function results.
    /// </summary>
    private static bool IsLikelyErrorString(string? s) =>
        !string.IsNullOrEmpty(s) &&
        (s.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
         s.StartsWith("Failed:", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("exception occurred", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("exception was thrown", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("rate limit exceeded", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("rate limited", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("quota reached", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Saves pending writes (successful function results) in a fire-and-forget manner.
    /// This allows the system to recover partial progress if a crash occurs before the iteration checkpoint completes.
    /// </summary>
    private void SavePendingWritesFireAndForget(
        ChatMessage toolResultMessage,
        AgentLoopState state,
        IThreadCheckpointer checkpointer,
        string threadId)
    {
        // Extract successful function results
        var successfulResults = toolResultMessage.Contents
            .OfType<FunctionResultContent>()
            .Where(IsFunctionResultSuccessful)
            .ToList();

        if (successfulResults.Count == 0)
            return;

        // Create pending writes
        var pendingWrites = new List<PendingWrite>();
        foreach (var result in successfulResults)
        {
            var pendingWrite = new PendingWrite
            {
                CallId = result.CallId,
                FunctionName = result.CallId, // Note: We don't have function name here, but CallId is unique
                ResultJson = System.Text.Json.JsonSerializer.Serialize(result.Result),
                CompletedAt = DateTime.UtcNow,
                Iteration = state.Iteration,
                ThreadId = threadId
            };
            pendingWrites.Add(pendingWrite);
        }

        // Record telemetry
        _telemetryService?.RecordPendingWritesSave(pendingWrites.Count, threadId, state.Iteration);

        // Save in background (fire-and-forget)
        _ = Task.Run(async () =>
        {
            try
            {
                await checkpointer.SavePendingWritesAsync(
                    threadId,
                    state.ETag!,
                    pendingWrites,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Swallow errors - pending writes are optimization, not critical
                // If saving fails, the system will just re-execute the functions on resume
            }
        });
    }

    #endregion

    #region IChatClient Implementation



    /// <summary>
    /// Protocol-agnostic core agentic loop that emits internal events.
    /// This method contains all the agent logic without any protocol-specific concerns.
    /// Adapters convert internal events to protocol-specific formats as needed.
    /// </summary>

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
        UsageDetails? usage = null;

        foreach (var update in updates)
        {
            if (update.Contents != null)
            {
                foreach (var content in update.Contents)
                {
                    // Extract usage from UsageContent (streaming providers send this in final chunk)
                    if (content is UsageContent usageContent)
                    {
                        usage = usageContent.Details;
                    }
                    // Only include TextContent in message (exclude TextReasoningContent to save tokens)
                    else if (content is TextContent && content is not TextReasoningContent)
                    {
                        allContents.Add(content);
                    }
                }
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
                CreatedAt = createdAt,
                Usage = usage
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
            CreatedAt = createdAt,
            Usage = usage
        };
    }

    /// <inheritdoc />

    /// <inheritdoc />
    public void Dispose()
    {
        _baseClient?.Dispose();
        _eventCoordinator?.Dispose();
        _telemetryService?.Dispose();
        _turnGate?.Dispose();
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
        // Use the base client for summarization
        // Note: Custom summarizer provider configuration has been removed
        IChatClient summarizerClient = baseClient;

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
        var agentFunctionCalls = ContentExtractor.ExtractFunctionCallsFromHistory(finalHistory, _name);

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
    /// Generates a human-readable function signature for circuit breaker tracking.
    /// Wraps FunctionCallContent to use the standardized ComputeFunctionSignature method.
    /// </summary>
    /// <param name="toolCall">The function call to generate a signature for</param>
    /// <returns>Human-readable signature in format: "FunctionName(arg1=value1,arg2=value2)"</returns>
    private static string ComputeFunctionSignatureFromContent(FunctionCallContent toolCall)
    {
        // Convert FunctionCallContent to AgentToolCallRequest
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();
        var request = new AgentToolCallRequest(
            toolCall.Name ?? string.Empty,
            toolCall.CallId,
            args.ToImmutableDictionary());

        // Use the standardized signature computation
        return AgentDecisionEngine.ComputeFunctionSignature(request);
    }

    /// <summary>
    /// Builds lightweight configuration for decision engine from full agent config.
    /// </summary>
    /// <param name="options">Chat options containing tool list</param>
    /// <returns>Configuration with only fields needed for decision-making</returns>
    private AgentConfiguration BuildDecisionConfiguration(ChatOptions? options)
    {
        // Extract available tool names from options
        var availableTools = new HashSet<string>(StringComparer.Ordinal);

        if (options?.Tools != null)
        {
            foreach (var tool in options.Tools)
            {
                if (tool is AIFunction func && !string.IsNullOrEmpty(func.Name))
                    availableTools.Add(func.Name);
            }
        }

        // Build configuration from AgentConfig fields
        return AgentConfiguration.FromAgentConfig(
            Config,
            _maxFunctionCalls,
            availableTools);
    }

    #endregion

    #region Testing and Advanced API

    /// <summary>
    /// Runs the agentic loop and streams internal agent events (for testing and advanced scenarios).
    /// This exposes the raw internal event stream without protocol conversion.
    /// Use this for testing to verify event sequences and agent behavior.
    /// 
    /// <strong>Thread Safety:</strong> This method acquires a semaphore that ensures only one
    /// RunAgenticLoopAsync call per Agent instance can execute concurrently. Subsequent calls will wait
    /// for the current one to complete. This prevents race conditions on _conversationId and
    /// shared event coordinator state.
    /// </summary>
    /// <param name="messages">The conversation messages</param>
    /// <param name="options">Chat options including tools</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of internal agent events</returns>
    public async IAsyncEnumerable<InternalAgentEvent> RunAgenticLoopAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Acquire exclusive turn gate to ensure single concurrent execution
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var turnHistory = new List<ChatMessage>();
            var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);

            await foreach (var evt in RunAgenticLoopInternal(
                messages,
                options,
                documentPaths: null,
                turnHistory,
                historyCompletionSource,
                reductionCompletionSource,
                thread: null,
                cancellationToken))
            {
                yield return evt;
            }
        }
        finally
        {
            _turnGate.Release();
        }
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
    /// Runs the agent with messages (streaming). Returns the internal event stream.
    /// This is the primary public API method for agent execution.
    /// 
    /// <strong>Thread Safety:</strong> This method acquires a semaphore that ensures only one
    /// RunAsync call per Agent instance can execute concurrently. Subsequent calls will wait
    /// for the current one to complete. This prevents race conditions on _conversationId and
    /// shared event coordinator state.
    /// </summary>
    /// <param name="messages">Messages to process</param>
    /// <param name="options">Optional chat options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of internal agent events</returns>
    public async IAsyncEnumerable<InternalAgentEvent> RunAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Acquire exclusive turn gate to ensure single concurrent execution
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var turnHistory = new List<ChatMessage>();
            var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var internalStream = RunAgenticLoopInternal(
                messages.ToList(),
                options,
                documentPaths: null,
                turnHistory,
                historyCompletionSource,
                reductionCompletionSource,
                thread: null,
                cancellationToken);

            await foreach (var evt in internalStream.WithCancellation(cancellationToken))
            {
                yield return evt;
            }
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Runs the agent with checkpoint/resume support via ConversationThread.
    /// Use this overload for durable execution with crash recovery.
    /// </summary>
    /// <param name="messages">Messages to process (empty array when resuming)</param>
    /// <param name="options">Chat options</param>
    /// <param name="thread">Thread containing conversation history and optional execution state checkpoint</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of internal agent events</returns>
    public async IAsyncEnumerable<InternalAgentEvent> RunAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        ConversationThread thread,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Validate resume semantics (4 scenarios)
        var hasMessages = messages?.Any() ?? false;
        var hasCheckpoint = thread.ExecutionState != null;
        var currentMessageCount = (await thread.GetMessagesAsync(cancellationToken)).Count;
        var hasHistory = currentMessageCount > 0;

        // Scenario 1: No checkpoint, no messages, no history → Error
        if (!hasCheckpoint && !hasMessages && !hasHistory)
        {
            throw new InvalidOperationException(
                "Cannot run agent with empty thread and no messages. " +
                "Provide either:\n" +
                "  1. New messages to process, OR\n" +
                "  2. A thread with existing history, OR\n" +
                "  3. A checkpoint to resume from");
        }

        // Scenario 4: Has checkpoint, has messages → Error
        if (hasCheckpoint && hasMessages)
        {
            throw new InvalidOperationException(
                $"Cannot add new messages when resuming mid-execution. " +
                $"Thread '{thread.Id}' is at iteration {thread.ExecutionState?.Iteration ?? 0}.\n\n" +
                "To resume execution:\n  await agent.RunAsync(Array.Empty<ChatMessage>(), thread);");
        }

        // Scenario 3: Has checkpoint, no messages → Resume (validate consistency)
        if (hasCheckpoint && !hasMessages)
        {
            thread.ExecutionState?.ValidateConsistency(currentMessageCount);
        }

        // Acquire exclusive turn gate to ensure single concurrent execution
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var turnHistory = new List<ChatMessage>();
            var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var internalStream = RunAgenticLoopInternal(
                messages.ToList(),
                options,
                documentPaths: null,
                turnHistory,
                historyCompletionSource,
                reductionCompletionSource,
                thread,
                cancellationToken);

            await foreach (var evt in internalStream.WithCancellation(cancellationToken))
            {
                yield return evt;
            }
        }
        finally
        {
            _turnGate.Release();
        }
    }

    #endregion

}

#region Agent Decision Engine
/// <summary>
/// Pure decision engine for agent execution loop.
/// Contains ZERO I/O operations - all decisions are deterministic and testable.
/// This is the "Functional Core" of the agent architecture.
/// </summary>
/// <remarks>
/// Key principles:
/// - Pure functions: Same inputs always produce same outputs
/// - No side effects: Doesn't modify external state
/// - No I/O: Doesn't call LLM, execute tools, or access network/disk
/// - Synchronous: All operations complete immediately
/// - Testable: Can be tested in microseconds without mocking
///
/// Design rationale:
/// By extracting decision logic from I/O operations, we achieve:
/// - Easy to reason about (pure state in, decision out)
/// - Property-based testing possible
/// </remarks>
internal sealed class AgentDecisionEngine
{
    /// <summary>
    /// Decides what the agent should do next based on current state.
    /// This is a pure function - same inputs always produce same output.
    /// </summary>
    /// <param name="state">Current immutable state</param>
    /// <param name="lastResponse">Response from last LLM call (null on first iteration)</param>
    /// <param name="config">Agent configuration (max iterations, circuit breaker settings, etc.)</param>
    /// <returns>Decision for what action to take next</returns>
    /// <remarks>
    /// Decision priority (checked in this order):
    /// 1. Termination conditions (already terminated, max iterations, too many failures)
    /// 2. First iteration (must call LLM)
    /// 3. Extract tool requests from LLM response
    /// 4. No tools = completion
    /// 5. Circuit breaker check (prevent infinite loops)
    /// 6. Unknown tools check (optional)
    /// 7. Execute tools
    /// </remarks>
    public AgentDecision DecideNextAction(
        AgentLoopState state,
        ChatResponse? lastResponse,
        AgentConfiguration config)
    {
        // ═══════════════════════════════════════════════════════
        // PRIORITY 1: TERMINATION CONDITIONS
        // These checks happen first - most important
        // ═══════════════════════════════════════════════════════

        // Check: Already terminated by external source (e.g., permission filter, manual termination)
        if (state.IsTerminated)
            return new AgentDecision.Terminate(state.TerminationReason ?? "Terminated");

        // Check: Max iterations exceeded
        if (state.Iteration >= config.MaxIterations)
            return new AgentDecision.Terminate(
                $"Maximum iterations ({config.MaxIterations}) reached");

        // Check: Too many consecutive errors
        if (state.ConsecutiveFailures >= config.MaxConsecutiveFailures)
            return new AgentDecision.Terminate(
                $"Maximum consecutive failures ({config.MaxConsecutiveFailures}) exceeded");

        // ═══════════════════════════════════════════════════════
        // PRIORITY 2: FIRST ITERATION
        // If no response yet, must call LLM
        // ═══════════════════════════════════════════════════════

        if (lastResponse == null)
            return AgentDecision.CallLLM.Instance;

        // ═══════════════════════════════════════════════════════
        // PRIORITY 3: CHECK IF RESPONSE IS COMPLETE
        // If last response has no tool calls, we're done
        // ═══════════════════════════════════════════════════════

        // Check if response has any tool calls
        bool hasToolCalls = lastResponse.Messages
            .Any(m => m.Contents.OfType<FunctionCallContent>().Any());

        if (!hasToolCalls)
            return new AgentDecision.Complete(lastResponse);

        // ═══════════════════════════════════════════════════════
        // PRIORITY 4: CIRCUIT BREAKER CHECK
        // Prevent infinite loops from repeated identical tool calls
        // ═══════════════════════════════════════════════════════

        if (config.MaxConsecutiveFunctionCalls.HasValue)
        {
            var toolRequests = ExtractToolRequestsFromResponse(lastResponse);

            foreach (var toolRequest in toolRequests)
            {
                var signature = ComputeFunctionSignature(toolRequest);

                // Check if this function signature has been called too many times consecutively
                if (state.ConsecutiveCountPerTool.TryGetValue(toolRequest.Name, out var count) &&
                    count >= config.MaxConsecutiveFunctionCalls.Value)
                {
                    return new AgentDecision.Terminate(
                        $"Circuit breaker triggered: function '{toolRequest.Name}' called {count} consecutive times with identical arguments");
                }
            }
        }

        // ═══════════════════════════════════════════════════════
        // PRIORITY 5: UNKNOWN TOOLS CHECK
        // Check if all requested tools are available (optional)
        // ═══════════════════════════════════════════════════════

        if (config.TerminateOnUnknownCalls && config.AvailableTools != null)
        {
            var toolRequests = ExtractToolRequestsFromResponse(lastResponse);
            var unknownTools = toolRequests
                .Where(req => !config.AvailableTools.Contains(req.Name))
                .Select(req => req.Name)
                .ToList();

            if (unknownTools.Count > 0)
            {
                return new AgentDecision.Terminate(
                    $"Unknown tools requested: {string.Join(", ", unknownTools)}");
            }
        }

        // ═══════════════════════════════════════════════════════
        // PRIORITY 6: CALL LLM AGAIN
        // If response had tool calls, they will be executed inline
        // and we need to call the LLM again with the results
        // ═══════════════════════════════════════════════════════

        return AgentDecision.CallLLM.Instance;
    }

    /// <summary>
    /// Extracts tool/function call requests from LLM response.
    /// Searches all messages and all contents for FunctionCallContent.
    /// </summary>
    /// <param name="response">LLM response to parse</param>
    /// <returns>List of tool requests (empty if none found)</returns>
    private static IReadOnlyList<AgentToolCallRequest> ExtractToolRequestsFromResponse(
        ChatResponse response)
    {
        var requests = new List<AgentToolCallRequest>();

        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent fcc && !string.IsNullOrEmpty(fcc.Name))
                {
                    var immutableArgs = (fcc.Arguments ?? new Dictionary<string, object?>())
                        .ToImmutableDictionary();

                    requests.Add(new AgentToolCallRequest(
                        fcc.Name,
                        fcc.CallId,
                        immutableArgs));
                }
            }
        }

        return requests;
    }

    /// <summary>
    /// Computes deterministic signature for a function call.
    /// Used by circuit breaker to detect identical repeated calls.
    /// </summary>
    /// <param name="request">Tool call request</param>
    /// <returns>Signature in format: "FunctionName(arg1=value1,arg2=value2,...)" with sorted args</returns>
    /// <remarks>
    /// Signature generation:
    /// - Arguments are sorted alphabetically by key for determinism
    /// - Values are JSON-serialized for correct comparison (handles nested objects, arrays)
    /// - Example: "get_weather(city="Seattle",units="celsius")"
    ///
    /// Why JSON serialization?
    /// - Handles complex types (nested objects, arrays)
    /// - Deterministic (same object always produces same JSON)
    /// - Type-safe (distinguishes between "42" string and 42 number)
    /// </remarks>
    public static string ComputeFunctionSignature(AgentToolCallRequest request)
    {
        var sortedArgs = string.Join(",",
            request.Arguments
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp =>
                {
                    var value = SerializeArgumentValue(kvp.Value);
                    return $"{kvp.Key}={value}";
                }));

        return $"{request.Name}({sortedArgs})";
    }

    /// <summary>
    /// Serializes an argument value to a deterministic string representation.
    /// Handles all edge cases: nested objects, arrays, nulls, type differences.
    /// </summary>
    /// <param name="value">Argument value (can be any type)</param>
    /// <returns>Deterministic string representation</returns>
    private static string SerializeArgumentValue(object? value)
    {
        if (value == null)
            return "null";

        // Use JSON serialization for determinism
        // This handles nested objects, arrays, and all edge cases correctly
        try
        {
            return JsonSerializer.Serialize(value, SerializationOptions);
        }
        catch (JsonException)
        {
            // Fallback for non-serializable types (very rare)
            // Use type name + hash code for uniqueness
            return $"\"{value.GetType().Name}:{value.GetHashCode()}\"";
        }
    }

    /// <summary>
    /// JSON serialization options for deterministic function signature generation.
    /// </summary>
    private static readonly JsonSerializerOptions SerializationOptions = new()
    {
        WriteIndented = false,                                     // Compact (no whitespace)
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,        // Include nulls
        PropertyNamingPolicy = null,                               // Preserve casing
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,    // Readable (no excessive escaping)
        MaxDepth = 64                                              // Prevent deep recursion attacks
    };
}

/// <summary>
/// Discriminated union representing all possible agent decisions.
/// The decision engine returns one of these sealed record types.
/// Pattern matching ensures exhaustive handling of all cases.
/// </summary>
/// <remarks>
/// This is a sealed hierarchy - no new decision types can be added outside this file.
/// This ensures the decision logic is complete and all cases are handled.
///
/// Example usage:
/// <code>
/// var decision = decisionEngine.DecideNextAction(state, lastResponse, config);
/// var result = decision switch
/// {
///     AgentDecision.CallLLM => await ExecuteCallLLMAsync(...),
///     AgentDecision.ExecuteTools et => await ExecuteToolsAsync(et.Tools, ...),
///     AgentDecision.Complete c => ExecutionResult.Completed(state, c.FinalResponse),
///     AgentDecision.Terminate t => ExecutionResult.Terminated(state, t.Reason),
///     _ => throw new InvalidOperationException($"Unknown decision: {decision}")
/// };
/// </code>
/// </remarks>
internal abstract record AgentDecision
{
    /// <summary>
    /// Decision: Call the LLM with current conversation messages.
    /// This is the default action when starting a new iteration or after tool execution.
    /// </summary>
    public sealed record CallLLM : AgentDecision
    {
        // Singleton pattern - only one instance needed
        public static readonly CallLLM Instance = new();
        private CallLLM() { }
    }

    /// <summary>
    /// Decision: Agent completed successfully (no more tools to execute).
    /// The LLM provided a text response without requesting any tool calls.
    /// </summary>
    /// <param name="FinalResponse">The final response from the LLM</param>
    public sealed record Complete(ChatResponse FinalResponse) : AgentDecision;

    /// <summary>
    /// Decision: Terminate agent execution with specified reason.
    /// Can be triggered by:
    /// - Max iterations reached
    /// - Circuit breaker triggered
    /// - Too many consecutive errors
    /// - External termination (e.g., permission denied)
    /// </summary>
    /// <param name="Reason">Human-readable termination reason</param>
    public sealed record Terminate(string Reason) : AgentDecision;

    // Private constructor prevents external inheritance
    private AgentDecision() { }
}

/// <summary>
/// Represents a request to invoke a tool/function.
/// Contains all information needed to execute the tool.
/// </summary>
/// <param name="Name">Name of the tool to invoke</param>
/// <param name="CallId">Unique identifier for this specific invocation (for correlation)</param>
/// <param name="Arguments">Dictionary of argument names to values</param>
internal sealed record AgentToolCallRequest(
    string Name,
    string CallId,
    IReadOnlyDictionary<string, object?> Arguments)
{
    /// <summary>
    /// Creates a ToolCallRequest with immutable arguments dictionary.
    /// </summary>
    public static AgentToolCallRequest Create(string name, string callId, IDictionary<string, object?>? arguments = null)
    {
        var immutableArgs = arguments != null
            ? arguments.ToImmutableDictionary()
            : ImmutableDictionary<string, object?>.Empty;

        return new AgentToolCallRequest(name, callId, immutableArgs);
    }
}

/// <summary>
/// Immutable snapshot of agent execution loop state.
/// Consolidates all 11 state variables that were scattered in RunAgenticLoopInternal.
/// Thread-safe and testable - enables pure decision-making logic.
/// </summary>
/// <remarks>
/// This record is the foundation of the "Functional Core" pattern.
/// All state transitions create new instances via 'with' expressions,
/// making state changes explicit and testable.
/// </remarks>
public sealed record AgentLoopState
{
    // ═══════════════════════════════════════════════════════
    // CORE STATE
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Unique identifier for this agent run/turn.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Conversation ID this run belongs to.
    /// </summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// Name of the agent executing this run.
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// When this agent run started (UTC).
    /// </summary>
    public required DateTime StartTime { get; init; }

    /// <summary>
    /// Messages in the current conversation context (full history).
    /// This is the complete conversation that gets sent to the LLM.
    /// </summary>
    public required IReadOnlyList<ChatMessage> CurrentMessages { get; init; }

    /// <summary>
    /// Messages accumulated during this agent turn (for response history).
    /// These messages represent what was added during this RunAsync call.
    /// </summary>
    public required IReadOnlyList<ChatMessage> TurnHistory { get; init; }

    /// <summary>
    /// Current iteration number (0-based).
    /// Each iteration represents one LLM call + tool execution cycle.
    /// </summary>
    public required int Iteration { get; init; }

    /// <summary>
    /// Whether the loop has been terminated (by any mechanism).
    /// Once true, the loop will exit on next check.
    /// </summary>
    public required bool IsTerminated { get; init; }

    /// <summary>
    /// Human-readable reason for termination (if terminated).
    /// Examples: "Max iterations reached", "Circuit breaker triggered", etc.
    /// </summary>
    public string? TerminationReason { get; init; }

    // ═══════════════════════════════════════════════════════
    // ERROR TRACKING
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Number of consecutive iterations with errors.
    /// Reset to 0 on any successful iteration.
    /// Triggers termination if it reaches MaxConsecutiveFailures.
    /// </summary>
    public required int ConsecutiveFailures { get; init; }

    // ═══════════════════════════════════════════════════════
    // CIRCUIT BREAKER STATE
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Last function signature per tool (for detecting infinite loops).
    /// Key: Tool name
    /// Value: Signature (FunctionName(arg1=val1,arg2=val2,...))
    /// </summary>
    public required ImmutableDictionary<string, string> LastSignaturePerTool { get; init; }

    /// <summary>
    /// Consecutive identical call count per tool.
    /// Key: Tool name
    /// Value: Number of times called consecutively with identical arguments
    /// Triggers circuit breaker when threshold is exceeded.
    /// </summary>
    public required ImmutableDictionary<string, int> ConsecutiveCountPerTool { get; init; }

    // ═══════════════════════════════════════════════════════
    // PLUGIN/SKILL SCOPING STATE
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Plugins that have been expanded in this turn.
    /// Used for container expansion pattern - once a container is expanded,
    /// its member functions become available.
    /// </summary>
    public required ImmutableHashSet<string> ExpandedPlugins { get; init; }

    /// <summary>
    /// Skills that have been expanded in this turn.
    /// Used for container expansion pattern (skills version).
    /// </summary>
    public required ImmutableHashSet<string> ExpandedSkills { get; init; }

    // ═══════════════════════════════════════════════════════
    // FUNCTION TRACKING
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Functions completed in this run (for telemetry and deduplication).
    /// Tracks which functions have been successfully executed.
    /// </summary>
    public required ImmutableHashSet<string> CompletedFunctions { get; init; }

    // ═══════════════════════════════════════════════════════
    // HISTORY OPTIMIZATION STATE
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Whether the LLM service manages conversation history server-side.
    /// When true, we only send delta messages (significant token savings).
    /// Detected automatically when service returns a ConversationId.
    /// </summary>
    public required bool InnerClientTracksHistory { get; init; }

    /// <summary>
    /// Number of messages already sent to the server (for delta sending).
    /// Used to calculate which messages to send when InnerClientTracksHistory is true.
    /// </summary>
    public required int MessagesSentToInnerClient { get; init; }

    // ═══════════════════════════════════════════════════════
    // STREAMING STATE
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Last assistant message ID (for event correlation).
    /// Used to link events (text deltas, reasoning, etc.) to specific messages.
    /// </summary>
    public string? LastAssistantMessageId { get; init; }

    /// <summary>
    /// Accumulated streaming updates (for final response construction).
    /// Collected during LLM streaming to build complete ChatResponse.
    /// </summary>
    public required IReadOnlyList<ChatResponseUpdate> ResponseUpdates { get; init; }

    // ═══════════════════════════════════════════════════════
    // FACTORY METHOD
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Creates initial state for a new agent execution.
    /// All collections are empty, counters are zero.
    /// </summary>
    /// <param name="messages">Initial conversation messages</param>
    /// <param name="runId">Unique identifier for this run</param>
    /// <param name="conversationId">Conversation identifier</param>
    /// <param name="agentName">Name of the agent</param>
    /// <returns>Fresh state ready for first iteration</returns>
    public static AgentLoopState Initial(IReadOnlyList<ChatMessage> messages, string runId, string conversationId, string agentName) => new()
    {
        RunId = runId,
        ConversationId = conversationId,
        AgentName = agentName,
        StartTime = DateTime.UtcNow,
        CurrentMessages = messages,
        TurnHistory = ImmutableList<ChatMessage>.Empty,
        Iteration = 0,
        IsTerminated = false,
        TerminationReason = null,
        ConsecutiveFailures = 0,
        LastSignaturePerTool = ImmutableDictionary<string, string>.Empty,
        ConsecutiveCountPerTool = ImmutableDictionary<string, int>.Empty,
        ExpandedPlugins = ImmutableHashSet<string>.Empty,
        ExpandedSkills = ImmutableHashSet<string>.Empty,
        CompletedFunctions = ImmutableHashSet<string>.Empty,
        InnerClientTracksHistory = false,
        MessagesSentToInnerClient = 0,
        LastAssistantMessageId = null,
        ResponseUpdates = ImmutableList<ChatResponseUpdate>.Empty,
        Version = 1,
        Metadata = new CheckpointMetadata
        {
            Source = CheckpointSource.Input,
            Step = -1 // -1 indicates initial state before first iteration
        },
        ETag = null // Will be generated on first serialize
    };

    // ═══════════════════════════════════════════════════════
    // STATE TRANSITIONS (Immutable Updates)
    // All methods return NEW instances - never mutate existing state
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Advances to the next iteration.
    /// </summary>
    public AgentLoopState NextIteration() =>
        this with { Iteration = Iteration + 1 };

    /// <summary>
    /// Updates the current conversation messages.
    /// Used after adding new messages from LLM or tool results.
    /// </summary>
    public AgentLoopState WithMessages(IReadOnlyList<ChatMessage> messages) =>
        this with { CurrentMessages = messages };

    /// <summary>
    /// Appends a message to the turn history.
    /// Turn history tracks what was added during this RunAsync call.
    /// </summary>
    public AgentLoopState AppendToTurnHistory(ChatMessage message)
    {
        var updatedHistory = new List<ChatMessage>(TurnHistory) { message };
        return this with { TurnHistory = updatedHistory };
    }

    /// <summary>
    /// Resets consecutive failure counter (after successful iteration).
    /// </summary>
    public AgentLoopState WithSuccess() =>
        this with { ConsecutiveFailures = 0 };

    /// <summary>
    /// Increments consecutive failure counter.
    /// </summary>
    public AgentLoopState WithFailure() =>
        this with { ConsecutiveFailures = ConsecutiveFailures + 1 };

    /// <summary>
    /// Terminates the loop with the specified reason.
    /// </summary>
    public AgentLoopState Terminate(string reason) =>
        this with { IsTerminated = true, TerminationReason = reason };

    /// <summary>
    /// Records that a plugin container has been expanded.
    /// </summary>
    public AgentLoopState WithExpandedPlugin(string pluginName) =>
        this with { ExpandedPlugins = ExpandedPlugins.Add(pluginName) };

    /// <summary>
    /// Records that a skill container has been expanded.
    /// </summary>
    public AgentLoopState WithExpandedSkill(string skillName) =>
        this with { ExpandedSkills = ExpandedSkills.Add(skillName) };

    /// <summary>
    /// Adds a pending write for a completed function call.
    /// Used during execution to track successful operations before checkpoint.
    /// </summary>
    public AgentLoopState WithPendingWrite(PendingWrite write) =>
        this with { PendingWrites = PendingWrites.Add(write) };

    /// <summary>
    /// Clears all pending writes.
    /// Called after successful checkpoint (writes are now captured in checkpoint).
    /// </summary>
    public AgentLoopState ClearPendingWrites() =>
        this with { PendingWrites = ImmutableList<PendingWrite>.Empty };

    /// <summary>
    /// Records a function completion for telemetry tracking (successful calls only).
    /// 
    /// NOTE: This is part of a dual tracking system:
    /// - RecordToolCall: Tracks ALL attempted function calls for circuit breaker detection
    /// - CompleteFunction: Tracks only SUCCESSFUL function calls for telemetry/analytics
    /// </summary>
    /// <param name="functionName">Name of the completed function</param>
    /// <returns>New state with updated function tracking</returns>
    public AgentLoopState CompleteFunction(string functionName) =>
        this with { CompletedFunctions = CompletedFunctions.Add(functionName) };

    /// <summary>
    /// Records a tool call for circuit breaker tracking (all attempted calls).
    /// Compares signature with last call to detect identical consecutive calls.
    /// 
    /// NOTE: This is part of a dual tracking system:
    /// - RecordToolCall: Tracks ALL attempted function calls for circuit breaker detection
    /// - CompleteFunction: Tracks only SUCCESSFUL function calls for telemetry/analytics
    /// </summary>
    /// <param name="toolName">Name of the tool being called</param>
    /// <param name="signature">Deterministic signature (name + sorted args)</param>
    /// <returns>New state with updated circuit breaker tracking</returns>
    public AgentLoopState RecordToolCall(string toolName, string signature)
    {
        var lastSig = LastSignaturePerTool.GetValueOrDefault(toolName);
        var isIdentical = !string.IsNullOrEmpty(lastSig) && signature == lastSig;

        return this with
        {
            LastSignaturePerTool = LastSignaturePerTool.SetItem(toolName, signature),
            ConsecutiveCountPerTool = isIdentical
                ? ConsecutiveCountPerTool.SetItem(toolName, ConsecutiveCountPerTool.GetValueOrDefault(toolName, 0) + 1)
                : ConsecutiveCountPerTool.SetItem(toolName, 1)
        };
    }

    /// <summary>
    /// Enables server-side history tracking after detecting ConversationId in response.
    /// Significant token savings for multi-turn conversations.
    /// </summary>
    /// <param name="messageCount">Number of messages sent to server</param>
    public AgentLoopState EnableHistoryTracking(int messageCount) =>
        this with
        {
            InnerClientTracksHistory = true,
            MessagesSentToInnerClient = messageCount
        };

    /// <summary>
    /// Disables server-side history tracking (fall back to sending full history).
    /// </summary>
    public AgentLoopState DisableHistoryTracking() =>
        this with
        {
            InnerClientTracksHistory = false,
            MessagesSentToInnerClient = 0
        };

    /// <summary>
    /// Sets the last assistant message ID (for event correlation).
    /// </summary>
    public AgentLoopState WithLastAssistantMessageId(string messageId) =>
        this with { LastAssistantMessageId = messageId };

    /// <summary>
    /// Accumulates a streaming response update.
    /// Used during LLM streaming to collect all deltas.
    /// </summary>
    public AgentLoopState AccumulateResponseUpdate(ChatResponseUpdate update)
    {
        var updatedUpdates = new List<ChatResponseUpdate>(ResponseUpdates) { update };
        return this with { ResponseUpdates = updatedUpdates };
    }

    /// <summary>
    /// Clears accumulated response updates (after building final response).
    /// </summary>
    public AgentLoopState ClearResponseUpdates() =>
        this with { ResponseUpdates = ImmutableList<ChatResponseUpdate>.Empty };

    // ═══════════════════════════════════════════════════════
    // CHECKPOINTING METADATA (NEW)
    // ═══════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════
    // PENDING WRITES (FOR PARTIAL FAILURE RECOVERY)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Pending writes from function calls that completed successfully
    /// but before the iteration checkpoint was saved.
    /// Used for partial failure recovery in parallel execution scenarios.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When parallel function calls execute, successful results are saved immediately
    /// as "pending writes" before the full iteration checkpoint completes. If a crash
    /// occurs, these pending writes can be restored on resume to avoid re-executing
    /// successful operations.
    /// </para>
    /// <para>
    /// <strong>Lifecycle:</strong>
    /// <list type="number">
    /// <item>Function completes → Added to PendingWrites</item>
    /// <item>Checkpoint saves → PendingWrites cleared (captured in checkpoint)</item>
    /// <item>On resume → Pending writes restored from checkpointer as tool messages</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Note:</strong> Pending writes are NOT serialized in AgentLoopState itself.
    /// They are stored separately by IThreadCheckpointer and restored during resume.
    /// This property tracks them during execution only.
    /// </para>
    /// </remarks>
    public ImmutableList<PendingWrite> PendingWrites { get; init; }
        = ImmutableList<PendingWrite>.Empty;

    /// <summary>
    /// Schema version for forward/backward compatibility.
    /// Increment when making breaking changes to this record.
    ///
    /// Version History:
    /// - v1: Initial implementation (all current fields)
    /// - v2: Added PendingWrites support for parallel execution recovery
    ///
    /// Breaking changes requiring version bump:
    /// - Removing/renaming required properties
    /// - Changing ImmutableDictionary key types
    /// - Changing collection types (e.g., List to ImmutableList)
    ///
    /// Non-breaking changes (OK to keep same version):
    /// - Adding new optional properties (init with default)
    /// - Adding metadata fields
    /// </summary>
    public int Version { get; init; } = 2;

    /// <summary>
    /// Metadata about how this checkpoint was created.
    /// Used for time-travel debugging and audit trails.
    /// </summary>
    public CheckpointMetadata? Metadata { get; init; }

    /// <summary>
    /// ETag for optimistic concurrency control (future).
    /// Generated on each checkpoint save to prevent concurrent modification conflicts.
    /// Format: GUID string
    /// </summary>
    public string? ETag { get; init; }

    // ═══════════════════════════════════════════════════════
    // SERIALIZATION (NEW) - Leverages Microsoft.Extensions.AI
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Serializes this state to JSON for checkpointing.
    /// Uses Microsoft.Extensions.AI's built-in serialization for ChatMessage and AIContent.
    /// Handles immutable collections, polymorphic content, and all message types automatically.
    /// </summary>
    public string Serialize()
    {
        // Generate new ETag for optimistic concurrency
        var stateWithETag = this with { ETag = Guid.NewGuid().ToString() };

        // Use AIJsonUtilities.DefaultOptions which provides:
        // - ChatMessage serialization (with [JsonConstructor])
        // - AIContent polymorphism (via [JsonPolymorphic])
        // - ChatResponseUpdate serialization
        // - Native AOT compatibility
        return JsonSerializer.Serialize(stateWithETag, AIJsonUtilities.DefaultOptions);
    }

    /// <summary>
    /// Deserializes state from JSON checkpoint.
    /// Includes version migration logic for backward compatibility.
    /// Uses Microsoft.Extensions.AI's built-in deserialization.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when deserialization fails</exception>
    /// <exception cref="NotSupportedException">Thrown when version is not supported</exception>
    /// <exception cref="CheckpointVersionTooNewException">Thrown when checkpoint version is newer than supported</exception>
    public static AgentLoopState Deserialize(string json)
    {
        var doc = JsonDocument.Parse(json);
        var version = doc.RootElement.TryGetProperty(nameof(Version), out var vProp)
            ? vProp.GetInt32()
            : 1;

        // Fail-fast if version is too new (prevents silent data corruption)
        const int MaxSupportedVersion = 2;
        if (version > MaxSupportedVersion)
        {
            throw new CheckpointVersionTooNewException(
                $"Checkpoint version {version} is newer than supported version {MaxSupportedVersion}. " +
                $"Please upgrade HPD-Agent to deserialize this checkpoint.");
        }

        // Handle version-specific deserialization
        if (version == 1)
        {
            // v1: No pending writes support
            var state = JsonSerializer.Deserialize<AgentLoopState>(json, AIJsonUtilities.DefaultOptions)
                ?? throw new InvalidOperationException("Failed to deserialize AgentLoopState v1");

            // v1 checkpoints don't have PendingWrites - initialize to empty
            return state with { PendingWrites = ImmutableList<PendingWrite>.Empty };
        }
        else if (version == 2)
        {
            // v2: Pending writes support
            return JsonSerializer.Deserialize<AgentLoopState>(json, AIJsonUtilities.DefaultOptions)
                ?? throw new InvalidOperationException("Failed to deserialize AgentLoopState v2");
        }
        else
        {
            throw new NotSupportedException($"AgentLoopState version {version} is not supported by this version of HPD-Agent");
        }
    }

    /// <summary>
    /// Validates that this checkpoint state is compatible with the current conversation state.
    /// Checks for staleness, iteration continuity, and message count consistency.
    /// </summary>
    /// <param name="currentMessageCount">Number of messages currently in the conversation</param>
    /// <param name="allowStaleCheckpoint">If true, allows resuming from older checkpoint (time-travel scenario)</param>
    /// <exception cref="CheckpointStaleException">Thrown when checkpoint is stale and allowStaleCheckpoint is false</exception>
    public void ValidateConsistency(int currentMessageCount, bool allowStaleCheckpoint = false)
    {
        // Check message count consistency
        if (!allowStaleCheckpoint && currentMessageCount != CurrentMessages.Count)
        {
            throw new CheckpointStaleException(
                $"Checkpoint is stale. Conversation has {currentMessageCount} messages " +
                $"but checkpoint expects {CurrentMessages.Count}. " +
                $"This checkpoint was created at iteration {Iteration} but conversation has progressed.");
        }

        // Check for corrupted state
        if (Iteration < 0)
        {
            throw new InvalidOperationException($"Checkpoint has invalid iteration: {Iteration}");
        }

        if (ConsecutiveFailures < 0)
        {
            throw new InvalidOperationException($"Checkpoint has invalid ConsecutiveFailures: {ConsecutiveFailures}");
        }
    }
}

/// <summary>
/// Configuration data for the agent decision engine (pure data, no behavior).
/// Contains all settings needed for decision-making logic.
/// Immutable and easily testable.
/// </summary>
/// <remarks>
/// This is intentionally separate from AgentConfig (the full agent configuration).
/// AgentConfiguration contains ONLY what the decision engine needs, making it:
/// - Easy to test (no complex dependencies)
/// - Easy to understand (minimal surface area)
/// - Easy to evolve (changes don't ripple through the system)
/// </remarks>
internal sealed record AgentConfiguration
{
    /// <summary>
    /// Maximum iterations before forced termination.
    /// Each iteration = one LLM call + tool execution cycle.
    /// Prevents runaway loops and excessive costs.
    /// </summary>
    public required int MaxIterations { get; init; }

    /// <summary>
    /// Maximum consecutive failures before termination.
    /// Failures = iterations where all tool executions failed.
    /// Prevents infinite retry loops.
    /// </summary>
    public required int MaxConsecutiveFailures { get; init; }

    /// <summary>
    /// Whether to terminate on unknown tool requests (vs. pass through for multi-agent scenarios).
    ///
    /// When true: If LLM requests a tool that doesn't exist, terminate immediately.
    /// When false: Unknown tools are passed through (enables multi-agent handoffs).
    /// </summary>
    public required bool TerminateOnUnknownCalls { get; init; }

    /// <summary>
    /// Set of tool names available for execution.
    /// Used to detect unknown tool requests when TerminateOnUnknownCalls is true.
    /// </summary>
    public required IReadOnlySet<string> AvailableTools { get; init; }

    /// <summary>
    /// Maximum times a function can be called consecutively with identical arguments.
    /// Null = no circuit breaker (not recommended).
    ///
    /// Circuit breaker prevents infinite loops where the LLM repeatedly calls
    /// the same tool with identical arguments (e.g., failed API calls).
    ///
    /// Example: If set to 3, and the LLM calls "get_weather(city=Seattle)"
    /// three times in a row, the circuit breaker triggers and terminates.
    /// </summary>
    public int? MaxConsecutiveFunctionCalls { get; init; }

    /// <summary>
    /// Factory method: Create configuration from AgentConfig.
    /// Extracts only the fields needed for decision-making.
    /// </summary>
    /// <param name="config">Full agent configuration</param>
    /// <param name="maxIterations">Max iterations (from Agent constructor parameter)</param>
    /// <param name="availableTools">Set of available tool names</param>
    /// <returns>Lightweight configuration for decision engine</returns>
    public static AgentConfiguration FromAgentConfig(
        AgentConfig? config,
        int maxIterations,
        IReadOnlySet<string> availableTools)
    {
        return new AgentConfiguration
        {
            MaxIterations = maxIterations,
            MaxConsecutiveFailures = config?.ErrorHandling?.MaxRetries ?? 3,
            TerminateOnUnknownCalls = config?.AgenticLoop?.TerminateOnUnknownCalls ?? false,
            AvailableTools = availableTools,
            MaxConsecutiveFunctionCalls = config?.AgenticLoop?.MaxConsecutiveFunctionCalls
        };
    }

    /// <summary>
    /// Factory method: Create default configuration for testing.
    /// </summary>
    /// <param name="maxIterations">Maximum iterations (default: 10)</param>
    /// <param name="maxConsecutiveFailures">Max consecutive failures (default: 3)</param>
    /// <param name="maxConsecutiveFunctionCalls">Circuit breaker threshold (default: 5)</param>
    /// <param name="availableTools">Available tool names (default: empty)</param>
    /// <param name="terminateOnUnknownCalls">Whether to terminate on unknown tools (default: false)</param>
    /// <returns>Configuration with sensible defaults for testing</returns>
    public static AgentConfiguration Default(
        int maxIterations = 10,
        int maxConsecutiveFailures = 3,
        int? maxConsecutiveFunctionCalls = 5,
        IReadOnlySet<string>? availableTools = null,
        bool terminateOnUnknownCalls = false)
    {
        return new AgentConfiguration
        {
            MaxIterations = maxIterations,
            MaxConsecutiveFailures = maxConsecutiveFailures,
            TerminateOnUnknownCalls = terminateOnUnknownCalls,
            AvailableTools = availableTools ?? new HashSet<string>(),
            MaxConsecutiveFunctionCalls = maxConsecutiveFunctionCalls
        };
    }
}

#endregion

#region Function Map Builder Utilities

/// <summary>
/// Builds O(1) lookup maps for AIFunctions from tool lists.
/// Handles merging, priority, and efficient lookups.
/// Eliminates duplication of function map building logic across FunctionCallProcessor,
/// PermissionManager, and ToolScheduler.
/// </summary>
internal static class FunctionMapBuilder
{
    /// <summary>
    /// Builds map with server tools (low priority) and request tools (high priority).
    /// Request tools can override server-configured tools.
    /// </summary>
    /// <param name="serverTools">Tools configured server-side (lower priority)</param>
    /// <param name="requestTools">Tools provided in the request (higher priority, can override)</param>
    /// <returns>Dictionary mapping function names to AIFunction instances, or null if no functions</returns>
    public static Dictionary<string, AIFunction>? BuildMergedMap(
        IList<AITool>? serverTools,
        IList<AITool>? requestTools)
    {
        if (serverTools is not { Count: > 0 } &&
            requestTools is not { Count: > 0 })
        {
            return null;
        }

        var map = new Dictionary<string, AIFunction>(StringComparer.Ordinal);

        // Add server-configured tools first (lower priority)
        if (serverTools is { Count: > 0 })
        {
            for (int i = 0; i < serverTools.Count; i++)
            {
                if (serverTools[i] is AIFunction af)
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
    /// Builds map from single tool source.
    /// </summary>
    /// <param name="tools">The tool list to build the map from</param>
    /// <returns>Dictionary mapping function names to AIFunction instances, or null if no functions</returns>
    public static Dictionary<string, AIFunction>? BuildMap(IList<AITool>? tools)
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
    /// O(1) lookup in pre-built map.
    /// </summary>
    /// <param name="name">The function name to find</param>
    /// <param name="map">Pre-built map of function names to AIFunction instances</param>
    /// <returns>The AIFunction if found, null otherwise</returns>
    public static AIFunction? FindFunction(
        string name,
        Dictionary<string, AIFunction>? map)
    {
        return map?.TryGetValue(name, out var func) == true ? func : null;
    }

    /// <summary>
    /// O(n) search when no map available (fallback).
    /// Use this when building a map is too expensive for single lookups.
    /// </summary>
    /// <param name="name">The function name to find</param>
    /// <param name="tools">The tool list to search</param>
    /// <returns>The AIFunction if found, null otherwise</returns>
    public static AIFunction? FindFunctionInList(
        string name,
        IList<AITool>? tools)
    {
        if (tools is not { Count: > 0 }) return null;

        for (int i = 0; i < tools.Count; i++)
        {
            if (tools[i] is AIFunction af && af.Name == name)
                return af;
        }
        return null;
    }
}

#endregion

#region Content Extraction Utilities

/// <summary>
/// High-performance content extraction and filtering utilities.
/// Optimized with manual iteration to avoid LINQ overhead.
/// Eliminates duplication of content extraction logic across Agent, MessageProcessor,
/// and AgentDocumentProcessor.
/// </summary>
internal static class ContentExtractor
{
    /// <summary>
    /// Creates canonical string for content comparison (deduplication).
    /// Covers all major content types to prevent duplicate message appending.
    /// </summary>
    /// <param name="contents">The content list to canonicalize</param>
    /// <returns>A deterministic string representation of the contents</returns>
    public static string Canonicalize(IList<AIContent> contents)
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
                    if (!data.Data.IsEmpty)
                    {
                        sb.Append(HashDataBytes(data.Data));
                    }
                    break;
            }
        }
        return sb.ToString();
    }
    
    /// <summary>
    /// Extracts only text content (ignores function calls, data, etc.).
    /// Used for text-only comparison and deduplication.
    /// </summary>
    /// <param name="contents">The content list to extract text from</param>
    /// <returns>Combined text from all TextContent and TextReasoningContent items</returns>
    public static string ExtractTextOnly(IList<AIContent> contents)
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
    /// Extracts text from a message (handles multiple TextContent items).
    /// Uses LINQ for simplicity when performance is not critical.
    /// </summary>
    /// <param name="message">The message to extract text from</param>
    /// <returns>Combined text from all non-empty TextContent items, space-separated</returns>
    public static string ExtractText(ChatMessage message)
    {
        var textContents = message.Contents
            .OfType<TextContent>()
            .Select(tc => tc.Text)
            .Where(text => !string.IsNullOrEmpty(text));

        return string.Join(" ", textContents);
    }
    
    /// <summary>
    /// Extracts all function call names from contents (optimized).
    /// Manual iteration to avoid LINQ overhead.
    /// </summary>
    /// <param name="contents">The content list to extract function names from</param>
    /// <returns>List of function call names (may contain duplicates)</returns>
    public static List<string> ExtractFunctionNames(IList<AIContent> contents)
    {
        var names = new List<string>();
        for (int i = 0; i < contents.Count; i++)
        {
            if (contents[i] is FunctionCallContent fc && !string.IsNullOrEmpty(fc.Name))
                names.Add(fc.Name);
        }
        return names;
    }
    
    /// <summary>
    /// Extracts all function call names from message history (optimized).
    /// Returns a dictionary mapping agent names to their function calls.
    /// </summary>
    /// <param name="history">The message history to scan</param>
    /// <param name="agentName">The agent name to attribute function calls to</param>
    /// <returns>Dictionary mapping agent name to list of function call names</returns>
    public static Dictionary<string, List<string>> ExtractFunctionCallsFromHistory(
        IReadOnlyList<ChatMessage> history,
        string agentName)
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
                if (!metadata.TryGetValue(agentName, out var existingList))
                {
                    metadata[agentName] = functionCalls;
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
    
    /// <summary>
    /// Filters out specific content types (e.g., remove reasoning to save tokens).
    /// </summary>
    /// <typeparam name="TExclude">The content type to exclude</typeparam>
    /// <param name="contents">The content list to filter</param>
    /// <returns>New list with excluded type removed</returns>
    public static List<AIContent> FilterByType<TExclude>(
        IList<AIContent> contents) where TExclude : AIContent
    {
        var filtered = new List<AIContent>(contents.Count);
        for (int i = 0; i < contents.Count; i++)
        {
            if (contents[i] is not TExclude)
                filtered.Add(contents[i]);
        }
        return filtered;
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
}

#endregion

#region FunctionCallProcessor

/// <summary>
/// Handles all function calling logic, including multi-turn execution and filter pipelines.
/// </summary>
internal class FunctionCallProcessor
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
        AgentLoopState agentLoopState,
        CancellationToken cancellationToken)
    {
        var resultMessages = new List<ChatMessage>();

        // Build function map per execution (Microsoft pattern for thread-safety)
        // This avoids shared mutable state and stale cache issues
        // Merge server-configured tools with request tools (request tools take precedence)
        var functionMap = FunctionMapBuilder.BuildMergedMap(_serverConfiguredTools, options?.Tools);

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

            var context = new FunctionInvocationContext
            {
                ToolCallRequest = toolCallRequest,
                Function = FunctionMapBuilder.FindFunction(functionCall.Name, functionMap),
                Arguments = toolCallRequest.Arguments,
                ArgumentsWrapper = new AIFunctionArguments(toolCallRequest.Arguments),
                State = agentLoopState,  // Use AgentLoopState as single source of truth
                AgentName = agentLoopState.AgentName,  // ✅ Get from state
                CallId = functionCall.CallId,
                Iteration = agentLoopState.Iteration,
                TotalFunctionCallsInRun = agentLoopState.CompletedFunctions.Count,
                // Point to Agent's shared channel for event emission
                OutboundEvents = _agent.FilterEventWriter,
                Agent = _agent
            };

            // Store CallId in metadata for extensibility
            context.Metadata["CallId"] = functionCall.CallId;

            // Check if function is unknown and TerminateOnUnknownCalls is enabled
            if (context.Function == null && _agenticLoopConfig?.TerminateOnUnknownCalls == true)
            {
                // Terminate the loop - don't process this or any remaining functions
                // The function call will be returned to the caller for handling (e.g., multi-agent handoff)
                context.IsTerminated = true;
                
                // Don't add any result message - let the caller handle the unknown function
                break;
            }

            // Check permissions using PermissionManager BEFORE building execution pipeline
            var permissionResult = await _permissionManager.CheckPermissionAsync(
                functionCall,
                context.Function,
                agentLoopState,
                _agent,
                cancellationToken).ConfigureAwait(false);

            // If permission denied, record the denial and skip execution
            if (!permissionResult.IsApproved)
            {
                context.Result = permissionResult.DenialReason ?? "Permission denied";
                context.IsTerminated = true;
                
                // Note: Function completion tracking is handled by caller using state updates

                var denialResult = new FunctionResultContent(functionCall.CallId, context.Result);
                var denialMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { denialResult });
                resultMessages.Add(denialMessage);
                continue; // Skip to next function call
            }

            // Permission approved - proceed with execution pipeline
            // The final step in the pipeline is the actual function invocation with retry logic.
            Func<FunctionInvocationContext, Task> finalInvoke = async (ctx) =>
            {
                if (ctx.Function is null)
                {
                    ctx.Result = $"Function '{ctx.FunctionName}' not found.";
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

            // Note: Function completion tracking is handled by caller using state updates

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
    private async Task ExecuteWithRetryAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        if (context.Function is null)
        {
            context.Result = $"Function '{context.ToolCallRequest?.FunctionName ?? "Unknown"}' not found.";
            return;
        }

        // Set AsyncLocal function invocation context for ambient access
        // Store the full context so plugins can access ALL capabilities (Emit, WaitForResponseAsync, etc.)
        Agent.CurrentFunctionContext = context;

        var retryExecutor = new FunctionRetryExecutor(_errorHandlingConfig);
        
        try
        {
            context.Result = await retryExecutor.ExecuteWithRetryAsync(
                context.Function,
                context.Arguments,
                context.FunctionName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // Function-specific timeout
            context.Result = FormatErrorForLLM(ex, context.FunctionName);
        }
        catch (Exception ex)
        {
            // All retries exhausted or non-retryable error
            context.Result = FormatErrorForLLM(ex, context.FunctionName);
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
    /// Prepares the various chat message lists after a response from the inner client and before invoking functions
    /// </summary>
}

#endregion 

#region MessageProcessor

/// <summary>
/// Handles all pre-processing of chat messages and options before sending to the LLM.
/// </summary>
internal class MessageProcessor
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
    /// TODO: NOT IMPLEMENTED - Token tracking requires Token Flow Architecture Map.
    /// See docs/NEED_FOR_TOKEN_FLOW_ARCHITECTURE_MAP.md for details.
    /// Falls back to message-count reduction (Priority 3).
    /// </summary>
    private bool ShouldReduceByPercentage(List<ChatMessage> messagesList, int lastSummaryIndex)
    {
        // TODO: Token tracking not implemented - requires architecture map
        // This would track ephemeral context (system prompts, RAG docs, memory)
        // and persistent context (user/assistant/tool messages) separately
        return false;
    }

    /// <summary>
    /// Checks if reduction should be triggered based on absolute token budget.
    /// TODO: NOT IMPLEMENTED - Token tracking requires Token Flow Architecture Map.
    /// See docs/NEED_FOR_TOKEN_FLOW_ARCHITECTURE_MAP.md for details.
    /// Falls back to message-count reduction (Priority 3).
    /// </summary>
    private bool ShouldReduceByTokens(List<ChatMessage> messagesList, int lastSummaryIndex)
    {
        // TODO: Token tracking not implemented - requires architecture map
        // This would track ephemeral context (system prompts, RAG docs, memory)
        // and persistent context (user/assistant/tool messages) separately
        return false;
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

        // Build and execute the prompt filt er pipeline using FilterChain
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
internal class AgentTurn
{
    private readonly IChatClient _baseClient;
    private readonly Action<ChatOptions>? _configureOptions;
    private readonly List<Func<IChatClient, IServiceProvider?, IChatClient>>? _middleware;
    private readonly IServiceProvider? _serviceProvider;

    /// <summary>
    /// The ConversationId from the most recent response (if the service manages history server-side).
    /// Null if the service doesn't track conversation history.
    /// </summary>
    public string? LastResponseConversationId { get; private set; }

    /// <summary>
    /// Initializes a new instance of AgentTurn
    /// </summary>
    /// <param name="baseClient">The underlying chat client to use for LLM calls</param>
    /// <param name="configureOptions">Optional callback to configure options before each LLM call</param>
    /// <param name="middleware">Optional middleware to wrap the client dynamically on each request</param>
    /// <param name="serviceProvider">Optional service provider for middleware dependency injection</param>
    public AgentTurn(
        IChatClient baseClient,
        Action<ChatOptions>? configureOptions = null,
        List<Func<IChatClient, IServiceProvider?, IChatClient>>? middleware = null,
        IServiceProvider? serviceProvider = null)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _configureOptions = configureOptions;
        _middleware = middleware;
        _serviceProvider = serviceProvider;
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
        // Apply runtime options configuration callback if configured
        if (_configureOptions != null && options != null)
        {
            _configureOptions(options);
        }

        // Apply middleware dynamically (if any)
        // This allows runtime provider switching - new providers automatically get wrapped
        var effectiveClient = _baseClient;
        if (_middleware != null && _middleware.Count > 0)
        {
            foreach (var mw in _middleware)
            {
                effectiveClient = mw(effectiveClient, _serviceProvider);
                if (effectiveClient == null)
                {
                    throw new InvalidOperationException("Chat client middleware returned null");
                }
            }
        }

        // Get the streaming response from the effective client (base or wrapped)
        var stream = effectiveClient.GetStreamingResponseAsync(messages, options, cancellationToken);

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
internal record ReductionMetadata
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
internal class StreamingTurnResult
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
internal class ToolScheduler
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
    /// Determines if a function result indicates success.
    /// Checks for exceptions and error-like result strings.
    /// </summary>
    private static bool IsFunctionResultSuccessful(FunctionResultContent result)
    {
        // Exception present = failure
        if (result.Exception != null)
            return false;

        // Check if result looks like an error message
        var resultStr = result.Result?.ToString();
        return !IsLikelyErrorString(resultStr);
    }

    /// <summary>
    /// Heuristic to detect error strings in function results.
    /// Mirrors the error detection logic used in result filtering.
    /// </summary>
    private static bool IsLikelyErrorString(string? s) =>
        !string.IsNullOrEmpty(s) &&
        (s.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
         s.StartsWith("Failed:", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("exception occurred", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("exception was thrown", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("rate limit exceeded", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("rate limited", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("quota reached", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("timeout", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tries to get the tool name for a given call ID from the request list.
    /// </summary>
    private static bool TryGetToolName(IList<FunctionCallContent> requests, string callId, out string name)
    {
        for (int i = 0; i < requests.Count; i++)
        {
            if (requests[i].CallId == callId)
            {
                name = requests[i].Name ?? "_unknown";
                return true;
            }
        }
        name = "";
        return false;
    }

    /// <summary>
    /// Extracts successful function names from execution results.
    /// Only includes functions that completed without errors.
    /// </summary>
    private static HashSet<string> ExtractSuccessfulFunctions(
        IList<AIContent> resultContents,
        IList<FunctionCallContent> toolRequests)
    {
        var successful = new HashSet<string>();

        foreach (var content in resultContents)
        {
            if (content is FunctionResultContent frc && IsFunctionResultSuccessful(frc))
            {
                if (TryGetToolName(toolRequests, frc.CallId, out var toolName))
                {
                    successful.Add(toolName);
                }
            }
        }

        return successful;
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
    public async Task<(ChatMessage Message, HashSet<string> PluginExpansions, HashSet<string> SkillExpansions, HashSet<string> SuccessfulFunctions)> ExecuteToolsAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentLoopState agentLoopState,
        CancellationToken cancellationToken)
    {
        // For single tool calls, use sequential execution (no parallelization overhead)
        if (toolRequests.Count <= 1)
        {
            return await ExecuteSequentiallyAsync(currentHistory, toolRequests, options, agentLoopState, cancellationToken).ConfigureAwait(false);
        }

        // For multiple tool calls, execute in parallel for better performance
        return await ExecuteInParallelAsync(currentHistory, toolRequests, options, agentLoopState, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes tools sequentially (used for single tools or as fallback)
    /// </summary>
    private async Task<(ChatMessage Message, HashSet<string> PluginExpansions, HashSet<string> SkillExpansions, HashSet<string> SuccessfulFunctions)> ExecuteSequentiallyAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentLoopState agentLoopState,
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
            var function = FunctionMapBuilder.FindFunctionInList(toolRequest.Name, options?.Tools);

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
            currentHistory, options, toolRequests, agentLoopState, cancellationToken).ConfigureAwait(false);

        // Combine results
        foreach (var message in resultMessages)
        {
            foreach (var content in message.Contents)
            {
                allContents.Add(content);
            }
        }

        // Extract plugin expansions from results
        var pluginExpansions = new HashSet<string>();
        var skillExpansions = new HashSet<string>();

        foreach (var content in allContents)
        {
            if (content is FunctionResultContent functionResult)
            {
                if (pluginContainerExpansions.TryGetValue(functionResult.CallId, out var pluginName))
                {
                    pluginExpansions.Add(pluginName);
                }
                else if (skillContainerExpansions.TryGetValue(functionResult.CallId, out var skillName))
                {
                    skillExpansions.Add(skillName);
                }
            }
        }

        // ✅ Extract successful functions from actual results (not before execution)
        var successfulFunctions = ExtractSuccessfulFunctions(allContents, toolRequests);

        return (new ChatMessage(ChatRole.Tool, allContents), pluginExpansions, skillExpansions, successfulFunctions);
    }

    /// <summary>
    /// Executes tools in parallel for improved performance with multiple independent tools
    /// </summary>
    private async Task<(ChatMessage Message, HashSet<string> PluginExpansions, HashSet<string> SkillExpansions, HashSet<string> SuccessfulFunctions)> ExecuteInParallelAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentLoopState agentLoopState,
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
            var function = FunctionMapBuilder.FindFunctionInList(toolRequest.Name, options?.Tools);

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
            toolRequests, options, agentLoopState, _agent, cancellationToken).ConfigureAwait(false);

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
                    currentHistory, options, singleToolList, agentLoopState, cancellationToken).ConfigureAwait(false);

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

        // Extract plugin expansions from results
        var pluginExpansions = new HashSet<string>();
        var skillExpansions = new HashSet<string>();

        // Add results from approved tools
        foreach (var result in results)
        {
            if (result.Success)
            {
                foreach (var message in result.Messages)
                {
                    allContents.AddRange(message.Contents);
                }

                // Check if this was a container and track expansion
                foreach (var content in result.Messages.SelectMany(m => m.Contents))
                {
                    if (content is FunctionResultContent functionResult)
                    {
                        if (pluginContainerExpansions.TryGetValue(functionResult.CallId, out var pluginName))
                        {
                            pluginExpansions.Add(pluginName);
                        }
                        else if (skillContainerExpansions.TryGetValue(functionResult.CallId, out var skillName))
                        {
                            skillExpansions.Add(skillName);
                        }
                    }
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

        // ✅ Extract successful functions from actual results (not before execution)
        var successfulFunctions = ExtractSuccessfulFunctions(allContents, approvedTools);

        return (new ChatMessage(ChatRole.Tool, allContents), pluginExpansions, skillExpansions, successfulFunctions);
    }

    /// <summary>
    /// Fast O(1) function lookup helper for ToolScheduler.
    /// Uses manual iteration
    /// PERFORMANCE: Replaces OfType + FirstOrDefault LINQ chain with direct iteration.
    /// </summary>
    /// <param name="functionName">The name of the function to find</param>
    /// <param name="tools">The list of available tools</param>
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
        var originalText = ContentExtractor.ExtractText(lastUserMessage);

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
}

#endregion

#region Permission Management

/// <summary>
/// Strongly-typed permission result (replaces bool return)
/// </summary>
internal record PermissionResult(
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
internal record PermissionBatchResult(
    List<FunctionCallContent> Approved,
    List<(FunctionCallContent Tool, string Reason)> Denied);

/// <summary>
/// Centralized manager for all permission-related logic.
/// Eliminates duplication and provides single source of truth for permission decisions.
/// </summary>
internal class PermissionManager
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
        AgentLoopState state,
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
        var toolCallRequest = new ToolCallRequest
        {
            FunctionName = functionCall.Name,
            Arguments = functionCall.Arguments ?? new Dictionary<string, object?>()
        };
        
        var context = new FunctionInvocationContext
        {
            ToolCallRequest = toolCallRequest,
            Function = function,
            Arguments = toolCallRequest.Arguments,
            ArgumentsWrapper = new AIFunctionArguments(toolCallRequest.Arguments),
            State = state,  // Use AgentLoopState as single source of truth
            AgentName = state.AgentName,
            CallId = functionCall.CallId,
            Iteration = state.Iteration,
            TotalFunctionCallsInRun = state.CompletedFunctions.Count,
            // Set OutboundEvents and Agent for permission event emission
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
        AgentLoopState state,
        Agent agent,
        CancellationToken cancellationToken)
    {
        var approved = new List<FunctionCallContent>();
        var denied = new List<(FunctionCallContent Tool, string Reason)>();
        
        // Build function map ONCE per batch (not per request)
        var functionMap = FunctionMapBuilder.BuildMap(options?.Tools);
        
        foreach (var toolRequest in toolRequests)
        {
            // O(1) lookup instead of O(n) LINQ scan
            var function = FunctionMapBuilder.FindFunction(toolRequest.Name ?? string.Empty, functionMap);
            
            var result = await CheckPermissionAsync(
                toolRequest, function, state, agent, cancellationToken).ConfigureAwait(false);
            
            if (result.IsApproved)
                approved.Add(toolRequest);
            else
                denied.Add((toolRequest, result.DenialReason ?? "Unknown"));
        }
        
        return new PermissionBatchResult(approved, denied);
    }
    
    /// <summary>
    /// Executes the permission filter pipeline (single responsibility)
    /// </summary>
    private async Task ExecutePermissionPipeline(
        FunctionInvocationContext context, 
        CancellationToken cancellationToken)
    {
        // Build and execute the permission filter pipeline using FilterChain
        Func<FunctionInvocationContext, Task> finalAction = _ => Task.CompletedTask;
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
/// - Full event streaming with Run/Step/Tool lifecycle
/// - Simplified content-only streaming (Microsoft.Extensions.AI)
/// - Error handling wrapper for all protocols
/// </summary>
internal static class EventStreamAdapter
{
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
internal class BidirectionalEventCoordinator : IDisposable
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
    /// <exception cref="InvalidOperationException">If setting this parent would create a cycle</exception>
    /// <remarks>
    /// Use this when an agent is being used as a tool by another agent (via AsAIFunction).
    /// This enables events from nested agents to be visible to the orchestrator.
    ///
    /// <b>Cycle Detection:</b>
    /// This method validates that setting the parent does not create a cycle in the parent chain.
    /// A cycle would cause infinite recursion during Emit(), leading to stack overflow.
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
        if (parent == null)
            throw new ArgumentNullException(nameof(parent));

        // Check for self-reference (simplest cycle)
        if (parent == this)
            throw new InvalidOperationException(
                "Cannot set coordinator as its own parent. This would create an infinite loop during event emission.");

        // Check for cycles in the parent chain
        // Walk up the parent chain and ensure we don't encounter 'this' coordinator
        var current = parent;
        var visited = new HashSet<BidirectionalEventCoordinator> { this };

        while (current != null)
        {
            if (!visited.Add(current))
            {
                // We've seen this coordinator before in the chain - cycle detected
                throw new InvalidOperationException(
                    "Cycle detected in parent coordinator chain. Setting this parent would create an infinite loop during event emission. " +
                    "Ensure parent chains form a tree structure (child -> parent -> grandparent) without loops.");
            }

            current = current._parentCoordinator;
        }

        _parentCoordinator = parent;
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
    /// Called by external handlers when user provides input.
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
    public static Func<FunctionInvocationContext, Task> BuildAiFunctionPipeline(
        IEnumerable<IAiFunctionFilter> filters,
        Func<FunctionInvocationContext, Task> finalAction)
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
    public static Func<FunctionInvocationContext, Task> BuildPermissionPipeline(
        IEnumerable<IPermissionFilter> filters,
        Func<FunctionInvocationContext, Task> finalAction)
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
/// independent of any specific protocol.
///
/// KEY CONCEPTS:
/// - MESSAGE TURN: The entire user interaction (user sends message → agent responds)
///   May contain multiple agent turns if tools are called
/// - AGENT TURN: A single call to the LLM (one iteration in the agentic loop)
///   Multiple agent turns happen within one message turn when using tools
///
/// Adapters convert these to protocol-specific formats as needed.
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

/// <summary>
/// Emitted during agent execution to expose internal state for testing/debugging.
/// NOT intended for production use - only for characterization tests and debugging.
/// </summary>
public record InternalStateSnapshotEvent(
    int CurrentIteration,
    int MaxIterations,
    bool IsTerminated,
    string? TerminationReason,
    int ConsecutiveErrorCount,
    List<string> CompletedFunctions) : InternalAgentEvent;

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
/// Sent by external handler back to waiting filter.
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
/// Sent by external handler back to waiting agent/plugin.
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

#region Function Invocation Context

/// <summary>
/// Unified function invocation context for HPD-Agent.
/// Provides ambient context (via AsyncLocal) AND rich orchestration capabilities.
/// Consolidates all function execution metadata, event coordination, and bidirectional communication.
/// </summary>
/// <remarks>
/// This class serves dual purposes:
/// 1. AMBIENT CONTEXT: Flows across async calls via AsyncLocal in Agent.CurrentFunctionContext
/// 2. ORCHESTRATION CONTEXT: Passed through filter pipelines with rich coordination features
///
/// Key capabilities:
/// - Function metadata (name, description, arguments, CallId)
/// - Agent context (AgentName, RunContext, Iteration tracking)
/// - Bidirectional communication (Emit, WaitForResponseAsync)
/// - Event coordination and bubbling (Agent reference, OutboundEvents)
/// - Filter pipeline control (IsTerminated, Result)
///
/// Use cases:
/// - Plugins accessing execution context via Agent.CurrentFunctionContext
/// - Filters emitting events and waiting for user responses
/// - Permission/clarification workflows (human-in-the-loop)
/// - Telemetry, logging, and security auditing
/// - Nested agent coordination and event bubbling
/// </remarks>
/// <summary>
/// Context for function invocations in the filter pipeline.
/// Internal class for HPD-Agent internals.
/// </summary>
internal class FunctionInvocationContext
{
    /// <summary>
    /// The AI function being invoked.
    /// </summary>
    public AIFunction? Function { get; set; }

    /// <summary>
    /// Name of the function being invoked.
    /// </summary>
    public string FunctionName => Function?.Name ?? ToolCallRequest?.FunctionName ?? string.Empty;

    /// <summary>
    /// Description of the function being invoked.
    /// </summary>
    public string? FunctionDescription => Function?.Description;

    /// <summary>
    /// Arguments being passed to the function (structured access).
    /// For filter pipeline: Use this for AIFunctionArguments wrapper.
    /// For ambient access: Use Arguments property for raw dictionary.
    /// </summary>
    public AIFunctionArguments? ArgumentsWrapper { get; set; }

    /// <summary>
    /// Arguments being passed to the function (raw dictionary access).
    /// </summary>
    public IDictionary<string, object?> Arguments { get; set; } = new Dictionary<string, object?>();

    /// <summary>
    /// Unique identifier for this function call (for correlation).
    /// </summary>
    public string CallId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the agent that initiated this function call.
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// Current iteration number in the agent's execution loop.
    /// </summary>
    public int Iteration { get; set; }

    /// <summary>
    /// Total number of function calls made in this agent run so far.
    /// </summary>
    public int TotalFunctionCallsInRun { get; set; }

    /// <summary>
    /// Current agent loop state (immutable state tracking)
    /// </summary>
    public AgentLoopState? State { get; set; }

    /// <summary>
    /// Extensible metadata dictionary for custom data.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// The raw tool call request from the Language Model (for filter pipeline).
    /// </summary>
    public ToolCallRequest? ToolCallRequest { get; set; }

    /// <summary>
    /// A flag to allow a filter to terminate the pipeline.
    /// </summary>
    public bool IsTerminated { get; set; } = false;

    /// <summary>
    /// The result of the function invocation, to be set by the final step.
    /// </summary>
    public object? Result { get; set; }

    /// <summary>
    /// Channel writer for emitting events during filter execution.
    /// Points to Agent's shared channel - events are immediately visible to background drainer.
    ///
    /// Thread-safety: Multiple filters in the pipeline can emit concurrently.
    /// Event ordering: FIFO within each filter, interleaved across filters.
    /// Lifetime: Valid for entire filter execution.
    /// </summary>
    internal ChannelWriter<InternalAgentEvent>? OutboundEvents { get; set; }

    /// <summary>
    /// Reference to the agent for response coordination.
    /// Lifetime: Set by ProcessFunctionCallsAsync, valid for entire filter execution.
    /// </summary>
    internal Agent? Agent { get; set; }

    /// <summary>
    /// Emits an event that will be yielded by RunAgenticLoopInternal.
    /// Events are delivered immediately to background drainer (not batched).
    /// Automatically bubbles events to parent agent if this is a nested agent call.
    ///
    /// Thread-safety: Safe to call from any filter in the pipeline.
    /// Performance: Non-blocking write (unbounded channel).
    /// Event ordering: Guaranteed FIFO per filter, interleaved across filters.
    /// Real-time visibility: Handler sees event WHILE filter is executing (not after).
    /// Event bubbling: If Agent.RootAgent is set, events bubble to orchestrator.
    /// </summary>
    /// <param name="evt">The event to emit</param>
    /// <exception cref="ArgumentNullException">If event is null</exception>
    /// <exception cref="InvalidOperationException">If Agent reference is not configured</exception>
    public void Emit(InternalAgentEvent evt)
    {
        if (evt == null)
            throw new ArgumentNullException(nameof(evt));

        if (Agent == null)
            throw new InvalidOperationException("Agent reference not configured for this context");

        // Emit to local agent's coordinator
        Agent.EventCoordinator.Emit(evt);

        // If we're a nested agent (RootAgent is set and different from us), bubble to root
        // RootAgent is a static property on the Agent class
        var rootAgent = HPD.Agent.Agent.RootAgent;
        if (rootAgent != null && rootAgent != Agent)
        {
            rootAgent.EventCoordinator.Emit(evt);
        }
    }

    /// <summary>
    /// Emits an event and returns immediately (async version for bounded channels if needed).
    /// Current implementation uses unbounded channels, so this is identical to Emit().
    /// Kept for future extensibility if bounded channels are introduced.
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
    ///
    /// Thread-safety: Safe to call from any filter.
    /// Cancellation: Respects both timeout and external cancellation token.
    /// Type safety: Validates response type and throws clear error on mismatch.
    /// Cleanup: Automatically removes TCS from waiters dictionary on completion/timeout/cancellation.
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

    /// <summary>
    /// Gets a string representation for logging/debugging.
    /// </summary>
    public override string ToString() =>
        $"Function: {FunctionName}, CallId: {CallId}, Agent: {AgentName ?? "Unknown"}, Iteration: {Iteration}";
}
# endregion

/// <summary>
/// Structured logging service with state awareness for agent execution.
/// Provides HPD-Agent specific logging that Microsoft.Extensions.AI doesn't provide:
/// - Agent decision logging
/// - Circuit breaker warnings
/// - State snapshots
///
/// Note: Basic invocation logging (requests/responses) should be handled by
/// Microsoft's LoggingChatClient middleware when applied to the base client.
/// </summary>
internal sealed class AgentLoggingService
{
    private readonly ILogger _logger;
    private readonly bool _enableSensitiveData;

    public AgentLoggingService(ILogger logger, LoggingConfig config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enableSensitiveData = config.EnableSensitiveData;
    }

    public void LogDecision(
        string agentName,
        AgentDecision decision,
        AgentLoopState state)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

        _logger.LogDebug(
            "Agent '{AgentName}' iteration {Iteration}: Decision={Decision}, " +
            "State(Failures={Failures}, Plugins={Plugins}, Functions={Functions})",
            agentName,
            state.Iteration,
            decision.GetType().Name,
            state.ConsecutiveFailures,
            state.ExpandedPlugins.Count,
            state.CompletedFunctions.Count);
    }

    public void LogIterationStart(
        string agentName,
        int iteration,
        int maxIterations)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

        _logger.LogDebug(
            "Agent '{AgentName}' iteration {Iteration}/{MaxIterations} started",
            agentName,
            iteration,
            maxIterations);
    }

    public void LogCompletion(
        string agentName,
        int iteration)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;

        _logger.LogInformation(
            "Agent '{AgentName}' completed after {Iterations} iterations",
            agentName,
            iteration);
    }

    public void LogCircuitBreakerTriggered(
        string agentName,
        string functionName,
        int consecutiveCount)
    {
        _logger.LogWarning(
            "Agent '{AgentName}' circuit breaker triggered: '{FunctionName}' called {Count} times consecutively",
            agentName,
            functionName,
            consecutiveCount);
    }

    public void LogCheckpointFailure(
        Exception exception,
        string threadId,
        int iteration)
    {
        _logger.LogWarning(
            exception,
            "Failed to checkpoint at iteration {Iteration} for thread {ThreadId}",
            iteration,
            threadId);
    }

}

/// <summary>
/// OpenTelemetry instrumentation service for agent orchestration.
/// Tracks HPD-Agent specific metrics that Microsoft.Extensions.AI doesn't provide:
/// - Agent decision tracking (CallLLM, Complete, Terminate)
/// - Circuit breaker triggers
/// - Iteration counts per orchestration run
///
/// Note: Standard Gen AI metrics (token usage, duration) should be handled by
/// Microsoft's OpenTelemetryChatClient middleware when applied to the base client.
/// </summary>
internal sealed class AgentTelemetryService : IDisposable
{
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly bool _enableSensitiveData;
    private readonly JsonSerializerOptions _jsonOptions;

    // HPD-Agent specific metrics (not provided by Microsoft)
    private readonly Counter<int> _decisionCounter;
    private readonly Counter<int> _circuitBreakerCounter;
    private readonly Histogram<int> _iterationHistogram;
    private readonly Counter<long> _checkpointErrorCounter;
    private readonly Histogram<double> _checkpointDuration;
    private readonly Counter<long> _pendingWritesSaveCounter;
    private readonly Counter<long> _pendingWritesLoadCounter;
    private readonly Counter<long> _pendingWritesDeleteCounter;
    private readonly Histogram<int> _pendingWritesCountHistogram;

    public AgentTelemetryService(TelemetryConfig config)
    {
        var sourceName = config.SourceName ?? "HPD.Agent";
        _activitySource = new ActivitySource(sourceName);
        _meter = new Meter(sourceName);

        // Check environment variable for sensitive data override
        var envVar = Environment.GetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT");
        _enableSensitiveData = config.EnableSensitiveData ||
                               (envVar?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false);

        _jsonOptions = global::Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions;

        // Agent-specific metrics (unique to HPD-Agent)
        _decisionCounter = _meter.CreateCounter<int>(
            "hpd.agent.decision.count",
            "decisions",
            "Agent decision engine execution count");

        _circuitBreakerCounter = _meter.CreateCounter<int>(
            "hpd.agent.circuit_breaker.triggered",
            "triggers",
            "Circuit breaker activation count");

        _iterationHistogram = _meter.CreateHistogram<int>(
            "hpd.agent.iteration.count",
            "iterations",
            "Distribution of iteration counts per agent run");

        _checkpointErrorCounter = _meter.CreateCounter<long>(
            "hpd.agent.checkpoint.errors",
            "errors",
            "Number of checkpoint save failures");

        _checkpointDuration = _meter.CreateHistogram<double>(
            "hpd.agent.checkpoint.duration",
            "ms",
            "Time taken to save checkpoint");

        _pendingWritesSaveCounter = _meter.CreateCounter<long>(
            "hpd.agent.pending_writes.saves",
            "writes",
            "Number of pending writes saved");

        _pendingWritesLoadCounter = _meter.CreateCounter<long>(
            "hpd.agent.pending_writes.loads",
            "loads",
            "Number of pending writes load operations");

        _pendingWritesDeleteCounter = _meter.CreateCounter<long>(
            "hpd.agent.pending_writes.deletes",
            "deletes",
            "Number of pending writes delete operations");

        _pendingWritesCountHistogram = _meter.CreateHistogram<int>(
            "hpd.agent.pending_writes.count",
            "writes",
            "Distribution of pending write counts per operation");
    }

    public Activity? StartOrchestration(
        string agentName,
        string? modelId,
        string? providerKey,
        int maxIterations,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        AgentLoopState initialState)
    {
        var activity = _activitySource.StartActivity(
            string.IsNullOrWhiteSpace(options?.ModelId) ? "chat" : $"chat {options.ModelId}",
            ActivityKind.Client);

        if (activity is { IsAllDataRequested: true })
        {
            // Standard Gen AI tags
            activity
                .AddTag("gen_ai.operation.name", "chat")
                .AddTag("gen_ai.request.model", options?.ModelId ?? modelId)
                .AddTag("gen_ai.system", providerKey);

            // Agent-specific tags (state-aware)
            activity
                .AddTag("agent.name", agentName)
                .AddTag("agent.max_iterations", maxIterations)
                .AddTag("agent.run_id", initialState.RunId)
                .AddTag("agent.conversation_id", initialState.ConversationId)
                .AddTag("agent.initial_message_count", initialState.CurrentMessages.Count);

            // Sensitive data (opt-in)
            if (_enableSensitiveData)
            {
                try
                {
                    var messagesJson = JsonSerializer.Serialize(messages, _jsonOptions);
                    activity.AddTag("gen_ai.prompt", messagesJson);
                }
                catch (Exception)
                {
                    // Serialization errors shouldn't break execution
                }
            }
        }

        return activity;
    }

    public void RecordDecision(
        Activity? activity,
        AgentDecision decision,
        AgentLoopState state,
        string agentName,
        AgentConfiguration config)
    {
        if (activity is { IsAllDataRequested: true })
        {
            // Decision type tag
            var decisionType = decision switch
            {
                AgentDecision.CallLLM => "call_llm",
                AgentDecision.Complete => "complete",
                AgentDecision.Terminate => "terminate",
                _ => "unknown"
            };

            activity.AddTag($"agent.iteration.{state.Iteration}.decision", decisionType);

            // State snapshot at decision point
            activity
                .AddTag($"agent.iteration.{state.Iteration}.consecutive_failures", state.ConsecutiveFailures)
                .AddTag($"agent.iteration.{state.Iteration}.expanded_plugins", state.ExpandedPlugins.Count)
                .AddTag($"agent.iteration.{state.Iteration}.completed_functions", state.CompletedFunctions.Count);

            // Circuit breaker proximity warning
            if (state.ConsecutiveCountPerTool.Any())
            {
                var maxConsecutive = state.ConsecutiveCountPerTool.Values.Max();
                var threshold = config.MaxConsecutiveFunctionCalls ?? int.MaxValue;
                if (maxConsecutive >= threshold - 1)
                {
                    activity.AddTag("agent.circuit_breaker.near_threshold", true);
                }
            }
        }

        // Record metric
        _decisionCounter.Add(1, new TagList
        {
            { "agent.name", agentName },
            { "decision.type", decision switch
                {
                    AgentDecision.CallLLM => "call_llm",
                    AgentDecision.Complete => "complete",
                    AgentDecision.Terminate => "terminate",
                    _ => "unknown"
                }
            },
            { "iteration", state.Iteration.ToString() }
        });
    }

    public void RecordCircuitBreakerTrigger(
        string agentName,
        string functionName,
        int consecutiveCount,
        int threshold)
    {
        _circuitBreakerCounter.Add(1, new TagList
        {
            { "agent.name", agentName },
            { "function.name", functionName },
            { "consecutive_count", consecutiveCount.ToString() }
        });
    }

    public void RecordCompletion(
        Activity? activity,
        AgentLoopState finalState,
        string? modelId)
    {
        if (activity is { IsAllDataRequested: true })
        {
            // HPD-specific: Total iterations
            activity.AddTag("agent.total_iterations", finalState.Iteration);

            // Set status
            activity.SetStatus(ActivityStatusCode.Ok);
        }

        // HPD-specific: Iteration histogram
        _iterationHistogram.Record(finalState.Iteration, new TagList
        {
            { "agent.name", finalState.AgentName },
            { "gen_ai.request.model", modelId }
        });
    }

    /// <summary>
    /// Records a successful checkpoint save operation.
    /// </summary>
    public void RecordCheckpointSuccess(TimeSpan duration, string threadId, int iteration)
    {
        _checkpointDuration.Record(duration.TotalMilliseconds, new TagList
        {
            { "thread.id", threadId },
            { "iteration", iteration.ToString() },
            { "success", "true" }
        });
    }

    /// <summary>
    /// Records a failed checkpoint save operation.
    /// </summary>
    public void RecordCheckpointFailure(Exception ex, string threadId, int iteration)
    {
        _checkpointErrorCounter.Add(1, new TagList
        {
            { "thread.id", threadId },
            { "iteration", iteration.ToString() },
            { "error.type", ex.GetType().Name }
        });
    }

    /// <summary>
    /// Records a pending writes save operation.
    /// </summary>
    public void RecordPendingWritesSave(int count, string threadId, int iteration)
    {
        _pendingWritesSaveCounter.Add(count, new TagList
        {
            { "thread.id", threadId },
            { "iteration", iteration.ToString() }
        });

        _pendingWritesCountHistogram.Record(count, new TagList
        {
            { "operation", "save" },
            { "thread.id", threadId }
        });
    }

    /// <summary>
    /// Records a pending writes load operation.
    /// </summary>
    public void RecordPendingWritesLoad(int count, string threadId)
    {
        _pendingWritesLoadCounter.Add(1, new TagList
        {
            { "thread.id", threadId },
            { "loaded_count", count.ToString() }
        });

        if (count > 0)
        {
            _pendingWritesCountHistogram.Record(count, new TagList
            {
                { "operation", "load" },
                { "thread.id", threadId }
            });
        }
    }

    /// <summary>
    /// Records a pending writes delete operation.
    /// </summary>
    public void RecordPendingWritesDelete(string threadId, int iteration)
    {
        _pendingWritesDeleteCounter.Add(1, new TagList
        {
            { "thread.id", threadId },
            { "iteration", iteration.ToString() }
        });
    }

    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }
}