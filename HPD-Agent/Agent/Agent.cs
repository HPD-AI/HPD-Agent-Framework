using Microsoft.Extensions.AI;
using HPD.Agent.Middleware;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using HPD.Agent.StructuredOutput;
using HPD.Events;


namespace HPD.Agent;

/// <summary>
/// Core Agent class implementing agentic behavior with function calling, middleware, and event coordination.
/// </code>
/// </summary>
public sealed class Agent
{
    private readonly IChatClient _baseClient;
    private readonly string _name;
    private readonly ChatClientMetadata _metadata;
    // OpenTelemetry Activity Source for telemetry
    private static readonly ActivitySource ActivitySource = new("HPD.Agent");
    // V2: AgentContext is now passed through middleware hooks, no need for AsyncLocal storage
    // AsyncLocal storage for root agent tracking in nested agent calls
    private static readonly AsyncLocal<Agent?> _rootAgent = new();
    // AsyncLocal storage for current conversation session (flows across async calls)
    // Provides access to session context (project, documents, etc.) throughout the agent execution
    private static readonly AsyncLocal<AgentSession?> _currentSession = new();

    // Specialized component fields for delegation
    private readonly MessageProcessor _messageProcessor;
    private readonly FunctionCallProcessor _functionCallProcessor;
    private readonly AgentTurn _agentTurn;
    private readonly HPD.Events.IEventCoordinator _eventCoordinator;
    // Unified middleware pipeline
    private readonly AgentMiddlewarePipeline _middlewarePipeline;
    // Observer pattern for event-driven observability
    private readonly IReadOnlyList<IAgentEventObserver> _observers;
    private readonly IReadOnlyList<IAgentEventHandler> _eventHandlers;
    private readonly ObserverHealthTracker? _observerHealthTracker;
    private readonly ILogger? _observerErrorLogger;
    private readonly Counter<long>? _observerErrorCounter;

    // Durable execution service for checkpointing (optional, created when DurableExecutionConfig is set)
    private readonly DurableExecution? _durableExecutionService;

    // Provider registry for runtime provider switching via AgentRunOptions.ProviderKey/ModelId
    private readonly Providers.IProviderRegistry? _providerRegistry;

    // Service provider for creating new clients
    private readonly IServiceProvider? _serviceProvider;

    // Middleware state factories for cross-assembly state discovery
    // Passed from AgentBuilder, used for session persistence and schema validation
    private readonly ImmutableDictionary<string, MiddlewareStateFactory> _stateFactories;

    /// <summary>
    /// Agent configuration object containing all settings
    /// </summary>
    public AgentConfig? Config { get; private set; }

    /// <summary>
    /// Gets the base chat client used by this agent.
    /// This can be used by SubAgents to inherit the parent's client when no provider is specified.
    /// </summary>
    public IChatClient BaseClient => _baseClient;

    /// <summary>
    /// Gets the middleware state factories registered for this agent.
    /// Used for session persistence, schema validation, and cross-assembly state discovery.
    /// </summary>
    internal IReadOnlyDictionary<string, MiddlewareStateFactory> StateFactories => _stateFactories;

    // V2: CurrentFunctionContext restored as AsyncLocal<HookContext?> for AIFunction context access
    // Functions like ClarificationFunction need access to Emit(), WaitForResponseAsync(), etc.
    // HookContext provides these capabilities in a clean, type-safe way
    private static readonly AsyncLocal<HookContext?> _currentFunctionContext = new();

    /// <summary>
    /// Gets the current function execution context (available during AIFunction execution).
    /// Used by special AIFunctions like ClarificationFunction that need to emit events or wait for responses.
    /// Returns null if not currently executing a function.
    /// </summary>
    /// <remarks>
    /// This is set to the BeforeFunctionContext during function execution, providing access to:
    /// - Emit() - emit events to the agent's event stream
    /// - WaitForResponseAsync() - wait for bidirectional event responses
    /// - AgentName - the name of the executing agent
    /// - State - current agent state
    /// </remarks>
    public static HookContext? CurrentFunctionContext
    {
        get => _currentFunctionContext.Value;
        internal set => _currentFunctionContext.Value = value;
    }

    /// <summary>
    /// Gets or sets the root agent in the current execution chain.
    /// Returns null if no root agent is set (single-agent execution).
    /// </summary>
    public static Agent? RootAgent
    {
        get => _rootAgent.Value;
        internal set => _rootAgent.Value = value;
    }

