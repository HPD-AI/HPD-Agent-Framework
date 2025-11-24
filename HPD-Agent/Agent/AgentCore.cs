using Microsoft.Extensions.AI;
using System.Threading.Channels;
using HPD.Agent.Internal.MiddleWare;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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
using HPD_Agent.Scoping;


namespace HPD.Agent;

/// <summary>
/// Protocol-agnostic agent execution engine.
/// Provides a pure, composable core for building AI agents without framework dependencies.
/// Delegates to specialized components for clean separation of concerns.
/// INTERNAL: Use HPD.Agent.Microsoft.Agent or HPD.Agent.AGUI.Agent for protocol-specific APIs.
///
/// <strong>Concurrency Model: Stateless (Fully Concurrent)</strong>
/// This Agent instance is now fully stateless and thread-safe. Multiple concurrent RunAsync() calls
/// on the same instance are supported - each call operates on its own ConversationThread.
///
/// Architecture:
/// - Agent is stateless (no mutable conversation state)
/// - ConversationThread owns conversation-specific state (messages, ConversationId, etc.)
/// - BidirectionalEventCoordinator is thread-safe (uses Channel and ConcurrentDictionary)
/// - One agent instance can serve unlimited concurrent threads
///
/// For parallel agent execution across multiple threads, reuse the same Agent instance:
/// Example:
/// <code>
/// // ✅ NOW SUPPORTED: Concurrent calls on same agent instance with different threads
/// var agent = new Agent(...);
/// var thread1 = agent.CreateThread();
/// var thread2 = agent.CreateThread();
///
/// var results = await Task.WhenAll(
///     agent.RunAsync(messages1, thread: thread1).ToListAsync(),
///     agent.RunAsync(messages2, thread: thread2).ToListAsync()
/// );
///
/// // Each thread maintains its own conversation state, ConversationId, and message history
/// // The agent is stateless and can serve both threads concurrently
/// </code>
/// </summary>
internal sealed class AgentCore
{
    private readonly IChatClient _baseClient;
    private readonly IChatClient? _summarizerClient;
    private readonly string _name;
    private readonly ScopedFunctionMiddlewareManager? _ScopedFunctionMiddlewareManager;
    private readonly int _maxFunctionCalls;
    private readonly IServiceProvider? _serviceProvider; // Used for middleware dependency injection

    // Microsoft.Extensions.AI compliance fields
    private readonly ChatClientMetadata _metadata;
    private readonly ErrorHandlingPolicy _errorPolicy;

    // OpenTelemetry Activity Source for telemetry
    private static readonly ActivitySource ActivitySource = new("HPD.Agent");

    // AsyncLocal storage for function invocation context (flows across async calls)
    // Stores the full FunctionInvocationContext with all orchestration capabilities
    private static readonly AsyncLocal<FunctionInvocationContext?> _currentFunctionContext = new();

    // AsyncLocal storage for root agent tracking in nested agent calls
    // Used for event bubbling from nested agents to their orchestrator
    // When an agent calls another agent (via AsAIFunction), this tracks the top-level orchestrator
    // Flows automatically through AsyncLocal propagation across nested async calls
    private static readonly AsyncLocal<AgentCore?> _rootAgent = new();

    // AsyncLocal storage for current conversation thread (flows across async calls)
    // Provides access to thread context (project, documents, etc.) throughout the agent execution
    private static readonly AsyncLocal<ConversationThread?> _currentThread = new();

    // Specialized component fields for delegation
    private readonly MessageProcessor _messageProcessor;
    private readonly FunctionCallProcessor _functionCallProcessor;
    private readonly AgentTurn _agentTurn;
    private readonly UnifiedScopingManager _scopingManager;
    private readonly PermissionManager _permissionManager;
    private readonly BidirectionalEventCoordinator _eventCoordinator;
    private readonly IReadOnlyList<IAIFunctionMiddleware> _AIFunctionMiddlewares;
    private readonly IReadOnlyList<IMessageTurnMiddleware> _MessageTurnMiddlewares;
    private readonly IReadOnlyList<IIterationMiddleWare> _IterationMiddleWares;
    private readonly ErrorHandling.IProviderErrorHandler _providerErrorHandler;

    // Observer pattern for event-driven observability
    private readonly IReadOnlyList<IAgentEventObserver> _observers;
    private readonly ObserverHealthTracker? _observerHealthTracker;
    private readonly ILogger? _observerErrorLogger;
    private readonly Counter<long>? _observerErrorCounter;

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
    /// Use this in plugins/Middlewares to access metadata about the current function call:
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
    public static AgentCore? RootAgent
    {
        get => _rootAgent.Value;
        internal set => _rootAgent.Value = value;
    }

    /// <summary>
    /// Gets or sets the current conversation thread in the execution context.
    /// This flows across async calls and provides access to thread context throughout agent execution.
    /// </summary>
    /// <remarks>
    /// This property enables Middlewares and other components to access thread-specific context
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
    /// Error handling policy for normalizing provider errors
    /// </summary>
    public ErrorHandlingPolicy ErrorPolicy => _errorPolicy;

    /// <summary>
    /// Internal access to event coordinator for context setup and nested agent configuration.
    /// </summary>
    internal BidirectionalEventCoordinator EventCoordinator => _eventCoordinator;

    /// <summary>
    /// Internal access to Middleware event channel writer for context setup.
    /// Delegates to the event coordinator.
    /// </summary>
    internal ChannelWriter<InternalAgentEvent> MiddlewareEventWriter => _eventCoordinator.EventWriter;

    /// <summary>
    /// Internal access to Middleware event channel reader for RunAgenticLoopInternal.
    /// Delegates to the event coordinator.
    /// </summary>
    internal ChannelReader<InternalAgentEvent> MiddlewareEventReader => _eventCoordinator.EventReader;

    /// <summary>
    /// Sends a response to a Middleware waiting for a specific request.
    /// Called by external handlers when user provides input.
    /// Thread-safe: Can be called from any thread.
    /// Delegates to the event coordinator.
    /// </summary>
    /// <param name="requestId">The unique identifier for the request</param>
    /// <param name="response">The response event to deliver</param>
    /// <exception cref="ArgumentNullException">If response is null</exception>
    public void SendMiddlewareResponse(string requestId, InternalAgentEvent response)
    {
        _eventCoordinator.SendResponse(requestId, response);
    }