    /// <summary>
    /// Gets or sets the current conversation session in the execution context.
    /// This flows across async calls and provides access to session context throughout agent execution.
    /// </summary>
    public static AgentSession? CurrentSession
    {
        get => _currentSession.Value;
        internal set => _currentSession.Value = value;
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
    /// Execution context for this agent (agent name, ID, hierarchy).
    /// Set during agent initialization to enable event attribution in multi-agent systems.
    /// </summary>
    private AgentExecutionContext? _executionContextValue;
    public AgentExecutionContext? ExecutionContext
    {
        get => _executionContextValue;
        set { _executionContextValue = value; }
    }

    /// <summary>
    /// Internal access to event coordinator for context setup and nested agent configuration.
    /// </summary>
    public HPD.Events.IEventCoordinator EventCoordinator => _eventCoordinator;

    /// <summary>
    /// Internal access to event coordinator for middleware event emission.
    /// Use Emit() method for priority-aware routing.
    /// </summary>
    internal HPD.Events.IEventCoordinator MiddlewareEventCoordinator => _eventCoordinator;

    /// <summary>
    /// Sends a response to a pending middleware request (e.g., permission response, clarification).
    /// Convenience method that delegates to the underlying event coordinator.
    /// </summary>
    /// <param name="requestId">The request ID to respond to</param>
    /// <param name="response">The response event</param>
    public void SendMiddlewareResponse(string requestId, AgentEvent response)
    {
        _eventCoordinator.SendResponse(requestId, response);
    }

    /// <summary>
    /// Sets the execution context for event attribution.
    /// Called when the execution context is lazily initialized (e.g., on first RunAsync).
    /// Thread-safe: Can be called from any session.
    /// </summary>
    /// <param name="executionContext">The execution context to attach to events</param>

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
        IReadOnlyDictionary<string, string>? functionToToolkitMap = null,
        IReadOnlyDictionary<string, string>? functionToSkillMap = null,
        IReadOnlyList<IAgentMiddleware>? middlewares = null,
        IServiceProvider? serviceProvider = null,
        IEnumerable<IAgentEventObserver>? observers = null,
        IEnumerable<IAgentEventHandler>? eventHandlers = null,
        Providers.IProviderRegistry? providerRegistry = null,
        IReadOnlyDictionary<string, MiddlewareStateFactory>? stateFactories = null)
    {
        _providerRegistry = providerRegistry;
        _serviceProvider = serviceProvider;
        _stateFactories = stateFactories?.ToImmutableDictionary()
            ?? ImmutableDictionary<string, MiddlewareStateFactory>.Empty;
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = config.Name ?? "Agent"; // Default to "Agent" to prevent null dictionary key exceptions

        // Initialize unified middleware pipeline
        // Note: Error handler is now passed directly to FunctionRetryMiddleware, not stored here
        if (Config.ErrorHandling == null) Config.ErrorHandling = new ErrorHandlingConfig();


        // Initialize Microsoft.Extensions.AI compliance metadata
        _metadata = new ChatClientMetadata(
            providerName: config.Provider?.ProviderKey,
            providerUri: null,
            defaultModelId: config.Provider?.ModelName
        );

        // Initialize unified middleware pipeline
        _middlewarePipeline = new AgentMiddlewarePipeline(middlewares ?? Array.Empty<IAgentMiddleware>());

        // Create event coordinator for Middleware events and human-in-the-loop
        // Direct use of HPD.Events.EventCoordinator (no wrapper)
        _eventCoordinator = new HPD.Events.Core.EventCoordinator();

        // Plan mode instructions now injected by AgentPlanAgentMiddleware (middleware-based)
        _messageProcessor = new MessageProcessor(
            config.SystemInstructions, // Use base instructions; middleware adds plan mode guidance
            mergedOptions ?? config.Provider?.DefaultChatOptions);
        _functionCallProcessor = new FunctionCallProcessor(
            _eventCoordinator, // Pass IEventCoordinator for decoupled event emission
            _middlewarePipeline, // Pass unified middleware pipeline for permission checks
            config.MaxAgenticIterations,
            config.ErrorHandling,
            config.ServerConfiguredTools,
            config.AgenticLoop);  // Pass agentic loop config for TerminateOnUnknownCalls
        _agentTurn = new AgentTurn(
            _baseClient,
            config.ConfigureOptions,
            config.ChatClientMiddleware,
            serviceProvider);  

        // Resolve optional dependencies from service provider
        var loggerFactory = serviceProvider?.GetService(typeof(ILoggerFactory))
            as ILoggerFactory;

        // Initialize event observers (fire-and-forget, for telemetry/logging)
        _observers = observers?.ToList() ?? new List<IAgentEventObserver>();

        // Initialize event handlers (synchronous, ordered, for UI)
        _eventHandlers = eventHandlers?.ToList() ?? new List<IAgentEventHandler>();

        // Initialize observer health tracker if observers are configured
        if (_observers.Count > 0 && loggerFactory != null)
        {
            _observerErrorLogger = loggerFactory.CreateLogger<Agent>();

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

        // Initialize DurableExecutionService if configured
        // This provides retention policy enforcement and cleaner checkpointing API
        if (config.DurableExecutionConfig != null && config.SessionStore != null)
        {
            _durableExecutionService = new DurableExecution(
                config.SessionStore,
                config.DurableExecutionConfig);
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
    /// Agent middlewares applied to the agent lifecycle (message turns, iterations, functions).
    /// These are the unified IAgentMiddleware instances with built-in Collapsing support.
    /// </summary>
    public IReadOnlyList<IAgentMiddleware> Middlewares =>
        _middlewarePipeline.Middlewares;

    /// <summary>
    /// Validates and migrates middleware state schema when resuming from checkpoint.
    /// Detects added/removed middleware and logs changes for operational visibility.
    /// Schema is computed from runtime-registered factories (not compiled constants).
    /// </summary>
    /// <param name="checkpointState">Middleware state from checkpoint</param>
    /// <returns>Updated middleware state with current schema metadata</returns>
    private MiddlewareState ValidateAndMigrateSchema(MiddlewareState checkpointState)
    {
        // Compute current schema signature from runtime-registered factories
        var currentSignature = string.Join(",",
            _stateFactories.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var currentVersions = _stateFactories.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Version);

        // Case 1: Pre-versioning checkpoint (SchemaSignature is null)
        if (checkpointState.SchemaSignature == null)
        {
            _observerErrorLogger?.LogInformation(
                "Resuming from checkpoint created before schema versioning. " +
                "Upgrading to current schema.");

            var upgradeEvent = new SchemaChangedEvent(
                OldSignature: null,
                NewSignature: currentSignature,
                RemovedTypes: Array.Empty<string>(),
                AddedTypes: Array.Empty<string>(),
                IsUpgrade: true);

            // Notify observers using existing NotifyObservers method
            NotifyObservers(upgradeEvent);

            return new MiddlewareState
            {
                States = checkpointState.States,
                SchemaSignature = currentSignature,
                SchemaVersion = 1,
                StateVersions = currentVersions
            };
        }

        // Case 2: Schema matches (common case - no changes)
        if (checkpointState.SchemaSignature == currentSignature)
        {
            return checkpointState;
        }

        // Case 3: Schema changed - detect and log differences
        var oldTypes = checkpointState.SchemaSignature
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();
        var newTypes = _stateFactories.Keys.ToHashSet();

        var removed = oldTypes.Except(newTypes).ToList();
        var added = newTypes.Except(oldTypes).ToList();

        // Log removed middleware (potential data loss - WARNING level)
        if (removed.Count > 0)
        {
            var removedNames = removed.Select(fqn => fqn.Split('.').Last()).ToList();

            _observerErrorLogger?.LogWarning(
                "Checkpoint contains state for {RemovedCount} middleware that no longer exist: {RemovedMiddleware}. " +
                "State will be discarded (this is expected after middleware removal).",
                removed.Count,
                string.Join(", ", removedNames));
        }

        // Log added middleware (expected behavior - INFO level)
        if (added.Count > 0)
        {
            var addedNames = added.Select(fqn => fqn.Split('.').Last()).ToList();

            _observerErrorLogger?.LogInformation(
                "Detected {AddedCount} new middleware not present in checkpoint: {AddedMiddleware}. " +
                "State will be initialized to defaults.",
                added.Count,
                string.Join(", ", addedNames));
        }

        // Emit telemetry event for monitoring
        var schemaEvent = new SchemaChangedEvent(
            OldSignature: checkpointState.SchemaSignature,
            NewSignature: currentSignature,
            RemovedTypes: removed,
            AddedTypes: added,
            IsUpgrade: false);

        // Notify observers using existing NotifyObservers method
        NotifyObservers(schemaEvent);

        // Update to current schema metadata
        return new MiddlewareState
        {
            States = checkpointState.States,
            SchemaSignature = currentSignature,
            SchemaVersion = 1,
            StateVersions = currentVersions
        };
    }

    /// <summary>
    /// Notifies all registered observers about an event using fire-and-forget pattern.
    /// Observer failures are logged but don't impact agent execution.
    /// Circuit breaker pattern automatically disables failing observers.
    /// </summary>
    private void NotifyObservers(AgentEvent evt)
    {
        if (_observers.Count == 0) return;

        // Skip observability events if disabled (default)
        if (evt is IObservabilityEvent && !(Config?.Observability?.EmitObservabilityEvents ?? false))
        {
            return;
        }

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

    /// <summary>
    /// Processes event handlers synchronously, guaranteeing ordered execution.
    /// Unlike NotifyObservers which fires-and-forgets, this awaits each handler.
    /// Handler failures are logged but don't crash the agent.
    /// </summary>
    private async Task ProcessEventHandlersAsync(AgentEvent evt, CancellationToken cancellationToken)
    {
        if (_eventHandlers.Count == 0) return;

        foreach (var handler in _eventHandlers)
        {
            // Check handler-specific filter
            if (!handler.ShouldProcess(evt))
            {
                continue;
            }

            try
            {
                await handler.OnEventAsync(evt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancellation requested - stop processing handlers
                throw;
            }
            catch (Exception ex)
            {
                // Log but don't crash - handler failures shouldn't break the agent
                _observerErrorLogger?.LogError(ex,
                    "EventHandler {HandlerType} failed processing {EventType}",
                    handler.GetType().Name, evt.GetType().Name);
            }
        }
    }
        /// <summary>
    /// - Accepts PreparedTurn (functional preparation from MessageProcessor.PrepareTurnAsync)
    /// - Uses AgentDecisionEngine (pure, testable) for all decision logic
    /// - Executes decisions INLINE to preserve real-time streaming
    /// - State managed via immutable AgentLoopState for testability
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> RunAgenticLoopInternal(
        PreparedTurn turn,
        List<ChatMessage> turnHistory,
        TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletionSource,
        AgentSession? session = null,
        Dictionary<string, object>? initialContextProperties = null,
        AgentRunOptions? runOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var orchestrationStartTime = DateTime.UtcNow;

        // Create orchestration activity to group all agent turns and function calls
        using var orchestrationActivity = ActivitySource.StartActivity(
            "agent.orchestration",
            ActivityKind.Internal);

        orchestrationActivity?.SetTag("agent.name", _name);
        orchestrationActivity?.SetTag("agent.provider", ProviderKey);
        orchestrationActivity?.SetTag("agent.model", ModelId);

        // Track root agent for event bubbling across nested agent calls
        var previousRootAgent = RootAgent;
        RootAgent ??= this;

        // Initialize root orchestrator execution context if this is the root agent
        if (RootAgent == this && ExecutionContext == null)
        {
            var randomId = Guid.NewGuid().ToString("N")[..8];
            ExecutionContext = new AgentExecutionContext
            {
                AgentName = _name,
                AgentId = $"{_name}-{randomId}",
                ParentAgentId = null,
                AgentChain = new[] { _name },
                Depth = 0
            };
        }

        IReadOnlyList<ChatMessage> messages = turn.MessagesForLLM;
        var newInputMessages = turn.NewInputMessages;

        // Create linked cancellation token for turn timeout
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Config?.AgenticLoop?.MaxTurnDuration is { } turnTimeout)
        {
            turnCts.CancelAfter(turnTimeout);
        }
        var effectiveCancellationToken = turnCts.Token;

        // Generate IDs for this message turn
        var messageTurnId = Guid.NewGuid().ToString();

        // Extract conversation ID from turn.Options, session, or generate new one
        string conversationId;
        if (turn.Options?.AdditionalProperties?.TryGetValue("ConversationId", out var convIdObj) == true && convIdObj is string convId)
        {
            conversationId = convId;
        }
        else if (!string.IsNullOrWhiteSpace(session?.ConversationId))
        {
            conversationId = session.ConversationId;
        }
        else
        {
            conversationId = Guid.NewGuid().ToString();
        }

        // Store on session for future runs (stateless agent pattern)
        if (session != null)
        {
            session.ConversationId = conversationId;
        }

        try
        {
            // Emit MESSAGE TURN started event
            yield return new MessageTurnStartedEvent(
                messageTurnId,
                conversationId,
                _name);
 
            // MESSAGE PREPARATION: Split logic between Fresh Run vs Resume
            // FRESH RUN: Process documents → PrepareMessages → Create initial state
            // RESUME:    Use state.CurrentMessages as-is (already prepared)
            AgentLoopState state;
            IEnumerable<ChatMessage> effectiveMessages;
            ChatOptions? effectiveOptions;

            if (session?.ExecutionState is { } executionState)
            {
                var checkpointRestoreStart = DateTimeOffset.UtcNow;
                var restoreStopwatch = Stopwatch.StartNew();

                state = executionState;

                // Validate and migrate middleware schema when resuming from checkpoint
                // This detects added/removed middleware and logs changes for operational visibility
                state = state with
                {
                    MiddlewareState = ValidateAndMigrateSchema(state.MiddlewareState)
                };

                // Use messages from restored state (already prepared - includes system instructions)
                effectiveMessages = state.CurrentMessages;

                // Use options from PreparedTurn (already merged and Middlewareed)
                effectiveOptions = turn.Options;

                restoreStopwatch.Stop();

                // Emit checkpoint restored event
                yield return new CheckpointEvent(
                    Operation: CheckpointOperation.Restored,
                    SessionId: session.Id,
                    Timestamp: DateTimeOffset.UtcNow,
                    Duration: restoreStopwatch.Elapsed,
                    Iteration: state.Iteration,
                    MessageCount: state.CurrentMessages.Count);

                //
                // RESTORE PENDING WRITES (partial failure recovery via DurableExecutionService)
                //
                if (_durableExecutionService != null && state.ETag != null)
                {
                    int pendingWritesCount = 0;
                    bool pendingWritesLoaded = false;

                    try
                    {
                        var pendingWrites = await _durableExecutionService.LoadPendingWritesAsync(
                            session.Id,
                            state.ETag,
                            effectiveCancellationToken).ConfigureAwait(false);

                        pendingWritesCount = pendingWrites.Count;
                        pendingWritesLoaded = true;

                        if (pendingWrites.Count > 0)
                        {
                            state = state with { PendingWrites = pendingWrites.ToImmutableList() };
                        }
                    }
                    catch (Exception)
                    {
                        // Swallow errors - pending writes are an optimization
                    }

                    if (pendingWritesLoaded)
                    {
                        yield return new CheckpointEvent(
                            Operation: CheckpointOperation.PendingWritesLoaded,
                            SessionId: session.Id,
                            Timestamp: DateTimeOffset.UtcNow,
                            WriteCount: pendingWritesCount);
                    }
                }

                // Log resume (logging deferred until after config is built below)
                // Observability will happen after the common configuration section
            }
            else
            {
                //
                // FRESH RUN PATH: Use PreparedTurn directly (all preparation already done)
                //

                // Load persistent middleware state from session (cross-run caching)
                var persistentState = MiddlewareState.LoadFromSession(session, _stateFactories);

                // Initialize state with FULL unreduced history
                // PreparedTurn.MessagesForLLM contains the reduced version (for LLM calls)
                // We store the full history in state for proper message counting
                state = AgentLoopState.Initial(messages.ToList(), messageTurnId, conversationId, this.Name, persistentState);

                // Use PreparedTurn's already-prepared messages and options
                effectiveMessages = turn.MessagesForLLM;
                effectiveOptions = turn.Options;  // Already merged + Middlewareed
            }

            //     
            // BUILD CONFIGURATION & DECISION ENGINE (common to both paths)
            //     

            var config = BuildDecisionConfiguration(effectiveOptions);
            var decisionEngine = new AgentDecisionEngine();
            
            // INITIALIZE TURN HISTORY: Add only NEW input messages (Option 2 pattern)
            // All NEW messages from this turn will be saved to session at the end
            // PreparedTurn separates MessagesForLLM (full history) from NewInputMessages (to persist)
            
            foreach (var msg in newInputMessages)
            {
                turnHistory.Add(msg);
            }

            ChatResponse? lastResponse = null;

            // Collect all response updates to build final history
            var responseUpdates = new List<ChatResponseUpdate>();

            // Resolve override client from AgentRunOptions (if any)
            // This enables runtime provider switching without rebuilding the agent
            var overrideClient = ResolveClientForOptions(runOptions);

            // Resolve background responses settings from AgentRunOptions → Config → false
            var allowBackgroundResponses = runOptions?.AllowBackgroundResponses
                ?? Config?.BackgroundResponses?.DefaultAllow
                ?? false;

            // BACKGROUND RESPONSES VALIDATION: Log warnings for common mistakes
            // Philosophy: "Let it flow" - warn via logging but don't block, allow graceful degradation
            ValidateBackgroundResponsesUsage(runOptions, allowBackgroundResponses, newInputMessages.Count);

            // Apply background responses settings to effectiveOptions
            // Note: This requires pragma suppression for experimental M.E.AI feature
            if (allowBackgroundResponses || runOptions?.ContinuationToken != null)
            {
                effectiveOptions = ApplyBackgroundResponsesOptions(
                    effectiveOptions,
                    allowBackgroundResponses,
                    runOptions?.ContinuationToken);
            }

            // OBSERVABILITY: Start telemetry and logging

            var turnStopwatch = System.Diagnostics.Stopwatch.StartNew();


            // INITIALIZE AGENT CONTEXT (V2 - Single unified context for entire turn)
            // This replaces the dual-context system (turnContext + middlewareContext)
            var agentContext = new Middleware.AgentContext(
                agentName: _name,
                conversationId: conversationId,
                initialState: state,
                eventCoordinator: _eventCoordinator,
                session: session,
                cancellationToken: effectiveCancellationToken,
                parentChatClient: _baseClient,  // Pass chat client for SubAgent inheritance
                services: _serviceProvider);  // Pass service provider for DI

            // IMPORTANT: Create runOptions instance ONCE and reuse it throughout the entire turn
            // Middleware may modify runOptions (e.g., AgentPlanAgentMiddleware sets AdditionalSystemInstructions)
            // We must use the SAME instance for BeforeMessageTurnAsync and BeforeIterationAsync
            var effectiveRunOptions = runOptions ?? new AgentRunOptions();

            // MIDDLEWARE: BeforeMessageTurnAsync (turn-level hook)
            var beforeTurnContext = agentContext.AsBeforeMessageTurn(
                userMessage: newInputMessages.FirstOrDefault(),
                conversationHistory: effectiveMessages.ToList(),
                runOptions: effectiveRunOptions);

            await _middlewarePipeline.ExecuteBeforeMessageTurnAsync(beforeTurnContext, effectiveCancellationToken);

            // V2: State updates are immediate - no GetPendingState() needed!
            state = agentContext.State;

            // UPDATE TURN HISTORY: If middleware transformed the user message (e.g., DataContent → UriContent),
            // replace it in turnHistory so the transformed version gets persisted to session
            if (beforeTurnContext.UserMessage != null && newInputMessages.Count > 0)
            {
                var originalMessage = newInputMessages[0];
                if (!ReferenceEquals(originalMessage, beforeTurnContext.UserMessage))
                {
                    // Middleware transformed the message - update turnHistory
                    for (int i = 0; i < turnHistory.Count; i++)
                    {
                        if (ReferenceEquals(turnHistory[i], originalMessage))
                        {
                            turnHistory[i] = beforeTurnContext.UserMessage;
                            break;
                        }
                    }
                }
            }

            // UPDATE EFFECTIVE MESSAGES: If middleware modified ConversationHistory (e.g., HistoryReductionMiddleware),
            // update effectiveMessages to use the modified history
            effectiveMessages = beforeTurnContext.ConversationHistory;

            // Drain middleware events
            while (_eventCoordinator.TryRead(out var middlewareEvt))
                yield return (AgentEvent)middlewareEvt;


            // MAIN AGENTIC LOOP (Hybrid: Pure Decisions + Inline Execution)
            // NOTE: Iteration limit enforcement is handled by ContinuationPermissionMiddleware.
            // The middleware checks the limit and requests user permission to continue.
            // This allows clean separation: loop continues until middleware signals termination.

            while (!state.IsTerminated)
            {
                // Generate message ID for this iteration
                var assistantMessageId = Guid.NewGuid().ToString();

                // Emit iteration start
                yield return new AgentTurnStartedEvent(state.Iteration);

                // Emit state snapshot for testing/debugging
                yield return new StateSnapshotEvent(
                    CurrentIteration: state.Iteration,
                    MaxIterations: state.MiddlewareState.ContinuationPermission()?.CurrentExtendedLimit ?? config.MaxIterations,
                    IsTerminated: state.IsTerminated,
                    TerminationReason: state.TerminationReason,
                    ConsecutiveErrorCount: state.MiddlewareState.ErrorTracking()?.ConsecutiveFailures ?? 0,
                    CompletedFunctions: new List<string>(state.CompletedFunctions),
                    AgentName: _name);

                // Drain middleware events before decision
                while (_eventCoordinator.TryRead(out var MiddlewareEvt))
                    yield return (AgentEvent)MiddlewareEvt;

                //      
                // FUNCTIONAL CORE: Pure Decision (No I/O)
                //      

                var decision = decisionEngine.DecideNextAction(state, lastResponse, config);

                //      
                // OBSERVABILITY: Emit iteration and decision events
                //      

                // Emit iteration start event
                yield return new IterationStartEvent(
                    AgentName: _name,
                    Iteration: state.Iteration,
                    MaxIterations: config.MaxIterations,
                    CurrentMessageCount: state.CurrentMessages.Count,
                    HistoryMessageCount: 0, // History is part of CurrentMessages
                    TurnHistoryMessageCount: state.TurnHistory.Count,
                    CompletedFunctionsCount: state.CompletedFunctions.Count);

                // Emit decision event
                yield return new AgentDecisionEvent(
                    AgentName: _name,
                    DecisionType: decision.GetType().Name,
                    Iteration: state.Iteration,
                    ConsecutiveFailures: state.MiddlewareState.ErrorTracking()?.ConsecutiveFailures ?? 0,
                    CompletedFunctionsCount: state.CompletedFunctions.Count);

                // NOTE: Circuit breaker events are now emitted directly by CircuitBreakerIterationMiddleware
                // via context.Emit() in BeforeToolExecutionAsync.

                // Drain middleware events after decision-making, before execution
                // CRITICAL: Ensures events emitted during decision logic are yielded before LLM streaming starts
                while (_eventCoordinator.TryRead(out var MiddlewareEvt))
                    yield return (AgentEvent)MiddlewareEvt;

                //     
                // ARCHITECTURAL DECISION: Inline Execution for Zero-Latency Streaming
                //     
                //
                // LLM calls and tool execution happen INLINE (not extracted to methods)
                // to preserve real-time streaming. Extracting would add 200-3000ms latency
                // due to buffering events before returning them.
                //

                if (decision is AgentDecision.CallLLM)
                {    

                    // Determine messages to send with cache-aware reduction
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
                        // First iteration: Use effectiveMessages (reduced) from PrepareTurnAsync
                        // This applies history reduction for the initial LLM call
                        // effectiveMessages already contains reduced history if reduction was applied
                        messagesToSend = effectiveMessages;
                        messageCountToSend = effectiveMessages.Count();  // Reduced count!
                    }
                    else
                    {
                        // Subsequent iterations (iteration > 0):
                        // Option 1: Apply reduction if configured and available (optimal tokens)
                        // Option 2: Use full history (simpler, current default)

                        // For now, use full history (includes tool results from previous iterations)
                        // Future enhancement: HistoryReductionMiddleware could re-apply reduction
                        // on every iteration for very long conversations
                        messagesToSend = state.CurrentMessages;
                        messageCountToSend = state.CurrentMessages.Count;
                    }

                    // ═══════════════════════════════════════════════════════════════
                    // RUNTIME TOOLS MERGE (for structured output tool mode)
                    // Must happen BEFORE BeforeIterationAsync so middleware sees the output tool
                    // Only merge once - subsequent iterations reuse the merged options
                    // ═══════════════════════════════════════════════════════════════
                    var hasRuntimeTools = runOptions?.RuntimeTools?.Count > 0;
                    var hasAdditionalTools = runOptions?.AdditionalTools?.Count > 0;

                    if ((hasRuntimeTools || hasAdditionalTools) && state.Iteration == 0)
                    {
                        // Clone options to avoid mutating shared instances
                        effectiveOptions = effectiveOptions?.Clone() ?? new ChatOptions();

                        // Merge runtime tools with existing tools
                        var allTools = new List<AITool>();
                        if (effectiveOptions.Tools != null)
                            allTools.AddRange(effectiveOptions.Tools);

                        // Add internal runtime tools (from structured output)
                        if (hasRuntimeTools)
                            allTools.AddRange(runOptions!.RuntimeTools!);

                        // Add user-provided additional tools
                        if (hasAdditionalTools)
                            allTools.AddRange(runOptions!.AdditionalTools!);

                        effectiveOptions.Tools = allTools;
                    }

                    // ═══════════════════════════════════════════════════════════════
                    // RUNTIME TOOL MODE OVERRIDE (for structured output tool/union mode)
                    // Forces LLM to call a tool - provider-enforced, not prompt-based
                    // Only apply on first iteration - subsequent iterations follow same mode
                    // ═══════════════════════════════════════════════════════════════
                    // Public ToolModeOverride takes precedence over internal RuntimeToolMode
                    var toolModeOverride = runOptions?.ToolModeOverride ?? runOptions?.RuntimeToolMode;
                    if (toolModeOverride != null && state.Iteration == 0)
                    {
                        effectiveOptions = effectiveOptions?.Clone() ?? new ChatOptions();
                        effectiveOptions.ToolMode = toolModeOverride;
                    }

                    // UPDATE AGENT CONTEXT STATE (sync state changes from previous iteration)
                    agentContext.SyncState(state);

                    // CREATE TYPED ITERATION CONTEXT (V2)
                    // Note: Tool Collapsing is handled by ToolCollapsingMiddleware in BeforeIterationAsync
                    // The middleware will filter tools and emit CollapsedToolsVisibleEvent
                    var beforeIterationContext = agentContext.AsBeforeIteration(
                        iteration: state.Iteration,
                        messages: messagesToSend.ToList(),
                        options: effectiveOptions ?? new ChatOptions(),
                        runOptions: effectiveRunOptions);  // Use the SAME instance from BeforeMessageTurnAsync

                    // EXECUTE BEFORE ITERATION MIDDLEWARES
                    // Run with event polling to support bidirectional events (e.g., ContinuationPermissionMiddleware)
                    var beforeIterationTask = _middlewarePipeline.ExecuteBeforeIterationAsync(
                        beforeIterationContext,
                        effectiveCancellationToken);

                    // Poll for events while middleware is executing (CRITICAL for permission requests)
                    while (!beforeIterationTask.IsCompleted)
                    {
                        var delayTask = Task.Delay(10, effectiveCancellationToken);
                        await Task.WhenAny(beforeIterationTask, delayTask).ConfigureAwait(false);

                        while (_eventCoordinator.TryRead(out var middlewareEvt))
                        {
                            yield return (AgentEvent)middlewareEvt;
                        }
                    }

                    // Await to propagate any exceptions
                    await beforeIterationTask.ConfigureAwait(false);

                    // Final drain of events from middleware
                    while (_eventCoordinator.TryRead(out var middlewareEvt))
                    {
                        yield return (AgentEvent)middlewareEvt;
                    }

                    // V2: State updates are immediate - no GetPendingState() needed!
                    state = agentContext.State;

                    // Use potentially modified values from Middlewares
                    messagesToSend = beforeIterationContext.Messages;
                    var CollapsedOptions = beforeIterationContext.Options;

                    // Streaming state
                    var assistantContents = new List<AIContent>();
                    var toolRequests = new List<FunctionCallContent>();
                    bool messageStarted = false;
                    bool reasoningMessageStarted = false;
                    bool backgroundOperationEventEmitted = false;
                    ResponseContinuationToken? lastContinuationToken = null;

                    // Execute LLM call (unless skipped by Middleware)

                    if (beforeIterationContext.SkipLLMCall)
                    {
                        // Use cached/provided response from Middleware
                        if (beforeIterationContext.OverrideResponse != null)
                        {
                            assistantContents.AddRange(beforeIterationContext.OverrideResponse.Contents);

                            // Emit events for middleware-provided response (matching normal LLM flow)
                            foreach (var content in beforeIterationContext.OverrideResponse.Contents)
                            {
                                if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                                {
                                    if (!messageStarted)
                                    {
                                        yield return new TextMessageStartEvent(assistantMessageId, "assistant");
                                        messageStarted = true;
                                    }
                                    yield return new TextDeltaEvent(textContent.Text, assistantMessageId);
                                }
                                else if (content is FunctionCallContent functionCall)
                                {
                                    if (!messageStarted)
                                    {
                                        yield return new TextMessageStartEvent(assistantMessageId, "assistant");
                                        messageStarted = true;
                                    }
                                    yield return new ToolCallStartEvent(
                                        functionCall.CallId,
                                        functionCall.Name ?? string.Empty,
                                        assistantMessageId);

                                    if (functionCall.Arguments != null && functionCall.Arguments.Count > 0)
                                    {
                                        var argsJson = JsonSerializer.Serialize(
                                            functionCall.Arguments,
                                            HPDJsonContext.Default.DictionaryStringObject);
                                        yield return new ToolCallArgsEvent(functionCall.CallId, argsJson);
                                    }
                                }
                            }
                        }
                        // Tool calls come from the override response
                        if (beforeIterationContext.OverrideResponse != null)
                        {
                            toolRequests.AddRange(beforeIterationContext.OverrideResponse.Contents
                                .OfType<FunctionCallContent>());
                        }
                    }
                    else
                    {
                        // Emit iteration messages event
                        yield return new IterationMessagesEvent(
                            _name,
                            state.Iteration,
                            messagesToSend.Count(),
                            DateTimeOffset.UtcNow);

                        // CREATE MODEL REQUEST (V2 - immutable request pattern)
                        var modelRequest = new Middleware.ModelRequest
                        {
                            Model = overrideClient ?? _baseClient,
                            Messages = messagesToSend.ToList(),
                            Options = CollapsedOptions,
                            State = agentContext.State,
                            Iteration = state.Iteration,
                            Streams = _eventCoordinator.Streams,
                            RunOptions = effectiveRunOptions,
                            EventCoordinator = _eventCoordinator
                        };

                        // Check if we should coalesce deltas (run options override config default)
                        bool coalesceDeltas = effectiveRunOptions.CoalesceDeltas ?? Config?.CoalesceDeltas ?? false;

                        if (coalesceDeltas)
                        {
                            // COALESCE MODE: Buffer all updates, then emit coalesced events
                            await foreach (var update in _middlewarePipeline.ExecuteModelCallStreamingAsync(
                                modelRequest,
                                (req) => _agentTurn.RunAsync(req.Messages, req.Options, req.Model as IChatClient, effectiveCancellationToken),
                                effectiveCancellationToken))
                            {
                                // Store update for building final history
                                responseUpdates.Add(update);

                                // Check for background operation continuation token (M.E.AI 10.1.1+ strongly-typed)
#pragma warning disable MEAI001 // Experimental API - Background Responses
                                var continuationToken = update.ContinuationToken;
                                if (continuationToken != null)
                                {
                                    lastContinuationToken = continuationToken;

                                    // Emit background operation started event on first token
                                    if (!backgroundOperationEventEmitted && allowBackgroundResponses)
                                    {
                                        backgroundOperationEventEmitted = true;
                                        yield return new BackgroundOperationStartedEvent(
                                            ContinuationToken: continuationToken,
                                            Status: OperationStatus.InProgress,
                                            OperationId: assistantMessageId);

                                        // Track in agent loop state for crash recovery
                                        state = state.WithBackgroundOperation(new BackgroundOperationInfo
                                        {
                                            TokenData = Convert.ToBase64String(continuationToken.ToBytes().Span),
                                            Iteration = state.Iteration,
                                            StartedAt = DateTimeOffset.UtcNow,
                                            LastKnownStatus = OperationStatus.InProgress
                                        });
                                    }
                                }
#pragma warning restore MEAI001

                                // Accumulate content without emitting events yet
                                if (update.Contents != null)
                                {
                                    foreach (var content in update.Contents)
                                    {
                                        if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                                        {
                                            assistantContents.Add(reasoning);
                                        }
                                        else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                                        {
                                            assistantContents.Add(textContent);
                                        }
                                        else if (content is FunctionCallContent functionCall)
                                        {
                                            toolRequests.Add(functionCall);
                                            assistantContents.Add(functionCall);
                                        }
                                    }
                                }

                                // Still emit middleware events immediately
                                while (_eventCoordinator.TryRead(out var MiddlewareEvt))
                                {
                                    yield return (AgentEvent)MiddlewareEvt;
                                }
                            }

                            // Now coalesce and emit events
                            var coalescedContents = CoalesceTextContents(assistantContents);

                            foreach (var content in coalescedContents)
                            {
                                if (content is TextReasoningContent reasoning)
                                {
                                    if (!reasoningMessageStarted)
                                    {
                                        yield return new ReasoningMessageStartEvent(
                                            MessageId: assistantMessageId,
                                            Role: "assistant");
                                        reasoningMessageStarted = true;
                                    }
                                    yield return new ReasoningDeltaEvent(
                                        Text: reasoning.Text,
                                        MessageId: assistantMessageId);
                                }
                                else if (content is TextContent textContent)
                                {
                                    if (reasoningMessageStarted)
                                    {
                                        yield return new ReasoningMessageEndEvent(
                                            MessageId: assistantMessageId);
                                        reasoningMessageStarted = false;
                                    }
                                    if (!messageStarted)
                                    {
                                        yield return new TextMessageStartEvent(assistantMessageId, "assistant");
                                        messageStarted = true;
                                    }
                                    yield return new TextDeltaEvent(textContent.Text, assistantMessageId);
                                }
                                else if (content is FunctionCallContent functionCall)
                                {
                                    if (reasoningMessageStarted)
                                    {
                                        yield return new ReasoningMessageEndEvent(
                                            MessageId: assistantMessageId);
                                        reasoningMessageStarted = false;
                                    }
                                    if (!messageStarted)
                                    {
                                        yield return new TextMessageStartEvent(assistantMessageId, "assistant");
                                        messageStarted = true;
                                    }

                                    yield return new ToolCallStartEvent(
                                        functionCall.CallId,
                                        functionCall.Name ?? string.Empty,
                                        assistantMessageId);

                                    if (functionCall.Arguments != null && functionCall.Arguments.Count > 0)
                                    {
                                        var argsJson = JsonSerializer.Serialize(
                                            functionCall.Arguments,
                                            HPDJsonContext.Default.DictionaryStringObject);

                                        yield return new ToolCallArgsEvent(functionCall.CallId, argsJson);
                                    }
                                }
                            }

                            if (reasoningMessageStarted)
                            {
                                yield return new ReasoningMessageEndEvent(
                                    MessageId: assistantMessageId);
                                reasoningMessageStarted = false;
                            }
                        }
                        else
                        {
                            // STREAMING MODE: Emit immediately (existing behavior)
                            await foreach (var update in _middlewarePipeline.ExecuteModelCallStreamingAsync(
                                modelRequest,
                                (req) => _agentTurn.RunAsync(req.Messages, req.Options, req.Model as IChatClient, effectiveCancellationToken),
                                effectiveCancellationToken))
                            {
                                // Store update for building final history
                                responseUpdates.Add(update);

                                // Check for background operation continuation token (M.E.AI 10.1.1+ strongly-typed)
#pragma warning disable MEAI001 // Experimental API - Background Responses
                                var continuationToken = update.ContinuationToken;
                                if (continuationToken != null)
                                {
                                    lastContinuationToken = continuationToken;

                                    // Emit background operation started event on first token
                                    if (!backgroundOperationEventEmitted && allowBackgroundResponses)
                                    {
                                        backgroundOperationEventEmitted = true;
                                        yield return new BackgroundOperationStartedEvent(
                                            ContinuationToken: continuationToken,
                                            Status: OperationStatus.InProgress,
                                            OperationId: assistantMessageId);

                                        // Track in agent loop state for crash recovery
                                        state = state.WithBackgroundOperation(new BackgroundOperationInfo
                                        {
                                            TokenData = Convert.ToBase64String(continuationToken.ToBytes().Span),
                                            Iteration = state.Iteration,
                                            StartedAt = DateTimeOffset.UtcNow,
                                            LastKnownStatus = OperationStatus.InProgress
                                        });
                                    }
                                }
#pragma warning restore MEAI001

                                // Process contents and emit internal events
                                if (update.Contents != null)
                                {
                                    foreach (var content in update.Contents)
                                    {
                                        if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                                        {
                                            if (!reasoningMessageStarted)
                                            {
                                                yield return new ReasoningMessageStartEvent(
                                                    MessageId: assistantMessageId,
                                                    Role: "assistant");
                                                reasoningMessageStarted = true;
                                            }

                                            yield return new ReasoningDeltaEvent(
                                                Text: reasoning.Text,
                                                MessageId: assistantMessageId);
                                            assistantContents.Add(reasoning);
                                        }
                                        else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                                        {
                                            if (reasoningMessageStarted)
                                            {
                                                yield return new ReasoningMessageEndEvent(
                                                    MessageId: assistantMessageId);
                                                reasoningMessageStarted = false;
                                            }

                                            if (!messageStarted)
                                            {
                                                yield return new TextMessageStartEvent(assistantMessageId, "assistant");
                                                messageStarted = true;
                                            }

                                            assistantContents.Add(textContent);
                                            yield return new TextDeltaEvent(textContent.Text, assistantMessageId);
                                        }
                                        else if (content is FunctionCallContent functionCall)
                                        {
                                            if (!messageStarted)
                                            {
                                                yield return new TextMessageStartEvent(assistantMessageId, "assistant");
                                                messageStarted = true;
                                            }

                                            yield return new ToolCallStartEvent(
                                                functionCall.CallId,
                                                functionCall.Name ?? string.Empty,
                                                assistantMessageId);

                                            if (functionCall.Arguments != null && functionCall.Arguments.Count > 0)
                                            {
                                                var argsJson = JsonSerializer.Serialize(
                                                    functionCall.Arguments,
                                                     HPDJsonContext.Default.DictionaryStringObject);

                                                yield return new ToolCallArgsEvent(functionCall.CallId, argsJson);
                                            }

                                            toolRequests.Add(functionCall);
                                            assistantContents.Add(functionCall);
                                        }
                                    }
                                }

                                // Periodically yield Middleware events during LLM streaming
                                while (_eventCoordinator.TryRead(out var MiddlewareEvt))
                                {
                                    yield return (AgentEvent)MiddlewareEvt;
                                }

                                // Check for stream completion
                                if (update.FinishReason != null)
                                {
                                    if (reasoningMessageStarted)
                                    {
                                        yield return new ReasoningMessageEndEvent(
                                            MessageId: assistantMessageId);
                                        reasoningMessageStarted = false;
                                    }
                                }
                            }
                        }

                        // Capture ConversationId from the agent turn response and update session
                        if (_agentTurn.LastResponseConversationId != null)
                        {
                            if (session != null)
                            {
                                session.ConversationId = _agentTurn.LastResponseConversationId;
                            }
                        }
                        else if (state.InnerClientTracksHistory)
                        {
                            // Service stopped returning ConversationId - disable tracking
                            state = state.DisableHistoryTracking();
                        }
                    } // End of else block (LLM call not skipped)

                    // Clear background operation state when completed (token becomes null)
                    if (backgroundOperationEventEmitted && lastContinuationToken == null)
                    {
                        // Operation completed - clear the tracked background operation
                        state = state.WithBackgroundOperation(null);

                        // Emit completion status event
                        yield return new BackgroundOperationStatusEvent(
                            ContinuationToken: null!,  // null indicates completion
                            Status: OperationStatus.Completed,
                            StatusMessage: "Background operation completed successfully");
                    }

                    // Close the message if we started one (applies to both middleware and normal flow)
                    if (messageStarted)
                    {
                        yield return new TextMessageEndEvent(assistantMessageId);
                    }

                    // V2: Sync state after LLM call (middleware may have updated it)
                    state = agentContext.State;

                    // Check for early termination from BeforeIteration middleware (e.g., ContinuationPermissionMiddleware)
                    if (state.IsTerminated)
                    {
                        break;
                    }

                    // If there are tool requests, execute them immediately
                    if (toolRequests.Count > 0)
                    {
                        // Coalesce text content before creating the message
                        var coalescedContents = CoalesceTextContents(assistantContents);
                        
                        // Create assistant message with tool calls
                        var assistantMessage = new ChatMessage(ChatRole.Assistant, coalescedContents);

                        var currentMessages = state.CurrentMessages.ToList();
                        currentMessages.Add(assistantMessage);

                        // Update state immediately after modifying messages
                        state = state.WithMessages(currentMessages);

                        // Use messageCountToSend (actual messages sent to server)
                        if (_agentTurn.LastResponseConversationId != null)
                        {
                            state = state.EnableHistoryTracking(messageCountToSend);
                        }

                        // Create assistant message for history
                        // By default, exclude reasoning content to save tokens (configurable via PreserveReasoningInHistory)
                        var historyContents = Config?.PreserveReasoningInHistory == true
                            ? coalescedContents.ToList()
                            : coalescedContents.Where(c => c is not TextReasoningContent).ToList();

                        // Add to history if there's ANY content (text OR tool calls)
                        if (historyContents.Count > 0)
                        {
                            var historyMessage = new ChatMessage(ChatRole.Assistant, historyContents);
                            turnHistory.Add(historyMessage);
                        }

                        var effectiveOptionsForTools = beforeIterationContext.Options;

                        // Yield Middleware events before tool execution
                        while (_eventCoordinator.TryRead(out var MiddlewareEvt))
                        {
                            yield return (AgentEvent)MiddlewareEvt;
                        }

                        // UPDATE AGENT CONTEXT STATE before tool execution hook
                        agentContext.SyncState(state);

                        // EXECUTE BEFORE TOOL EXECUTION MIDDLEWARES (V2)
                        // Allows middlewares (e.g., circuit breaker) to inspect pending
                        // tool calls and prevent execution if needed.
                        var assistantResponse = new ChatMessage(ChatRole.Assistant, assistantContents);
                        var beforeToolContext = agentContext.AsBeforeToolExecution(
                            response: assistantResponse,
                            toolCalls: toolRequests.AsReadOnly(),
                            runOptions: effectiveRunOptions);

                        await _middlewarePipeline.ExecuteBeforeToolExecutionAsync(
                            beforeToolContext,
                            effectiveCancellationToken).ConfigureAwait(false);

                        // Drain events from middleware
                        while (_eventCoordinator.TryRead(out var middlewareEvt))
                        {
                            yield return (AgentEvent)middlewareEvt;
                        }

                        // V2: Sync state after middleware
                        state = agentContext.State;

                        // Check if middleware signaled to skip tool execution (e.g., circuit breaker)
                        if (beforeToolContext.SkipToolExecution)
                        {
                            // Check for termination
                            if (state.IsTerminated)
                            {
                                // Drain any final events from middleware (e.g., TextDeltaEvent from circuit breaker)
                                while (_eventCoordinator.TryRead(out var terminationEvt))
                                {
                                    yield return (AgentEvent)terminationEvt;
                                }
                                break; // Exit the main loop WITHOUT executing tools
                            }

                            // If not terminated, continue to next iteration without executing tools
                            continue;
                        }

                        // Execute tools with event polling (CRITICAL for permissions)
                        var executeTask = _functionCallProcessor.ExecuteToolsAsync(
                            currentMessages,
                            toolRequests,
                            effectiveOptionsForTools,
                            state,
                            effectiveRunOptions,
                            agentContext,
                            effectiveCancellationToken);

                        // Poll for Middleware events while tool execution is in progress
                        while (!executeTask.IsCompleted)
                        {
                            var delayTask = Task.Delay(10, effectiveCancellationToken);
                            await Task.WhenAny(executeTask, delayTask).ConfigureAwait(false);

                            while (_eventCoordinator.TryRead(out var MiddlewareEvt))
                            {
                                yield return (AgentEvent)MiddlewareEvt;
                            }
                        }

                        var executionResult = await executeTask.ConfigureAwait(false);

                        // Extract structured results from ToolExecutionResult
                        var toolResultMessage = executionResult.Message;
                        var successfulFunctions = executionResult.SuccessfulFunctions;

                        // Final drain
                        while (_eventCoordinator.TryRead(out var MiddlewareEvt))
                        {
                            yield return (AgentEvent)MiddlewareEvt;
                        }

                        // ═══════════════════════════════════════════════════════════════
                        // OUTPUT TOOL TERMINATION (structured output tool mode)
                        // When an output tool is called, terminate immediately.
                        // RunStructuredAsync captures the args and handles parsing.
                        // ═══════════════════════════════════════════════════════════════
                        if (executionResult.OutputToolCalled)
                        {
                            // Emit ToolCallEndEvent for output tools so RunStructuredAsync knows args are complete
                            foreach (var toolRequest in toolRequests)
                            {
                                if (_functionCallProcessor.IsOutputToolByName(toolRequest.Name, effectiveOptionsForTools?.Tools))
                                {
                                    yield return new ToolCallEndEvent(toolRequest.CallId);
                                }
                            }
                            state = state.Terminate("Output tool called - structured output complete");
                            break;
                        }

                        // SYNC STATE: Get any updates from middleware (e.g., error tracking)
                        // During tool execution, OnErrorAsync may have updated error counts
                        state = agentContext.State;

                        // EXECUTE AFTER ITERATION MIDDLEWARES (V2 - post-tool execution)
                        var afterIterationContext = agentContext.AsAfterIteration(
                            iteration: state.Iteration,
                            toolResults: toolResultMessage.Contents
                                .OfType<FunctionResultContent>()
                                .ToList()
                                .AsReadOnly(),
                            runOptions: effectiveRunOptions);

                        await _middlewarePipeline.ExecuteAfterIterationAsync(
                            afterIterationContext,
                            effectiveCancellationToken).ConfigureAwait(false);

                        // V2: Sync state after middleware (middleware may have updated state)
                        state = agentContext.State;

                        // Check if middleware signaled termination
                        if (state.IsTerminated)
                        {
                            // Drain any events emitted during termination (e.g., StateSnapshotEvent from ErrorTrackingMiddleware)
                            while (_eventCoordinator.TryRead(out var terminationEvt))
                            {
                                yield return (AgentEvent)terminationEvt;
                            }
                            break;
                        }

                        //
                        // PENDING WRITES (save successful function results immediately via DurableExecutionService)
                        //
                        if (_durableExecutionService != null &&
                            session != null &&
                            state.ETag != null)
                        {
                            _durableExecutionService.SavePendingWriteFireAndForget(
                                session.Id,
                                state.ETag,
                                toolResultMessage,
                                state.Iteration);
                        }
     
                        // UPDATE STATE WITH COMPLETED FUNCTIONS   
                        foreach (var functionName in successfulFunctions)
                        {
                            state = state.CompleteFunction(functionName);
                        }

                        // ALWAYS add unfiltered results to currentMessages (LLM needs to see container expansions)
                        currentMessages.Add(toolResultMessage);

                        // Add all results to turnHistory (middleware will filter ephemeral results in AfterMessageTurnAsync)
                        turnHistory.Add(toolResultMessage);
     
                        // EMIT TOOL RESULT EVENTS   
                        foreach (var content in toolResultMessage.Contents)
                        {
                            if (content is FunctionResultContent result)
                            {
                                yield return new ToolCallEndEvent(result.CallId);
                                yield return new ToolCallResultEvent(result.CallId, result.Result?.ToString() ?? "null");
                            }
                        }
                        // Update state with new messages
                        state = state.WithMessages(currentMessages);

                        // Build ChatResponse for decision engine (after execution)
                        lastResponse = new ChatResponse(currentMessages.Where(m => m.Role == ChatRole.Assistant).ToList());

                        // Clear responseUpdates after building the response
                        responseUpdates.Clear();
                    }
                    else
                    {
                        // No tools called - we're done
                        // SYNC STATE: Get any updates from middleware
                        state = agentContext.State;

                        // Call AfterIterationAsync with empty ToolResults for final iteration (V2)
                        var afterIterationContext = agentContext.AsAfterIteration(
                            iteration: state.Iteration,
                            toolResults: Array.Empty<FunctionResultContent>(),
                            runOptions: effectiveRunOptions);

                        await _middlewarePipeline.ExecuteAfterIterationAsync(
                            afterIterationContext,
                            effectiveCancellationToken).ConfigureAwait(false);

                        // V2: Sync state after middleware
                        state = agentContext.State;

                        var finalResponse = ConstructChatResponseFromUpdates(responseUpdates, Config?.PreserveReasoningInHistory ?? false);
                        lastResponse = finalResponse;

                        // Add final assistant message to turnHistory before clearing responseUpdates
                        // This ensures the assistant's response is persisted to the session
                        if (finalResponse.Messages.Count > 0)
                        {
                            var finalAssistantMessage = finalResponse.Messages[0];
                            if (finalAssistantMessage.Contents.Count > 0)
                            {
                                // Add to state.CurrentMessages
                                var currentMessages = state.CurrentMessages.ToList();
                                currentMessages.Add(finalAssistantMessage);
                                state = state.WithMessages(currentMessages);

                                // Add to turnHistory for session persistence
                                turnHistory.Add(finalAssistantMessage);
                            }
                        }

                        // Clear responseUpdates after constructing final response
                        responseUpdates.Clear();

                        // Update history tracking if we have ConversationId
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
                yield return new AgentTurnFinishedEvent(state.Iteration);

                // Check if middleware signaled termination (e.g., circuit breaker, error threshold)
                // This is a safety check in case the break statements inside nested blocks didn't exit properly
                if (state.IsTerminated)
                {
                    break;
                }

                // Advance to next iteration
                state = state.NextIteration();

                //
                // CHECKPOINT AFTER EACH ITERATION (via DurableExecutionService)
                //

                if (session != null && _durableExecutionService != null &&
                    _durableExecutionService.ShouldCheckpoint(state.Iteration, turnComplete: false))
                {
                    // Fire-and-forget async checkpoint via DurableExecutionService
                    var checkpointState = state;
                    var service = _durableExecutionService;

                    _ = Task.Run(async () =>
                    {
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            await service.SaveCheckpointAsync(session, checkpointState, CancellationToken.None);
                            stopwatch.Stop();

                            NotifyObservers(new CheckpointEvent(
                                Operation: CheckpointOperation.Saved,
                                SessionId: session.Id,
                                Timestamp: DateTimeOffset.UtcNow,
                                Duration: stopwatch.Elapsed,
                                Iteration: checkpointState.Iteration,
                                Success: true));
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();

                            NotifyObservers(new CheckpointEvent(
                                Operation: CheckpointOperation.Saved,
                                SessionId: session.Id,
                                Timestamp: DateTimeOffset.UtcNow,
                                Duration: stopwatch.Elapsed,
                                Iteration: checkpointState.Iteration,
                                Success: false,
                                ErrorMessage: ex.Message));
                        }
                    }, CancellationToken.None);
                }
            }

            if (responseUpdates.Any())
            {
                var finalResponse = ConstructChatResponseFromUpdates(responseUpdates, Config?.PreserveReasoningInHistory ?? false);
                if (finalResponse.Messages.Count > 0)
                {
                    var finalAssistantMessage = finalResponse.Messages[0];

                    if (finalAssistantMessage.Contents.Count > 0)
                    {
                        // Add final message to both state and turnHistory for consistency
                        var currentMessages = state.CurrentMessages.ToList();
                        currentMessages.Add(finalAssistantMessage);
                        state = state.WithMessages(currentMessages);

                        // Also add to turnHistory for session persistence
                        turnHistory.Add(finalAssistantMessage);
                    }
                }
            }

            // Final drain of middleware events after loop
            while (_eventCoordinator.TryRead(out var MiddlewareEvt))
                yield return (AgentEvent)MiddlewareEvt;

            // Emit MESSAGE TURN finished event
            turnStopwatch.Stop();
            yield return new MessageTurnFinishedEvent(
                messageTurnId,
                conversationId,
                _name,
                turnStopwatch.Elapsed);

            // Record orchestration telemetry metrics
            orchestrationActivity?.SetTag("agent.total_iterations", state.Iteration);
            orchestrationActivity?.SetTag("agent.total_function_calls", state.CompletedFunctions.Count);
            orchestrationActivity?.SetTag("agent.termination_reason", state.TerminationReason ?? "completed");
            orchestrationActivity?.SetTag("agent.was_terminated", state.IsTerminated);


            // Emit agent completion event
            yield return new AgentCompletionEvent(
                _name,
                state.Iteration,
                turnStopwatch.Elapsed,
                DateTimeOffset.UtcNow);
    
            // FINAL CHECKPOINT (PerTurn - via DurableExecutionService)
            if (session != null && _durableExecutionService != null)
            {
                var finalState = state;
                var service = _durableExecutionService;

                var checkpointTask = Task.Run(async () =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    bool success = false;
                    string? errorMessage = null;

                    try
                    {
                        await service.SaveCheckpointAsync(session, finalState, CancellationToken.None);
                        success = true;
                    }
                    catch (Exception ex)
                    {
                        errorMessage = ex.Message;
                    }
                    finally
                    {
                        stopwatch.Stop();

                        NotifyObservers(new CheckpointEvent(
                            Operation: CheckpointOperation.Saved,
                            SessionId: session.Id,
                            Timestamp: DateTimeOffset.UtcNow,
                            Duration: stopwatch.Elapsed,
                            Iteration: finalState.Iteration,
                            Success: success,
                            ErrorMessage: errorMessage));
                    }
                });

                await checkpointTask;
            }

            // MIDDLEWARE: AfterMessageTurnAsync (V2 - turn-level hook)
            // Update AgentContext with final state
            agentContext.SyncState(state);

            // Only call AfterMessageTurn if we have a response (may be null if terminated early)
            if (lastResponse != null)
            {
                // Create typed context for AfterMessageTurn
                var afterTurnContext = agentContext.AsAfterMessageTurn(
                    finalResponse: lastResponse,
                    turnHistory: turnHistory,
                    runOptions: effectiveRunOptions);

                // Execute AfterMessageTurnAsync in REVERSE order (stack unwinding)
                await _middlewarePipeline.ExecuteAfterMessageTurnAsync(afterTurnContext, effectiveCancellationToken);
            }

            // V2: Sync state after middleware
            state = agentContext.State;

            // Note: If AfterMessageTurn was called, middleware may have modified turnHistory
            // The turnHistory variable is passed by reference and may have been updated

            // Drain middleware events
            while (_eventCoordinator.TryRead(out var middlewareEvt))
                yield return (AgentEvent)middlewareEvt;

            // PERSISTENCE: Save complete turn history to session
            if (session != null && turnHistory.Count > 0)
            {
                try
                {
                    // Save ALL messages from this turn (user + assistant + tool)
                    // Input messages were added to turnHistory at the start of execution
                    // Middleware may have filtered this list (e.g., removed ephemeral container results)
                    await session.AddMessagesAsync(turnHistory, effectiveCancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Ignore errors - message persistence is not critical to execution
                }
            }

            // PERSISTENCE: Save persistent middleware state to session (cross-run caching)
            if (session != null)
            {
                try
                {
                    state.MiddlewareState.SaveToSession(session, _stateFactories);
                }
                catch (Exception)
                {
                    // Ignore errors - middleware state persistence is not critical to execution
                }

                // Clear ExecutionState on successful completion
                // ExecutionState is only for crash recovery - once we complete successfully,
                // it should be null so subsequent runs start fresh (not as a "resume")
                session.ExecutionState = null;
            }

            historyCompletionSource.TrySetResult(turnHistory);
        }
        finally
        {
            RootAgent = previousRootAgent;
        }
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
                    else if (content is TextContent)
                    {
                        allContents.Add(content);
                    }
                    // Include TextReasoningContent only if preserveReasoning is true (to save tokens by default)
                    else if (content is TextReasoningContent && preserveReasoning)
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

        // Coalesce consecutive TextContent items into a single TextContent
        // This prevents streaming chunks from being saved as separate content items
        var coalescedContents = CoalesceTextContents(allContents);

        // Don't create empty assistant messages
        if (coalescedContents.Count == 0)
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
        var chatMessage = new ChatMessage(ChatRole.Assistant, coalescedContents)
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

    /// <summary>
    /// Coalesces consecutive TextContent items into single TextContent items.
    /// Non-text content (FunctionCallContent, etc.) is preserved as-is and acts as a boundary.
    /// </summary>
    private static List<AIContent> CoalesceTextContents(List<AIContent> contents)
    {
        if (contents.Count <= 1)
            return contents;

        var result = new List<AIContent>();
        var textBuilder = new System.Text.StringBuilder();
        var reasoningBuilder = new System.Text.StringBuilder();

        foreach (var content in contents)
        {
            if (content is TextReasoningContent reasoningContent)
            {
                // Flush any accumulated regular text first
                if (textBuilder.Length > 0)
                {
                    result.Add(new TextContent(textBuilder.ToString()));
                    textBuilder.Clear();
                }
                // Accumulate reasoning content
                reasoningBuilder.Append(reasoningContent.Text);
            }
            else if (content is TextContent textContent)
            {
                // Flush any accumulated reasoning first
                if (reasoningBuilder.Length > 0)
                {
                    result.Add(new TextReasoningContent(reasoningBuilder.ToString()));
                    reasoningBuilder.Clear();
                }
                // Accumulate text content
                textBuilder.Append(textContent.Text);
            }
            else
            {
                // Non-text content - flush any accumulated text first
                if (textBuilder.Length > 0)
                {
                    result.Add(new TextContent(textBuilder.ToString()));
                    textBuilder.Clear();
                }
                if (reasoningBuilder.Length > 0)
                {
                    result.Add(new TextReasoningContent(reasoningBuilder.ToString()));
                    reasoningBuilder.Clear();
                }
                // Add the non-text content
                result.Add(content);
            }
        }

        // Flush any remaining text
        if (textBuilder.Length > 0)
        {
            result.Add(new TextContent(textBuilder.ToString()));
        }
        if (reasoningBuilder.Length > 0)
        {
            result.Add(new TextReasoningContent(reasoningBuilder.ToString()));
        }

        return result;
    }

    /// <inheritdoc />

    /// <inheritdoc />
    public void Dispose()
    {
        _baseClient?.Dispose();
        (_eventCoordinator as IDisposable)?.Dispose();
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
            Config?.MaxAgenticIterations ?? 10,
            availableTools);
    }

    #region Testing and Advanced API

    /// <summary>
    /// Runs the agentic loop and streams  agent events.
    /// The agent is stateless; all conversation state is managed externally or in session parameters.
    /// </summary>
    /// <param name="messages">The conversation messages</param>
    /// <param name="options">Chat options including tools</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of agent events</returns>
    public async IAsyncEnumerable<AgentEvent> RunAgenticLoopAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Prepare turn (stateless - no session)
        var inputMessages = messages.ToList();
        var turn = await _messageProcessor.PrepareTurnAsync(
            session: null,
            inputMessages,
            options,
            Name,
            cancellationToken);

        var turnHistory = new List<ChatMessage>();
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);

        await foreach (var evt in RunAgenticLoopInternal(
            turn,
            turnHistory,
            historyCompletionSource,
            session: null,
            initialContextProperties: null,
            cancellationToken: cancellationToken))
        {
            // 1. Event handlers FIRST (awaited, ordered) - for UI
            await ProcessEventHandlersAsync(evt, cancellationToken).ConfigureAwait(false);

            // 2. Yield event (for direct stream consumers)
            yield return evt;

            // 3. Observers LAST (fire-and-forget) - for telemetry
            NotifyObservers(evt);
        }
    }

    #endregion



    /// <summary>
    /// Creates a new conversation session.
    /// </summary>
    /// <returns>A new AgentSession instance</returns>
    public AgentSession CreateSession()
    {
        return new AgentSession();
    }


    //──────────────────────────────────────────────────────────────────
    // PRIMARY RUN API (Consolidated)
    //──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the agent with a string message.
    /// Convenience overload that wraps the message as a user ChatMessage.
    /// </summary>
    /// <param name="userMessage">The user's message text</param>
    /// <param name="session">Optional session containing conversation history</param>
    /// <param name="options">Optional per-invocation run options for customization</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of agent events</returns>
    /// <remarks>
    /// <para>
    /// <b>Provider Switching Priority:</b>
    /// 1. options.OverrideChatClient (highest - direct client override)
    /// 2. options.ProviderKey + options.ModelId (via registry)
    /// 3. Agent's default client (lowest)
    /// </para>
    /// <para>
    /// <b>Example:</b>
    /// <code>
    /// // Simple stateless call
    /// await agent.RunAsync("Hello");
    ///
    /// // With session
    /// var session = agent.CreateSession();
    /// await agent.RunAsync("Hello", session);
    ///
    /// // With options
    /// var options = new AgentRunOptions
    /// {
    ///     ProviderKey = "anthropic",
    ///     ModelId = "claude-opus",
    ///     Chat = new ChatRunOptions { Temperature = 0.7 }
    /// };
    /// await agent.RunAsync("Hello", session, options);
    /// </code>
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<AgentEvent> RunAsync(
        string userMessage,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(
            [new ChatMessage(ChatRole.User, userMessage)],
            session,
            options,
            cancellationToken);
    }

    /// <summary>
    /// Run the agent with options-only (UserMessage and/or Attachments from options).
    /// Enables content-first interactions: audio-only, image-only, document-only runs.
    /// </summary>
    /// <param name="options">Run options containing UserMessage and/or Attachments</param>
    /// <param name="session">Optional session for conversation continuity</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of agent events</returns>
    /// <exception cref="ArgumentException">Thrown if both UserMessage and Attachments are null/empty</exception>
    /// <remarks>
    /// <para>
    /// This overload enables clean DX for content-first interactions:
    /// - Voice assistants: User sends only audio
    /// - Vision apps: User sends only images
    /// - Document analysis: User sends only documents
    /// </para>
    /// <para>
    /// The middleware pipeline handles content transformation before the LLM call.
    /// For example, AudioPipelineMiddleware transcribes audio → text.
    /// </para>
    /// <para>
    /// <b>Example - Text + Attachments:</b>
    /// <code>
    /// await agent.RunAsync(new AgentRunOptions
    /// {
    ///     UserMessage = "Analyze this document",
    ///     Attachments = [await DocumentContent.FromFileAsync("report.pdf")]
    /// });
    /// </code>
    /// </para>
    /// <para>
    /// <b>Example - Audio Only:</b>
    /// <code>
    /// await agent.RunAsync(new AgentRunOptions
    /// {
    ///     Attachments = [new AudioContent(audioBytes)]
    /// });
    /// </code>
    /// </para>
    /// <para>
    /// <b>Example - Multiple Attachments:</b>
    /// <code>
    /// await agent.RunAsync(new AgentRunOptions
    /// {
    ///     Attachments = [
    ///         new ImageContent(screenshotBytes),
    ///         await DocumentContent.FromFileAsync("context.pdf")
    ///     ]
    /// });
    /// </code>
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunOptions options,
        AgentSession? session = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var contents = new List<AIContent>();

        if (!string.IsNullOrEmpty(options.UserMessage))
            contents.Add(new TextContent(options.UserMessage));

        if (options.Attachments?.Count > 0)
            contents.AddRange(options.Attachments);

        if (contents.Count == 0)
        {
            throw new ArgumentException(
                "AgentRunOptions must provide UserMessage or Attachments (at least one).",
                nameof(options));
        }

        var message = new ChatMessage(ChatRole.User, contents);
        return RunAsync([message], session, options, cancellationToken);
    }

    /// <summary>
    /// Runs the agent with messages. This is the core RunAsync implementation.
    /// All other RunAsync overloads delegate to this method.
    /// </summary>
    /// <param name="messages">Messages to process</param>
    /// <param name="session">Optional session containing conversation history. If null, runs stateless.</param>
    /// <param name="options">Optional per-invocation run options for customization</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of agent events</returns>
    /// <remarks>
    /// <para>
    /// <b>Session Behavior:</b>
    /// - If session is null: Runs stateless (no history persistence)
    /// - If session is provided: History is maintained across calls
    /// </para>
    /// <para>
    /// <b>Options:</b>
    /// Use <see cref="AgentRunOptions"/> for per-invocation customization:
    /// - Provider switching via ProviderKey/ModelId or OverrideChatClient
    /// - System instruction overrides
    /// - Chat parameters (temperature, tokens, etc.) via Chat property
    /// - Client tool configuration via ClientToolInput
    /// - Context overrides and runtime middleware
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Session-specific validation
        if (session != null)
        {
            var hasMessages = messages?.Any() ?? false;
            var hasCheckpoint = session.ExecutionState != null;
            var currentMessageCount = (await session.GetMessagesAsync(cancellationToken)).Count;
            var hasHistory = currentMessageCount > 0;

            if (!hasCheckpoint && !hasMessages && !hasHistory)
            {
                throw new InvalidOperationException(
                    "Cannot run agent with empty session and no messages.");
            }

            if (hasCheckpoint && hasMessages)
            {
                throw new InvalidOperationException(
                    "Cannot add new messages when resuming mid-execution.");
            }

            if (hasCheckpoint && !hasMessages)
            {
                session.ExecutionState?.ValidateConsistency(currentMessageCount);
            }
        }

        // Resolve chat options from AgentRunOptions and apply system instruction overrides
        var chatOptions = options?.Chat?.MergeWith(Config?.Provider?.DefaultChatOptions);
        chatOptions = ApplySystemInstructionOverrides(chatOptions, options);

        // Prepare turn
        var inputMessages = messages?.ToList() ?? new List<ChatMessage>();
        var turn = await _messageProcessor.PrepareTurnAsync(
            session,
            inputMessages,
            chatOptions,
            Name,
            cancellationToken);

        var turnHistory = new List<ChatMessage>();
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Build initial context properties from AgentRunOptions
        var initialProperties = BuildInitialContextProperties(options);

        // Execute agentic loop
        var internalStream = RunAgenticLoopInternal(
            turn,
            turnHistory,
            historyCompletionSource,
            session: session,
            initialContextProperties: initialProperties,
            runOptions: options,
            cancellationToken: cancellationToken);

        await foreach (var evt in internalStream.WithCancellation(cancellationToken))
        {
            // Custom streaming callback if provided
            if (options?.CustomStreamCallback != null)
            {
                await options.CustomStreamCallback(evt).ConfigureAwait(false);
            }

            // Standard event processing
            await ProcessEventHandlersAsync(evt, cancellationToken).ConfigureAwait(false);
            yield return evt;
            NotifyObservers(evt);
        }
    }

    #region Structured Output

    /// <summary>
    /// Runs the agent with structured output, yielding typed results.
    /// This is the primary implementation - all other overloads delegate to this.
    /// Preserves all bidirectional events (permissions, continuations, custom events).
    /// </summary>
    /// <typeparam name="T">The output type. Must be a reference type for JSON deserialization.</typeparam>
    /// <param name="messages">Messages to process</param>
    /// <param name="session">Optional session for conversation history</param>
    /// <param name="options">Per-invocation run options (includes StructuredOutput config)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of agent events including StructuredResultEvent&lt;T&gt;</returns>
    /// <remarks>
    /// <para><b>Generic Constraint:</b> The <c>where T : class</c> constraint is required because:</para>
    /// <list type="bullet">
    /// <item>JSON deserialization returns null for invalid input - structs can't be null</item>
    /// <item>Partial results may have uninitialized fields - reference types handle this gracefully</item>
    /// <item>Consistent with M.E.AI's structured output patterns</item>
    /// </list>
    /// <para>If you need to return a primitive or struct, wrap it in a class:</para>
    /// <code>public record CountResult(int Count);</code>
    /// </remarks>
    public async IAsyncEnumerable<AgentEvent> RunStructuredAsync<T>(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        // Ensure StructuredOutput options exist
        options ??= new AgentRunOptions();
        options.StructuredOutput ??= new StructuredOutputOptions();

        var structuredOpts = options.StructuredOutput;
        var schemaName = structuredOpts.SchemaName ?? typeof(T).Name;

        // Resolve serializer options (AOT-safe pattern)
        var serializerOptions = ResolveSerializerOptions(structuredOpts);

        // Configure ChatOptions based on mode
        ConfigureStructuredOutputOptions<T>(options, serializerOptions);

        // State for streaming
        var textAccumulator = new StringBuilder();
        var debounceStopwatch = Stopwatch.StartNew();
        string? lastPartialJson = null;  // Compare JSON strings, not object references
        string? outputToolCallId = null;  // Track output tool call ID for tool mode
        Type? matchedUnionType = null;   // Track which union type was matched (union mode only)

        // Determine the mode we're operating in
        var isToolMode = structuredOpts.Mode.Equals("tool", StringComparison.OrdinalIgnoreCase);
        var isUnionMode = structuredOpts.Mode.Equals("union", StringComparison.OrdinalIgnoreCase);
        var isNativeUnionMode = !isToolMode && !isUnionMode && structuredOpts.UnionTypes is { Length: > 0 };

        // Check if tool mode is using union types (merged union behavior)
        var isToolModeWithUnionTypes = isToolMode && structuredOpts.UnionTypes is { Length: > 0 };
        var outputToolName = structuredOpts.ToolName ?? $"return_{schemaName}";

        // Build set of output tool names for union mode OR tool mode with union types
        HashSet<string>? unionToolNames = null;
        Dictionary<string, Type>? unionToolTypeMap = null;
        if ((isUnionMode || isToolModeWithUnionTypes) && structuredOpts.UnionTypes != null)
        {
            unionToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            unionToolTypeMap = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var unionType in structuredOpts.UnionTypes)
            {
                var toolName = $"return_{unionType.Name}";
                unionToolNames.Add(toolName);
                unionToolTypeMap[toolName] = unionType;
            }
        }