    /// <summary>
    /// Internal method for Middlewares to wait for responses.
    /// Called by AiFunctionContext.WaitForResponseAsync().
    /// Delegates to the event coordinator.
    /// </summary>
    internal async Task<T> WaitForMiddlewareResponseAsync<T>(
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
    public AgentCore(
        AgentConfig config,
        IChatClient baseClient,
        ChatOptions? mergedOptions,
        List<IPromptMiddleware> PromptMiddlewares,
        ScopedFunctionMiddlewareManager ScopedFunctionMiddlewareManager,
        ErrorHandling.IProviderErrorHandler providerErrorHandler,
        IReadOnlyList<IPermissionMiddleware>? PermissionMiddlewares = null,
        IReadOnlyList<IAIFunctionMiddleware>? AIFunctionMiddlewares = null,
        IReadOnlyList<IMessageTurnMiddleware>? MessageTurnMiddlewares = null,
        IReadOnlyList<IIterationMiddleWare>? IterationMiddleWares = null,
        IServiceProvider? serviceProvider = null,
        IEnumerable<IAgentEventObserver>? observers = null,
        IChatClient? summarizerClient = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _summarizerClient = summarizerClient;
        _name = config.Name ?? "Agent"; // Default to "Agent" to prevent null dictionary key exceptions
        _ScopedFunctionMiddlewareManager = ScopedFunctionMiddlewareManager ?? throw new ArgumentNullException(nameof(ScopedFunctionMiddlewareManager));
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

        // Fix: Store and use AI function Middlewares
        _AIFunctionMiddlewares = AIFunctionMiddlewares ?? new List<IAIFunctionMiddleware>();
        _MessageTurnMiddlewares = MessageTurnMiddlewares ?? new List<IMessageTurnMiddleware>();
        _IterationMiddleWares = IterationMiddleWares ?? new List<IIterationMiddleWare>();

        // Create bidirectional event coordinator for Middleware events and human-in-the-loop
        _eventCoordinator = new BidirectionalEventCoordinator();

        // Create permission manager
        _permissionManager = new PermissionManager(PermissionMiddlewares);

        // Create history reducer if configured
        var chatReducer = CreateChatReducer(config, baseClient);

        // Augment system instructions with plan mode guidance if enabled
        var systemInstructions = AugmentSystemInstructionsForPlanMode(config);

        _messageProcessor = new MessageProcessor(
            systemInstructions,
            mergedOptions ?? config.Provider?.DefaultChatOptions,
            PromptMiddlewares,
            chatReducer,
            config.HistoryReduction);
        _functionCallProcessor = new FunctionCallProcessor(
            this, // NEW: Pass agent reference for Middleware event coordination
            ScopedFunctionMiddlewareManager,
            _permissionManager,
            _AIFunctionMiddlewares,
            config.MaxAgenticIterations,
            config.ErrorHandling,
            config.ServerConfiguredTools,
            config.AgenticLoop);  // NEW: Pass agentic loop config for TerminateOnUnknownCalls
        _agentTurn = new AgentTurn(
            _baseClient,
            config.ConfigureOptions,
            config.ChatClientMiddleware,
            serviceProvider);

        // Initialize unified scoping manager (without deprecated SkillDefinitions)
        var initialTools = (mergedOptions ?? config.Provider?.DefaultChatOptions)?.Tools?
            .OfType<AIFunction>().ToList() ?? new List<AIFunction>();

        // Get explicitly registered plugins from config
        var explicitlyRegisteredPlugins = config.ExplicitlyRegisteredPlugins ??
            ImmutableHashSet<string>.Empty;

        _scopingManager = new UnifiedScopingManager(
            initialTools,
            explicitlyRegisteredPlugins);

        // ToolScheduler removed - FunctionCallProcessor now handles all tool execution

        // ═══════════════════════════════════════════════════════
        // INITIALIZE OBSERVABILITY SERVICES
        // ═══════════════════════════════════════════════════════

        // Resolve optional dependencies from service provider
        var loggerFactory = serviceProvider?.GetService(typeof(ILoggerFactory))
            as ILoggerFactory;
        _cache = serviceProvider?.GetService(typeof(IDistributedCache)) as IDistributedCache;
        _jsonOptions = global::Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions;
        _cachingConfig = config.Caching;

        // Initialize event observers
        _observers = observers?.ToList() ?? new List<IAgentEventObserver>();

        // Initialize observer health tracker if observers are configured
        if (_observers.Count > 0 && loggerFactory != null)
        {
            _observerErrorLogger = loggerFactory.CreateLogger<AgentCore>();

            var meterFactory = serviceProvider?.GetService(typeof(IMeterFactory)) as IMeterFactory;
            if (meterFactory != null)
            {
                var observerMeter = meterFactory.Create("HPD.Agent.Observers");
                _observerErrorCounter = observerMeter.CreateCounter<long>(
                    "agent.observer.errors",
                    description: "Number of observer processing failures");
            }

            var observabilityConfig = config.Observability ?? new ObservabilityConfig();
            _observerHealthTracker = new ObserverHealthTracker(
                _observerErrorLogger,
                _observerErrorCounter,
                observabilityConfig.MaxConsecutiveFailures,
                observabilityConfig.SuccessesToResetCircuitBreaker);
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
    /// AIFuncton Middlewares applied to tool calls in conversations (via ScopedFunctionMiddlewareManager)
    /// </summary>
    public IReadOnlyList<IAIFunctionMiddleware> AIFunctionMiddlewares => _AIFunctionMiddlewares;

    /// <summary>
    /// Maximum number of function calls allowed in a single conversation turn
    /// </summary>
    public int MaxFunctionCalls => _maxFunctionCalls;

    /// <summary>
    /// Scoped Middleware manager for applying Middlewares based on function/plugin scope
    /// </summary>
    public ScopedFunctionMiddlewareManager? ScopedFunctionMiddlewareManager => _ScopedFunctionMiddlewareManager;

    #region internal loop
    /// <summary>
    /// Protocol-agnostic core agentic loop that emits internal events.
    /// This method contains all the agent logic without any protocol-specific concerns.
    /// Adapters convert internal events to protocol-specific formats as needed.
    ///
    /// ARCHITECTURE (Option 2 Pattern - Clean Break):
    /// - Accepts PreparedTurn (functional preparation from MessageProcessor.PrepareTurnAsync)
    /// - Uses AgentDecisionEngine (pure, testable) for all decision logic
    /// - Executes decisions INLINE to preserve real-time streaming
    /// - State managed via immutable AgentLoopState for testability
    ///
    /// This delivers:
    /// - Clean separation: Preparation (MessageProcessor) vs Execution (this method)
    /// - Type-safe prepared state (PreparedTurn record)
    /// - Testable decision logic (unit tests in microseconds)
    /// - Real-time streaming (no buffering overhead)
    /// - Cache-aware history reduction (90% cost savings)
    /// </summary>

    /// <summary>
    /// Notifies all registered observers about an event using fire-and-forget pattern.
    /// Observer failures are logged but don't impact agent execution.
    /// Circuit breaker pattern automatically disables failing observers.
    /// </summary>
    private void NotifyObservers(InternalAgentEvent evt)
    {
        if (_observers.Count == 0) return;

        foreach (var observer in _observers)
        {
            // Skip observers with open circuit breakers
            if (_observerHealthTracker != null && !_observerHealthTracker.ShouldProcess(observer))
            {
                continue;
            }

            // Check observer-specific Middleware
            if (!observer.ShouldProcess(evt))
            {
                continue;
            }

            // Fire-and-forget: run observer asynchronously without waiting
            _ = Task.Run(async () =>
            {
                try
                {
                    await observer.OnEventAsync(evt).ConfigureAwait(false);

                    // Record success (may close circuit if it was open)
                    _observerHealthTracker?.RecordSuccess(observer);
                }
                catch (Exception ex)
                {
                    // Structured logging - failures are visible
                    _observerErrorLogger?.LogError(ex,
                        "Observer {ObserverType} failed processing {EventType}",
                        observer.GetType().Name, evt.GetType().Name);

                    // Record failure (may open circuit)
                    _observerHealthTracker?.RecordFailure(observer, ex);
                }
            });
        }
    }

    private async IAsyncEnumerable<InternalAgentEvent> RunAgenticLoopInternal(
        PreparedTurn turn,
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

        // ═══════════════════════════════════════════════════════
        // EXTRACT PREPARED STATE (Option 2 Pattern)
        // ═══════════════════════════════════════════════════════
        // PreparedTurn contains:
        // - MessagesForLLM: Full history + new input (optionally reduced)
        // - NewInputMessages: Only the NEW messages (for persistence)
        // - Options: Merged options with system instructions
        // - ActiveReduction: Reduction state (if applied)
        IReadOnlyList<ChatMessage> messages = turn.MessagesForLLM;
        var newInputMessages = turn.NewInputMessages;

        // Process documents if provided (modifies messages in-place)
        if (documentPaths?.Length > 0)
        {
            var processedMessages = await AgentDocumentProcessor.ProcessDocumentsAsync(messages, documentPaths, Config, cancellationToken).ConfigureAwait(false);
            messages = processedMessages.ToList();
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

        // Extract conversation ID from turn.Options, thread, or generate new one
        string conversationId;
        if (turn.Options?.AdditionalProperties?.TryGetValue("ConversationId", out var convIdObj) == true && convIdObj is string convId)
        {
            conversationId = convId;
        }
        else if (!string.IsNullOrWhiteSpace(thread?.ConversationId))
        {
            conversationId = thread.ConversationId;
        }
        else
        {
            conversationId = Guid.NewGuid().ToString();
        }

        // Store on thread for future runs (stateless agent pattern)
        if (thread != null)
        {
            thread.ConversationId = conversationId;
        }

        try
        {
            // Emit MESSAGE TURN started event
            yield return new InternalMessageTurnStartedEvent(
                messageTurnId,
                conversationId,
                _name,
                DateTimeOffset.UtcNow);

            // ═══════════════════════════════════════════════════════════════════════════
            // MESSAGE PREPARATION: Split logic between Fresh Run vs Resume
            //
            // FRESH RUN: Process documents → PrepareMessages → Create initial state
            // RESUME:    Use state.CurrentMessages as-is (already prepared)
            // ═══════════════════════════════════════════════════════════════════════════

            AgentLoopState state;
            ReductionMetadata? reductionMetadata = null;
            IEnumerable<ChatMessage> effectiveMessages;
            ChatOptions? effectiveOptions;

            if (thread?.ExecutionState is { } executionState)
            {
                // ═══════════════════════════════════════════════════════════════════════
                // RESUME PATH: Skip preparation (state already has prepared messages)
                // ═══════════════════════════════════════════════════════════════════════

                var checkpointRestoreStart = DateTimeOffset.UtcNow;
                var restoreStopwatch = Stopwatch.StartNew();

                state = executionState;

                // Use messages from restored state (already prepared - includes system instructions)
                effectiveMessages = state.CurrentMessages;

                // Use options from PreparedTurn (already merged and Middlewareed)
                effectiveOptions = turn.Options;

                // No reduction on resume (messages were already reduced in original run)
                reductionMetadata = null;
                reductionCompletionSource.TrySetResult(null);

                restoreStopwatch.Stop();

                // Emit checkpoint restored event
                yield return new InternalCheckpointEvent(
                    Operation: CheckpointOperation.Restored,
                    ThreadId: thread.Id,
                    Timestamp: DateTimeOffset.UtcNow,
                    Duration: restoreStopwatch.Elapsed,
                    Iteration: state.Iteration,
                    MessageCount: state.CurrentMessages.Count);

                // ═══════════════════════════════════════════════════════════════════════
                // RESTORE PENDING WRITES (partial failure recovery)
                // ═══════════════════════════════════════════════════════════════════════
                if (Config?.EnablePendingWrites == true &&
                    Config?.Checkpointer != null &&
                    state.ETag != null)
                {
                    int pendingWritesCount = 0;
                    bool pendingWritesLoaded = false;

                    try
                    {
                        var pendingWrites = await Config.Checkpointer.LoadPendingWritesAsync(
                            thread.Id,
                            state.ETag,
                            effectiveCancellationToken).ConfigureAwait(false);

                        pendingWritesCount = pendingWrites.Count;
                        pendingWritesLoaded = true;

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

                    // Emit observability event outside try-catch
                    if (pendingWritesLoaded)
                    {
                        yield return new InternalCheckpointEvent(
                            Operation: CheckpointOperation.PendingWritesLoaded,
                            ThreadId: thread.Id,
                            Timestamp: DateTimeOffset.UtcNow,
                            WriteCount: pendingWritesCount);
                    }
                }

                // Log resume (logging deferred until after config is built below)
                // Observability will happen after the common configuration section
            }
            else
            {
                // ═══════════════════════════════════════════════════════════════════════
                // FRESH RUN PATH: Use PreparedTurn directly (all preparation already done)
                // ═══════════════════════════════════════════════════════════════════════

                // Initialize state with FULL unreduced history
                // PreparedTurn.MessagesForLLM contains the reduced version (for LLM calls)
                // We store the full history in state for proper message counting
                state = AgentLoopState.Initial(messages.ToList(), messageTurnId, conversationId, this.Name);

                // Use PreparedTurn's already-prepared messages and options
                effectiveMessages = turn.MessagesForLLM;  // Already reduced (if needed)
                effectiveOptions = turn.Options;  // Already merged + Middlewareed
                reductionMetadata = turn.NewReductionMetadata;  // Already computed (if new reduction)

                // Apply reduction state if available
                if (turn.ActiveReduction != null)
                {
                    state = state.WithReduction(turn.ActiveReduction);

                    // Emit cache hit/miss event based on whether this was a new reduction
                    if (turn.NewReductionMetadata != null)
                    {
                        // Cache miss - new reduction was performed in PrepareTurnAsync
                        yield return new InternalHistoryReductionCacheEvent(
                            _name,
                            IsHit: false,
                            turn.ActiveReduction.CreatedAt,
                            turn.ActiveReduction.SummarizedUpToIndex,
                            messages.ToList().Count,
                            TokenSavings: null,
                            DateTimeOffset.UtcNow);
                    }
                    else
                    {
                        // Cache hit - existing reduction was reused
                        yield return new InternalHistoryReductionCacheEvent(
                            _name,
                            IsHit: true,
                            turn.ActiveReduction.CreatedAt,
                            turn.ActiveReduction.SummarizedUpToIndex,
                            messages.ToList().Count,
                            TokenSavings: null,
                            DateTimeOffset.UtcNow);
                    }
                }

                // Set reduction metadata immediately
                reductionCompletionSource.TrySetResult(reductionMetadata);

                // ✅ NOTE: state.CurrentMessages contains FULL history (unreduced)
                // ✅ NOTE: state.ActiveReduction contains reduction metadata (if reduced)
                // ✅ NOTE: effectiveMessages contains REDUCED history (for LLM calls only)
                // ✅ NOTE: All preparation done in PrepareTurnAsync - no duplicate work!
            }

            // ═══════════════════════════════════════════════════════════════════════════
            // BUILD CONFIGURATION & DECISION ENGINE (common to both paths)
            // ═══════════════════════════════════════════════════════════════════════════

            var config = BuildDecisionConfiguration(effectiveOptions);
            var decisionEngine = new AgentDecisionEngine();

            // ✅ REMOVED: Duplicate state snapshot emission
            // State snapshots are emitted at the start of each iteration inside the main loop (line 753)
            // Emitting here caused duplicate snapshots at iteration 0, breaking StateSnapshotTests
            // This was added in commit 6646836 (2025-11-15) and caused test failures

            // ═══════════════════════════════════════════════════════
            // INITIALIZE TURN HISTORY: Add only NEW input messages (Option 2 pattern)
            // All NEW messages from this turn will be saved to thread at the end
            // PreparedTurn separates MessagesForLLM (full history) from NewInputMessages (to persist)
            // ═══════════════════════════════════════════════════════
            foreach (var msg in newInputMessages)
            {
                turnHistory.Add(msg);
            }

            ChatResponse? lastResponse = null;

            // Track expanded plugins/skills (message-turn scoped)
            // Note: These are now tracked in state.expandedScopedPluginContainers and state.ExpandedSkillContainers

            // Collect all response updates to build final history
            var responseUpdates = new List<ChatResponseUpdate>();

            // ═══════════════════════════════════════════════════════
            // OBSERVABILITY: Start telemetry and logging
            // ═══════════════════════════════════════════════════════
            var turnStopwatch = System.Diagnostics.Stopwatch.StartNew();
            Activity? telemetryActivity = null;
            try
            {
                // Note: Basic orchestration start logging removed - use Microsoft's LoggingChatClient instead
            }
            catch (Exception)
            {
                // Observability errors shouldn't break agent execution
            }


            // ═══════════════════════════════════════════════════════
            // MAIN AGENTIC LOOP (Hybrid: Pure Decisions + Inline Execution)
            // ═══════════════════════════════════════════════════════
            // NOTE: Use <= so that when MaxAgenticIterations=2, we allow iterations 0, 1, AND 2.
            // Iteration 2 will hit the continuation Middleware before attempting the 3rd LLM call.
            // This allows the Middleware to emit the continuation request event.

            while (!state.IsTerminated && state.Iteration <= config.MaxIterations)
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
                    CompletedFunctions: new List<string>(state.CompletedFunctions),
                    AgentName: _name,
                    Timestamp: DateTimeOffset.UtcNow);

                // Drain Middleware events before decision
                while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                    yield return MiddlewareEvt;

                // ═══════════════════════════════════════════════════
                // FUNCTIONAL CORE: Pure Decision (No I/O)
                // ═══════════════════════════════════════════════════

                var decision = decisionEngine.DecideNextAction(state, lastResponse, config);

                // ═══════════════════════════════════════════════════
                // OBSERVABILITY: Emit iteration and decision events
                // ═══════════════════════════════════════════════════

                // Emit iteration start event
                yield return new InternalIterationStartEvent(
                    AgentName: _name,
                    Iteration: state.Iteration,
                    MaxIterations: config.MaxIterations,
                    CurrentMessageCount: state.CurrentMessages.Count,
                    HistoryMessageCount: 0, // History is part of CurrentMessages
                    TurnHistoryMessageCount: state.TurnHistory.Count,
                    ExpandedPluginsCount: state.expandedScopedPluginContainers.Count,
                    ExpandedSkillsCount: state.ExpandedSkillContainers.Count,
                    CompletedFunctionsCount: state.CompletedFunctions.Count,
                    Timestamp: DateTimeOffset.UtcNow);

                // Emit decision event
                yield return new InternalAgentDecisionEvent(
                    AgentName: _name,
                    DecisionType: decision.GetType().Name,
                    Iteration: state.Iteration,
                    ConsecutiveFailures: state.ConsecutiveFailures,
                    ExpandedPluginsCount: state.expandedScopedPluginContainers.Count,
                    CompletedFunctionsCount: state.CompletedFunctions.Count,
                    Timestamp: DateTimeOffset.UtcNow);

                // If circuit breaker triggered, emit circuit breaker event
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

                        yield return new InternalCircuitBreakerTriggeredEvent(
                            AgentName: _name,
                            FunctionName: functionName,
                            ConsecutiveCount: count,
                            Iteration: state.Iteration,
                            Timestamp: DateTimeOffset.UtcNow);
                    }
                }

                // Drain Middleware events after decision-making, before execution
                // CRITICAL: Ensures events emitted during decision logic are yielded before LLM streaming starts
                while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                    yield return MiddlewareEvt;

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
                    // EXECUTE LLM CALL WITH ITERATION Middlewares
                    // ═══════════════════════════════════════════════════════

                    // ✅ NEW: Determine messages to send with cache-aware reduction
                    IEnumerable<ChatMessage> messagesToSend;
                    int messageCountToSend;  // Track actual count sent for history tracking

                    if (state.InnerClientTracksHistory && state.Iteration > 0)
                    {
                        // Server manages history - send only delta (new messages since last call)
                        messagesToSend = state.CurrentMessages.Skip(state.MessagesSentToInnerClient);
                        messageCountToSend = state.CurrentMessages.Count;  // Total count including previous
                    }
                    else if (state.Iteration == 0)
                    {
                        // ✅ FIRST ITERATION: Use effectiveMessages (reduced) from PrepareTurnAsync
                        // This applies history reduction for the initial LLM call
                        // effectiveMessages already contains reduced history if reduction was applied
                        messagesToSend = effectiveMessages;
                        messageCountToSend = effectiveMessages.Count();  // Reduced count!
                    }
                    else
                    {
                        // ✅ SUBSEQUENT ITERATIONS (iteration > 0):
                        // Option 1: Apply reduction if configured and available (optimal tokens)
                        // Option 2: Use full history (simpler, current default)

                        // For now, use full history (includes tool results from previous iterations)
                        // Future enhancement: Re-apply reduction on every iteration for very long conversations
                        messagesToSend = state.CurrentMessages;
                        messageCountToSend = state.CurrentMessages.Count;

                        // Future optimization (commented out for now):
                        // if (state.ActiveReduction != null && Config.HistoryReduction?.Enabled == true)
                        // {
                        //     // Re-apply reduction to include new tool results
                        //     var systemMsg = state.CurrentMessages.FirstOrDefault(m => m.Role == ChatRole.System);
                        //     messagesToSend = state.ActiveReduction.ApplyToMessages(
                        //         state.CurrentMessages.Where(m => m.Role != ChatRole.System),
                        //         systemMsg);
                        //     messageCountToSend = messagesToSend.Count();
                        // }
                    }

                    // Apply plugin scoping if enabled
                    var scopedOptions = effectiveOptions;
                    if (Config?.Scoping?.Enabled == true && effectiveOptions?.Tools != null && effectiveOptions.Tools.Count > 0)
                    {
                        scopedOptions = ApplyPluginScoping(effectiveOptions, state.expandedScopedPluginContainers, state.ExpandedSkillContainers);

                        // Emit scoped tools visible event
                        if (scopedOptions?.Tools != null)
                        {
                            var visibleToolNames = scopedOptions.Tools.Select(t => t.Name).ToList();
                            yield return new InternalScopedToolsVisibleEvent(
                                _name,
                                state.Iteration,
                                visibleToolNames,
                                state.expandedScopedPluginContainers,
                                state.ExpandedSkillContainers,
                                visibleToolNames.Count,
                                DateTimeOffset.UtcNow);
                        }
                    }
                    else if (state.InnerClientTracksHistory && scopedOptions != null && scopedOptions.ConversationId != thread?.ConversationId)
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
                            ConversationId = thread?.ConversationId
                        };
                    }

                    // ═══════════════════════════════════════════════════════
                    // ✅ NEW: Create iteration Middleware context
                    // ═══════════════════════════════════════════════════════

                    var MiddlewareContext = new IterationMiddleWareContext
                    {
                        Iteration = state.Iteration,
                        AgentName = _name,
                        Messages = messagesToSend.ToList(),
                        Options = scopedOptions,
                        State = state,
                        CancellationToken = effectiveCancellationToken,
                        Agent = this  // Enable bidirectional event communication
                    };

                    // ═══════════════════════════════════════════════════════
                    // ✅ NEW: Execute BEFORE iteration Middlewares
                    // ═══════════════════════════════════════════════════════
                    // CRITICAL: Execute iteration Middlewares with event polling
                    // This allows bidirectional events (continuation requests) to be
                    // yielded to the consumer while Middlewares wait for responses.
                    // Without polling, continuation Middleware requests would deadlock.

                    foreach (var Middleware in _IterationMiddleWares)
                    {
                        // Execute Middleware as a task and poll for events while it runs
                        var MiddlewareTask = Middleware.BeforeIterationAsync(
                            MiddlewareContext,
                            effectiveCancellationToken);

                        // Poll for Middleware events while iteration Middleware is executing
                        // This is identical to the polling pattern used for tool execution
                        while (!MiddlewareTask.IsCompleted)
                        {
                            var delayTask = Task.Delay(10, effectiveCancellationToken);
                            await Task.WhenAny(MiddlewareTask, delayTask).ConfigureAwait(false);

                            // Drain any events that were emitted during Middleware execution
                            while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                            {
                                yield return MiddlewareEvt;
                            }
                        }

                        // Ensure Middleware completes (won't block since task is complete)
                        await MiddlewareTask.ConfigureAwait(false);

                        if (MiddlewareContext.SkipLLMCall)
                        {
                            // Middleware wants to skip (e.g., cached response)
                            // Response and ToolCalls should be populated by Middleware
                            break;
                        }
                    }

                    // Final drain of any remaining events from iteration Middlewares
                    while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                    {
                        yield return MiddlewareEvt;
                    }

                    // Use potentially modified values from Middlewares
                    messagesToSend = MiddlewareContext.Messages;
                    scopedOptions = MiddlewareContext.Options;

                    // Streaming state
                    var assistantContents = new List<AIContent>();
                    var toolRequests = new List<FunctionCallContent>();
                    bool messageStarted = false;
                    bool reasoningStarted = false;
                    bool reasoningMessageStarted = false;

                    // ═══════════════════════════════════════════════════════
                    // Execute LLM call (unless skipped by Middleware)
                    // ═══════════════════════════════════════════════════════

                    if (MiddlewareContext.SkipLLMCall)
                    {
                        // Use cached/provided response from Middleware
                        if (MiddlewareContext.Response != null)
                        {
                            assistantContents.AddRange(MiddlewareContext.Response.Contents);
                        }
                        toolRequests.AddRange(MiddlewareContext.ToolCalls);
                    }
                    else
                    {
                        // Emit iteration messages event
                        yield return new InternalIterationMessagesEvent(
                            _name,
                            state.Iteration,
                            messagesToSend.Count(),
                            DateTimeOffset.UtcNow);

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
                                        yield return new InternalReasoningEvent(
                                            Phase: ReasoningPhase.SessionStart,
                                            MessageId: assistantMessageId);
                                        reasoningStarted = true;
                                    }

                                    if (!reasoningMessageStarted)
                                    {
                                        yield return new InternalReasoningEvent(
                                            Phase: ReasoningPhase.MessageStart,
                                            MessageId: assistantMessageId,
                                            Role: "assistant");
                                        reasoningMessageStarted = true;
                                    }

                                    yield return new InternalReasoningEvent(
                                        Phase: ReasoningPhase.Delta,
                                        MessageId: assistantMessageId,
                                        Text: reasoning.Text);
                                    assistantContents.Add(reasoning);
                                }
                                else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                                {
                                    if (reasoningMessageStarted)
                                    {
                                        yield return new InternalReasoningEvent(
                                            Phase: ReasoningPhase.MessageEnd,
                                            MessageId: assistantMessageId);
                                        reasoningMessageStarted = false;
                                    }
                                    if (reasoningStarted)
                                    {
                                        yield return new InternalReasoningEvent(
                                            Phase: ReasoningPhase.SessionEnd,
                                            MessageId: assistantMessageId);
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
                                             HPDJsonContext.Default.DictionaryStringObject);

                                        yield return new InternalToolCallArgsEvent(functionCall.CallId, argsJson);
                                    }

                                    toolRequests.Add(functionCall);
                                    assistantContents.Add(functionCall);
                                }
                            }
                        }

                        // Periodically yield Middleware events during LLM streaming
                        while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                        {
                            yield return MiddlewareEvt;
                        }

                        // Check for stream completion
                        if (update.FinishReason != null)
                        {
                            if (reasoningMessageStarted)
                            {
                                yield return new InternalReasoningEvent(
                                    Phase: ReasoningPhase.MessageEnd,
                                    MessageId: assistantMessageId);
                                reasoningMessageStarted = false;
                            }
                            if (reasoningStarted)
                            {
                                yield return new InternalReasoningEvent(
                                    Phase: ReasoningPhase.SessionEnd,
                                    MessageId: assistantMessageId);
                                reasoningStarted = false;
                            }
                        }
                    }

                        // Capture ConversationId from the agent turn response and update thread
                        if (_agentTurn.LastResponseConversationId != null)
                        {
                            if (thread != null)
                            {
                                thread.ConversationId = _agentTurn.LastResponseConversationId;
                            }
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
                    } // End of else block (LLM call not skipped)

                    // ═══════════════════════════════════════════════════════
                    // ✅ NEW: Populate context with results
                    // ═══════════════════════════════════════════════════════

                    MiddlewareContext.Response = new ChatMessage(
                        ChatRole.Assistant, assistantContents);
                    MiddlewareContext.ToolCalls = toolRequests.AsReadOnly();
                    MiddlewareContext.Exception = null;

                    // ═══════════════════════════════════════════════════════
                    // ✅ NEW: Execute AFTER iteration Middlewares
                    // ═══════════════════════════════════════════════════════

                    foreach (var Middleware in _IterationMiddleWares)
                    {
                        await Middleware.AfterIterationAsync(
                            MiddlewareContext,
                            effectiveCancellationToken).ConfigureAwait(false);
                    }

                    // ═══════════════════════════════════════════════════════
                    // ✅ NEW: Process Middleware signals
                    // ═══════════════════════════════════════════════════════

                    ProcessIterationMiddleWareSignals(MiddlewareContext, ref state);

                    // If there are tool requests, execute them immediately
                    if (toolRequests.Count > 0)
                    {
                        // Create assistant message with tool calls
                        var assistantMessage = new ChatMessage(ChatRole.Assistant, assistantContents);

                        var currentMessages = state.CurrentMessages.ToList();
                        currentMessages.Add(assistantMessage);

                        // ✅ FIXED: Update state immediately after modifying messages
                        state = state.WithMessages(currentMessages);

                        // ✅ FIX: Use messageCountToSend (actual messages sent to server)
                        // NOT state.CurrentMessages.Count (which may be unreduced full history)
                        // This ensures delta sending works correctly with history reduction
                        if (_agentTurn.LastResponseConversationId != null)
                        {
                            state = state.EnableHistoryTracking(messageCountToSend);
                        }

                        // Create assistant message for history
                        // By default, exclude reasoning content to save tokens (configurable via PreserveReasoningInHistory)
                        var historyContents = Config?.PreserveReasoningInHistory == true
                            ? assistantContents.ToList()
                            : assistantContents.Where(c => c is not TextReasoningContent).ToList();

                        // ✅ FIX: Add to history if there's ANY content (text OR tool calls)
                        // Previous code checked hasNonEmptyText which excluded tool-only messages
                        // This caused assistant messages with ONLY tool calls to be lost from history
                        if (historyContents.Count > 0)
                        {
                            var historyMessage = new ChatMessage(ChatRole.Assistant, historyContents);
                            turnHistory.Add(historyMessage);
                        }

                        // ═══════════════════════════════════════════════════════
                        // TOOL EXECUTION (Inline - NOT via decision engine)
                        // ═══════════════════════════════════════════════════════

                        // Apply plugin scoping if enabled
                        var effectiveOptionsForTools = effectiveOptions;
                        if (Config?.Scoping?.Enabled == true && effectiveOptions?.Tools != null && effectiveOptions.Tools.Count > 0)
                        {
                            effectiveOptionsForTools = ApplyPluginScoping(effectiveOptions, state.expandedScopedPluginContainers, state.ExpandedSkillContainers);
                        }

                        // Yield Middleware events before tool execution
                        while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                        {
                            yield return MiddlewareEvt;
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
                        var executeTask = _functionCallProcessor.ExecuteToolsAsync(
                            currentMessages,
                            toolRequests,
                            effectiveOptionsForTools,
                            state,
                            effectiveCancellationToken);

                        // Poll for Middleware events while tool execution is in progress
                        while (!executeTask.IsCompleted)
                        {
                            var delayTask = Task.Delay(10, effectiveCancellationToken);
                            await Task.WhenAny(executeTask, delayTask).ConfigureAwait(false);

                            while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                            {
                                yield return MiddlewareEvt;
                            }
                        }

                        var executionResult = await executeTask.ConfigureAwait(false);

                        // Extract structured results from ToolExecutionResult
                        var toolResultMessage = executionResult.Message;
                        var pluginExpansions = executionResult.PluginExpansions;
                        var skillExpansions = executionResult.SkillExpansions;
                        var skillInstructions = executionResult.SkillInstructions;
                        var successfulFunctions = executionResult.SuccessfulFunctions;

                        // Final drain
                        while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                        {
                            yield return MiddlewareEvt;
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

                        // Accumulate skill instructions in state (for prompt Middleware)
                        foreach (var (skillName, instructions) in skillInstructions)
                        {
                            state = state with
                            {
                                ActiveSkillInstructions = state.ActiveSkillInstructions.SetItem(skillName, instructions)
                            };
                        }

                        // ═══════════════════════════════════════════════════════
                        // UPDATE STATE WITH COMPLETED FUNCTIONS  
                        // ═══════════════════════════════════════════════════════
                        foreach (var functionName in successfulFunctions)
                        {
                            state = state.CompleteFunction(functionName);
                        }



                        // ═══════════════════════════════════════════════════════
                        // Middleware CONTAINER EXPANSIONS FOR PERSISTENCE
                        // Container expansion results are temporary (turn-scoped only)
                        // They should NOT be saved to persistent history or thread storage
                        // BUT they MUST be visible to the LLM within the current turn
                        // ═══════════════════════════════════════════════════════
                        var nonContainerResults = MiddlewareContainerResults(
                            toolResultMessage.Contents,
                            toolRequests,
                            effectiveOptionsForTools);

                        // ✅ ALWAYS add unMiddlewareed results to currentMessages (LLM needs to see container expansions)
                        currentMessages.Add(toolResultMessage);

                        // ✅ Only add Middlewareed results to turnHistory (for persistence - exclude containers)
                        if (nonContainerResults.Count > 0)
                        {
                            var MiddlewareedMessage = new ChatMessage(ChatRole.Tool, nonContainerResults);
                            turnHistory.Add(MiddlewareedMessage);
                        }

                        // Note: turnHistory does NOT include container results
                        // This ensures container expansions don't pollute persistent thread storage

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
                        var finalResponse = ConstructChatResponseFromUpdates(responseUpdates, Config?.PreserveReasoningInHistory ?? false);
                        lastResponse = finalResponse;

                        // ✅ FIX: Add final assistant message to turnHistory BEFORE clearing responseUpdates
                        // This ensures the assistant's response is persisted to the thread
                        if (finalResponse.Messages.Count > 0)
                        {
                            var finalAssistantMessage = finalResponse.Messages[0];
                            if (finalAssistantMessage.Contents.Count > 0)
                            {
                                // Add to state.CurrentMessages
                                var currentMessages = state.CurrentMessages.ToList();
                                currentMessages.Add(finalAssistantMessage);
                                state = state.WithMessages(currentMessages);

                                // Add to turnHistory for thread persistence
                                turnHistory.Add(finalAssistantMessage);
                            }
                        }

                        // ✅ FIXED: Clear responseUpdates AFTER constructing final response
                        responseUpdates.Clear();

                        // ✅ FIX: Update history tracking if we have ConversationId
                        if (_agentTurn.LastResponseConversationId != null)
                        {
                            // For non-tool responses, use messageCountToSend (actual messages sent)
                            // NOT state.CurrentMessages.Count (which may be unreduced full history)
                            state = state.EnableHistoryTracking(messageCountToSend);
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
                else if (decision is AgentDecision.Terminate terminateDecision)
                {
                    state = state.Terminate(terminateDecision.Reason);
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

                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await Config.Checkpointer.DeletePendingWritesAsync(
                                            threadId,
                                            checkpointState.ETag,
                                            CancellationToken.None);

                                        // Emit pending writes deleted event
                                        NotifyObservers(new InternalCheckpointEvent(
                                            Operation: CheckpointOperation.PendingWritesDeleted,
                                            ThreadId: threadId,
                                            Timestamp: DateTimeOffset.UtcNow));
                                    }
                                    catch
                                    {
                                        // Swallow errors - cleanup is best-effort
                                    }
                                });
                            }

                            stopwatch.Stop();

                            // Emit checkpoint success event
                            NotifyObservers(new InternalCheckpointEvent(
                                Operation: CheckpointOperation.Saved,
                                ThreadId: thread.Id,
                                Timestamp: DateTimeOffset.UtcNow,
                                Duration: stopwatch.Elapsed,
                                Iteration: checkpointState.Iteration,
                                Success: true));
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();

                            // Emit checkpoint failure event
                            NotifyObservers(new InternalCheckpointEvent(
                                Operation: CheckpointOperation.Saved,
                                ThreadId: thread.Id,
                                Timestamp: DateTimeOffset.UtcNow,
                                Duration: stopwatch.Elapsed,
                                Iteration: checkpointState.Iteration,
                                Success: false,
                                ErrorMessage: ex.Message));

                            // Checkpoint failures are non-fatal (fire-and-forget)
                            // Agent execution continues even if checkpoint fails
                        }
                    }, CancellationToken.None);
                }
            }

            // ═══════════════════════════════════════════════════════
            // FINALIZATION
            // ═══════════════════════════════════════════════════════
            // NOTE: Do NOT auto-generate termination messages for max iterations.
            // Iteration Middlewares (like ContinuationPermissionIterationMiddleWare) are responsible
            // for emitting bidirectional events (InternalContinuationRequestEvent) that the
            // consumer can handle. The fallback termination message would mask these events,
            // defeating the purpose of bidirectional communication.
            // Let the event flow to the consumer - they decide what to do.

            // Build the complete history including the final assistant message
            // ✅ FIX: Always check for pending responseUpdates, regardless of tool history
            // The previous condition (!hadAnyToolCalls) prevented final messages from being
            // added after tool execution, causing message loss in multi-iteration scenarios.
            if (responseUpdates.Any())
            {
                var finalResponse = ConstructChatResponseFromUpdates(responseUpdates, Config?.PreserveReasoningInHistory ?? false);
                if (finalResponse.Messages.Count > 0)
                {
                    var finalAssistantMessage = finalResponse.Messages[0];

                    if (finalAssistantMessage.Contents.Count > 0)
                    {
                        // ✅ FIX: Add final message to BOTH state and turnHistory
                        // This ensures state consistency for checkpointing
                        var currentMessages = state.CurrentMessages.ToList();
                        currentMessages.Add(finalAssistantMessage);
                        state = state.WithMessages(currentMessages);

                        // Also add to turnHistory for thread persistence
                        turnHistory.Add(finalAssistantMessage);
                    }
                }
            }

            // Final drain of Middleware events after loop
            while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                yield return MiddlewareEvt;

            // Emit MESSAGE TURN finished event
            turnStopwatch.Stop();
            yield return new InternalMessageTurnFinishedEvent(
                messageTurnId,
                conversationId,
                _name,
                turnStopwatch.Elapsed,
                DateTimeOffset.UtcNow);

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

                telemetryActivity?.Dispose();
            }
            catch (Exception)
            {
                // Observability errors shouldn't break execution
            }

            // Emit agent completion event
            yield return new InternalAgentCompletionEvent(
                _name,
                state.Iteration,
                turnStopwatch.Elapsed,
                DateTimeOffset.UtcNow);

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

                // Use Task.Run to avoid try-catch in iterator method
                var checkpointTask = Task.Run(async () =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    bool success = false;
                    string? errorMessage = null;

                    try
                    {
                        thread.ExecutionState = finalState;
                        await Config.Checkpointer.SaveThreadAsync(thread, CancellationToken.None);
                        success = true;

                        // Cleanup pending writes after successful final checkpoint (fire-and-forget)
                        if (Config.EnablePendingWrites && finalState.ETag != null)
                        {
                            var iteration = finalState.Iteration;
                            var threadId = thread.Id;

                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Config.Checkpointer.DeletePendingWritesAsync(
                                        threadId,
                                        finalState.ETag,
                                        CancellationToken.None);

                                    // Emit pending writes deleted event
                                    NotifyObservers(new InternalCheckpointEvent(
                                        Operation: CheckpointOperation.PendingWritesDeleted,
                                        ThreadId: threadId,
                                        Timestamp: DateTimeOffset.UtcNow));
                                }
                                catch
                                {
                                    // Swallow errors - cleanup is best-effort
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        errorMessage = ex.Message;
                        // Checkpoint failures are non-fatal - continue execution
                    }
                    finally
                    {
                        stopwatch.Stop();

                        // Emit checkpoint event
                        NotifyObservers(new InternalCheckpointEvent(
                            Operation: CheckpointOperation.Saved,
                            ThreadId: thread.Id,
                            Timestamp: DateTimeOffset.UtcNow,
                            Duration: stopwatch.Elapsed,
                            Iteration: finalState.Iteration,
                            Success: success,
                            ErrorMessage: errorMessage));
                    }
                });

                // Wait for checkpoint to complete
                await checkpointTask;
            }

            // ═══════════════════════════════════════════════════════
            // PERSISTENCE: Save complete turn history to thread
            // ═══════════════════════════════════════════════════════
            if (thread != null && turnHistory.Count > 0)
            {
                try
                {
                    // Save ALL messages from this turn (user + assistant + tool)
                    // Input messages were added to turnHistory at the start of execution
                    await thread.AddMessagesAsync(turnHistory, effectiveCancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Ignore errors - message persistence is not critical to execution
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
        ImmutableHashSet<string> expandedScopedPluginContainers,
        ImmutableHashSet<string> ExpandedSkillContainers)
    {
        if (options?.Tools == null || Config?.Scoping?.Enabled != true)
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
            expandedScopedPluginContainers,
            ExpandedSkillContainers);

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
            ConversationId = options.ConversationId  // Preserve from input options
        };
    }

    /// <summary>
    /// Middlewares out container expansion results from history.
    /// Container expansions are temporary - only relevant within the current turn.
    /// Persistent history should NOT contain "ExpandPlugin" or "ExpandSkill" results.
    /// </summary>
    /// <param name="contents">All tool result contents</param>
    /// <param name="toolRequests">Original tool call requests</param>
    /// <param name="options">Chat options containing tool metadata</param>
    /// <returns>Middlewareed contents (non-container results only)</returns>
    private static List<AIContent> MiddlewareContainerResults(
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
    /// Checks if a function result is from ANY container expansion (scoped plugin OR skill).
    /// All container activation messages are Middlewareed from persistent history because:
    /// 1. They're only relevant within the current message turn
    /// 2. They prevent history pollution across message turns
    /// 3. Containers re-collapse at the start of each new turn
    ///
    /// Container results are still added to turnHistory (visible within turn) but not currentMessages (persistent).
    /// </summary>
    /// <param name="result">The function result to check</param>
    /// <param name="toolRequests">Original tool call requests</param>
    /// <param name="options">Chat options containing tool metadata</param>
    /// <returns>True if this result is from any container (scoped plugin or skill)</returns>
    private static bool IsContainerResult(
        FunctionResultContent result,
        IList<FunctionCallContent> toolRequests,
        ChatOptions? options)
    {
        return toolRequests.Any(tr =>
        {
            if (tr.CallId != result.CallId)
                return false;

            var function = options?.Tools?.OfType<AIFunction>()
                .FirstOrDefault(t => t.Name == tr.Name);

            if (function?.AdditionalProperties == null)
                return false;

            // Check if it's a container
            var isContainer = function.AdditionalProperties.TryGetValue("IsContainer", out var containerVal) == true
                && containerVal is bool isCont && isCont;

            // Middleware ALL containers (both scoped plugins AND skills) from persistent history
            // Container activation messages are only relevant within the current turn
            // and should not pollute the persistent chat history across message turns
            return isContainer;
        });
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

                // Emit pending writes saved event
                NotifyObservers(new InternalCheckpointEvent(
                    Operation: CheckpointOperation.PendingWritesSaved,
                    ThreadId: threadId,
                    Timestamp: DateTimeOffset.UtcNow,
                    WriteCount: pendingWrites.Count));
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
    private static ChatResponse ConstructChatResponseFromUpdates(List<ChatResponseUpdate> updates, bool preserveReasoning = false)
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
                    // Include TextContent in message
                    // By default, exclude TextReasoningContent to save tokens (configurable via preserveReasoning)
                    else if (content is TextContent && (preserveReasoning || content is not TextReasoningContent))
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
    /// Creates a SummarizingChatReducer with custom configuration.
    /// Supports using a separate, cheaper model for summarization (cost optimization).
    /// </summary>
    private SummarizingChatReducer CreateSummarizingReducer(IChatClient baseClient, HistoryReductionConfig historyConfig, AgentConfig agentConfig)
    {
        // Determine which chat client to use for summarization
        // If a custom summarizer client was provided (via AgentBuilder), use it
        // Otherwise, fall back to the base client
        var summarizerClient = _summarizerClient ?? baseClient;

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

    #region Message Turn Middlewares

    /// <summary>
    /// Applies message turn Middlewares after a complete turn (including all function calls) finishes.
    /// 
    /// IMPORTANT: Message turn Middlewares are READ-ONLY for observation and logging purposes.
    /// Mutations made by Middlewares to the MessageTurnMiddlewareContext are NOT persisted back to conversation history.
    /// 
    /// Use cases for message turn Middlewares:
    /// - Telemetry and logging of completed turns
    /// - Monitoring agent behavior and function call patterns
    /// - Triggering side effects based on conversation events
    /// 
    /// If you need to mutate conversation history, use:
    /// - IPromptMiddleware (pre-turn) to modify messages before LLM execution
    /// - IAIFunctionMiddleware (during-turn) to intercept and modify tool execution
    /// - Conversation APIs directly to append/modify persisted messages
    /// </summary>
    private async Task ApplyMessageTurnMiddlewares(
        ChatMessage userMessage,
        IReadOnlyList<ChatMessage> finalHistory,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        if (!_MessageTurnMiddlewares.Any())
        {
            return;
        }

        // Extract assistant messages from final history as the response
        var assistantMessages = finalHistory.Where(m => m.Role == ChatRole.Assistant).ToList();
        if (!assistantMessages.Any())
        {
            return; // No response to Middleware
        }

        var response = new ChatResponse(assistantMessages);

        // Collect agent function call metadata
        var agentFunctionCalls = ContentExtractor.ExtractFunctionCallsFromHistory(finalHistory, _name);

        // Create Middleware context (convert AdditionalProperties to Dictionary if available)
        Dictionary<string, object>? metadata = null;
        if (options?.AdditionalProperties != null)
        {
            metadata = new Dictionary<string, object>(options.AdditionalProperties!);
        }

        // Extract conversationId from options or generate new one
        var conversationId = options?.ConversationId ?? Guid.NewGuid().ToString();

        var context = new MessageTurnMiddlewareContext(
            conversationId,
            userMessage,
            response,
            agentFunctionCalls,
            metadata,
            options,
            cancellationToken);

        // Build and execute the message turn Middleware pipeline using MiddlewareChain
        Func<MessageTurnMiddlewareContext, Task> finalAction = _ => Task.CompletedTask;
        var pipeline = MiddlewareChain.BuildMessageTurnPipeline(_MessageTurnMiddlewares, finalAction);
        await pipeline(context).ConfigureAwait(false);
    }

    #endregion

    #region History Reduction Metadata

    // Reduction metadata is now properly handled via StreamingTurnResult.ReductionTask.
    // This ensures the metadata is available when the turn completes and PrepareTurnAsync
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
    /// <strong>Thread Safety:</strong> This method is fully thread-safe and supports concurrent execution.
    /// Multiple calls on the same agent instance can execute concurrently without interference.
    /// The agent is stateless; all conversation state is managed externally or in thread parameters.
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
        // Prepare turn (stateless - no thread)
        var inputMessages = messages.ToList();
        var turn = await _messageProcessor.PrepareTurnAsync(
            thread: null,
            inputMessages,
            options,
            Name,
            cancellationToken);

        var turnHistory = new List<ChatMessage>();
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await foreach (var evt in RunAgenticLoopInternal(
            turn,
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
    /// <strong>Thread Safety:</strong> This method is fully thread-safe and supports concurrent execution.
    /// Multiple calls on the same agent instance can execute concurrently without interference.
    /// For multi-turn conversations, pass a ConversationThread to maintain state across runs.
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
        // Prepare turn (stateless - no thread)
        var inputMessages = messages.ToList();
        var turn = await _messageProcessor.PrepareTurnAsync(
            thread: null,
            inputMessages,
            options,
            Name,
            cancellationToken);

        var turnHistory = new List<ChatMessage>();
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var internalStream = RunAgenticLoopInternal(
            turn,
            documentPaths: null,
            turnHistory,
            historyCompletionSource,
            reductionCompletionSource,
            thread: null,
            cancellationToken);

        await foreach (var evt in internalStream.WithCancellation(cancellationToken))
        {
            // Yield event first (real-time protocol stream - zero delay)
            yield return evt;

            // Then notify observers (fire-and-forget, non-blocking)
            NotifyObservers(evt);
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

        // ═══════════════════════════════════════════════════════════════════════════
        // PREPARE TURN: Load history, apply reduction, merge options (Option 2 pattern)
        // ═══════════════════════════════════════════════════════════════════════════
        var inputMessages = messages?.ToList() ?? new List<ChatMessage>();
        var turn = await _messageProcessor.PrepareTurnAsync(
            thread,
            inputMessages,
            options,
            Name,
            cancellationToken);

        // Cache new reduction if created
        if (turn.NewReductionMetadata != null && turn.ActiveReduction != null)
        {
            thread.LastReduction = turn.ActiveReduction;
        }

        var turnHistory = new List<ChatMessage>();
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reductionCompletionSource = new TaskCompletionSource<ReductionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // ═══════════════════════════════════════════════════════════════════════════
        // EXECUTE AGENTIC LOOP with PreparedTurn
        // ═══════════════════════════════════════════════════════════════════════════
        var internalStream = RunAgenticLoopInternal(
            turn,  // ✅ PreparedTurn contains MessagesForLLM, NewInputMessages, and Options
            documentPaths: null,
            turnHistory,
            historyCompletionSource,
            reductionCompletionSource,
            thread: thread,  // ← CRITICAL: Pass thread for persistence and checkpointing
            cancellationToken);

        await foreach (var evt in internalStream.WithCancellation(cancellationToken))
        {
            // Yield event first (real-time protocol stream - zero delay)
            yield return evt;

            // Then notify observers (fire-and-forget, non-blocking)
            NotifyObservers(evt);
        }
    }

    // ═══════════════════════════════════════════════════════
    // ITERATION Middleware SUPPORT
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Processes signals from iteration Middlewares (cleanup requests, etc.).
    /// Called after iteration Middlewares complete.
    /// </summary>
    /// <param name="context">The iteration Middleware context with potential signals</param>
    /// <param name="state">The current agent loop state (may be updated based on signals)</param>
    private void ProcessIterationMiddleWareSignals(
        IterationMiddleWareContext context,
        ref AgentLoopState state)
    {
        // Check for skill cleanup signal
        if (context.IsFinalIteration &&
            context.Properties.TryGetValue("ShouldClearActiveSkills", out var clearSkills) &&
            clearSkills is true)
        {
            // Clear active skill instructions at end of final iteration
            // This ensures skills don't leak across message turns
            state = state with
            {
                ActiveSkillInstructions = ImmutableDictionary<string, string>.Empty
            };
        }

        // Future: Add more signal handlers here as needed
        // Example: context.Properties["ShouldTerminate"] = true;
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

        // Check: Already terminated by external source (e.g., permission Middleware, manual termination)
        if (state.IsTerminated)
            return new AgentDecision.Terminate(state.TerminationReason ?? "Terminated");

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
    public required ImmutableHashSet<string> expandedScopedPluginContainers { get; init; }

    /// <summary>
    /// Skills that have been expanded in this turn.
    /// Used for container expansion pattern (skills version).
    /// </summary>
    public required ImmutableHashSet<string> ExpandedSkillContainers { get; init; }

    /// <summary>
    /// Skill instructions for expanded skills (accumulated during turn).
    /// Maps skill name → full instruction text from container metadata.
    /// Ephemeral - cleared at end of message turn, used to inject into system prompt via Middleware.
    /// Accumulates ALL skills activated within the turn across iterations.
    /// </summary>
    public required ImmutableDictionary<string, string> ActiveSkillInstructions { get; init; }

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
    /// Active history reduction state for this execution (cache-aware).
    /// When set, indicates that history has been reduced and contains reduction metadata.
    /// Used to apply reduction to messages sent to LLM while preserving full history in storage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the NEW first-class reduction tracking. It replaces the old __summary__ marker approach.
    /// Reduction state is external metadata, NOT embedded in messages.
    /// </para>
    /// <para>
    /// <b>Usage:</b>
    /// - Fresh run (iteration 0): Check if thread.LastReduction.IsValidFor() → cache hit
    /// - If valid: Set ActiveReduction and use ApplyToMessages() for LLM calls
    /// - If invalid: Run reduction, create new HistoryReductionState, store in thread.LastReduction
    /// </para>
    /// </remarks>
    public HistoryReductionState? ActiveReduction { get; init; }

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
        expandedScopedPluginContainers = ImmutableHashSet<string>.Empty,
        ExpandedSkillContainers = ImmutableHashSet<string>.Empty,
        ActiveSkillInstructions = ImmutableDictionary<string, string>.Empty,
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
        this with { expandedScopedPluginContainers = expandedScopedPluginContainers.Add(pluginName) };

    /// <summary>
    /// Records that a skill container has been expanded.
    /// </summary>
    public AgentLoopState WithExpandedSkill(string skillName) =>
        this with { ExpandedSkillContainers = ExpandedSkillContainers.Add(skillName) };

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
    /// Sets or updates the active history reduction state.
    /// Call this after creating a new reduction or loading from cache.
    /// </summary>
    /// <param name="reduction">History reduction state to apply</param>
    /// <returns>New state with updated reduction</returns>
    public AgentLoopState WithReduction(HistoryReductionState reduction) =>
        this with { ActiveReduction = reduction };

    /// <summary>
    /// Clears the active history reduction state.
    /// Useful for testing or when reduction is no longer valid.
    /// </summary>
    /// <returns>New state without reduction</returns>
    public AgentLoopState ClearReduction() =>
        this with { ActiveReduction = null };

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
/// High-performance content extraction and Middlewareing utilities.
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
                        HPDJsonContext.Default.DictionaryStringObject));
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
    /// Middlewares out specific content types (e.g., remove reasoning to save tokens).
    /// </summary>
    /// <typeparam name="TExclude">The content type to exclude</typeparam>
    /// <param name="contents">The content list to Middleware</param>
    /// <returns>New list with excluded type removed</returns>
    public static List<AIContent> MiddlewareByType<TExclude>(
        IList<AIContent> contents) where TExclude : AIContent
    {
        var Middlewareed = new List<AIContent>(contents.Count);
        for (int i = 0; i < contents.Count; i++)
        {
            if (contents[i] is not TExclude)
                Middlewareed.Add(contents[i]);
        }
        return Middlewareed;
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
/// Handles all function calling logic, including multi-turn execution and Middleware pipelines.
/// </summary>
internal class FunctionCallProcessor
{
    private readonly AgentCore _agent; // NEW: Reference to agent for Middleware event coordination
    private readonly ScopedFunctionMiddlewareManager? _ScopedFunctionMiddlewareManager;
    private readonly PermissionManager _permissionManager;
    private readonly IReadOnlyList<IAIFunctionMiddleware> _AIFunctionMiddlewares;
    private readonly int _maxFunctionCalls;
    private readonly ErrorHandlingConfig? _errorHandlingConfig;
    private readonly IList<AITool>? _serverConfiguredTools;
    private readonly AgenticLoopConfig? _agenticLoopConfig;

    public FunctionCallProcessor(
        AgentCore agent, // NEW: Added parameter
        ScopedFunctionMiddlewareManager? ScopedFunctionMiddlewareManager,
        PermissionManager permissionManager,
        IReadOnlyList<IAIFunctionMiddleware>? AIFunctionMiddlewares,
        int maxFunctionCalls,
        ErrorHandlingConfig? errorHandlingConfig = null,
        IList<AITool>? serverConfiguredTools = null,
        AgenticLoopConfig? agenticLoopConfig = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _ScopedFunctionMiddlewareManager = ScopedFunctionMiddlewareManager;
        _permissionManager = permissionManager ?? throw new ArgumentNullException(nameof(permissionManager));
        _AIFunctionMiddlewares = AIFunctionMiddlewares ?? new List<IAIFunctionMiddleware>();
        _maxFunctionCalls = maxFunctionCalls;
        _errorHandlingConfig = errorHandlingConfig;
        _serverConfiguredTools = serverConfiguredTools;
        _agenticLoopConfig = agenticLoopConfig;
    }


    /// <summary>
    /// Executes function calls with automatic routing between sequential/parallel execution.
    /// Handles container detection, permission checking, and result aggregation.
    /// This is the new consolidated API that replaces ToolScheduler.
    /// </summary>
    /// <param name="currentHistory">Current conversation messages</param>
    /// <param name="toolRequests">Function calls to execute</param>
    /// <param name="options">Chat options containing tool definitions</param>
    /// <param name="agentLoopState">Current agent loop state</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Structured result with message, expansions, and successful functions</returns>
    public async Task<ToolExecutionResult> ExecuteToolsAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentLoopState agentLoopState,
        CancellationToken cancellationToken)
    {
        // PHASE 0: Container detection (ONCE, not duplicated)
        var containerInfo = DetectContainers(toolRequests, options);

        // PHASE 1: Route to appropriate execution strategy
        // For single tool calls, inline execution (no parallel overhead)
        if (toolRequests.Count <= 1)
        {
            return await ExecuteSequentiallyAsync(
                currentHistory, toolRequests, options, agentLoopState,
                containerInfo, cancellationToken).ConfigureAwait(false);
        }

        // For multiple tools, use parallel execution with throttling
        return await ExecuteInParallelAsync(
            currentHistory, toolRequests, options, agentLoopState,
            containerInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes tools sequentially (used for single tools).
    /// No permission duplication - checks once per tool.
    /// </summary>
    private async Task<ToolExecutionResult> ExecuteSequentiallyAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentLoopState agentLoopState,
        ContainerDetectionInfo containerInfo,
        CancellationToken cancellationToken)
    {
        var allContents = new List<AIContent>();
        var pluginExpansions = new HashSet<string>();
        var skillExpansions = new HashSet<string>();

        // Process ALL tools (containers + regular) through the existing processor
        var resultMessages = await ProcessFunctionCallsAsync(
            currentHistory, options, toolRequests, agentLoopState, cancellationToken).ConfigureAwait(false);

        // Combine results
        foreach (var message in resultMessages)
        {
            foreach (var content in message.Contents)
            {
                allContents.Add(content);
            }
        }

        // Track container expansions from results
        foreach (var content in allContents)
        {
            if (content is FunctionResultContent functionResult)
            {
                if (containerInfo.PluginContainers.TryGetValue(functionResult.CallId, out var pluginName))
                {
                    pluginExpansions.Add(pluginName);
                }
                else if (containerInfo.SkillContainers.TryGetValue(functionResult.CallId, out var skillName))
                {
                    skillExpansions.Add(skillName);
                }
            }
        }

        // Extract successful functions
        var successfulFunctions = ExtractSuccessfulFunctions(allContents, toolRequests);

        return new ToolExecutionResult(
            new ChatMessage(ChatRole.Tool, allContents),
            pluginExpansions,
            skillExpansions,
            containerInfo.SkillInstructions,
            successfulFunctions);
    }

    /// <summary>
    /// Executes tools in parallel with throttling and batch permission checking.
    /// Eliminates redundant per-tool permission checks.
    /// </summary>
    private async Task<ToolExecutionResult> ExecuteInParallelAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentLoopState agentLoopState,
        ContainerDetectionInfo containerInfo,
        CancellationToken cancellationToken)
    {
        // PHASE 1: Batch permission check (ONCE, not duplicated per-tool)
        var permissionResult = await _permissionManager.CheckPermissionsAsync(
            toolRequests, options, agentLoopState, _agent, cancellationToken).ConfigureAwait(false);

        var approvedTools = permissionResult.Approved;
        var deniedTools = permissionResult.Denied;

        // PHASE 2: Parallel execution with semaphore throttling
        var maxParallel = _agenticLoopConfig?.MaxParallelFunctions ?? Environment.ProcessorCount * 4;
        using var semaphore = new SemaphoreSlim(maxParallel);

        var executionTasks = approvedTools.Select(async toolRequest =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Execute each approved tool call through the processor
                // NO permission check here - already done in batch above
                var singleToolList = new List<FunctionCallContent> { toolRequest };
                var resultMessages = await ProcessFunctionCallsAsync(
                    currentHistory, options, singleToolList, agentLoopState, cancellationToken).ConfigureAwait(false);

                return (Success: true, Messages: resultMessages, Error: (Exception?)null, ToolRequest: toolRequest);
            }
            catch (Exception ex)
            {
                return (Success: false, Messages: new List<ChatMessage>(), Error: ex, ToolRequest: toolRequest);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        // Wait for all approved tasks to complete
        var results = await Task.WhenAll(executionTasks).ConfigureAwait(false);

        // Aggregate results
        var allContents = new List<AIContent>();
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
                        if (containerInfo.PluginContainers.TryGetValue(functionResult.CallId, out var pluginName))
                        {
                            pluginExpansions.Add(pluginName);
                        }
                        else if (containerInfo.SkillContainers.TryGetValue(functionResult.CallId, out var skillName))
                        {
                            skillExpansions.Add(skillName);
                        }
                    }
                }
            }
            else if (result.Error != null)
            {
                // Include error in results
                var errorContent = new TextContent($"⚠️ Error executing tool: {result.Error.Message}");
                allContents.Add(errorContent);
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

        // Extract successful functions
        var successfulFunctions = ExtractSuccessfulFunctions(allContents, approvedTools);

        return new ToolExecutionResult(
            new ChatMessage(ChatRole.Tool, allContents),
            pluginExpansions,
            skillExpansions,
            containerInfo.SkillInstructions,
            successfulFunctions);
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
                // Find the tool name from the original request
                foreach (var toolRequest in toolRequests)
                {
                    if (toolRequest.CallId == frc.CallId && !string.IsNullOrEmpty(toolRequest.Name))
                    {
                        successful.Add(toolRequest.Name);
                        break;
                    }
                }
            }
        }

        return successful;
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
    /// Mirrors the error detection logic used in AgentCore.
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

        // Process each function call through the Middleware pipeline
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
                OutboundEvents = _agent.MiddlewareEventWriter,
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
                    // Generate a more descriptive error message if this is a scoped function
                    ctx.Result = GenerateFunctionNotFoundMessage(
                        ctx.FunctionName,
                        ctx.State?.expandedScopedPluginContainers ?? ImmutableHashSet<string>.Empty,
                        ctx.State?.ExpandedSkillContainers ?? ImmutableHashSet<string>.Empty);
                    return;
                }

                await ExecuteWithRetryAsync(ctx, cancellationToken).ConfigureAwait(false);
            };

            // Get scoped Middlewares for this function
            // Extract plugin name from function metadata if available
            string? pluginTypeName = null;
            if (context.Function?.AdditionalProperties?.TryGetValue("ParentPlugin", out var parentPlugin) == true)
            {
                pluginTypeName = parentPlugin as string;
            }
            else if (context.Function?.AdditionalProperties?.TryGetValue("PluginName", out var pluginName) == true)
            {
                // For container functions, PluginName IS the plugin type
                pluginTypeName = pluginName as string;
            }

            // Extract skill metadata
            string? skillName = null;
            bool isSkillContainer = false;

            // Check if this function IS a skill container
            if (context.Function?.AdditionalProperties?.TryGetValue("IsSkill", out var isSkillValue) == true
                && isSkillValue is bool isS && isS)
            {
                isSkillContainer = true;
                // Note: When invoking a skill container, skillName remains null
                // The container IS the skill, it doesn't have a "parent skill"
            }

            // For regular functions, skillName will be resolved via fallback mapping
            // in GetApplicableMiddlewares() using _functionToSkillMap

            var scopedMiddlewares = _ScopedFunctionMiddlewareManager?.GetApplicableMiddlewares(
                functionCall.Name,
                pluginTypeName,
                skillName,
                isSkillContainer)
                                ?? Enumerable.Empty<IAIFunctionMiddleware>();

            // Combine scoped Middlewares with general AI function Middlewares
            var allStandardMiddlewares = _AIFunctionMiddlewares.Concat(scopedMiddlewares);

            // ✅ PHASE 2: Track AIFunction Middleware pipeline execution
            var Middlewarestopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Build and execute the Middleware pipeline using MiddlewareChain
            var pipeline = MiddlewareChain.BuildAiFunctionPipeline(allStandardMiddlewares, finalInvoke);

            // Execute pipeline SYNCHRONOUSLY (no Task.Run!)
            // Events flow directly to shared channel, drained by background task
            try
            {
                await pipeline(context).ConfigureAwait(false);

                Middlewarestopwatch.Stop();
            }
            catch (Exception ex)
            {
                Middlewarestopwatch.Stop();

                // Emit error event before handling
                context.OutboundEvents?.TryWrite(new InternalMiddlewareErrorEvent(
                    "MiddlewarePipeline",
                    $"Error in Middleware pipeline: {ex.Message}",
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
        AgentCore.CurrentFunctionContext = context;

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
            AgentCore.CurrentFunctionContext = null;
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
    /// Generates a descriptive error message when a function is not found.
    /// If the function belongs to a scoped plugin or skill that hasn't been expanded, provides guidance to call the container first.
    /// Handles the case where a function can belong to BOTH a plugin container AND a skill container.
    /// </summary>
    private string GenerateFunctionNotFoundMessage(
        string functionName,
        ImmutableHashSet<string> expandedPlugins,
        ImmutableHashSet<string> expandedSkills)
    {
        // Check if this function belongs to a scoped plugin by searching all registered tools
        if (_serverConfiguredTools != null)
        {
            foreach (var tool in _serverConfiguredTools)
            {
                if (tool is AIFunction func &&
                    string.Equals(func.Name, functionName, StringComparison.OrdinalIgnoreCase))
                {
                    // Found the function in registered tools
                    // A function can belong to BOTH a plugin container AND a skill container
                    var unexpandedContainers = new List<string>();

                    // Check if it belongs to a scoped plugin
                    if (func.AdditionalProperties?.TryGetValue("ParentPlugin", out var parentPluginObj) == true &&
                        parentPluginObj is string parentPlugin &&
                        !string.IsNullOrEmpty(parentPlugin))
                    {
                        // Check if this plugin has already been expanded
                        if (!expandedPlugins.Contains(parentPlugin))
                        {
                            unexpandedContainers.Add(parentPlugin);
                        }
                    }

                    // Check if it belongs to a skill container (ParentSkillContainer)
                    if (func.AdditionalProperties?.TryGetValue("ParentSkillContainer", out var skillContainerObj) == true &&
                        skillContainerObj is string skillContainer &&
                        !string.IsNullOrEmpty(skillContainer))
                    {
                        // Check if this skill container has already been expanded
                        if (!expandedSkills.Contains(skillContainer))
                        {
                            unexpandedContainers.Add(skillContainer);
                        }
                    }

                    // Generate appropriate error message based on what containers exist
                    if (unexpandedContainers.Count > 0)
                    {
                        if (unexpandedContainers.Count == 1)
                        {
                            return $"Function '{functionName}' is not currently available. It belongs to the '{unexpandedContainers[0]}' container. Call {unexpandedContainers[0]}() first to unlock this function.";
                        }
                        else
                        {
                            // Multiple containers - list them all
                            var containerList = string.Join(" or ", unexpandedContainers.Select(c => $"{c}()"));
                            return $"Function '{functionName}' is not currently available. It can be unlocked by calling one of these containers: {containerList}.";
                        }
                    }
                }
            }
        }

        // Default error message - function truly doesn't exist
        return $"Function '{functionName}' not found.";
    }

    /// <summary>
    /// Detects plugin and skill containers in tool requests (consolidates duplication).
    /// Extracts container metadata to eliminate duplicate detection logic between sequential/parallel paths.
    /// </summary>
    /// <param name="toolRequests">The tool call requests to analyze</param>
    /// <param name="options">Chat options containing tool definitions</param>
    /// <returns>Container detection info with plugin/skill metadata</returns>
    private ContainerDetectionInfo DetectContainers(
        List<FunctionCallContent> toolRequests,
        ChatOptions? options)
    {
        var pluginContainers = new Dictionary<string, string>(); // callId -> pluginName
        var skillContainers = new Dictionary<string, string>(); // callId -> skillName
        var skillInstructions = new Dictionary<string, string>(); // skillName -> instructions

        foreach (var toolRequest in toolRequests)
        {
            // Find the function in the options to check if it's a container
            var function = FunctionMapBuilder.FindFunctionInList(toolRequest.Name, options?.Tools);

            if (function == null) continue;

            // Check if it's a container (plugin or skill)
            if (function.AdditionalProperties?.TryGetValue("IsContainer", out var isCont) == true
                && isCont is bool isC && isC)
            {
                // Check if it's a skill container (has both IsContainer=true AND IsSkill=true)
                var isSkill = function.AdditionalProperties?.TryGetValue("IsSkill", out var isSkillValue) == true
                    && isSkillValue is bool isS && isS;

                if (isSkill)
                {
                    // Skill container
                    var skillName = function.Name ?? toolRequest.Name;
                    skillContainers[toolRequest.CallId] = skillName;

                    // Extract instructions from metadata for prompt Middleware
                    if (function.AdditionalProperties?.TryGetValue("Instructions", out var instructionsObj) == true
                        && instructionsObj is string instructions
                        && !string.IsNullOrWhiteSpace(instructions))
                    {
                        skillInstructions[skillName] = instructions;
                    }
                }
                else
                {
                    // Plugin container
                    var pluginName = function.AdditionalProperties
                        ?.TryGetValue("PluginName", out var value) == true && value is string pn
                        ? pn
                        : toolRequest.Name;

                    pluginContainers[toolRequest.CallId] = pluginName;
                }
            }
        }

        return new ContainerDetectionInfo(pluginContainers, skillContainers, skillInstructions);
    }

    /// <summary>
    /// Prepares the various chat message lists after a response from the inner client and before invoking functions
    /// </summary>
}

#endregion

#region PreparedTurn

/// <summary>
/// Encapsulates all prepared state for a single agent turn.
/// Separates message preparation (functional, pure) from execution (I/O, stateful).
/// </summary>
/// <remarks>
/// <para>
/// <b>Design Philosophy:</b> "Functional Core, Imperative Shell"
/// - <b>Preparation</b> (MessageProcessor.PrepareTurnAsync): Load history, apply reduction, merge options, Middleware messages → PreparedTurn
/// - <b>Execution</b> (RunAgenticLoopInternal): LLM calls, tool invocations, checkpointing, persistence
/// </para>
/// <para>
/// <b>Why This Design?</b>
/// <list type="bullet">
/// <item>Clean separation: Preparation logic isolated from execution logic</item>
/// <item>Type safety: Strongly typed properties vs scattered variables</item>
/// <item>Testability: PreparedTurn can be constructed and tested without I/O</item>
/// <item>Cache awareness: ActiveReduction enables reduction cache hits</item>
/// <item>Microsoft alignment: Similar to ChatClientAgent.PrepareThreadAndMessagesAsync pattern</item>
/// </list>
/// </para>
/// <para>
/// <b>Usage Pattern:</b>
/// <code>
/// // Step 1: Prepare turn (functional - loads history, applies reduction, etc.)
/// var turn = await _messageProcessor.PrepareTurnAsync(thread, inputMessages, options, ct);
///
/// // Step 2: Initialize state
/// var state = AgentLoopState.Initial(turn.MessagesForLLM, runId, conversationId, agentName);
/// if (turn.ActiveReduction != null)
///     state = state.WithReduction(turn.ActiveReduction);
///
/// // Step 3: Execute agentic loop (imperative - LLM calls, tool execution, persistence)
/// await foreach (var evt in RunAgenticLoopInternal(turn, state, ...))
/// {
///     yield return evt;
/// }
/// </code>
/// </para>
/// </remarks>
internal record PreparedTurn
{
    /// <summary>
    /// Messages to send to the LLM (includes thread history + new input, optionally reduced).
    /// This is the "effective" message list after all preparation steps.
    /// </summary>
    public required IReadOnlyList<ChatMessage> MessagesForLLM { get; init; }

    /// <summary>
    /// NEW input messages only (what the caller provided).
    /// Used for persistence - these are the messages to add to thread history.
    /// </summary>
    public required IReadOnlyList<ChatMessage> NewInputMessages { get; init; }

    /// <summary>
    /// Final chat options after merging defaults, applying Middlewares, and adding system instructions.
    /// </summary>
    public ChatOptions? Options { get; init; }

    /// <summary>
    /// Active history reduction state (if reduction was applied).
    /// Null if no reduction occurred or history reduction is disabled.
    /// </summary>
    public HistoryReductionState? ActiveReduction { get; init; }

    /// <summary>
    /// Reduction metadata (if a NEW reduction was performed during preparation).
    /// Null if reduction was cached or not performed.
    /// </summary>
    /// <remarks>
    /// This differs from ActiveReduction:
    /// - <b>ReductionMetadata</b>: NEW reduction performed during PrepareTurnAsync
    /// - <b>ActiveReduction</b>: Reduction being used (may be cached from previous turn)
    /// </remarks>
    public ReductionMetadata? NewReductionMetadata { get; init; }
}

#endregion

#region MessageProcessor

/// <summary>
/// Handles all pre-processing of chat messages and options before sending to the LLM.
/// </summary>
internal class MessageProcessor
{
    private readonly IReadOnlyList<IPromptMiddleware> _PromptMiddlewares;
    private readonly string? _systemInstructions;
    private readonly ChatOptions? _defaultOptions;
    private readonly IChatReducer? _chatReducer;
    private readonly HistoryReductionConfig? _reductionConfig;

    public MessageProcessor(
        string? systemInstructions,
        ChatOptions? defaultOptions,
        IReadOnlyList<IPromptMiddleware> PromptMiddlewares,
        IChatReducer? chatReducer,
        HistoryReductionConfig? reductionConfig)
    {
        _systemInstructions = systemInstructions;
        _defaultOptions = defaultOptions;
        _PromptMiddlewares = PromptMiddlewares ?? new List<IPromptMiddleware>();
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
    /// Prepares a complete turn for execution.
    /// Loads thread history, merges options, adds system instructions, applies reduction (with caching), and Middlewares messages.
    /// </summary>
    /// <param name="thread">Conversation thread (null for stateless execution).</param>
    /// <param name="inputMessages">NEW messages from the caller (to be added to history).</param>
    /// <param name="options">Chat options to merge with defaults.</param>
    /// <param name="agentName">Agent name for logging/Middlewareing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="expandedSkills">Optional set of expanded skill names for SkillInstructionPromptMiddleware.</param>
    /// <param name="skillInstructions">Optional dictionary of skill-specific instructions for SkillInstructionPromptMiddleware.</param>
    /// <returns>PreparedTurn with all state needed for execution.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is the SINGLE ENTRY POINT for all turn preparation</b>.
    /// All message preparation logic is consolidated here - no more layering through PrepareMessagesAsync.
    /// </para>
    /// <para>
    /// <b>Steps:</b>
    /// <list type="number">
    /// <item>Load thread history (if thread provided)</item>
    /// <item>Add new input messages to history</item>
    /// <item>Merge ChatOptions and inject system instructions</item>
    /// <item>Check reduction cache (thread.LastReduction.IsValidFor)</item>
    /// <item>Apply cached reduction OR perform new reduction via IChatReducer</item>
    /// <item>Apply prompt Middlewares (can modify messages, options, instructions)</item>
    /// <item>Return PreparedTurn with all prepared state</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Cache Optimization:</b>
    /// If thread.LastReduction exists and IsValidFor(currentMessageCount), reuses reduction without LLM call (90% cost savings).
    /// </para>
    /// </remarks>
    public async Task<PreparedTurn> PrepareTurnAsync(
        ConversationThread? thread,
        IEnumerable<ChatMessage> inputMessages,
        ChatOptions? options,
        string agentName,
        CancellationToken cancellationToken)
    {
        var inputMessagesList = inputMessages.ToList();
        var messagesForLLM = new List<ChatMessage>();

        // STEP 1: Load thread history
        if (thread != null)
        {
            var threadMessages = await thread.GetMessagesAsync(cancellationToken);
            messagesForLLM.AddRange(threadMessages);
        }

        // STEP 2: Add new input messages
        messagesForLLM.AddRange(inputMessagesList);

        // STEP 3: Merge options and add system instructions
        var effectiveOptions = MergeOptions(options);

        // Add system instructions to ChatOptions.Instructions (Microsoft's pattern)
        // This follows the official Microsoft.Extensions.AI pattern used by ChatClientAgent
        if (!string.IsNullOrEmpty(_systemInstructions))
        {
            effectiveOptions ??= new ChatOptions();

            // Avoid duplicate injection if system instructions already present
            if (string.IsNullOrWhiteSpace(effectiveOptions.Instructions))
            {
                effectiveOptions.Instructions = _systemInstructions;
            }
            else if (!effectiveOptions.Instructions.Contains(_systemInstructions))
            {
                effectiveOptions.Instructions = $"{_systemInstructions}\n{effectiveOptions.Instructions}";
            }
        }

        // STEP 4: Check reduction cache (cache-aware reduction)
        HistoryReductionState? activeReduction = null;
        ReductionMetadata? newReductionMetadata = null;
        bool usedCachedReduction = false;

        if (_reductionConfig?.Enabled == true && thread?.LastReduction != null)
        {
            if (thread.LastReduction.IsValidFor(messagesForLLM.Count))
            {
                // ✅ CACHE HIT: Reuse existing reduction
                activeReduction = thread.LastReduction;
                usedCachedReduction = true;

                // Apply cached reduction to messages
                messagesForLLM = activeReduction.ApplyToMessages(
                    messagesForLLM.Where(m => m.Role != ChatRole.System),
                    systemMessage: null).ToList(); // System instructions handled via ChatOptions.Instructions
            }
        }

        // STEP 5: Apply new reduction if cache miss and reducer is configured
        if (!usedCachedReduction && _chatReducer != null)
        {
            bool shouldReduce = ShouldTriggerReduction(messagesForLLM);

            if (shouldReduce)
            {
                var reduced = await _chatReducer.ReduceAsync(messagesForLLM, cancellationToken).ConfigureAwait(false);

                if (reduced != null)
                {
                    var reducedList = reduced.ToList();

                    // Extract summary message by position (not marker - Microsoft's reducer doesn't add markers to output)
                    // Microsoft.Extensions.AI.SummarizingChatReducer returns: [System?] [Summary?] [Unsummarized messages...]
                    // The summary is always the first Assistant message after any System messages
                    var summaryMsg = reducedList
                        .SkipWhile(m => m.Role == ChatRole.System)
                        .FirstOrDefault(m => m.Role == ChatRole.Assistant);

                    if (summaryMsg != null)
                    {
                        // Calculate how many messages were removed
                        int removedCount = messagesForLLM.Count - reducedList.Count + 1; // +1 for summary itself

                        // Create reduction metadata
                        newReductionMetadata = new ReductionMetadata
                        {
                            SummaryText = summaryMsg.Text,
                            MessagesRemovedCount = removedCount
                        };

                        // Create HistoryReductionState from metadata
                        var summarizedUpToIndex = removedCount;

                        activeReduction = HistoryReductionState.Create(
                            messagesForLLM,
                            summaryMsg.Text,
                            summarizedUpToIndex,
                            _reductionConfig?.TargetMessageCount ?? 20,
                            _reductionConfig?.SummarizationThreshold ?? 5);
                    }

                    messagesForLLM = reducedList;
                }
            }
        }

        // STEP 6: Apply prompt Middlewares
        var preparedMessages = await ApplyPromptMiddlewaresAsync(
            messagesForLLM,
            effectiveOptions,
            agentName,
            cancellationToken).ConfigureAwait(false);

        // STEP 7: Return PreparedTurn
        return new PreparedTurn
        {
            MessagesForLLM = preparedMessages.ToList(),
            NewInputMessages = inputMessagesList,
            Options = effectiveOptions,
            ActiveReduction = activeReduction,
            NewReductionMetadata = newReductionMetadata
        };
    }


    /// <summary>
    /// Determines if history reduction should be triggered based on configured thresholds.
    /// Implements priority system: Percentage > Absolute Tokens > Message Count.
    /// Cache awareness is handled upstream by HistoryReductionState.IsValidFor().
    /// </summary>
    private bool ShouldTriggerReduction(List<ChatMessage> messagesList)
    {
        if (_reductionConfig == null) return false;

        // PRIORITY 1: Percentage-based (when configured)
        if (_reductionConfig.TokenBudgetTriggerPercentage.HasValue && _reductionConfig.ContextWindowSize.HasValue)
        {
            return ShouldReduceByPercentage(messagesList);
        }

        // PRIORITY 2: Absolute token budget (when configured)
        if (_reductionConfig.MaxTokenBudget.HasValue)
        {
            return ShouldReduceByTokens(messagesList);
        }

        // PRIORITY 3: Message-based (default)
        return ShouldReduceByMessages(messagesList);
    }

    /// <summary>
    /// Checks if reduction should be triggered based on percentage of context window.
    /// TODO: NOT IMPLEMENTED - Token tracking requires Token Flow Architecture Map.
    /// See docs/NEED_FOR_TOKEN_FLOW_ARCHITECTURE_MAP.md for details.
    /// Falls back to message-count reduction (Priority 3).
    /// </summary>
    private bool ShouldReduceByPercentage(List<ChatMessage> messagesList)
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
    private bool ShouldReduceByTokens(List<ChatMessage> messagesList)
    {
        // TODO: Token tracking not implemented - requires architecture map
        // This would track ephemeral context (system prompts, RAG docs, memory)
        // and persistent context (user/assistant/tool messages) separately
        return false;
    }

    /// <summary>
    /// Checks if reduction should be triggered based on message count.
    /// Simple total count check - cache awareness is handled upstream by HistoryReductionState.IsValidFor().
    /// </summary>
    private bool ShouldReduceByMessages(List<ChatMessage> messagesList)
    {
        var targetCount = _reductionConfig!.TargetMessageCount;
        var threshold = _reductionConfig.SummarizationThreshold ?? 5;

        // Simple total count check (cache awareness handled by IsValidFor upstream)
        return messagesList.Count > (targetCount + threshold);
    }

    // ✅ REMOVED: PrependSystemInstructions method
    // System instructions are now added via ChatOptions.Instructions (Microsoft's pattern)
    // This eliminates the need to create ChatMessage(Role.System) objects

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
            // ✅ NEW: Merge Instructions property (follows Microsoft's ChatClientAgent pattern)
            Instructions = providedOptions.Instructions ?? _defaultOptions.Instructions,
            AdditionalProperties = MergeDictionaries(_defaultOptions.AdditionalProperties, providedOptions.AdditionalProperties)
        };
    }

    /// <summary>
    /// Applies the registered prompt Middlewares pipeline.
    /// </summary>
    private async Task<IEnumerable<ChatMessage>> ApplyPromptMiddlewaresAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string agentName,
        CancellationToken cancellationToken)
    {
        if (!_PromptMiddlewares.Any())
        {
            return messages;
        }

        // Create Middleware context
        var context = new PromptMiddlewareContext(messages, options, agentName, cancellationToken);

        // Transfer additional properties to Middleware context
        if (options?.AdditionalProperties != null)
        {
            foreach (var kvp in options.AdditionalProperties)
            {
                context.Properties[kvp.Key] = kvp.Value!;
            }
        }

        // ✅ PHASE 2: Track Middleware pipeline execution
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Build and execute the prompt filt er pipeline using MiddlewareChain
        Func<PromptMiddlewareContext, Task<IEnumerable<ChatMessage>>> finalAction = ctx => Task.FromResult(ctx.Messages);
        var pipeline = MiddlewareChain.BuildPromptPipeline(_PromptMiddlewares, finalAction);
        var result = await pipeline(context).ConfigureAwait(false);

        stopwatch.Stop();

        return result;
    }

    /// <summary>
    /// Applies post-invocation Middlewares to process results, extract memories, etc.
    /// </summary>
    /// <param name="requestMessages">The messages sent to the LLM (after pre-processing)</param>
    /// <param name="responseMessages">The messages returned by the LLM, or null if failed</param>
    /// <param name="exception">Exception that occurred, or null if successful</param>
    /// <param name="options">The chat options used for the invocation</param>
    /// <param name="agentName">The agent name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task ApplyPostInvokeMiddlewaresAsync(
        IEnumerable<ChatMessage> requestMessages,
        IEnumerable<ChatMessage>? responseMessages,
        Exception? exception,
        ChatOptions? options,
        string agentName,
        CancellationToken cancellationToken)
    {
        if (!_PromptMiddlewares.Any())
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

        // Call PostInvokeAsync on all Middlewares (in order, not reversed)
        foreach (var Middleware in _PromptMiddlewares)
        {
            try
            {
                await Middleware.PostInvokeAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Log but don't fail - post-processing is best-effort
                // Individual Middleware failures shouldn't break the response
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
    /// The summary text content generated by the reducer.
    /// This is extracted from the summary message returned by the reducer.
    /// </summary>
    public string? SummaryText { get; init; }

    /// <summary>
    /// Number of messages that were removed during reduction.
    /// Used to calculate the reduction state for caching.
    /// </summary>
    public int MessagesRemovedCount { get; init; }
}

/// <summary>
/// Generic streaming result for protocol adapters.
/// Contains final history and reduction metadata after streaming completes.
/// Protocol-specific event streaming is handled by protocol adapters (AGUI, Microsoft, etc.)
/// </summary>
internal class StreamingTurnResult
{
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
    /// <param name="finalHistory">Task that provides the final turn history</param>
    /// <param name="reductionTask">Task that provides the reduction metadata</param>
    public StreamingTurnResult(
        Task<IReadOnlyList<ChatMessage>> finalHistory,
        Task<ReductionMetadata?> reductionTask)
    {
        FinalHistory = finalHistory ?? throw new ArgumentNullException(nameof(finalHistory));
        ReductionTask = reductionTask ?? Task.FromResult<ReductionMetadata?>(null);
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
    private readonly IReadOnlyList<IPermissionMiddleware> _PermissionMiddlewares;

    public PermissionManager(IReadOnlyList<IPermissionMiddleware>? PermissionMiddlewares)
    {
        _PermissionMiddlewares = PermissionMiddlewares ?? Array.Empty<IPermissionMiddleware>();
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
        AgentCore agent,
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

        // If no permission Middlewares are configured, auto-deny functions requiring permission
        // This is a safety measure - functions with [RequiresPermission] should not auto-approve
        if (_PermissionMiddlewares.Count == 0)
            return PermissionResult.Denied("No permission Middleware configured for function requiring permission");

        // Build and execute permission Middleware pipeline
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
            OutboundEvents = agent.MiddlewareEventWriter,
            Agent = agent
        };

        context.Metadata["CallId"] = functionCall.CallId;

        await ExecutePermissionPipeline(context, cancellationToken).ConfigureAwait(false);

        // If Middleware terminated the context, permission was denied
        if (context.IsTerminated)
        {
            var denialReason = context.Result?.ToString() ?? "Permission denied by Middleware";
            return PermissionResult.Denied(denialReason);
        }

        // If Middleware did not terminate, permission was approved
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
        AgentCore agent,
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
    /// Executes the permission Middleware pipeline (single responsibility)
    /// </summary>
    private async Task ExecutePermissionPipeline(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        // Build and execute the permission Middleware pipeline using MiddlewareChain
        Func<FunctionInvocationContext, Task> finalAction = _ => Task.CompletedTask;
        var pipeline = MiddlewareChain.BuildPermissionPipeline(_PermissionMiddlewares, finalAction);
        await pipeline(context).ConfigureAwait(false);
    }
}

#endregion

#region Error Formatting Helper

/// <summary>
/// Helper class for formatting detailed error messages with provider-specific information.
/// </summary>
internal static class ErrorFormatter
{
    /// <summary>
    /// Formats an exception with detailed error information for display to users.
    /// Extracts provider-specific error details using the error handler.
    /// </summary>
    internal static string FormatDetailedError(Exception ex, ErrorHandling.IProviderErrorHandler? errorHandler)
    {
        var sb = new System.Text.StringBuilder();

        // Try to get provider-specific error details
        var providerDetails = errorHandler?.ParseError(ex);

        if (providerDetails != null)
        {
            // Use structured error information from provider
            sb.AppendLine($"[{providerDetails.Category}] {providerDetails.Message}");

            if (providerDetails.StatusCode.HasValue)
                sb.AppendLine($"HTTP Status: {providerDetails.StatusCode}");

            if (!string.IsNullOrEmpty(providerDetails.ErrorCode))
                sb.AppendLine($"Error Code: {providerDetails.ErrorCode}");

            if (!string.IsNullOrEmpty(providerDetails.ErrorType))
                sb.AppendLine($"Error Type: {providerDetails.ErrorType}");

            if (!string.IsNullOrEmpty(providerDetails.RequestId))
                sb.AppendLine($"Request ID: {providerDetails.RequestId}");

            if (providerDetails.RetryAfter.HasValue)
                sb.AppendLine($"Retry After: {providerDetails.RetryAfter.Value.TotalSeconds:F1}s");

            if (providerDetails.RawDetails != null && providerDetails.RawDetails.Count > 0)
            {
                foreach (var kvp in providerDetails.RawDetails)
                {
                    sb.AppendLine($"{kvp.Key}: {kvp.Value}");
                }
            }
        }
        else
        {
            // Fallback to basic error message with exception type
            sb.AppendLine($"[{ex.GetType().Name}] {ex.Message}");

            if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
                sb.AppendLine($"HTTP Status: {(int)httpEx.StatusCode}");
        }

        // Add inner exception if present
        if (ex.InnerException != null)
        {
            sb.AppendLine($"Inner Exception: {ex.InnerException.Message}");
        }

        return sb.ToString().TrimEnd();
    }
}

#endregion

#region Event Stream Adapters

/// <summary>
/// Adapts protocol-agnostic internal agent events to specific protocol formats.
/// Eliminates duplication of event adaptation logic across the Agent codebase.
///
/// Supported protocols:
/// - Protocol-specific event adapters are in protocol libraries (AGUI, Microsoft)
/// - Core remains protocol-agnostic
/// </summary>
#endregion
#region

/// <summary>
/// Manages bidirectional event coordination for request/response patterns.
/// Used by Middlewares (permissions, clarifications) and supports nested agent communication.
/// Thread-safe for concurrent event emission and response coordination.
/// </summary>
/// <remarks>
/// This coordinator provides the infrastructure for:
/// - Event emission from Middlewares to handlers (one-way communication)
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
/// - Multiple Middlewares can emit concurrently
/// - Event channel supports multiple producers, single consumer
/// </remarks>
internal class BidirectionalEventCoordinator : IDisposable
{
    /// <summary>
    /// Shared event channel for all events.
    /// Unbounded to prevent blocking during event emission.
    /// Thread-safe: Multiple producers (Middlewares), single consumer (background drainer).
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
            SingleWriter = false,  // Multiple Middlewares can emit concurrently
            SingleReader = true,   // Only background drainer reads
            AllowSynchronousContinuations = false  // Performance & safety
        });
    }

    /// <summary>
    /// Gets the channel writer for event emission.
    /// Used by Middlewares and contexts to emit events directly to the channel.
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
    /// Sends a response to a Middleware waiting for a specific request.
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
    /// This method is used by Middlewares that need bidirectional communication:
    /// 1. Middleware emits request event (e.g., InternalPermissionRequestEvent)
    /// 2. Middleware calls WaitForResponseAsync() - BLOCKS HERE
    /// 3. Handler receives request event (via agent's event loop)
    /// 4. User provides input
    /// 5. Handler calls SendResponse()
    /// 6. Middleware receives response and continues
    ///
    /// Important: The Middleware is blocked during step 2-5, but events still flow
    /// because of the polling mechanism in RunAgenticLoopInternal.
    ///
    /// Timeout vs. Cancellation:
    /// - TimeoutException: No response received within the specified timeout
    /// - OperationCanceledException: External cancellation (e.g., user stopped agent)
    ///
    /// Example:
    /// <code>
    /// // In Middleware
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

#region Middleware Chain

/// <summary>
/// Builds and executes Middleware chains with proper ordering and pipeline execution.
/// Eliminates duplication of Middleware pipeline construction across the Agent codebase.
/// 
/// Usage Pattern:
/// 1. Define a final action (core logic)
/// 2. Build pipeline with Middlewares (automatically reversed for correct execution order)
/// 3. Execute the built pipeline
/// 
/// Supports all Middleware types: IAIFunctionMiddleware, IPromptMiddleware, IPermissionMiddleware, IMessageTurnMiddleware
/// </summary>
internal static class MiddlewareChain
{
    /// <summary>
    /// Builds an IAIFunctionMiddleware pipeline.
    /// Middlewares are applied in REVERSE order so they execute in the order provided.
    /// </summary>
    /// <param name="Middlewares">The Middlewares to apply, in the order they should execute</param>
    /// <param name="finalAction">The final action to execute after all Middlewares</param>
    /// <returns>A function that executes the complete pipeline</returns>
    public static Func<FunctionInvocationContext, Task> BuildAiFunctionPipeline(
        IEnumerable<IAIFunctionMiddleware> Middlewares,
        Func<FunctionInvocationContext, Task> finalAction)
    {
        if (finalAction == null)
            throw new ArgumentNullException(nameof(finalAction));

        var pipeline = finalAction;

        // Wrap in reverse order so Middlewares execute in the order provided
        foreach (var Middleware in Middlewares.Reverse())
        {
            var previous = pipeline;
            pipeline = ctx => Middleware.InvokeAsync(ctx, previous);
        }

        return pipeline;
    }

    /// <summary>
    /// Builds an IPromptMiddleware pipeline with result transformation.
    /// Middlewares can modify messages and return transformed results.
    /// </summary>
    /// <param name="Middlewares">The Middlewares to apply, in the order they should execute</param>
    /// <param name="finalAction">The final action that returns the messages</param>
    /// <returns>A function that executes the complete pipeline and returns messages</returns>
    public static Func<PromptMiddlewareContext, Task<IEnumerable<ChatMessage>>> BuildPromptPipeline(
        IEnumerable<IPromptMiddleware> Middlewares,
        Func<PromptMiddlewareContext, Task<IEnumerable<ChatMessage>>> finalAction)
    {
        if (finalAction == null)
            throw new ArgumentNullException(nameof(finalAction));

        var pipeline = finalAction;

        // Wrap in reverse order so Middlewares execute in the order provided
        foreach (var Middleware in Middlewares.Reverse())
        {
            var previous = pipeline;
            pipeline = ctx => Middleware.InvokeAsync(ctx, previous);
        }

        return pipeline;
    }

    /// <summary>
    /// Builds an IPermissionMiddleware pipeline.
    /// Used specifically for permission checking before function execution.
    /// </summary>
    /// <param name="Middlewares">The Middlewares to apply, in the order they should execute</param>
    /// <param name="finalAction">The final action (typically a no-op for permission checks)</param>
    /// <returns>A function that executes the complete pipeline</returns>
    public static Func<FunctionInvocationContext, Task> BuildPermissionPipeline(
        IEnumerable<IPermissionMiddleware> Middlewares,
        Func<FunctionInvocationContext, Task> finalAction)
    {
        if (finalAction == null)
            throw new ArgumentNullException(nameof(finalAction));

        var pipeline = finalAction;

        // Wrap in reverse order so Middlewares execute in the order provided
        foreach (var Middleware in Middlewares.Reverse())
        {
            var previous = pipeline;
            pipeline = ctx => Middleware.InvokeAsync(ctx, previous);
        }

        return pipeline;
    }

    /// <summary>
    /// Builds an IMessageTurnMiddleware pipeline.
    /// Used for post-turn observation and telemetry.
    /// </summary>
    /// <param name="Middlewares">The Middlewares to apply, in the order they should execute</param>
    /// <param name="finalAction">The final action (typically a no-op for observation)</param>
    /// <returns>A function that executes the complete pipeline</returns>
    public static Func<MessageTurnMiddlewareContext, Task> BuildMessageTurnPipeline(
        IEnumerable<IMessageTurnMiddleware> Middlewares,
        Func<MessageTurnMiddlewareContext, Task> finalAction)
    {
        if (finalAction == null)
            throw new ArgumentNullException(nameof(finalAction));

        var pipeline = finalAction;

        // Wrap in reverse order so Middlewares execute in the order provided
        foreach (var Middleware in Middlewares.Reverse())
        {
            var previous = pipeline;
            pipeline = ctx => Middleware.InvokeAsync(ctx, previous);
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
public record InternalMessageTurnStartedEvent(
    string MessageTurnId,
    string ConversationId,
    string AgentName,
    DateTimeOffset Timestamp) : InternalAgentEvent;

/// <summary>
/// Emitted when a message turn completes successfully
/// This represents the END of the entire agent execution for this user message.
/// </summary>
public record InternalMessageTurnFinishedEvent(
    string MessageTurnId,
    string ConversationId,
    string AgentName,
    TimeSpan Duration,
    DateTimeOffset Timestamp) : InternalAgentEvent;

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
    List<string> CompletedFunctions,
    string AgentName,
    DateTimeOffset Timestamp) : InternalAgentEvent;

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
/// Reasoning phase within a reasoning session.
/// </summary>
public enum ReasoningPhase
{
    /// <summary>Overall reasoning session begins</summary>
    SessionStart,
    /// <summary>Individual reasoning message starts</summary>
    MessageStart,
    /// <summary>Streaming reasoning content (delta)</summary>
    Delta,
    /// <summary>Individual reasoning message ends</summary>
    MessageEnd,
    /// <summary>Overall reasoning session ends</summary>
    SessionEnd
}

/// <summary>
/// Emitted for all reasoning-related events during agent execution.
/// Supports reasoning-capable models like o1, DeepSeek-R1.
/// </summary>
public record InternalReasoningEvent(
    ReasoningPhase Phase,
    string MessageId,
    string? Role = null,
    string? Text = null
) : InternalAgentEvent;

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

#region Middleware Events

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
    /// Name of the Middleware that emitted this event.
    /// </summary>
    string SourceName { get; }
}

/// <summary>
/// Marker interface for permission-related Middleware events.
/// Permission events are a specialized subset of Middleware events
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
/// Middleware requests permission to execute a function.
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
/// Sent by external handler back to waiting Middleware.
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
/// Middleware requests permission to continue beyond max iterations.
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
/// Middleware reports progress (one-way, no response needed).
/// </summary>
public record InternalMiddlewareProgressEvent(
    string SourceName,
    string Message,
    int? PercentComplete = null) : InternalAgentEvent, IBidirectionalEvent;

/// <summary>
/// Middleware reports an error (one-way, no response needed).
/// </summary>
public record InternalMiddlewareErrorEvent(
    string SourceName,
    string ErrorMessage,
    Exception? Exception = null) : InternalAgentEvent, IBidirectionalEvent;

#endregion

#region Observability Events (Internal diagnostics)

/// <summary>
/// Marker interface to distinguish observability events from protocol events.
/// Observability events are designed for logging, metrics, and monitoring.
/// They are processed by IAgentEventObserver implementations.
/// </summary>
public interface IObservabilityEvent { }

/// <summary>
/// Emitted when scoped tools visibility is determined for an iteration.
/// Contains full snapshot of what tools the LLM can see.
/// </summary>
public record InternalScopedToolsVisibleEvent(
    string AgentName,
    int Iteration,
    IReadOnlyList<string> VisibleToolNames,
    ImmutableHashSet<string> ExpandedPlugins,
    ImmutableHashSet<string> ExpandedSkills,
    int TotalToolCount,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when a plugin or skill container is expanded.
/// </summary>
public record InternalContainerExpandedEvent(
    string ContainerName,
    ContainerType Type,
    IReadOnlyList<string> UnlockedFunctions,
    int Iteration,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

public enum ContainerType { Plugin, Skill }

/// <summary>
/// Emitted when Middleware pipeline execution starts.
/// </summary>
public record InternalMiddlewarePipelineStartEvent(
    string FunctionName,
    int MiddlewareCount,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when Middleware pipeline execution completes.
/// </summary>
public record InternalMiddlewarePipelineEndEvent(
    string FunctionName,
    TimeSpan Duration,
    bool Success,
    string? ErrorMessage,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when a permission check occurs.
/// </summary>
public record InternalPermissionCheckEvent(
    string FunctionName,
    bool IsApproved,
    string? DenialReason,
    string AgentName,
    int Iteration,
    TimeSpan Duration,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when an iteration starts with full state snapshot.
/// </summary>
public record InternalIterationStartEvent(
    string AgentName,
    int Iteration,
    int MaxIterations,
    int CurrentMessageCount,
    int HistoryMessageCount,
    int TurnHistoryMessageCount,
    int ExpandedPluginsCount,
    int ExpandedSkillsCount,
    int CompletedFunctionsCount,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when circuit breaker is triggered.
/// </summary>
public record InternalCircuitBreakerTriggeredEvent(
    string AgentName,
    string FunctionName,
    int ConsecutiveCount,
    int Iteration,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when history reduction cache is checked.
/// </summary>
public record InternalHistoryReductionCacheEvent(
    string AgentName,
    bool IsHit,
    DateTime? ReductionCreatedAt,
    int? SummarizedUpToIndex,
    int CurrentMessageCount,
    int? TokenSavings,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Checkpoint operation type.
/// </summary>
public enum CheckpointOperation
{
    Saved,
    Restored,
    PendingWritesSaved,
    PendingWritesLoaded,
    PendingWritesDeleted
}

/// <summary>
/// Emitted for all checkpoint-related operations (save, restore, pending writes).
/// </summary>
public record InternalCheckpointEvent(
    CheckpointOperation Operation,
    string ThreadId,
    DateTimeOffset Timestamp,
    TimeSpan? Duration = null,
    int? Iteration = null,
    int? WriteCount = null,
    int? SizeBytes = null,
    int? MessageCount = null,
    bool? Success = null,
    string? ErrorMessage = null
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when parallel tool execution starts.
/// </summary>
public record InternalParallelToolExecutionEvent(
    string AgentName,
    int Iteration,
    int ToolCount,
    int ParallelBatchSize,
    int ApprovedCount,
    int DeniedCount,
    TimeSpan Duration,
    TimeSpan? SemaphoreWaitDuration,
    bool IsParallel,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Retry status for function execution.
/// </summary>
public enum RetryStatus
{
    /// <summary>Retry attempt in progress</summary>
    Attempting,
    /// <summary>All retry attempts exhausted</summary>
    Exhausted
}

/// <summary>
/// Emitted for all retry-related events during function execution.
/// </summary>
public record InternalRetryEvent(
    RetryStatus Status,
    string AgentName,
    string FunctionName,
    int AttemptNumber,
    int MaxRetries,
    DateTimeOffset Timestamp,
    string? ErrorMessage = null,
    TimeSpan? RetryDelay = null
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when delta sending is activated.
/// </summary>
public record InternalDeltaSendingActivatedEvent(
    string AgentName,
    int MessageCountSent,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when plan mode is activated.
/// </summary>
public record InternalPlanModeActivatedEvent(
    string AgentName,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when a nested agent is invoked.
/// </summary>
public record InternalNestedAgentInvokedEvent(
    string OrchestratorName,
    string ChildAgentName,
    int NestingDepth,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when document processing occurs.
/// </summary>
public record InternalDocumentProcessedEvent(
    string AgentName,
    string DocumentPath,
    long SizeBytes,
    TimeSpan Duration,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when message preparation completes.
/// </summary>
public record InternalMessagePreparedEvent(
    string AgentName,
    int Iteration,
    int FinalMessageCount,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when a bidirectional event is processed.
/// </summary>
public record InternalBidirectionalEventProcessedEvent(
    string AgentName,
    string EventType,
    bool RequiresResponse,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when agent makes a decision.
/// </summary>
public record InternalAgentDecisionEvent(
    string AgentName,
    string DecisionType,
    int Iteration,
    int ConsecutiveFailures,
    int ExpandedPluginsCount,
    int CompletedFunctionsCount,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when agent completes successfully.
/// </summary>
public record InternalAgentCompletionEvent(
    string AgentName,
    int TotalIterations,
    TimeSpan Duration,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when iteration messages are logged.
/// </summary>
public record InternalIterationMessagesEvent(
    string AgentName,
    int Iteration,
    int MessageCount,
    DateTimeOffset Timestamp
) : InternalAgentEvent, IObservabilityEvent;




#endregion

#endregion

#region Tool Execution Result Types

/// <summary>
/// Structured result from tool execution, replacing the 5-tuple return type.
/// Provides strongly-typed access to execution outcomes.
/// </summary>
internal record ToolExecutionResult(
    ChatMessage Message,
    HashSet<string> PluginExpansions,
    HashSet<string> SkillExpansions,
    Dictionary<string, string> SkillInstructions,
    HashSet<string> SuccessfulFunctions);

/// <summary>
/// Container detection metadata extracted from tool requests.
/// Consolidates plugin and skill container information to eliminate duplication.
/// </summary>
internal record ContainerDetectionInfo(
    Dictionary<string, string> PluginContainers,
    Dictionary<string, string> SkillContainers,
    Dictionary<string, string> SkillInstructions);

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
/// 2. ORCHESTRATION CONTEXT: Passed through Middleware pipelines with rich coordination features
///
/// Key capabilities:
/// - Function metadata (name, description, arguments, CallId)
/// - Agent context (AgentName, RunContext, Iteration tracking)
/// - Bidirectional communication (Emit, WaitForResponseAsync)
/// - Event coordination and bubbling (Agent reference, OutboundEvents)
/// - Middleware pipeline control (IsTerminated, Result)
///
/// Use cases:
/// - Plugins accessing execution context via Agent.CurrentFunctionContext
/// - Middlewares emitting events and waiting for user responses
/// - Permission/clarification workflows (human-in-the-loop)
/// - Telemetry, logging, and security auditing
/// - Nested agent coordination and event bubbling
/// </remarks>
/// <summary>
/// Context for function invocations in the Middleware pipeline.
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
    /// For Middleware pipeline: Use this for AIFunctionArguments wrapper.
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
    /// The raw tool call request from the Language Model (for Middleware pipeline).
    /// </summary>
    public ToolCallRequest? ToolCallRequest { get; set; }

    /// <summary>
    /// A flag to allow a Middleware to terminate the pipeline.
    /// </summary>
    public bool IsTerminated { get; set; } = false;

    /// <summary>
    /// The result of the function invocation, to be set by the final step.
    /// </summary>
    public object? Result { get; set; }

    /// <summary>
    /// Channel writer for emitting events during Middleware execution.
    /// Points to Agent's shared channel - events are immediately visible to background drainer.
    ///
    /// Thread-safety: Multiple Middlewares in the pipeline can emit concurrently.
    /// Event ordering: FIFO within each Middleware, interleaved across Middlewares.
    /// Lifetime: Valid for entire Middleware execution.
    /// </summary>
    internal ChannelWriter<InternalAgentEvent>? OutboundEvents { get; set; }

    /// <summary>
    /// Reference to the agent for response coordination.
    /// Lifetime: Set by ProcessFunctionCallsAsync, valid for entire Middleware execution.
    /// </summary>
    internal AgentCore? Agent { get; set; }

    /// <summary>
    /// Emits an event that will be yielded by RunAgenticLoopInternal.
    /// Events are delivered immediately to background drainer (not batched).
    /// Automatically bubbles events to parent agent if this is a nested agent call.
    ///
    /// Thread-safety: Safe to call from any Middleware in the pipeline.
    /// Performance: Non-blocking write (unbounded channel).
    /// Event ordering: Guaranteed FIFO per Middleware, interleaved across Middlewares.
    /// Real-time visibility: Handler sees event WHILE Middleware is executing (not after).
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
        var rootAgent = HPD.Agent.AgentCore.RootAgent;
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
    /// Used for request/response patterns in interactive Middlewares (permissions, approvals, etc.)
    ///
    /// Thread-safety: Safe to call from any Middleware.
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

        return await Agent.WaitForMiddlewareResponseAsync<T>(requestId, effectiveTimeout, cancellationToken);
    }

    /// <summary>
    /// Gets a string representation for logging/debugging.
    /// </summary>
    public override string ToString() =>
        $"Function: {FunctionName}, CallId: {CallId}, Agent: {AgentName ?? "Unknown"}, Iteration: {Iteration}";
}
# endregion

#region history reduction
/// <summary>
/// First-class immutable state for conversation history reduction.
/// Tracks which messages have been summarized and provides cache-aware reduction logic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design Philosophy:</b> History reduction is an LLM optimization, NOT a storage concern.
/// This class separates reduction metadata from message storage, allowing:
/// - Full unreduced history in ConversationThread and AgentLoopState
/// - Reduced messages sent to LLM only (token savings)
/// - Cache-aware incremental reduction (avoid redundant LLM calls)
/// - Integrity checking (detect if messages changed since reduction)
/// </para>
/// <para>
/// <b>Architecture:</b>
/// <code>
/// ┌─────────────────────────────────────────────────────────┐
/// │              Storage (Full History)                      │
/// │  - ConversationThread.Messages: [msg1...msg100]         │
/// │  - AgentLoopState.CurrentMessages: [msg1...msg100]      │
/// │  - AgentLoopState.ActiveReduction: HistoryReductionState│
/// │  - ConversationThread.LastReduction: HistoryReductionState│
/// └─────────────────────────────────────────────────────────┘
///                           ↓
///              ApplyToMessages(allMessages)
///                           ↓
/// ┌─────────────────────────────────────────────────────────┐
/// │              LLM Input (Reduced)                         │
/// │  - [Summary] "User discussed..."                        │
/// │  - msg91...msg100 (recent messages)                     │
/// │  Count: 11 messages (90% token savings!)                │
/// └─────────────────────────────────────────────────────────┘
/// </code>
/// </para>
/// </remarks>
public sealed record HistoryReductionState
{
    /// <summary>
    /// Message index where summary ends (exclusive).
    /// Messages [0..SummarizedUpToIndex) were summarized.
    /// Messages [SummarizedUpToIndex..end) are kept verbatim.
    /// </summary>
    /// <example>
    /// If 100 messages were reduced to keep last 10:
    /// - SummarizedUpToIndex = 90
    /// - Messages [0..90) → summarized
    /// - Messages [90..100) → kept
    /// </example>
    public required int SummarizedUpToIndex { get; init; }

    /// <summary>
    /// Total message count when this reduction was created.
    /// Used to detect cache invalidation (messages added or removed).
    /// </summary>
    public required int MessageCountAtReduction { get; init; }

    /// <summary>
    /// Generated summary content (text representation of old messages).
    /// This is what gets sent to the LLM in place of the summarized messages.
    /// </summary>
    public required string SummaryContent { get; init; }

    /// <summary>
    /// When this reduction was created (UTC).
    /// Useful for debugging and analytics.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// SHA256 hash of summarized messages (for integrity checking).
    /// Ensures messages haven't been modified/reordered since reduction.
    /// </summary>
    /// <remarks>
    /// Hash is computed from: message.Role + "|" + message.Content for each summarized message.
    /// If the hash doesn't match current messages, reduction is invalid.
    /// </remarks>
    public required string MessageHash { get; init; }

    /// <summary>
    /// Target message count configuration used for this reduction.
    /// Cached here to support IsValidFor checks without passing config.
    /// </summary>
    public required int TargetMessageCount { get; init; }

    /// <summary>
    /// Threshold for triggering re-reduction.
    /// Number of new messages allowed beyond TargetMessageCount before re-reduction is triggered.
    /// </summary>
    public required int ReductionThreshold { get; init; }

    // ═══════════════════════════════════════════════════════
    // CACHE VALIDATION
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Checks if this reduction is still valid for the current message count.
    /// A reduction is valid if:
    /// 1. Current message count >= MessageCountAtReduction (no deletions)
    /// 2. New messages since reduction <= threshold
    /// </summary>
    /// <param name="currentMessageCount">Current total message count</param>
    /// <returns>True if reduction can be reused (cache hit)</returns>
    /// <example>
    /// <code>
    /// // Reduction created when: 100 messages, target=20, threshold=5
    /// reduction.IsValidFor(100);  // ✅ True (no new messages)
    /// reduction.IsValidFor(104);  // ✅ True (4 new, under threshold)
    /// reduction.IsValidFor(106);  // ❌ False (6 new, exceeds threshold=5)
    /// reduction.IsValidFor(95);   // ❌ False (messages deleted!)
    /// </code>
    /// </example>
    public bool IsValidFor(int currentMessageCount)
    {
        // Check if messages were deleted (invalidates cache)
        if (currentMessageCount < MessageCountAtReduction)
            return false;

        // Check if too many new messages added
        int newMessagesSinceReduction = currentMessageCount - MessageCountAtReduction;
        return newMessagesSinceReduction <= ReductionThreshold;
    }

    /// <summary>
    /// Validates that summarized messages haven't changed since reduction.
    /// Computes hash of current messages and compares with stored hash.
    /// </summary>
    /// <param name="allMessages">Current full message history</param>
    /// <returns>True if messages match the stored hash</returns>
    /// <exception cref="ArgumentException">If message count is less than SummarizedUpToIndex</exception>
    public bool ValidateIntegrity(IEnumerable<ChatMessage> allMessages)
    {
        var messagesList = allMessages.ToList();

        if (messagesList.Count < SummarizedUpToIndex)
            throw new ArgumentException(
                $"Message count ({messagesList.Count}) is less than SummarizedUpToIndex ({SummarizedUpToIndex})");

        // Compute hash of messages that were summarized
        var currentHash = ComputeMessageHash(messagesList.Take(SummarizedUpToIndex));
        return currentHash == MessageHash;
    }

    // ═══════════════════════════════════════════════════════
    // MESSAGE TRANSFORMATION
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Applies this reduction to the full message history, producing reduced messages for LLM.
    /// Returns: [summary message] + [recent unreduced messages]
    /// </summary>
    /// <param name="allMessages">Full conversation history (unreduced)</param>
    /// <param name="systemMessage">Optional system message to prepend (now handled via ChatOptions.Instructions in PrepareTurnAsync)</param>
    /// <returns>Reduced messages ready for LLM (summary + recent)</returns>
    /// <exception cref="InvalidOperationException">If integrity check fails</exception>
    /// <example>
    /// <code>
    /// // Full history: 100 messages
    /// // Reduction: SummarizedUpToIndex=90, SummaryContent="User discussed greetings..."
    ///
    /// var reduced = reduction.ApplyToMessages(allMessages, systemMsg);
    /// // Result:
    /// // [0] System: "You are a helpful assistant"
    /// // [1] Assistant: "User discussed greetings..." (summary)
    /// // [2-11] msg91-100 (recent messages)
    /// // Total: 12 messages (vs 101 original)
    /// </code>
    /// </example>
    public IEnumerable<ChatMessage> ApplyToMessages(
        IEnumerable<ChatMessage> allMessages,
        ChatMessage? systemMessage = null)
    {
        var messagesList = allMessages.ToList();

        // Validate integrity (ensure messages haven't changed)
        if (!ValidateIntegrity(messagesList))
        {
            throw new InvalidOperationException(
                $"Message integrity check failed! Messages have been modified since reduction was created. " +
                $"Expected hash: {MessageHash}, Current messages changed.");
        }

        // Build reduced message list
        var result = new List<ChatMessage>();

        // Add system message first (if provided)
        if (systemMessage != null)
            result.Add(systemMessage);

        // Add summary message
        var summaryMessage = new ChatMessage(ChatRole.Assistant, SummaryContent);
        result.Add(summaryMessage);

        // Add recent unreduced messages
        var recentMessages = messagesList.Skip(SummarizedUpToIndex);
        result.AddRange(recentMessages);

        return result;
    }

    // ═══════════════════════════════════════════════════════
    // FACTORY METHOD
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new HistoryReductionState from reduction results.
    /// </summary>
    /// <param name="messages">Full message history that was reduced</param>
    /// <param name="summaryContent">Generated summary text</param>
    /// <param name="summarizedUpToIndex">Index where summary ends (exclusive)</param>
    /// <param name="targetMessageCount">Target message count from config</param>
    /// <param name="reductionThreshold">Threshold for triggering re-reduction</param>
    /// <returns>New HistoryReductionState instance</returns>
    public static HistoryReductionState Create(
        IReadOnlyList<ChatMessage> messages,
        string summaryContent,
        int summarizedUpToIndex,
        int targetMessageCount,
        int reductionThreshold)
    {
        var messageHash = ComputeMessageHash(messages.Take(summarizedUpToIndex));

        return new HistoryReductionState
        {
            SummarizedUpToIndex = summarizedUpToIndex,
            MessageCountAtReduction = messages.Count,
            SummaryContent = summaryContent,
            CreatedAt = DateTime.UtcNow,
            MessageHash = messageHash,
            TargetMessageCount = targetMessageCount,
            ReductionThreshold = reductionThreshold
        };
    }

    // ═══════════════════════════════════════════════════════
    // INTERNAL HELPERS
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Computes SHA256 hash of messages for integrity checking.
    /// Hash format: SHA256(role1|content1\nrole2|content2\n...)
    /// </summary>
    private static string ComputeMessageHash(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            sb.Append(msg.Role.Value);
            sb.Append('|');
            sb.Append(msg.Text ?? string.Empty);
            sb.Append('\n');
        }

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToBase64String(hashBytes);
    }
}
#endregion