        // Observability: Track metrics
        var messageId = Guid.NewGuid().ToString("N")[..12];
        var startTime = Stopwatch.GetTimestamp();
        var parseAttemptCount = 0;

        // Emit start event for observability
        var outputMode = isUnionMode ? "union" : (isToolMode ? "tool" : (isNativeUnionMode ? "native-union" : "native"));
        yield return new StructuredOutputStartEvent(
            MessageId: messageId,
            OutputTypeName: schemaName,
            OutputMode: outputMode);

        await foreach (var evt in RunAsync(messages, session, options, cancellationToken))
        {
            // ═══════════════════════════════════════════════════════════════
            // PASS-THROUGH: All bidirectional events (built-in + custom)
            // Uses interface check - supports PermissionRequestEvent,
            // ContinuationRequestEvent, ClarificationRequestEvent, and any
            // custom events implementing IBidirectionalEvent
            // ═══════════════════════════════════════════════════════════════
            if (evt is IBidirectionalAgentEvent)
            {
                yield return evt;
                continue;
            }

            // ═══════════════════════════════════════════════════════════════
            // TOOL MODE / UNION MODE: Capture output tool arguments
            // ═══════════════════════════════════════════════════════════════
            if (isToolMode || isUnionMode)
            {
                if (evt is ToolCallStartEvent toolStart)
                {
                    // Check if this is our output tool (tool mode) or a union output tool (union/tool+union mode)
                    if (isToolMode && !isToolModeWithUnionTypes && toolStart.Name == outputToolName)
                    {
                        // Single type tool mode: use the single return tool
                        outputToolCallId = toolStart.CallId;
                        continue;
                    }
                    else if ((isUnionMode || isToolModeWithUnionTypes) && unionToolNames!.Contains(toolStart.Name))
                    {
                        // Union mode or tool mode with union types: check against union tool names
                        outputToolCallId = toolStart.CallId;
                        matchedUnionType = unionToolTypeMap![toolStart.Name];
                        continue;
                    }
                }

                if (evt is ToolCallArgsEvent argsEvt && argsEvt.CallId == outputToolCallId)
                {
                    // Tool mode: Args arrive COMPLETE (M.E.AI accumulates internally)
                    // No streaming partials possible - just store for final parsing
                    textAccumulator.Clear();
                    textAccumulator.Append(argsEvt.ArgsJson);
                    continue;
                }

                if (evt is ToolCallEndEvent toolEnd && toolEnd.CallId == outputToolCallId)
                {
                    // Final parse for tool/union mode
                    // Note: Event draining is handled by RunAsync() - no need to drain here
                    var finalJson = textAccumulator.ToString();
                    var elapsed = Stopwatch.GetElapsedTime(startTime);
                    var resultTypeName = (isUnionMode || isToolModeWithUnionTypes) && matchedUnionType != null
                        ? matchedUnionType.Name
                        : schemaName;

                    // Emit observability complete event
                    yield return new StructuredOutputCompleteEvent(
                        MessageId: messageId,
                        OutputTypeName: resultTypeName,
                        TotalParseAttempts: parseAttemptCount,
                        FinalJsonLength: finalJson.Length,
                        Duration: elapsed);

                    if ((isUnionMode || isToolModeWithUnionTypes) && matchedUnionType != null)
                    {
                        // Union mode or tool mode with union types: deserialize to the specific union type, then cast to T
                        yield return EmitUnionResult<T>(finalJson, matchedUnionType, serializerOptions);
                    }
                    else
                    {
                        yield return EmitFinalResult<T>(finalJson, schemaName, serializerOptions);
                    }
                    yield break;
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // NATIVE MODE: Accumulate text deltas
            // ═══════════════════════════════════════════════════════════════
            if (evt is TextDeltaEvent delta)
            {
                textAccumulator.Append(delta.Text);

                // Debounced partial parsing
                if (structuredOpts.StreamPartials &&
                    debounceStopwatch.ElapsedMilliseconds >= structuredOpts.PartialDebounceMs)
                {
                    if (TryParsePartial<T>(textAccumulator.ToString(), serializerOptions, out var partial, out var closedJson) &&
                        closedJson != lastPartialJson)
                    {
                        lastPartialJson = closedJson;
                        debounceStopwatch.Restart();
                        parseAttemptCount++;

                        // Emit observability event for partial parse
                        yield return new StructuredOutputPartialEvent(
                            MessageId: messageId,
                            OutputTypeName: schemaName,
                            ParseAttempt: parseAttemptCount,
                            AccumulatedJsonLength: textAccumulator.Length);

                        yield return new StructuredResultEvent<T>(partial, IsPartial: true, closedJson);
                    }
                }
                continue;
            }

            // ═══════════════════════════════════════════════════════════════
            // NATIVE MODE STREAM END: Final validation
            // In tool/union mode, ignore TextMessageEndEvent - we wait for output tool
            // ═══════════════════════════════════════════════════════════════
            if (evt is TextMessageEndEvent && !isToolMode && !isUnionMode)
            {
                // Note: Event draining is handled by RunAsync() - no need to drain here
                var finalJson = textAccumulator.ToString();
                var elapsed = Stopwatch.GetElapsedTime(startTime);

                // Emit observability complete event
                yield return new StructuredOutputCompleteEvent(
                    MessageId: messageId,
                    OutputTypeName: schemaName,
                    TotalParseAttempts: parseAttemptCount,
                    FinalJsonLength: finalJson.Length,
                    Duration: elapsed);

                // Use appropriate emitter based on whether this is native union mode
                if (isNativeUnionMode)
                {
                    yield return EmitNativeUnionResult<T>(finalJson, structuredOpts.UnionTypes!, serializerOptions);
                }
                else
                {
                    yield return EmitFinalResult<T>(finalJson, schemaName, serializerOptions);
                }
                yield break;
            }

            // Pass through other events (observability, etc.)
            yield return evt;
        }

        // Stream ended without explicit end event - try to parse what we have
        // Only for native mode - tool/union mode must receive an output tool call
        if (textAccumulator.Length > 0 && !isToolMode && !isUnionMode)
        {
            var finalJson = textAccumulator.ToString();
            var elapsed = Stopwatch.GetElapsedTime(startTime);

            // Emit observability complete event
            yield return new StructuredOutputCompleteEvent(
                MessageId: messageId,
                OutputTypeName: schemaName,
                TotalParseAttempts: parseAttemptCount,
                FinalJsonLength: finalJson.Length,
                Duration: elapsed);

            // Use appropriate emitter based on whether this is native union mode
            if (isNativeUnionMode)
            {
                yield return EmitNativeUnionResult<T>(finalJson, structuredOpts.UnionTypes!, serializerOptions);
            }
            else
            {
                yield return EmitFinalResult<T>(finalJson, schemaName, serializerOptions);
            }
        }
    }

    /// <summary>
    /// Convenience overload: Runs structured output from a string message.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> RunStructuredAsync<T>(
        string userMessage,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
        => RunStructuredAsync<T>(
            new[] { new ChatMessage(ChatRole.User, userMessage) },
            session, options, cancellationToken);

    // ═══════════════════════════════════════════════════════════════
    // STRUCTURED OUTPUT PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves serializer options, ensuring they're ready for GetTypeInfo().
    /// Falls back to AIJsonUtilities.DefaultOptions if not specified.
    /// </summary>
    private static JsonSerializerOptions ResolveSerializerOptions(StructuredOutputOptions opts)
    {
        var options = opts.SerializerOptions ?? AIJsonUtilities.DefaultOptions;
        options.MakeReadOnly(); // Required before GetTypeInfo()
        return options;
    }

    private void ConfigureStructuredOutputOptions<T>(
        AgentRunOptions options,
        JsonSerializerOptions serializerOptions) where T : class
    {
        options.Chat ??= new ChatRunOptions();
        var chatOptions = options.Chat;
        var structuredOpts = options.StructuredOutput!;
        var schemaName = structuredOpts.SchemaName ?? typeof(T).Name;
        var schemaDesc = structuredOpts.SchemaDescription ?? $"Response of type {schemaName}";

        if (structuredOpts.Mode.Equals("tool", StringComparison.OrdinalIgnoreCase))
        {
            // Tool mode: Create output tool(s) and add to RuntimeTools
            // RuntimeTools are merged with agent's configured tools in RunAsync
            options.RuntimeTools ??= new List<AITool>();

            if (structuredOpts.UnionTypes is { Length: > 0 })
            {
                // Multiple types specified → create one tool per type (union behavior)
                // LLM chooses which type to return by calling the corresponding tool
                foreach (var unionType in structuredOpts.UnionTypes)
                {
                    var tool = CreateOutputToolForType(unionType, serializerOptions);
                    options.RuntimeTools.Add(tool);
                }
            }
            else
            {
                // Single type from generic parameter T
                var outputTool = CreateOutputTool<T>(structuredOpts, serializerOptions);
                options.RuntimeTools.Add(outputTool);
            }

            // Force LLM to call one of the return tools (provider-enforced, not prompt-based)
            // This ensures the LLM cannot output free text - it MUST call a return tool
            options.RuntimeToolMode = ChatToolMode.RequireAny;
        }
        else if (structuredOpts.Mode.Equals("union", StringComparison.OrdinalIgnoreCase))
        {
            // DEPRECATED: "union" mode is now just "tool" mode with UnionTypes set
            // Kept for backward compatibility - redirect to tool mode logic
            if (structuredOpts.UnionTypes == null || structuredOpts.UnionTypes.Length == 0)
            {
                throw new InvalidOperationException(
                    "Union mode requires UnionTypes to be specified with at least one type. " +
                    "Consider using Mode='tool' with UnionTypes instead.");
            }

            options.RuntimeTools ??= new List<AITool>();
            foreach (var unionType in structuredOpts.UnionTypes)
            {
                var tool = CreateOutputToolForType(unionType, serializerOptions);
                options.RuntimeTools.Add(tool);
            }

            options.RuntimeToolMode = ChatToolMode.RequireAny;
        }
        else
        {
            // Native mode: Set response format with JSON schema
            if (structuredOpts.UnionTypes is { Length: > 0 })
            {
                // Native union mode: Create anyOf schema combining all union types
                // Provider enforces schema validation, supports streaming partials
                var anyOfSchema = CreateAnyOfSchema(
                    structuredOpts.UnionTypes,
                    schemaName,
                    schemaDesc,
                    serializerOptions);

                chatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                    anyOfSchema,
                    schemaName: schemaName,
                    schemaDescription: schemaDesc);
            }
            else
            {
                // Single type native mode: Use provided serializerOptions for consistent schema generation
                chatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema<T>(
                    serializerOptions,
                    schemaName: schemaName,
                    schemaDescription: schemaDesc);
            }
        }
    }

    /// <summary>
    /// Creates an output tool using HPDAIFunctionFactory.
    /// Output tools are never executed - calling one terminates the agent run
    /// and the arguments ARE the structured output.
    /// </summary>
    private AIFunction CreateOutputTool<T>(
        StructuredOutputOptions options,
        JsonSerializerOptions serializerOptions) where T : class
    {
        var schemaName = options.SchemaName ?? typeof(T).Name;
        var toolName = options.ToolName ?? $"return_{schemaName}";
        var description = options.SchemaDescription ?? $"Submit the final {schemaName} result";

        // Use HPDAIFunctionFactory - our existing factory that supports AdditionalProperties
        return HPDAIFunctionFactory.Create(
            invocation: (_, _) => Task.FromResult<object?>(null), // Output tools never execute
            options: new HPDAIFunctionFactoryOptions
            {
                Name = toolName,
                Description = description,
                SchemaProvider = () => AIJsonUtilities.CreateJsonSchema(
                    typeof(T),
                    description: description,
                    serializerOptions: serializerOptions), // Use provided options for AOT compatibility
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["Kind"] = "Output",
                    ["OutputType"] = typeof(T).FullName
                }
            });
    }

    /// <summary>
    /// Creates an output tool for a specific type (used in union mode).
    /// Non-generic version that accepts a Type parameter.
    /// </summary>
    private AIFunction CreateOutputToolForType(
        Type outputType,
        JsonSerializerOptions serializerOptions)
    {
        var typeName = outputType.Name;
        var toolName = $"return_{typeName}";
        var description = $"Submit a {typeName} result";

        return HPDAIFunctionFactory.Create(
            invocation: (_, _) => Task.FromResult<object?>(null), // Output tools never execute
            options: new HPDAIFunctionFactoryOptions
            {
                Name = toolName,
                Description = description,
                SchemaProvider = () => AIJsonUtilities.CreateJsonSchema(
                    outputType,
                    description: description,
                    serializerOptions: serializerOptions),
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["Kind"] = "Output",
                    ["OutputType"] = outputType.FullName
                }
            });
    }

    /// <summary>
    /// Attempts to parse partial JSON into a typed result.
    /// Uses AOT-safe GetTypeInfo() pattern for deserialization.
    /// Returns the closed JSON string for deduplication (comparing JSON, not object references).
    /// </summary>
    private static bool TryParsePartial<T>(
        string json,
        JsonSerializerOptions serializerOptions,
        [NotNullWhen(true)] out T? result,
        [NotNullWhen(true)] out string? closedJson) where T : class
    {
        result = default;
        closedJson = null;

        var closed = PartialJsonCloser.TryClose(json);
        if (closed == null)
            return false;

        try
        {
            // AOT-safe: Use GetTypeInfo() instead of generic Deserialize<T>()
            var typeInfo = (JsonTypeInfo<T>)serializerOptions.GetTypeInfo(typeof(T));
            result = JsonSerializer.Deserialize(closed, typeInfo);
            if (result != null)
            {
                closedJson = closed;
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AgentEvent EmitFinalResult<T>(
        string rawJson,
        string typeName,
        JsonSerializerOptions serializerOptions) where T : class
    {
        // Strip markdown fences if present
        var json = StripMarkdownFences(rawJson);

        try
        {
            // AOT-safe: Use GetTypeInfo() instead of generic Deserialize<T>()
            var typeInfo = (JsonTypeInfo<T>)serializerOptions.GetTypeInfo(typeof(T));
            var result = JsonSerializer.Deserialize(json, typeInfo);

            if (result == null)
            {
                return new StructuredOutputErrorEvent(
                    json,
                    "Deserialization returned null",
                    typeName);
            }
            else
            {
                return new StructuredResultEvent<T>(result, IsPartial: false, json);
            }
        }
        catch (JsonException ex)
        {
            return new StructuredOutputErrorEvent(
                json,
                ex.Message,
                typeName,
                ex);
        }
    }

    /// <summary>
    /// Emits a structured result for union mode.
    /// Deserializes to the specific union type, then casts to the base type T.
    /// </summary>
    private static AgentEvent EmitUnionResult<T>(
        string rawJson,
        Type unionType,
        JsonSerializerOptions serializerOptions) where T : class
    {
        // Strip markdown fences if present
        var json = StripMarkdownFences(rawJson);
        var typeName = unionType.Name;

        try
        {
            // Non-generic deserialization for the specific union type
            var typeInfo = serializerOptions.GetTypeInfo(unionType);
            var result = JsonSerializer.Deserialize(json, typeInfo);

            if (result == null)
            {
                return new StructuredOutputErrorEvent(
                    json,
                    "Deserialization returned null",
                    typeName);
            }

            // Cast to T (the base type)
            if (result is T typedResult)
            {
                return new StructuredResultEvent<T>(typedResult, IsPartial: false, json);
            }
            else
            {
                return new StructuredOutputErrorEvent(
                    json,
                    $"Result of type {result.GetType().Name} is not assignable to {typeof(T).Name}",
                    typeName);
            }
        }
        catch (JsonException ex)
        {
            return new StructuredOutputErrorEvent(
                json,
                ex.Message,
                typeName,
                ex);
        }
    }

    private static string StripMarkdownFences(string json)
    {
        var span = json.AsSpan().Trim();

        if (span.StartsWith("```"))
        {
            var newlineIdx = span.IndexOf('\n');
            if (newlineIdx > 0)
                span = span[(newlineIdx + 1)..];

            if (span.EndsWith("```"))
                span = span[..^3].Trim();
        }

        return span.ToString();
    }

    /// <summary>
    /// Creates an anyOf JSON schema combining multiple union types.
    /// Used for native mode union support where the provider enforces schema validation.
    /// </summary>
    private static JsonElement CreateAnyOfSchema(
        Type[] unionTypes,
        string schemaName,
        string schemaDescription,
        JsonSerializerOptions serializerOptions)
    {
        var anyOfSchemas = new JsonArray();

        foreach (var unionType in unionTypes)
        {
            // Generate schema for each union type
            var typeSchema = AIJsonUtilities.CreateJsonSchema(
                unionType,
                description: unionType.Name,
                serializerOptions: serializerOptions);

            anyOfSchemas.Add(typeSchema);
        }

        // Create combined schema with anyOf
        var combinedSchema = new JsonObject
        {
            ["title"] = schemaName,
            ["description"] = schemaDescription,
            ["anyOf"] = anyOfSchemas
        };

        return JsonSerializer.SerializeToElement(combinedSchema);
    }

    /// <summary>
    /// Tries to detect which union type matches the given JSON by attempting deserialization.
    /// Returns the first type that successfully deserializes and is assignable to T.
    /// </summary>
    private static (T? result, Type? matchedType) TryDeserializeUnionType<T>(
        string json,
        Type[] unionTypes,
        JsonSerializerOptions serializerOptions) where T : class
    {
        foreach (var unionType in unionTypes)
        {
            try
            {
                var typeInfo = serializerOptions.GetTypeInfo(unionType);
                var result = JsonSerializer.Deserialize(json, typeInfo);

                if (result is T typedResult)
                {
                    return (typedResult, unionType);
                }
            }
            catch (JsonException)
            {
                // This type doesn't match, try next
                continue;
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Emits a structured result for native union mode.
    /// Tries to deserialize to each union type until one matches.
    /// </summary>
    private static AgentEvent EmitNativeUnionResult<T>(
        string rawJson,
        Type[] unionTypes,
        JsonSerializerOptions serializerOptions) where T : class
    {
        var json = StripMarkdownFences(rawJson);

        var (result, matchedType) = TryDeserializeUnionType<T>(json, unionTypes, serializerOptions);

        if (result != null && matchedType != null)
        {
            return new StructuredResultEvent<T>(result, IsPartial: false, json);
        }

        // No type matched
        var typeNames = string.Join(", ", unionTypes.Select(t => t.Name));
        return new StructuredOutputErrorEvent(
            json,
            $"JSON did not match any union type. Expected one of: {typeNames}",
            "UnionType");
    }

    #endregion

    /// <summary>
    /// Builds initial context properties dictionary from AgentRunOptions.
    /// Merges ClientToolInput and ContextOverrides into a single dictionary.
    /// </summary>
    private static Dictionary<string, object>? BuildInitialContextProperties(AgentRunOptions? options)
    {
        if (options == null)
            return null;

        Dictionary<string, object>? properties = null;

        // Add ClientToolInput if present
        if (options.ClientToolInput != null)
        {
            properties ??= new Dictionary<string, object>();
            properties["AgentRunInput"] = options.ClientToolInput;
        }

        // Add AgentRunOptions itself for middleware access
        properties ??= new Dictionary<string, object>();
        properties["AgentRunOptions"] = options;

        // Merge context overrides
        if (options.ContextOverrides != null)
        {
            properties ??= new Dictionary<string, object>();
            foreach (var kvp in options.ContextOverrides)
            {
                properties[kvp.Key] = kvp.Value;
            }
        }

        return properties;
    }

    /// <summary>
    /// Resolves the effective chat client for this run based on AgentRunOptions.
    /// Priority: OverrideChatClient > ProviderKey/ModelId > null (use default)
    /// </summary>
    /// <param name="options">Per-invocation options</param>
    /// <returns>Override client if specified, null to use default</returns>
    private IChatClient? ResolveClientForOptions(AgentRunOptions? options)
    {
        // Direct override: highest priority (for C# power users)
        if (options?.OverrideChatClient != null)
            return options.OverrideChatClient;

        // ProviderKey/ModelId: use registry to create client
        if (!string.IsNullOrEmpty(options?.ProviderKey))
        {
            if (_providerRegistry == null)
            {
                throw new InvalidOperationException(
                    $"Cannot switch to provider '{options.ProviderKey}' - no provider registry available. " +
                    "Ensure the agent was built with a provider registry.");
            }

            var provider = _providerRegistry.GetProvider(options.ProviderKey);
            if (provider == null)
            {
                throw new InvalidOperationException(
                    $"Provider '{options.ProviderKey}' is not registered. " +
                    $"Available providers: {string.Join(", ", _providerRegistry.GetRegisteredProviders())}");
            }

            // Build provider config for the new client
            var providerConfig = new ProviderConfig
            {
                ProviderKey = options.ProviderKey,
                // Use specified ModelId, or fall back to agent's configured model, or provider default
                ModelName = options.ModelId
                    ?? Config?.Provider?.ModelName
                    ?? "default",
                // Inherit API key and endpoint from agent's config if switching within same provider
                ApiKey = Config?.Provider?.ProviderKey == options.ProviderKey
                    ? Config?.Provider?.ApiKey
                    : null,
                Endpoint = Config?.Provider?.ProviderKey == options.ProviderKey
                    ? Config?.Provider?.Endpoint
                    : null,
                // Inherit default chat options from agent config
                DefaultChatOptions = Config?.Provider?.DefaultChatOptions
            };

            return provider.CreateChatClient(providerConfig, _serviceProvider);
        }

        // No override specified
        return null;
    }

    /// <summary>
    /// Resolves system instructions considering AgentRunOptions overrides.
    /// Priority: AgentRunOptions.SystemInstructions > Config.SystemInstructions
    /// If AdditionalSystemInstructions is set, it's appended.
    /// </summary>
    /// <param name="options">Per-invocation options</param>
    /// <returns>Resolved system instructions</returns>
    private string? ResolveSystemInstructions(AgentRunOptions? options)
    {
        // Use override if provided, otherwise fall back to config
        var instructions = options?.SystemInstructions
            ?? Config?.SystemInstructions
            ?? _messageProcessor.SystemInstructions;

        // Append additional instructions if provided
        if (!string.IsNullOrEmpty(options?.AdditionalSystemInstructions))
        {
            instructions = string.IsNullOrEmpty(instructions)
                ? options.AdditionalSystemInstructions
                : $"{instructions}\n\n{options.AdditionalSystemInstructions}";
        }

        return instructions;
    }

    /// <summary>
    /// Applies system instruction overrides from AgentRunOptions to ChatOptions.
    /// Creates a new ChatOptions instance with the resolved instructions.
    /// </summary>
    /// <param name="chatOptions">Base chat options (can be null)</param>
    /// <param name="runOptions">Per-invocation options</param>
    /// <returns>ChatOptions with resolved system instructions</returns>
    private ChatOptions? ApplySystemInstructionOverrides(ChatOptions? chatOptions, AgentRunOptions? runOptions)
    {
        // If no overrides, return as-is
        if (runOptions == null ||
            (string.IsNullOrEmpty(runOptions.SystemInstructions) &&
             string.IsNullOrEmpty(runOptions.AdditionalSystemInstructions)))
        {
            return chatOptions;
        }

        var resolvedInstructions = ResolveSystemInstructions(runOptions);
        if (string.IsNullOrEmpty(resolvedInstructions))
            return chatOptions;

        // Create new options with resolved instructions
        chatOptions ??= new ChatOptions();

        // Clone options to avoid mutating shared instances
        var newOptions = new ChatOptions
        {
            Temperature = chatOptions.Temperature,
            TopP = chatOptions.TopP,
            TopK = chatOptions.TopK,
            MaxOutputTokens = chatOptions.MaxOutputTokens,
            FrequencyPenalty = chatOptions.FrequencyPenalty,
            PresencePenalty = chatOptions.PresencePenalty,
            ModelId = chatOptions.ModelId,
            StopSequences = chatOptions.StopSequences,
            Seed = chatOptions.Seed,
            ResponseFormat = chatOptions.ResponseFormat,
            Tools = chatOptions.Tools,
            ToolMode = chatOptions.ToolMode,
            // Override instructions with resolved value
            Instructions = resolvedInstructions
        };

        // Copy additional properties
        if (chatOptions.AdditionalProperties?.Count > 0)
        {
            newOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            foreach (var kvp in chatOptions.AdditionalProperties)
            {
                newOptions.AdditionalProperties[kvp.Key] = kvp.Value;
            }
        }

        return newOptions;
    }

    /// <summary>
    /// Validates background responses usage and logs warnings for common mistakes.
    /// Philosophy: "Let it flow" - warn but don't block, allow graceful degradation.
    /// </summary>
    /// <param name="runOptions">Per-run options</param>
    /// <param name="allowBackgroundResponses">Resolved background responses setting</param>
    /// <param name="messageCount">Number of input messages</param>
    private void ValidateBackgroundResponsesUsage(
        AgentRunOptions? runOptions,
        bool allowBackgroundResponses,
        int messageCount)
    {
        // Skip validation if no background-related settings are used
        if (!allowBackgroundResponses && runOptions?.ContinuationToken == null)
            return;

        // Warning 1: Messages provided with ContinuationToken (messages will be ignored)
        if (runOptions?.ContinuationToken != null && messageCount > 0)
        {
            _observerErrorLogger?.LogWarning(
                "Background responses: Messages provided with ContinuationToken will be ignored during polling. " +
                "When polling with a token, only the token is used - messages are not sent to the provider.");
        }

        // Warning 2: ContinuationToken provided without AllowBackgroundResponses explicitly set
        // This might indicate the user doesn't realize they're in polling mode
        if (runOptions?.ContinuationToken != null && runOptions.AllowBackgroundResponses != true)
        {
            _observerErrorLogger?.LogInformation(
                "Background responses: ContinuationToken provided without AllowBackgroundResponses=true. " +
                "Token will be used for polling, but consider explicitly enabling background responses.");
        }

        // Warning 3: AutoPollToCompletion enabled with manual ContinuationToken
        // Auto-poll handles polling automatically - manual token might cause confusion
        if (Config?.BackgroundResponses?.AutoPollToCompletion == true && runOptions?.ContinuationToken != null)
        {
            _observerErrorLogger?.LogWarning(
                "Background responses: Manual ContinuationToken provided with AutoPollToCompletion enabled. " +
                "Auto-poll mode handles polling automatically. Manual token usage may cause unexpected behavior.");
        }
    }

    /// <summary>
    /// Applies background responses settings to ChatOptions.
    /// Sets AllowBackgroundResponses and ContinuationToken for M.E.AI providers.
    /// </summary>
    /// <param name="chatOptions">Base chat options (can be null)</param>
    /// <param name="allowBackground">Whether to allow background responses</param>
    /// <param name="continuationToken">Continuation token for polling/resumption</param>
    /// <returns>ChatOptions with background responses settings applied</returns>
#pragma warning disable MEAI001 // Experimental API - Background Responses
    private static ChatOptions ApplyBackgroundResponsesOptions(
        ChatOptions? chatOptions,
        bool allowBackground,
        ResponseContinuationToken? continuationToken)
    {
        // Create new options or clone existing to avoid mutation
        var newOptions = chatOptions != null
            ? new ChatOptions
            {
                Temperature = chatOptions.Temperature,
                TopP = chatOptions.TopP,
                TopK = chatOptions.TopK,
                MaxOutputTokens = chatOptions.MaxOutputTokens,
                FrequencyPenalty = chatOptions.FrequencyPenalty,
                PresencePenalty = chatOptions.PresencePenalty,
                ModelId = chatOptions.ModelId,
                StopSequences = chatOptions.StopSequences,
                Seed = chatOptions.Seed,
                ResponseFormat = chatOptions.ResponseFormat,
                Tools = chatOptions.Tools,
                ToolMode = chatOptions.ToolMode,
                Instructions = chatOptions.Instructions
            }
            : new ChatOptions();

        // Copy additional properties from base options
        if (chatOptions?.AdditionalProperties?.Count > 0)
        {
            newOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            foreach (var kvp in chatOptions.AdditionalProperties)
            {
                newOptions.AdditionalProperties[kvp.Key] = kvp.Value;
            }
        }

        // Apply background responses settings
        newOptions.AllowBackgroundResponses = allowBackground;
        newOptions.ContinuationToken = continuationToken;

        return newOptions;
    }
#pragma warning restore MEAI001

    //──────────────────────────────────────────────────────────────────
    // AUTO-POLL BACKGROUND RESPONSES SUPPORT
    //──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the agent with automatic polling for background operations.
    /// When enabled, this method internally uses background mode + polling to complete long-running operations,
    /// providing HTTP timeout resilience without changing caller code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is useful for scenarios where:
    /// - HTTP gateways have timeout limits (30-60s)
    /// - Serverless functions have execution limits
    /// - You want transparent timeout resilience
    /// </para>
    /// <para>
    /// Configuration is controlled by:
    /// - <see cref="BackgroundResponsesConfig.AutoPollToCompletion"/> - Enables auto-polling
    /// - <see cref="BackgroundResponsesConfig.DefaultPollingInterval"/> - Interval between polls
    /// - <see cref="BackgroundResponsesConfig.DefaultTimeout"/> - Maximum wait time
    /// - <see cref="BackgroundResponsesConfig.MaxPollAttempts"/> - Maximum poll attempts
    /// </para>
    /// </remarks>
    /// <param name="messages">Messages to process</param>
    /// <param name="session">Session containing conversation history</param>
    /// <param name="options">Optional per-invocation run options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of internal agent events</returns>
    public async IAsyncEnumerable<AgentEvent> RunWithAutoPollAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession session,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var config = Config?.BackgroundResponses;
        var autoPoll = config?.AutoPollToCompletion ?? false;

        if (!autoPoll)
        {
            // Auto-poll not enabled, just run normally
            await foreach (var evt in RunAsync(messages, session, options, cancellationToken))
            {
                yield return evt;
            }
            yield break;
        }

        // Auto-poll mode: Enable background responses and poll until completion
        options ??= new AgentRunOptions();
        options.AllowBackgroundResponses = true;

        var pollInterval = options.BackgroundPollingInterval ?? config!.DefaultPollingInterval;
        var timeout = options.BackgroundTimeout ?? config!.DefaultTimeout;
        var maxAttempts = config!.MaxPollAttempts;

        ResponseContinuationToken? lastToken = null;
        var startTime = DateTimeOffset.UtcNow;
        var attempts = 0;
        var isFirstRun = true;

        while (true)
        {
            // Check timeout
            if (timeout.HasValue && DateTimeOffset.UtcNow - startTime > timeout.Value)
            {
                yield return new BackgroundOperationStatusEvent(
                    ContinuationToken: lastToken!,
                    Status: OperationStatus.Failed,
                    StatusMessage: $"Background operation timed out after {timeout.Value}");
                yield break;
            }

            // Check max attempts (only after first run)
            if (!isFirstRun && attempts >= maxAttempts)
            {
                yield return new BackgroundOperationStatusEvent(
                    ContinuationToken: lastToken!,
                    Status: OperationStatus.Failed,
                    StatusMessage: $"Background operation exceeded max poll attempts ({maxAttempts})");
                yield break;
            }

            // Set continuation token for polling (not on first run)
            if (!isFirstRun && lastToken != null)
            {
                options.ContinuationToken = lastToken;
                attempts++;

                // Emit polling status event
                yield return new BackgroundOperationStatusEvent(
                    ContinuationToken: lastToken,
                    Status: OperationStatus.InProgress,
                    StatusMessage: $"Polling attempt {attempts}/{maxAttempts}");
            }

            // Run the agent
            var messagesForRun = isFirstRun ? messages : Enumerable.Empty<ChatMessage>();
            lastToken = null;

            await foreach (var evt in RunAsync(messagesForRun, session, options, cancellationToken))
            {
                yield return evt;

                // Capture continuation token from events
                if (evt is BackgroundOperationStartedEvent started)
                {
                    lastToken = started.ContinuationToken;
                }
                else if (evt is BackgroundOperationStatusEvent status && status.ContinuationToken != null)
                {
                    lastToken = status.ContinuationToken;
                }
            }

            isFirstRun = false;

            // If no token, operation completed
            if (lastToken == null)
            {
                yield break;
            }

            // Wait before next poll
            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Runs the agent with automatic polling for background operations (string message overload).
    /// </summary>
    public IAsyncEnumerable<AgentEvent> RunWithAutoPollAsync(
        string userMessage,
        AgentSession session,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return RunWithAutoPollAsync(
            [new ChatMessage(ChatRole.User, userMessage)],
            session,
            options,
            cancellationToken);
    }

    //──────────────────────────────────────────────────────────────────
    // SESSION-BASED API (New simplified API)
    //──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the agent with automatic session management.
    /// Loads the session from store, runs the agent, and saves if autoSave is enabled.
    /// </summary>
    /// <param name="userMessage">The user's message text</param>
    /// <param name="sessionId">Session identifier to load/create</param>
    /// <param name="options">Optional per-invocation run options for customization</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of internal agent events</returns>
    /// <exception cref="InvalidOperationException">Thrown if no session store is configured</exception>
    /// <remarks>
    /// <para>
    /// This is the recommended API for most use cases. It handles:
    /// <list type="bullet">
    /// <item>Loading or creating the session</item>
    /// <item>Running the agent with the session</item>
    /// <item>Saving the session (if autoSave is enabled)</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Example:</b>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithSessionStore(store, autoSave: true)
    ///     .Build();
    ///
    /// // Seamless session management
    /// await agent.RunAsync("Hello", "session-123");
    /// await agent.RunAsync("Follow up", "session-123");  // Continues conversation
    /// </code>
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        string userMessage,
        string sessionId,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);

        await foreach (var evt in RunAsync(userMessage, session, options, cancellationToken))
        {
            yield return evt;
        }

        // Auto-save if configured
        if (Config.SessionStoreOptions?.AutoSave == true)
        {
            await SaveSessionAsync(session, cancellationToken);
        }
    }

    /// <summary>
    /// Convenience overload for running with a single ChatMessage and session ID.
    /// Useful for sending messages with typed content (ImageContent, AudioContent, etc.)
    /// </summary>
    /// <param name="message">Single chat message to send</param>
    /// <param name="sessionId">Session ID for auto-save</param>
    /// <param name="options">Optional run options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of agent events</returns>
    /// <remarks>
    /// <para>
    /// <b>Example - Send image to vision model:</b>
    /// <code>
    /// var image = await ImageContent.FromFileAsync("photo.jpg");
    /// await agent.RunAsync(
    ///     new ChatMessage(ChatRole.User, [new TextContent("What's in this?"), image]),
    ///     sessionId);
    /// </code>
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        ChatMessage message,
        string sessionId,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);

        await foreach (var evt in RunAsync(new[] { message }, session, options, cancellationToken))
        {
            yield return evt;
        }

        // Auto-save if configured
        if (Config.SessionStoreOptions?.AutoSave == true)
        {
            await SaveSessionAsync(session, cancellationToken);
        }
    }

    /// <summary>
    /// Loads a session by ID from the configured store.
    /// Returns a new empty session if no saved session exists.
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The loaded session, or a new empty session if not found</returns>
    /// <exception cref="InvalidOperationException">Thrown if no session store is configured</exception>
    /// <remarks>
    /// <para>
    /// This method loads <see cref="SessionSnapshot"/> data (messages, metadata).
    /// It does NOT automatically check for execution checkpoints.
    /// </para>
    /// <para>
    /// <strong>For crash recovery:</strong> Use <see cref="GetCheckpointManifestAsync"/> to list
    /// available checkpoints, then <see cref="LoadSessionAtCheckpointAsync"/> to load a specific one.
    /// Automatic checkpoint loading is intentionally not supported because checkpoints are tied
    /// to specific message states and may be stale if turns ran without DurableExecution enabled.
    /// </para>
    /// </remarks>
    public async Task<AgentSession> LoadSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var store = Config.SessionStore
            ?? throw new InvalidOperationException(
                "No session store configured. Use WithSessionStore() on AgentBuilder to configure persistence.");

        var session = await store.LoadSessionAsync(sessionId, cancellationToken);
        return session ?? new AgentSession(sessionId);
    }

    /// <summary>
    /// Gets the checkpoint manifest for a session.
    /// Returns list of checkpoint metadata ordered by creation time (newest first).
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <param name="limit">Maximum number of entries to return (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of checkpoint manifest entries</returns>
    /// <exception cref="InvalidOperationException">Thrown if no session store is configured or store doesn't support history</exception>
    public async Task<List<CheckpointManifestEntry>> GetCheckpointManifestAsync(
        string sessionId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var store = Config.SessionStore
            ?? throw new InvalidOperationException(
                "No session store configured. Use WithSessionStore() on AgentBuilder to configure persistence.");

        if (!store.SupportsHistory)
        {
            throw new InvalidOperationException(
                "Session store does not support checkpoint history. " +
                "Use a store with SupportsHistory=true (e.g., JsonSessionStore with history enabled).");
        }

        return await store.GetCheckpointManifestAsync(sessionId, limit, cancellationToken);
    }

    /// <summary>
    /// Loads a session from a specific execution checkpoint.
    /// Use this for time-travel debugging or resuming from a specific point.
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <param name="executionCheckpointId">The specific checkpoint ID to load</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session restored from the checkpoint, or null if checkpoint doesn't exist</returns>
    /// <exception cref="InvalidOperationException">Thrown if no session store is configured or store doesn't support history</exception>
    public async Task<AgentSession?> LoadSessionAtCheckpointAsync(
        string sessionId,
        string executionCheckpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionCheckpointId);

        var store = Config.SessionStore
            ?? throw new InvalidOperationException(
                "No session store configured. Use WithSessionStore() on AgentBuilder to configure persistence.");

        if (!store.SupportsHistory)
        {
            throw new InvalidOperationException(
                "Session store does not support checkpoint history. " +
                "Use a store with SupportsHistory=true (e.g., JsonSessionStore with history enabled).");
        }

        var checkpoint = await store.LoadCheckpointAtAsync(sessionId, executionCheckpointId, cancellationToken);
        if (checkpoint == null)
        {
            return null;
        }

        return AgentSession.FromExecutionCheckpoint(checkpoint);
    }

    /// <summary>
    /// Saves the current session to the configured store.
    /// Use this in manual mode after running the agent.
    /// </summary>
    /// <param name="session">Session to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <exception cref="InvalidOperationException">Thrown if no session store is configured</exception>
    public async Task SaveSessionAsync(
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var store = Config.SessionStore
            ?? throw new InvalidOperationException(
                "No session store configured. Use WithSessionStore() on AgentBuilder to configure persistence.");

        await store.SaveSessionAsync(session, cancellationToken);

        // Apply retention policy if configured
        var options = Config.SessionStoreOptions;
        if (options?.Retention != null)
        {
            await options.Retention.ApplyAsync(store, session.Id, cancellationToken);
        }
    }

    /// <summary>
    /// Manually saves an execution checkpoint for the current session.
    /// Use this when DurableExecution is configured with <see cref="CheckpointFrequency.Manual"/>.
    /// </summary>
    /// <param name="session">Session to checkpoint (must have ExecutionState set)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The checkpoint ID that was created</returns>
    /// <exception cref="InvalidOperationException">Thrown if no session store is configured or session has no ExecutionState</exception>
    /// <remarks>
    /// <para>
    /// This method is for manual checkpoint control. The session must have an active
    /// <see cref="AgentLoopState"/> (i.e., be mid-execution) to create a checkpoint.
    /// </para>
    /// <para>
    /// For normal session persistence after a turn completes, use <see cref="SaveSessionAsync"/> instead.
    /// </para>
    /// </remarks>
    public async Task<string> SaveCheckpointAsync(
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var store = Config.SessionStore
            ?? throw new InvalidOperationException(
                "No session store configured. Use WithSessionStore() on AgentBuilder to configure persistence.");

        if (session.ExecutionState == null)
        {
            throw new InvalidOperationException(
                "Cannot create checkpoint without ExecutionState. " +
                "Use SaveSessionAsync() for saving session state after completion.");
        }

        var checkpoint = session.ToExecutionCheckpoint();
        var metadata = new CheckpointMetadata
        {
            Source = CheckpointSource.Manual,
            Step = session.ExecutionState.Iteration,
            MessageIndex = session.ExecutionState.CurrentMessages.Count
        };

        await store.SaveCheckpointAsync(checkpoint, metadata, cancellationToken);

        // Apply retention policy if configured
        var durableConfig = Config.DurableExecutionConfig;
        if (durableConfig?.Retention != null)
        {
            await durableConfig.Retention.ApplyAsync(store, session.Id, cancellationToken);
        }

        return checkpoint.ExecutionCheckpointId;
    }

    //
    // ITERATION Middleware SUPPORT
    //

    // V2: ProcessIterationMiddleWareSignals removed - state updates are immediate in V2

}

#region Agent Decision Engine
/// <summary>
/// Pure decision engine for agent execution loop.
/// Contains ZERO I/O operations - all decisions are deterministic and testable.
/// This is the "Functional Core" of the agent architecture.
/// </summary>
internal sealed class AgentDecisionEngine
{
    /// <summary>
    /// Decides what the agent should do next based on current state.
    /// This is a pure function - same inputs always produce same output.
    /// </summary>
    /// <param name="state">Current immutable state</param>
    /// <param name="lastResponse">Response from last LLM call (null on first iteration)</param>
    /// <param name="config">Agent configuration (max iterations, available tools, etc.)</param>
    /// <returns>Decision for what action to take next</returns>
    public AgentDecision DecideNextAction(
        AgentLoopState state,
        ChatResponse? lastResponse,
        AgentConfiguration config)
    {
        // Check: Already terminated by external source (e.g., permission Middleware, manual termination)
        if (state.IsTerminated)
            return new AgentDecision.Terminate(state.TerminationReason ?? "Terminated");

        // If no response yet, must call LLM

        if (lastResponse == null)
            return AgentDecision.CallLLM.Instance;

        // Check if response has any tool calls
        bool hasToolCalls = lastResponse.Messages
            .Any(m => m.Contents.OfType<FunctionCallContent>().Any());

        if (!hasToolCalls)
            return new AgentDecision.Complete(lastResponse);
        // Check if all requested tools are available (optional)

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
        // If response had tool calls, they will be executed inline
        // and we need to call the LLM again with the results

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
}

/// <summary>
/// Discriminated union representing all possible agent decisions.
/// The decision engine returns one of these sealed record types.
/// Pattern matching ensures exhaustive handling of all cases.
/// </summary>
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
    /// - Max iterations reached (checked by middleware)
    /// - Circuit breaker triggered (via CircuitBreakerIterationMiddleware)
    /// - Too many consecutive errors (via ErrorTrackingIterationMiddleware)
    /// - External termination (e.g., permission denied via middleware)
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
/// Information about an active background operation being tracked by the agent loop.
/// Used for crash recovery - allows resuming polling for in-flight background operations.
/// </summary>
public record BackgroundOperationInfo
{
    /// <summary>
    /// Serialized continuation token (Base64 encoded).
    /// Can be deserialized using ResponseContinuationToken.FromBytes().
    /// </summary>
    public required string TokenData { get; init; }

    /// <summary>
    /// Which iteration started this background operation.
    /// </summary>
    public required int Iteration { get; init; }

    /// <summary>
    /// When the background operation was started.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// Last known status from the provider.
    /// </summary>
    public OperationStatus? LastKnownStatus { get; init; }
}

/// <summary>
/// Immutable snapshot of agent execution loop state.
/// Consolidates all 11 state variables that were scattered in RunAgenticLoopInternal.
/// Thread-safe and testable - enables pure decision-making logic.
/// </summary>
public sealed record AgentLoopState
{
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

    //      
    // FUNCTION TRACKING
    //      

    /// <summary>
    /// Functions completed in this run (for telemetry and deduplication).
    /// Tracks which functions have been successfully executed.
    /// </summary>
    public required ImmutableHashSet<string> CompletedFunctions { get; init; }

    //      
    // HISTORY OPTIMIZATION STATE
    //      

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

    //      
    // STREAMING STATE
    //      

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

    //
    // MIDDLEWARE STATE (extensible, owned by middlewares)
    //

    /// <summary>
    /// Source-generated middleware state container.
    /// Provides strongly-typed properties for each middleware state type marked with [MiddlewareState].
    /// </summary>
    public MiddlewareState MiddlewareState { get; init; }
        = new MiddlewareState();

    //
    // BACKGROUND OPERATION TRACKING
    //

    /// <summary>
    /// Active background operation being tracked (if any).
    /// Used for crash recovery - allows resuming polling for in-flight background operations.
    /// Null when no background operation is active.
    /// </summary>
    public BackgroundOperationInfo? ActiveBackgroundOperation { get; init; }

    //
    // FACTORY METHOD
    //

    /// <summary>
    /// Creates initial state for a new agent execution.
    /// All collections are empty, counters are zero.
    /// </summary>
    /// <param name="messages">Initial conversation messages</param>
    /// <param name="runId">Unique identifier for this run</param>
    /// <param name="conversationId">Conversation identifier</param>
    /// <param name="agentName">Name of the agent</param>
    /// <returns>Fresh state ready for first iteration</returns>
    /// <remarks>
    /// The messages collection is defensively copied to ensure immutability.
    /// This prevents bugs where the caller modifies the original collection after state creation.
    /// </remarks>
    public static AgentLoopState Initial(
        IReadOnlyList<ChatMessage> messages,
        string runId,
        string conversationId,
        string agentName,
        MiddlewareState? persistentState = null) => new()
    {
        RunId = runId,
        ConversationId = conversationId,
        AgentName = agentName,
        StartTime = DateTime.UtcNow,
        // Defensive copy: ToList() creates a new list to avoid reference sharing with mutable source.
        // This is critical for checkpoint integrity - saved states must not change after saving.
        CurrentMessages = messages.ToList(),
        TurnHistory = ImmutableList<ChatMessage>.Empty,
        Iteration = 0,
        IsTerminated = false,
        TerminationReason = null,
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
        ETag = null, // Will be generated on first serialize
        MiddlewareState = persistentState ?? new MiddlewareState()
    };

    //      
    // STATE TRANSITIONS (Immutable Updates)
    // All methods return NEW instances - never mutate existing state
    //      

    /// <summary>
    /// Advances to the next iteration.
    /// </summary>
    public AgentLoopState NextIteration() =>
        this with { Iteration = Iteration + 1 };

    /// <summary>
    /// Updates the current conversation messages.
    /// Used after adding new messages from LLM or tool results.
    /// </summary>
    /// <remarks>
    /// For maximum safety, consider passing a fresh list. This method stores the reference directly
    /// for performance, as internal callers already provide new list instances.
    /// </remarks>
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
    /// Terminates the loop with the specified reason.
    /// </summary>
    public AgentLoopState Terminate(string reason) =>
        this with { IsTerminated = true, TerminationReason = reason };



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
    /// </summary>
    /// <param name="functionName">Name of the completed function</param>
    /// <returns>New state with updated function tracking</returns>
    public AgentLoopState CompleteFunction(string functionName) =>
        this with { CompletedFunctions = CompletedFunctions.Add(functionName) };

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

    /// <summary>
    /// Sets or clears the active background operation.
    /// Called when an LLM call returns a continuation token (backgrounded operation).
    /// </summary>
    /// <param name="operation">The background operation info, or null to clear.</param>
    public AgentLoopState WithBackgroundOperation(BackgroundOperationInfo? operation) =>
        this with { ActiveBackgroundOperation = operation };

    /// <summary>
    /// Pending writes from function calls that completed successfully
    /// but before the iteration checkpoint was saved.
    /// Used for partial failure recovery in parallel execution scenarios.
    /// </summary>
    public ImmutableList<PendingWrite> PendingWrites { get; init; }
        = ImmutableList<PendingWrite>.Empty;

    /// <summary>
    /// Schema version for forward/backward compatibility.
    /// Increment when making breaking changes to this record.
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
    
    /// <summary>
    /// Serializes this state to JSON for checkpointing.
    /// Uses Microsoft.Extensions.AI's built-in serialization for ChatMessage and AIContent.
    /// Handles immutable collections, polymorphic content, and all message types automatically.
    /// </summary>
    public string Serialize()
    {
        // Generate new ETag for optimistic concurrency
        var stateWithETag = this with { ETag = Guid.NewGuid().ToString() };
        return JsonSerializer.Serialize(stateWithETag, (JsonTypeInfo<object?>)AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)));
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
            var state = JsonSerializer.Deserialize<AgentLoopState>(json, (JsonTypeInfo<AgentLoopState>)AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentLoopState)))
                ?? throw new InvalidOperationException("Failed to deserialize AgentLoopState v1");

            // v1 checkpoints don't have PendingWrites - initialize to empty
            return state with { PendingWrites = ImmutableList<PendingWrite>.Empty };
        }
        else if (version == 2)
        {
            // v2: Pending writes support
            return JsonSerializer.Deserialize<AgentLoopState>(json, (JsonTypeInfo<AgentLoopState>)AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentLoopState)))
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

        // Check error tracking state from MiddlewareState (new pattern)
        var errorState = this.MiddlewareState.ErrorTracking();
        if (errorState != null && errorState.ConsecutiveFailures < 0)
        {
            throw new InvalidOperationException($"Checkpoint has invalid ConsecutiveFailures: {errorState.ConsecutiveFailures}");
        }
    }
}

/// <summary>
/// Configuration data for the agent decision engine (pure data, no behavior).
/// Contains all settings needed for decision-making logic.
/// Immutable and easily testable.
/// </summary>
/// <remarks>
internal sealed record AgentConfiguration
{
    /// <summary>
    /// Maximum iterations before forced termination.
    /// Each iteration = one LLM call + tool execution cycle.
    /// Prevents runaway loops and excessive costs.
    /// </summary>
    public required int MaxIterations { get; init; }

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
            TerminateOnUnknownCalls = config?.AgenticLoop?.TerminateOnUnknownCalls ?? false,
            AvailableTools = availableTools
        };
    }

    /// <summary>
    /// Factory method: Create default configuration for testing.
    /// </summary>
    /// <param name="maxIterations">Maximum iterations (default: 10)</param>
    /// <param name="availableTools">Available tool names (default: empty)</param>
    /// <param name="terminateOnUnknownCalls">Whether to terminate on unknown tools (default: false)</param>
    /// <returns>Configuration with sensible defaults for testing</returns>
    public static AgentConfiguration Default(
        int maxIterations = 10,
        IReadOnlySet<string>? availableTools = null,
        bool terminateOnUnknownCalls = false)
    {
        return new AgentConfiguration
        {
            MaxIterations = maxIterations,
            TerminateOnUnknownCalls = terminateOnUnknownCalls,
            AvailableTools = availableTools ?? new HashSet<string>()
        };
    }
}

#endregion

// Function map helpers moved into `FunctionCallProcessor` to reduce indirection.

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
                    sb.Append(JsonSerializer.Serialize(
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
    private readonly HPD.Events.IEventCoordinator _eventCoordinator;
    private readonly AgentMiddlewarePipeline _middlewarePipeline;
    private readonly ErrorHandlingConfig? _errorHandlingConfig;
    private readonly IList<AITool>? _serverConfiguredTools;
    private readonly AgenticLoopConfig? _agenticLoopConfig;

    public FunctionCallProcessor(
        HPD.Events.IEventCoordinator eventCoordinator,
        AgentMiddlewarePipeline middlewarePipeline,
        int maxFunctionCalls,
        ErrorHandlingConfig? errorHandlingConfig = null,
        IList<AITool>? serverConfiguredTools = null,
        AgenticLoopConfig? agenticLoopConfig = null)
    {
        _eventCoordinator = eventCoordinator ?? throw new ArgumentNullException(nameof(eventCoordinator));
        _middlewarePipeline = middlewarePipeline ?? throw new ArgumentNullException(nameof(middlewarePipeline));
        _errorHandlingConfig = errorHandlingConfig;
        _serverConfiguredTools = serverConfiguredTools;
        _agenticLoopConfig = agenticLoopConfig;
    }

    // Helpers moved here from FunctionMapBuilder to keep lookup logic next to caller
    private static Dictionary<string, AIFunction>? BuildMergedMap(
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

    private static AIFunction? FindFunction(
        string name,
        Dictionary<string, AIFunction>? map)
    {
        return map?.TryGetValue(name, out var func) == true ? func : null;
    }

    /// <summary>
    /// Checks if a function by name is an output tool (structured output tool mode).
    /// </summary>
    public bool IsOutputToolByName(string? functionName, IList<AITool>? tools)
    {
        if (string.IsNullOrEmpty(functionName))
            return false;
        var functionMap = BuildMergedMap(_serverConfiguredTools, tools);
        var function = FindFunction(functionName, functionMap);
        return IsOutputTool(function);
    }

    /// <summary>
    /// Checks if a function is an output tool (structured output tool mode).
    /// Output tools are never executed - their arguments ARE the structured output.
    /// </summary>
    private static bool IsOutputTool(AIFunction? function)
    {
        return function?.AdditionalProperties?.TryGetValue("Kind", out var kind) == true
               && kind?.ToString() == "Output";
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
        AgentRunOptions runOptions,
        Middleware.AgentContext agentContext,
        CancellationToken cancellationToken)
    {
        // Check if any tool request is an output tool (structured output termination)
        var functionMap = BuildMergedMap(_serverConfiguredTools, options?.Tools);
        bool outputToolCalled = toolRequests.Any(tr =>
            !string.IsNullOrEmpty(tr.Name) && IsOutputTool(FindFunction(tr.Name, functionMap)));

        // Route to appropriate execution strategy
        // For single tool calls, inline execution (no parallel overhead)
        if (toolRequests.Count <= 1)
        {
            var result = await ExecuteSequentiallyAsync(
                currentHistory, toolRequests, options, agentLoopState, runOptions, agentContext,
                cancellationToken).ConfigureAwait(false);
            return result with { OutputToolCalled = outputToolCalled };
        }

        // For multiple tools, use parallel execution with throttling
        var parallelResult = await ExecuteInParallelAsync(
            currentHistory, toolRequests, options, agentLoopState, runOptions, agentContext,
            cancellationToken).ConfigureAwait(false);
        return parallelResult with { OutputToolCalled = outputToolCalled };
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
        AgentRunOptions runOptions,
        Middleware.AgentContext agentContext,
        CancellationToken cancellationToken)
    {
        var allContents = new List<AIContent>();
        // Process ALL tools (containers + regular) through the existing processor
        var resultMessages = await ProcessFunctionCallsAsync(
            currentHistory, options, toolRequests, agentLoopState, runOptions, agentContext, cancellationToken).ConfigureAwait(false);

        // Combine results
        foreach (var message in resultMessages)
        {
            foreach (var content in message.Contents)
            {
                allContents.Add(content);
            }
        }

        // Extract successful functions
        var successfulFunctions = ExtractSuccessfulFunctions(allContents, toolRequests);

        return new ToolExecutionResult(
            new ChatMessage(ChatRole.Tool, allContents),
            successfulFunctions);
    }

    /// <summary>
    /// Executes tools in parallel with throttling.
    /// Permission checking is handled individually per tool via middleware pipeline.
    /// </summary>
    private async Task<ToolExecutionResult> ExecuteInParallelAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        AgentLoopState agentLoopState,
        AgentRunOptions runOptions,
        Middleware.AgentContext agentContext,
        CancellationToken cancellationToken)
    {
        // : Batch permission check via BeforeParallelBatchAsync hook
        // Build function map and collect parallel function information
        var functionMap = BuildMergedMap(_serverConfiguredTools, options?.Tools);
        var parallelFunctions = new List<ParallelFunctionInfo>();

        foreach (var toolRequest in toolRequests)
        {
            if (string.IsNullOrEmpty(toolRequest.Name))
                continue;

            var function = FindFunction(toolRequest.Name, functionMap);
            if (function == null)
                continue;

            // Extract Toolkit/skill metadata (same logic as ProcessFunctionCallsAsync)
            string? toolTypeName = null;
            if (function.AdditionalProperties?.TryGetValue("ParentToolkit", out var parentToolkitCtx) == true)
            {
                toolTypeName = parentToolkitCtx as string;
            }
            else if (function.AdditionalProperties?.TryGetValue("ToolkitName", out var toolNameProp) == true)
            {
                toolTypeName = toolNameProp as string;
            }

            string? skillName = null;

            if (function.AdditionalProperties?.TryGetValue("IsSkill", out var isSkillValueCtx) == true
                && isSkillValueCtx is bool isSCtx && isSCtx)
            {
                // This function is a skill container
            }

            parallelFunctions.Add(new ParallelFunctionInfo(
                function,
                toolRequest.CallId,
                (IReadOnlyDictionary<string, object?>)(toolRequest.Arguments ?? new Dictionary<string, object?>())));
        }

        // Create V2 AgentContext for parallel batch hook
        // Note: parentChatClient is not needed here since this is just for the batch hook,
        // not for SubAgent creation (which happens via agentContext passed to this method)
        var batchAgentContext = new Middleware.AgentContext(
            agentName: agentLoopState.AgentName,
            conversationId: agentLoopState.ConversationId,
            initialState: agentLoopState,
            eventCoordinator: _eventCoordinator,
            session: agentContext.Session,
            cancellationToken: cancellationToken,
            services: agentContext.Services);

        var batchContext = batchAgentContext.AsBeforeParallelBatch(
            parallelFunctions,
            runOptions);

        // Execute BeforeParallelBatchAsync middleware hooks
        await _middlewarePipeline.ExecuteBeforeParallelBatchAsync(
            batchContext, cancellationToken).ConfigureAwait(false);

        // V2: State updates are immediate - no GetPendingState() needed!
        agentLoopState = batchAgentContext.State;

        // All tools will be processed - individual permission checks happen in ProcessFunctionCallsAsync
        // (those checks will use the BatchPermissionState populated by BeforeParallelBatchAsync)
        var approvedTools = toolRequests;
        var deniedTools = new List<(FunctionCallContent Tool, string Reason)>();

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
                    currentHistory, options, singleToolList, agentLoopState, runOptions, agentContext, cancellationToken).ConfigureAwait(false);

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
                // Include error in results
                var errorContent = new TextContent($"  Error executing tool: {result.Error.Message}");
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
    /// Mirrors the error detection logic used in Agent.
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
        AgentRunOptions runOptions,
        Middleware.AgentContext agentContext,
        CancellationToken cancellationToken)
    {
        var resultMessages = new List<ChatMessage>();

        // Build function map per execution (Microsoft pattern for session-safety)
        // This avoids shared mutable state and stale cache issues
        // Merge server-configured tools with request tools (request tools take precedence)
        var functionMap = BuildMergedMap(_serverConfiguredTools, options?.Tools);

        // Process each function call through the unified middleware pipeline
        foreach (var functionCall in functionCallContents)
        {
            // Skip functions without names (safety check)
            if (string.IsNullOrEmpty(functionCall.Name))
                continue;

            // Resolve the function from the merged function map
            var function = FindFunction(functionCall.Name, functionMap);

            // ═══════════════════════════════════════════════════════════════
            // OUTPUT TOOL CHECK (structured output tool mode)
            // Output tools don't execute - their args ARE the structured output
            // RunStructuredAsync handles parsing via ToolCallArgsEvent
            // ═══════════════════════════════════════════════════════════════
            if (IsOutputTool(function))
            {
                // Skip execution - args are captured by RunStructuredAsync
                // Still emits ToolCallEndEvent so RunStructuredAsync knows args are complete
                continue;
            }

            // Extract Collapse information for middleware Collapsing
            string? toolTypeName = null;
            if (function?.AdditionalProperties?.TryGetValue("ParentToolkit", out var parentToolkitCtx) == true)
            {
                toolTypeName = parentToolkitCtx as string;
            }
            else if (function?.AdditionalProperties?.TryGetValue("ToolkitName", out var toolNameProp) == true)
            {
                // For container functions, ToolkitName IS the Toolkit type
                toolTypeName = toolNameProp as string;
            }

            // Fallback: Try function-to-Toolkit mapping
            if (string.IsNullOrEmpty(toolTypeName) && functionCall.Name != null)
            {
                // Toolkit metadata comes from AIFunction.AdditionalProperties (set by source generator)
            }

            // Extract skill metadata
            string? skillName = null;
            bool isSkillContainer = false;

            // Check if this function IS a skill container
            if (function?.AdditionalProperties?.TryGetValue("IsSkill", out var isSkillValueCtx) == true
                && isSkillValueCtx is bool isSCtx && isSCtx)
            {
                isSkillContainer = true;
                // Note: When invoking a skill container, skillName remains null
                // The container IS the skill, it doesn't have a "parent skill"
            }

            // Reuse the turn's AgentContext for consistent state tracking
            // This ensures error tracking and other middleware state persists across function calls
            var functionAgentContext = agentContext;

            // Check if function is unknown and TerminateOnUnknownCalls is enabled
            if (function == null && _agenticLoopConfig?.TerminateOnUnknownCalls == true)
            {
                // Terminate the loop - don't process this or any remaining functions
                // The function call will be returned to the caller for handling (e.g., multi-agent handoff)
                functionAgentContext.UpdateState(s => s with { IsTerminated = true });

                // Don't add any result message - let the caller handle the unknown function
                break;
            }

            // Create typed BeforeFunction context
            var beforeFunctionContext = functionAgentContext.AsBeforeFunction(
                function: function!,  // Will be null for unknown functions
                callId: functionCall.CallId,
                arguments: (IReadOnlyDictionary<string, object?>?)(functionCall.Arguments ?? new Dictionary<string, object?>()),
                runOptions: runOptions,
                toolkitName: toolTypeName,
                skillName: skillName);

            // Execute BeforeFunctionAsync middleware hooks (permission check happens here)
            await _middlewarePipeline.ExecuteBeforeFunctionAsync(
                beforeFunctionContext, cancellationToken).ConfigureAwait(false);

            // If middleware blocked execution (permission denied), record the denial and skip execution
            if (beforeFunctionContext.BlockExecution)
            {
                var denialResult = beforeFunctionContext.OverrideResult ?? "Permission denied";

                // Note: Function completion tracking is handled by caller using state updates

                var denialResultContent = new FunctionResultContent(functionCall.CallId, denialResult);
                var denialMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { denialResultContent });
                resultMessages.Add(denialMessage);
                continue; // Skip to next function call
            }

            // Permission approved - proceed with function execution
            object? executionResult = null;
            Exception? executionException = null;

            try
            {
                // Handle function not found case
                if (function is null)
                {
                    // Generate basic error message
                    // Note: ToolCollapsingMiddleware may have already set a more detailed message in BeforeToolExecutionAsync
                    executionResult = beforeFunctionContext.OverrideResult
                        ?? $"Function '{functionCall.Name ?? "Unknown"}' not found.";
                }
                else
                {
                    // Create FunctionRequest for V2 pipeline
                    var functionRequest = new Middleware.FunctionRequest
                    {
                        Function = function,
                        CallId = functionCall.CallId,
                        Arguments = (IReadOnlyDictionary<string, object?>?)(functionCall.Arguments ?? new Dictionary<string, object?>()) ?? new Dictionary<string, object?>(),
                        State = functionAgentContext.State
                    };

                    // Execute the function through V2 middleware pipeline (retry, timeout, etc.)
                    executionResult = await _middlewarePipeline.ExecuteFunctionCallAsync(
                        functionRequest,
                        coreHandler: async (req) =>
                        {
                            // V2: Set CurrentFunctionContext so AIFunctions can access context
                            // (e.g., ClarificationFunction needs Emit/WaitForResponseAsync)
                            Agent.CurrentFunctionContext = beforeFunctionContext;
                            try
                            {
                                // THIS IS THE ACTUAL FUNCTION EXECUTION (innermost call)
                                var args = new AIFunctionArguments(new Dictionary<string, object?>(req.Arguments));
                                return await req.Function.InvokeAsync(args, cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                // V2: Clear context after function execution
                                Agent.CurrentFunctionContext = null;
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Emit error event
                functionAgentContext.Emit(new MiddlewareErrorEvent(
                    "FunctionExecution",
                    $"Error executing function '{functionCall.Name}': {ex.Message}",
                    ex));

                // Notify error tracking middleware (V2 error hooks)
                var errorContext = functionAgentContext.AsError(
                    error: ex,
                    source: ErrorSource.ToolCall,
                    iteration: functionAgentContext.State.Iteration);

                try
                {
                    await _middlewarePipeline.ExecuteOnErrorAsync(errorContext, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Swallow errors in error handlers to preserve original error
                }

                // Don't automatically terminate on error - let error tracking middleware decide
                // The middleware (e.g., ErrorTrackingMiddleware) will set IsTerminated if threshold exceeded
                executionResult = $"Error executing function '{functionCall.Name}': {ex.Message}";
                executionException = ex;
            }

            // Create AfterFunction context
            var afterFunctionContext = functionAgentContext.AsAfterFunction(
                function: function!,
                callId: functionCall.CallId,
                result: executionResult,
                exception: executionException,
                runOptions: runOptions,
                toolkitName: toolTypeName,
                skillName: skillName);

            try
            {
                await _middlewarePipeline.ExecuteAfterFunctionAsync(
                    afterFunctionContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception afterEx)
            {
                // Log AfterFunction errors but don't fail the function execution
                functionAgentContext.Emit(new MiddlewareErrorEvent(
                    "AfterFunctionMiddleware",
                    $"Error in AfterFunction middleware: {afterEx.Message}",
                    afterEx));
            }

            // Note: Function completion tracking is handled by caller using state updates

            // CRITICAL: Use afterFunctionContext.Result (middleware may have transformed it)
            var functionResult = new FunctionResultContent(functionCall.CallId, afterFunctionContext.Result)
            {
                Exception = afterFunctionContext.Exception
            };
            var functionMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { functionResult });
            resultMessages.Add(functionMessage);
        }

        return resultMessages;
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

#region PreparedTurn

/// <summary>
/// Encapsulates all prepared state for a single agent turn.
/// Separates message preparation (functional, pure) from execution (I/O, stateful).
/// </summary>
internal record PreparedTurn
{
    /// <summary>
    /// Messages to send to the LLM (includes session history + new input, optionally reduced).
    /// This is the "effective" message list after all preparation steps.
    /// </summary>
    public required IReadOnlyList<ChatMessage> MessagesForLLM { get; init; }

    /// <summary>
    /// NEW input messages only (what the caller provided).
    /// Used for persistence - these are the messages to add to session history.
    /// </summary>
    public required IReadOnlyList<ChatMessage> NewInputMessages { get; init; }

    /// <summary>
    /// Final chat options after merging defaults, applying Middlewares, and adding system instructions.
    /// </summary>
    public ChatOptions? Options { get; init; }
}

#endregion

#region MessageProcessor

/// <summary>
/// Handles all pre-processing of chat messages and options before sending to the LLM.
/// </summary>
internal class MessageProcessor
{
    private readonly string? _systemInstructions;
    private readonly ChatOptions? _defaultOptions;

    public MessageProcessor(
        string? systemInstructions,
        ChatOptions? defaultOptions)
    {
        _systemInstructions = systemInstructions;
        _defaultOptions = defaultOptions;
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
    /// Loads session history, merges options, adds system instructions, applies reduction (with caching), and Middlewares messages.
    /// </summary>
    /// <param name="session">Agent session (null for stateless execution).</param>
    /// <param name="inputMessages">NEW messages from the caller (to be added to history).</param>
    /// <param name="options">Chat options to merge with defaults.</param>
    /// <param name="agentName">Agent name for logging/Middlewareing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>PreparedTurn with all state needed for execution.</returns>
    public async Task<PreparedTurn> PrepareTurnAsync(
        AgentSession? session,
        IEnumerable<ChatMessage> inputMessages,
        ChatOptions? options,
        string agentName,
        CancellationToken cancellationToken)
    {
        var inputMessagesList = inputMessages.ToList();
        var messagesForLLM = new List<ChatMessage>();

        // STEP 1: Load session history
        if (session != null)
        {
            var sessionMessages = await session.GetMessagesAsync(cancellationToken);
            messagesForLLM.AddRange(sessionMessages);
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

        // STEP 4: Apply prompt Middlewares
        var preparedMessages = await ApplyPromptMiddlewaresAsync(
            messagesForLLM,
            effectiveOptions,
            agentName,
            cancellationToken).ConfigureAwait(false);

        // STEP 5: Return PreparedTurn
        return new PreparedTurn
        {
            MessagesForLLM = preparedMessages.ToList(),
            NewInputMessages = inputMessagesList,
            Options = effectiveOptions
        };
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
            Instructions = providedOptions.Instructions ?? _defaultOptions.Instructions,
            AdditionalProperties = MergeDictionaries(_defaultOptions.AdditionalProperties, providedOptions.AdditionalProperties)
        };
    }

    /// <summary>
    /// Applies the registered prompt middlewares pipeline.
    /// NOTE: This is now a no-op - prompt middleware is handled via the unified AgentMiddlewarePipeline.
    /// </summary>
    private Task<IEnumerable<ChatMessage>> ApplyPromptMiddlewaresAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string agentName,
        CancellationToken cancellationToken)
    {
        // Prompt middlewares are now handled via BeforeMessageTurnAsync in the unified pipeline
        return Task.FromResult(messages);
    }

    /// <summary>
    /// Applies post-invocation middlewares to process results, extract memories, etc.
    /// NOTE: This is now a no-op - post-invoke middleware is handled via the unified AgentMiddlewarePipeline.
    /// </summary>
    public Task ApplyPostInvokeMiddlewaresAsync(
        IEnumerable<ChatMessage> requestMessages,
        IEnumerable<ChatMessage>? responseMessages,
        Exception? exception,
        ChatOptions? options,
        string agentName,
        CancellationToken cancellationToken)
    {
        // Post-invoke middlewares are now handled via AfterMessageTurnAsync in the unified pipeline
        return Task.CompletedTask;
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

        await foreach (var update in RunAsyncCore(messages, options, overrideClient: null, cancellationToken))
        {
            // Capture ConversationId from first update that has one
            if (LastResponseConversationId == null && update.ConversationId != null)
            {
                LastResponseConversationId = update.ConversationId;
            }

            yield return update;
        }
    }

    /// <summary>
    /// Runs a single turn with an optional override client for runtime provider switching.
    /// </summary>
    /// <param name="messages">The conversation history to send to the LLM</param>
    /// <param name="options">Optional chat options</param>
    /// <param name="overrideClient">Optional override client (for AgentRunOptions provider switching)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of ChatResponseUpdates representing the LLM's response</returns>
    public async IAsyncEnumerable<ChatResponseUpdate> RunAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IChatClient? overrideClient,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Reset ConversationId at start of new turn
        LastResponseConversationId = null;

        await foreach (var update in RunAsyncCore(messages, options, overrideClient, cancellationToken))
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
        IChatClient? overrideClient,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Apply runtime options configuration callback if configured
        if (_configureOptions != null && options != null)
        {
            _configureOptions(options);
        }

        // Apply middleware dynamically (if any)
        // This allows runtime provider switching - new providers automatically get wrapped
        // Use override client if provided (from AgentRunOptions), otherwise use base client
        var effectiveClient = overrideClient ?? _baseClient;
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
        var sb = new StringBuilder();

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

#region Tool Execution Result Types
/// <summary>
/// Structured result from tool execution, replacing the 5-tuple return type.
/// Provides strongly-typed access to execution outcomes.
/// </summary>
internal record ToolExecutionResult(
    ChatMessage Message,
    HashSet<string> SuccessfulFunctions,
    bool OutputToolCalled = false);

#endregion
