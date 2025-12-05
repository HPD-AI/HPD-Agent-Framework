using Microsoft.Extensions.AI;
using System.Threading.Channels;
using HPD.Agent.Middleware;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using HPD.Agent.Checkpointing;

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
    // AsyncLocal storage for function invocation context (flows across async calls)
    // Stores the full AgentMiddlewareContext with all orchestration capabilities
    private static readonly AsyncLocal<AgentMiddlewareContext?> _currentFunctionContext = new();
    // AsyncLocal storage for root agent tracking in nested agent calls
    private static readonly AsyncLocal<Agent?> _rootAgent = new();
    // AsyncLocal storage for current conversation thread (flows across async calls)
    // Provides access to thread context (project, documents, etc.) throughout the agent execution
    private static readonly AsyncLocal<ConversationThread?> _currentThread = new();

    // Specialized component fields for delegation
    private readonly MessageProcessor _messageProcessor;
    private readonly FunctionCallProcessor _functionCallProcessor;
    private readonly AgentTurn _agentTurn;
    private readonly BidirectionalEventCoordinator _eventCoordinator;
    // Unified middleware pipeline
    private readonly AgentMiddlewarePipeline _middlewarePipeline;
    // Observer pattern for event-driven observability
    private readonly IReadOnlyList<IAgentEventObserver> _observers;
    private readonly IReadOnlyList<IAgentEventHandler> _eventHandlers;
    private readonly ObserverHealthTracker? _observerHealthTracker;
    private readonly ILogger? _observerErrorLogger;
    private readonly Counter<long>? _observerErrorCounter;

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
    /// Gets the current function invocation context (if a function is currently being invoked).
    /// This flows across async calls via AsyncLocal storage.
    /// Returns null if no function is currently executing.
    /// </summary>
    public static AgentMiddlewareContext? CurrentFunctionContext
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
    /// Gets or sets the current conversation thread in the execution context.
    /// This flows across async calls and provides access to thread context throughout agent execution.
    /// </summary>
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
    /// Execution context for this agent (agent name, ID, hierarchy).
    /// Set during agent initialization to enable event attribution in multi-agent systems.
    /// </summary>
    private AgentExecutionContext? _executionContextValue;
    public AgentExecutionContext? ExecutionContext
    {
        get => _executionContextValue;
        set
        {
            _executionContextValue = value;
            // Sync with event coordinator so it can auto-attach context to events
            if (value != null)
            {
                _eventCoordinator.SetExecutionContext(value);
            }
        }
    }

    /// <summary>
    /// Internal access to event coordinator for context setup and nested agent configuration.
    /// </summary>
    public BidirectionalEventCoordinator EventCoordinator => _eventCoordinator;

    /// <summary>
    /// Internal access to Middleware event channel writer for context setup.
    /// Delegates to the event coordinator.
    /// </summary>
    internal ChannelWriter<AgentEvent> MiddlewareEventWriter => _eventCoordinator.EventWriter;

    /// <summary>
    /// Internal access to Middleware event channel reader for RunAgenticLoopInternal.
    /// Delegates to the event coordinator.
    /// </summary>
    internal ChannelReader<AgentEvent> MiddlewareEventReader => _eventCoordinator.EventReader;

    /// <summary>
    /// Sends a response to a Middleware waiting for a specific request.
    /// Called by external handlers when user provides input.
    /// Delegates to the event coordinator.
    /// </summary>
    /// <param name="requestId">The unique identifier for the request</param>
    /// <param name="response">The response event to deliver</param>
    /// <exception cref="ArgumentNullException">If response is null</exception>
    public void SendMiddlewareResponse(string requestId, AgentEvent response)
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
        CancellationToken cancellationToken) where T : AgentEvent
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
    /// <summary>
    /// Initializes a new Agent configured to run the agentic orchestration loop, with middleware, function-mapping, event coordination, and optional observers/handlers.
    /// </summary>
    /// <param name="config">Agent configuration controlling behavior, tools, error handling, and orchestration settings.</param>
    /// <param name="baseClient">The underlying chat client used for LLM interactions.</param>
    /// <param name="mergedOptions">Optional chat options to merge with the agent's defaults; if null, the provider's default options from <paramref name="config"/> are used.</param>
    /// <param name="providerErrorHandler">Provider-specific error handler to use for formatting and handling provider errors.</param>
    /// <param name="functionToPluginMap">Optional mapping of function names to plugin identifiers used to resolve function implementations.</param>
    /// <param name="functionToSkillMap">Optional mapping of function names to skill identifiers used to resolve function implementations.</param>
    /// <param name="middlewares">Optional ordered list of agent middlewares to include in the unified middleware pipeline.</param>
    /// <param name="serviceProvider">Optional service provider used to resolve auxiliary services (logging, meters, etc.).</param>
    /// <param name="observers">Optional observers that receive fire-and-forget agent events for telemetry or logging.</param>
    /// <param name="eventHandlers">Optional ordered event handlers invoked synchronously for UI or external handling.</param>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="config"/> or <paramref name="baseClient"/> is null.</exception>
    public Agent(
        AgentConfig config,
        IChatClient baseClient,
        ChatOptions? mergedOptions,
        HPD.Providers.Core.IProviderErrorHandler providerErrorHandler,
        IReadOnlyDictionary<string, string>? functionToPluginMap = null,
        IReadOnlyDictionary<string, string>? functionToSkillMap = null,
        IReadOnlyList<IAgentMiddleware>? middlewares = null,
        IServiceProvider? serviceProvider = null,
        IEnumerable<IAgentEventObserver>? observers = null,
        IEnumerable<IAgentEventHandler>? eventHandlers = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = config.Name ?? "Agent"; // Default to "Agent" to prevent null dictionary key exceptions

        // Initialize unified middleware pipeline
        // so that FunctionCallProcessor can access it without changing its signature yet.
        if (Config.ErrorHandling == null) Config.ErrorHandling = new ErrorHandlingConfig();
        Config.ErrorHandling.ProviderHandler = providerErrorHandler;


        // Initialize Microsoft.Extensions.AI compliance metadata
        _metadata = new ChatClientMetadata(
            providerName: config.Provider?.ProviderKey,
            providerUri: null,
            defaultModelId: config.Provider?.ModelName
        );

        // Initialize unified middleware pipeline
        _middlewarePipeline = new AgentMiddlewarePipeline(middlewares ?? Array.Empty<IAgentMiddleware>());

        // Create bidirectional event coordinator for Middleware events and human-in-the-loop
        // ExecutionContext is set lazily on first RunAsync via SetExecutionContext()
        _eventCoordinator = new BidirectionalEventCoordinator();

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
    /// These are the unified IAgentMiddleware instances with built-in scoping support.
    /// </summary>
    public IReadOnlyList<IAgentMiddleware> Middlewares =>
        _middlewarePipeline.Middlewares;

    /// <summary>
    /// Validates and migrates middleware state schema when resuming from checkpoint.
    /// Detects added/removed middleware and logs changes for operational visibility.
    /// </summary>
    /// <param name="checkpointState">Middleware state from checkpoint</param>
    /// <returns>Updated middleware state with current schema metadata</returns>
    private MiddlewareState ValidateAndMigrateSchema(MiddlewareState checkpointState)
    {
        // Case 1: Pre-versioning checkpoint (SchemaSignature is null)
        if (checkpointState.SchemaSignature == null)
        {
            _observerErrorLogger?.LogInformation(
                "Resuming from checkpoint created before schema versioning. " +
                "Upgrading to current schema.");

            var upgradeEvent = new SchemaChangedEvent(
                OldSignature: null,
                NewSignature: MiddlewareState.CompiledSchemaSignature,
                RemovedTypes: Array.Empty<string>(),
                AddedTypes: Array.Empty<string>(),
                IsUpgrade: true,
                Timestamp: DateTimeOffset.UtcNow);

            // Notify observers using existing NotifyObservers method
            NotifyObservers(upgradeEvent);

            return new MiddlewareState
            {
                States = checkpointState.States,
                SchemaSignature = MiddlewareState.CompiledSchemaSignature,
                SchemaVersion = MiddlewareState.CompiledSchemaVersion,
                StateVersions = MiddlewareState.CompiledStateVersions
            };
        }

        // Case 2: Schema matches (common case - no changes)
        if (checkpointState.SchemaSignature == MiddlewareState.CompiledSchemaSignature)
        {
            return checkpointState;
        }

        // Case 3: Schema changed - detect and log differences
        var oldTypes = checkpointState.SchemaSignature.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var newTypes = MiddlewareState.CompiledSchemaSignature.Split(',', StringSplitOptions.RemoveEmptyEntries);

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
            NewSignature: MiddlewareState.CompiledSchemaSignature,
            RemovedTypes: removed,
            AddedTypes: added,
            IsUpgrade: false,
            Timestamp: DateTimeOffset.UtcNow);

        // Notify observers using existing NotifyObservers method
        NotifyObservers(schemaEvent);

        // Update to current schema metadata
        return new MiddlewareState
        {
            States = checkpointState.States,
            SchemaSignature = MiddlewareState.CompiledSchemaSignature,
            SchemaVersion = MiddlewareState.CompiledSchemaVersion,
            StateVersions = MiddlewareState.CompiledStateVersions
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
        ConversationThread? thread = null,
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
            // Update coordinator with the lazily-initialized execution context
            _eventCoordinator.SetExecutionContext(ExecutionContext);
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
            yield return new MessageTurnStartedEvent(
                messageTurnId,
                conversationId,
                _name,
                DateTimeOffset.UtcNow);
 
            // MESSAGE PREPARATION: Split logic between Fresh Run vs Resume
            // FRESH RUN: Process documents → PrepareMessages → Create initial state
            // RESUME:    Use state.CurrentMessages as-is (already prepared)
            AgentLoopState state;
            IEnumerable<ChatMessage> effectiveMessages;
            ChatOptions? effectiveOptions;

            if (thread?.ExecutionState is { } executionState)
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
                    ThreadId: thread.Id,
                    Timestamp: DateTimeOffset.UtcNow,
                    Duration: restoreStopwatch.Elapsed,
                    Iteration: state.Iteration,
                    MessageCount: state.CurrentMessages.Count);

                //     
                // RESTORE PENDING WRITES (partial failure recovery)
                //     
                if (Config?.EnablePendingWrites == true &&
                    Config?.ThreadStore != null &&
                    state.ETag != null)
                {
                    int pendingWritesCount = 0;
                    bool pendingWritesLoaded = false;

                    try
                    {
                        var pendingWrites = await Config.ThreadStore.LoadPendingWritesAsync(
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
                        yield return new CheckpointEvent(
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
                //     
                // FRESH RUN PATH: Use PreparedTurn directly (all preparation already done)
                //     

                // Initialize state with FULL unreduced history
                // PreparedTurn.MessagesForLLM contains the reduced version (for LLM calls)
                // We store the full history in state for proper message counting
                state = AgentLoopState.Initial(messages.ToList(), messageTurnId, conversationId, this.Name);

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
            // All NEW messages from this turn will be saved to thread at the end
            // PreparedTurn separates MessagesForLLM (full history) from NewInputMessages (to persist)
            
            foreach (var msg in newInputMessages)
            {
                turnHistory.Add(msg);
            }

            ChatResponse? lastResponse = null;

            // Collect all response updates to build final history
            var responseUpdates = new List<ChatResponseUpdate>();

            // OBSERVABILITY: Start telemetry and logging
            
            var turnStopwatch = System.Diagnostics.Stopwatch.StartNew();

   
            // INITIALIZE TURN CONTEXT (turn-scoped, for BeforeMessageTurn/AfterMessageTurn hooks)
            
            var turnContext = new AgentMiddlewareContext
            {
                AgentName = _name,
                ConversationId = conversationId,
                EventCoordinator = _eventCoordinator,
                CancellationToken = effectiveCancellationToken
            };
            turnContext.SetOriginalState(state);

     
            turnContext.UserMessage = newInputMessages.FirstOrDefault();
            turnContext.ConversationHistory = effectiveMessages.ToList();    
            // MIDDLEWARE: BeforeMessageTurnAsync (turn-level hook)
            await _middlewarePipeline.ExecuteBeforeMessageTurnAsync(turnContext, effectiveCancellationToken);
            
            // Apply any pending state updates from middleware
            if (turnContext.HasPendingStateUpdates)
            {
                state = turnContext.GetPendingState()!;
                turnContext.SetOriginalState(state);
            }
            
            // Drain middleware events
            while (_eventCoordinator.EventReader.TryRead(out var middlewareEvt))
                yield return middlewareEvt;


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
                    MaxIterations: state.MiddlewareState.ContinuationPermission?.CurrentExtendedLimit ?? config.MaxIterations,
                    IsTerminated: state.IsTerminated,
                    TerminationReason: state.TerminationReason,
                    ConsecutiveErrorCount: state.MiddlewareState.ErrorTracking?.ConsecutiveFailures ?? 0,
                    CompletedFunctions: new List<string>(state.CompletedFunctions),
                    AgentName: _name,
                    Timestamp: DateTimeOffset.UtcNow);

                // Drain middleware events before decision
                while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                    yield return MiddlewareEvt;

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
                    CompletedFunctionsCount: state.CompletedFunctions.Count,
                    Timestamp: DateTimeOffset.UtcNow);

                // Emit decision event
                yield return new AgentDecisionEvent(
                    AgentName: _name,
                    DecisionType: decision.GetType().Name,
                    Iteration: state.Iteration,
                    ConsecutiveFailures: state.MiddlewareState.ErrorTracking?.ConsecutiveFailures ?? 0,
                    CompletedFunctionsCount: state.CompletedFunctions.Count,
                    Timestamp: DateTimeOffset.UtcNow);

                // NOTE: Circuit breaker events are now emitted directly by CircuitBreakerIterationMiddleware
                // via context.Emit() in BeforeToolExecutionAsync.

                // Drain middleware events after decision-making, before execution
                // CRITICAL: Ensures events emitted during decision logic are yielded before LLM streaming starts
                while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                    yield return MiddlewareEvt;

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

                    // CREATE MIDDLEWARE CONTEXT
                    // Note: Tool scoping is handled by ToolScopingMiddleware in BeforeIterationAsync
                    // The middleware will filter tools and emit ScopedToolsVisibleEvent
                    var middlewareContext = new Middleware.AgentMiddlewareContext
                    {
                        AgentName = _name,
                        ConversationId = thread?.ConversationId,
                        CancellationToken = effectiveCancellationToken,
                        Iteration = state.Iteration,
                        Messages = messagesToSend.ToList(),
                        Options = effectiveOptions,
                        EventCoordinator = _eventCoordinator
                    };
                    middlewareContext.SetOriginalState(state);

                    // EXECUTE BEFORE ITERATION MIDDLEWARES
                    await _middlewarePipeline.ExecuteBeforeIterationAsync(
                        middlewareContext,
                        effectiveCancellationToken).ConfigureAwait(false);

                    // Drain events from middleware
                    while (_eventCoordinator.EventReader.TryRead(out var middlewareEvt))
                    {
                        yield return middlewareEvt;
                    }

                    // Apply any state updates from middleware
                    if (middlewareContext.HasPendingStateUpdates)
                    {
                        var pendingState = middlewareContext.GetPendingState();
                        if (pendingState != null)
                        {
                            state = pendingState;
                        }
                    }

                    // Use potentially modified values from Middlewares
                    messagesToSend = middlewareContext.Messages;
                    var scopedOptions = middlewareContext.Options;

                    // Streaming state
                    var assistantContents = new List<AIContent>();
                    var toolRequests = new List<FunctionCallContent>();
                    bool messageStarted = false;
                    bool reasoningStarted = false;
                    bool reasoningMessageStarted = false;

                    // Execute LLM call (unless skipped by Middleware)

                    if (middlewareContext.SkipLLMCall)
                    {
                        // Use cached/provided response from Middleware
                        if (middlewareContext.Response != null)
                        {
                            assistantContents.AddRange(middlewareContext.Response.Contents);

                            // Emit events for middleware-provided response (matching normal LLM flow)
                            foreach (var content in middlewareContext.Response.Contents)
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
                        toolRequests.AddRange(middlewareContext.ToolCalls);
                    }
                    else
                    {
                        // Emit iteration messages event
                        yield return new IterationMessagesEvent(
                            _name,
                            state.Iteration,
                            messagesToSend.Count(),
                            DateTimeOffset.UtcNow);

                        // Stream LLM response through middleware pipeline with IMMEDIATE event yielding
                        await foreach (var update in _middlewarePipeline.ExecuteLLMCallAsync(
                            middlewareContext,
                            () => _agentTurn.RunAsync(messagesToSend, scopedOptions, effectiveCancellationToken),
                            effectiveCancellationToken))
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
                                        yield return new Reasoning(
                                            Phase: ReasoningPhase.SessionStart,
                                            MessageId: assistantMessageId);
                                        reasoningStarted = true;
                                    }

                                    if (!reasoningMessageStarted)
                                    {
                                        yield return new Reasoning(
                                            Phase: ReasoningPhase.MessageStart,
                                            MessageId: assistantMessageId,
                                            Role: "assistant");
                                        reasoningMessageStarted = true;
                                    }

                                    yield return new Reasoning(
                                        Phase: ReasoningPhase.Delta,
                                        MessageId: assistantMessageId,
                                        Text: reasoning.Text);
                                    assistantContents.Add(reasoning);
                                }
                                else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                                {
                                    if (reasoningMessageStarted)
                                    {
                                        yield return new Reasoning(
                                            Phase: ReasoningPhase.MessageEnd,
                                            MessageId: assistantMessageId);
                                        reasoningMessageStarted = false;
                                    }
                                    if (reasoningStarted)
                                    {
                                        yield return new Reasoning(
                                            Phase: ReasoningPhase.SessionEnd,
                                            MessageId: assistantMessageId);
                                        reasoningStarted = false;
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
                        while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                        {
                            yield return MiddlewareEvt;
                        }

                        // Check for stream completion
                        if (update.FinishReason != null)
                        {
                            if (reasoningMessageStarted)
                            {
                                yield return new Reasoning(
                                    Phase: ReasoningPhase.MessageEnd,
                                    MessageId: assistantMessageId);
                                reasoningMessageStarted = false;
                            }
                            if (reasoningStarted)
                            {
                                yield return new Reasoning(
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
                    } // End of else block (LLM call not skipped)

                    // Close the message if we started one (applies to both middleware and normal flow)
                    if (messageStarted)
                    {
                        yield return new TextMessageEndEvent(assistantMessageId);
                    }
   
                    // Populate context with results
                    middlewareContext.Response = new ChatMessage(
                        ChatRole.Assistant, assistantContents);
                    middlewareContext.ToolCalls = toolRequests.AsReadOnly();
                    middlewareContext.IterationException = null;

                    // If there are tool requests, execute them immediately
                    if (toolRequests.Count > 0)
                    {
                        // Create assistant message with tool calls
                        var assistantMessage = new ChatMessage(ChatRole.Assistant, assistantContents);

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
                            ? assistantContents.ToList()
                            : assistantContents.Where(c => c is not TextReasoningContent).ToList();

                        // Add to history if there's ANY content (text OR tool calls)
                        if (historyContents.Count > 0)
                        {
                            var historyMessage = new ChatMessage(ChatRole.Assistant, historyContents);
                            turnHistory.Add(historyMessage);
                        }

                        var effectiveOptionsForTools = middlewareContext.Options ?? effectiveOptions;

                        // Yield Middleware events before tool execution
                        while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                        {
                            yield return MiddlewareEvt;
                        }

                        // EXECUTE BEFORE TOOL EXECUTION MIDDLEWARES
                        // Allows middlewares (e.g., circuit breaker) to inspect pending
                        // tool calls and prevent execution if needed.
                        middlewareContext.ToolCalls = toolRequests.AsReadOnly();

                        await _middlewarePipeline.ExecuteBeforeToolExecutionAsync(
                            middlewareContext,
                            effectiveCancellationToken).ConfigureAwait(false);

                        // Drain events from middleware
                        while (_eventCoordinator.EventReader.TryRead(out var middlewareEvt))
                        {
                            yield return middlewareEvt;
                        }

                        // Check if middleware signaled to skip tool execution (e.g., circuit breaker)
                        if (middlewareContext.SkipToolExecution)
                        {
                            // Process termination signals from middleware
                            ProcessIterationMiddleWareSignals(middlewareContext, ref state);

                            if (middlewareContext.Properties.TryGetValue("IsTerminated", out var isTerminatedByMiddleware) &&
                                isTerminatedByMiddleware is true)
                            {
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
                        var successfulFunctions = executionResult.SuccessfulFunctions;

                        // Final drain
                        while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                        {
                            yield return MiddlewareEvt;
                        }

                        // EXECUTE AFTER ITERATION MIDDLEWARES (post-tool execution)
                        middlewareContext.ToolResults = toolResultMessage.Contents
                            .OfType<FunctionResultContent>()
                            .ToList()
                            .AsReadOnly();

                        await _middlewarePipeline.ExecuteAfterIterationAsync(
                            middlewareContext,
                            effectiveCancellationToken).ConfigureAwait(false);

                        // Process middleware signals (e.g., termination, state updates)
                        ProcessIterationMiddleWareSignals(middlewareContext, ref state);

                        // Check if middleware signaled termination
                        if (middlewareContext.Properties.TryGetValue("IsTerminated", out var terminated) &&
                            terminated is true)
                        {
                            var reason = middlewareContext.Properties.TryGetValue("TerminationReason", out var r)
                                ? r?.ToString() ?? "Middleware requested termination"
                                : "Middleware requested termination";
                            state = state.Terminate(reason);
                            break;
                        }

                        //      
                        // PENDING WRITES (save successful function results immediately)
                        //      
                        if (Config?.EnablePendingWrites == true &&
                            Config?.ThreadStore != null &&
                            thread != null &&
                            state.ETag != null)
                        {
                            SavePendingWritesFireAndForget(toolResultMessage, state, Config.ThreadStore, thread.Id);
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
                        // Call AfterIterationAsync with empty ToolResults for final iteration
                        middlewareContext.ToolResults = Array.Empty<FunctionResultContent>();

                        await _middlewarePipeline.ExecuteAfterIterationAsync(
                            middlewareContext,
                            effectiveCancellationToken).ConfigureAwait(false);

                        ProcessIterationMiddleWareSignals(middlewareContext, ref state);

                        var finalResponse = ConstructChatResponseFromUpdates(responseUpdates, Config?.PreserveReasoningInHistory ?? false);
                        lastResponse = finalResponse;

                        // Add final assistant message to turnHistory before clearing responseUpdates
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
                // CHECKPOINT AFTER EACH ITERATION (if configured)
                //      

                if (thread != null &&
                    Config?.CheckpointFrequency == CheckpointFrequency.PerIteration &&
                    Config?.ThreadStore != null)
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
                            await Config.ThreadStore.SaveThreadAsync(thread, CancellationToken.None);

                            // Cleanup pending writes after successful checkpoint (fire-and-forget)
                            if (Config.EnablePendingWrites && checkpointState.ETag != null)
                            {
                                var iteration = checkpointState.Iteration;
                                var threadId = thread.Id;

                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await Config.ThreadStore.DeletePendingWritesAsync(
                                            threadId,
                                            checkpointState.ETag,
                                            CancellationToken.None);

                                        // Emit pending writes deleted event
                                        NotifyObservers(new CheckpointEvent(
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
                            NotifyObservers(new CheckpointEvent(
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
                            NotifyObservers(new CheckpointEvent(
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

                        // Also add to turnHistory for thread persistence
                        turnHistory.Add(finalAssistantMessage);
                    }
                }
            }

            // Final drain of middleware events after loop
            while (_eventCoordinator.EventReader.TryRead(out var MiddlewareEvt))
                yield return MiddlewareEvt;

            // Emit MESSAGE TURN finished event
            turnStopwatch.Stop();
            yield return new MessageTurnFinishedEvent(
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


            // Emit agent completion event
            yield return new AgentCompletionEvent(
                _name,
                state.Iteration,
                turnStopwatch.Elapsed,
                DateTimeOffset.UtcNow);
    
            // FINAL CHECKPOINT (if configured)
            if (thread != null && Config?.ThreadStore != null)
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
                        await Config.ThreadStore.SaveThreadAsync(thread, CancellationToken.None);
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
                                    await Config.ThreadStore.DeletePendingWritesAsync(
                                        threadId,
                                        finalState.ETag,
                                        CancellationToken.None);

                                    // Emit pending writes deleted event
                                    NotifyObservers(new CheckpointEvent(
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
                        NotifyObservers(new CheckpointEvent(
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

            // MIDDLEWARE: AfterMessageTurnAsync (turn-level hook)
            turnContext.FinalResponse = lastResponse;
            turnContext.TurnHistory = turnHistory;
            
            // Execute AfterMessageTurnAsync in REVERSE order (stack unwinding)
            await _middlewarePipeline.ExecuteAfterMessageTurnAsync(turnContext, effectiveCancellationToken);
            
            // Middleware may have modified turnHistory (e.g., filtered ephemeral messages)
            // Use the modified list for persistence
            
            // Drain middleware events
            while (_eventCoordinator.EventReader.TryRead(out var middlewareEvt))
                yield return middlewareEvt;

            // PERSISTENCE: Save complete turn history to thread
            if (thread != null && turnHistory.Count > 0)
            {
                try
                {
                    // Save ALL messages from this turn (user + assistant + tool)
                    // Input messages were added to turnHistory at the start of execution
                    // Middleware may have filtered this list (e.g., removed ephemeral container results)
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
        IConversationThreadStore threadStore,
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
                ResultJson = System.Text.Json.JsonSerializer.Serialize(result.Result, (JsonTypeInfo<object?>)AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object))),
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
                await threadStore.SavePendingWritesAsync(
                    threadId,
                    state.ETag!,
                    pendingWrites,
                    CancellationToken.None).ConfigureAwait(false);

                // Emit pending writes saved event
                NotifyObservers(new CheckpointEvent(
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
    /// The agent is stateless; all conversation state is managed externally or in thread parameters.
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

        await foreach (var evt in RunAgenticLoopInternal(
            turn,
            turnHistory,
            historyCompletionSource,
            thread: null,
            cancellationToken))
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
    /// <param name="messages">Messages to process</param>
    /// <param name="options">Optional chat options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of internal agent events</returns>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
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

        var internalStream = RunAgenticLoopInternal(
            turn,
            turnHistory,
            historyCompletionSource,
            thread: null,
            cancellationToken);

        await foreach (var evt in internalStream.WithCancellation(cancellationToken))
        {
            // 1. Event handlers FIRST (awaited, ordered) - for UI
            await ProcessEventHandlersAsync(evt, cancellationToken).ConfigureAwait(false);

            // 2. Yield event (for direct stream consumers)
            yield return evt;

            // 3. Observers LAST (fire-and-forget) - for telemetry
            NotifyObservers(evt);
        }
    }

    /// <summary>
    /// Runs the agent with a simple string message.
    /// Convenience overload that wraps the message as a user ChatMessage.
    /// </summary>
    /// <param name="userMessage">The user's message text</param>
    /// <param name="thread">Thread containing conversation history</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of internal agent events</returns>
    public IAsyncEnumerable<AgentEvent> RunAsync(
        string userMessage,
        ConversationThread thread,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(
            [new ChatMessage(ChatRole.User, userMessage)],
            options: null,
            thread: thread,
            cancellationToken: cancellationToken);
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
    public async IAsyncEnumerable<AgentEvent> RunAsync(
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

        //     
        // PREPARE TURN: Load history, apply reduction, merge options (Option 2 pattern)
        //     
        var inputMessages = messages?.ToList() ?? new List<ChatMessage>();
        var turn = await _messageProcessor.PrepareTurnAsync(
            thread,
            inputMessages,
            options,
            Name,
            cancellationToken);

        // Note: History reduction caching is now handled by HistoryReductionMiddleware
        // The middleware updates its state directly via context.UpdateState<HistoryReductionStateData>()

        var turnHistory = new List<ChatMessage>();
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);

        //     
        // EXECUTE AGENTIC LOOP with PreparedTurn
        //     
        var internalStream = RunAgenticLoopInternal(
            turn,
            turnHistory,
            historyCompletionSource,
            thread: thread,  // ← CRITICAL: Pass thread for persistence and checkpointing
            cancellationToken);

        await foreach (var evt in internalStream.WithCancellation(cancellationToken))
        {
            // 1. Event handlers FIRST (awaited, ordered) - for UI
            await ProcessEventHandlersAsync(evt, cancellationToken).ConfigureAwait(false);

            // 2. Yield event (for direct stream consumers)
            yield return evt;

            // 3. Observers LAST (fire-and-forget) - for telemetry
            NotifyObservers(evt);
        }
    }

    //      
    // ITERATION Middleware SUPPORT
    //      

    /// <summary>
    /// Processes signals from iteration Middlewares (cleanup requests, etc.).
    /// Called after iteration Middlewares complete.
    /// </summary>
    /// <param name="context">The iteration Middleware context with potential signals</param>
    /// <param name="state">The current agent loop state (may be updated based on signals)</param>
    private void ProcessIterationMiddleWareSignals(
        Middleware.AgentMiddlewareContext context,
        ref AgentLoopState state)
    {
        if (context.GetPendingState() is { } pendingState)
        {
            state = pendingState;
        }

        // Handle termination signals from middleware
        if (context.Properties.TryGetValue("IsTerminated", out var isTerminated) &&
            isTerminated is true)
        {
            var terminationReason = context.Properties.TryGetValue("TerminationReason", out var reason)
                ? (reason?.ToString() ?? "Terminated by middleware")
                : "Terminated by middleware";

            state = state.Terminate(terminationReason);
        }
    }

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
        var errorState = this.MiddlewareState.ErrorTracking;
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
    private readonly IEventCoordinator _eventCoordinator;
    private readonly AgentMiddlewarePipeline _middlewarePipeline;
    private readonly ErrorHandlingConfig? _errorHandlingConfig;
    private readonly IList<AITool>? _serverConfiguredTools;
    private readonly AgenticLoopConfig? _agenticLoopConfig;

    public FunctionCallProcessor(
        IEventCoordinator eventCoordinator,
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
        // Route to appropriate execution strategy
        // For single tool calls, inline execution (no parallel overhead)
        if (toolRequests.Count <= 1)
        {
            return await ExecuteSequentiallyAsync(
                currentHistory, toolRequests, options, agentLoopState,
                cancellationToken).ConfigureAwait(false);
        }

        // For multiple tools, use parallel execution with throttling
        return await ExecuteInParallelAsync(
            currentHistory, toolRequests, options, agentLoopState,
            cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        var allContents = new List<AIContent>();
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
        CancellationToken cancellationToken)
    {
        // PHASE 1: Batch permission check via BeforeParallelFunctionsAsync hook
        // Build function map and collect parallel function information
        var functionMap = FunctionMapBuilder.BuildMergedMap(_serverConfiguredTools, options?.Tools);
        var parallelFunctions = new List<ParallelFunctionInfo>();

        foreach (var toolRequest in toolRequests)
        {
            if (string.IsNullOrEmpty(toolRequest.Name))
                continue;

            var function = FunctionMapBuilder.FindFunction(toolRequest.Name, functionMap);
            if (function == null)
                continue;

            // Extract plugin/skill metadata (same logic as ProcessFunctionCallsAsync)
            string? pluginTypeName = null;
            if (function.AdditionalProperties?.TryGetValue("ParentPlugin", out var parentPluginCtx) == true)
            {
                pluginTypeName = parentPluginCtx as string;
            }
            else if (function.AdditionalProperties?.TryGetValue("PluginName", out var pluginNameProp) == true)
            {
                pluginTypeName = pluginNameProp as string;
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
                toolRequest.Arguments ?? new Dictionary<string, object?>(),
                pluginTypeName,
                skillName));
        }

        // Create middleware context for batch permission check
        var batchContext = new AgentMiddlewareContext
        {
            AgentName = agentLoopState.AgentName,
            ConversationId = agentLoopState.ConversationId,
            Iteration = agentLoopState.Iteration,
            Messages = currentHistory,
            Options = options,
            ParallelFunctions = parallelFunctions,
            EventCoordinator = _eventCoordinator,
            CancellationToken = cancellationToken
        };
        batchContext.SetOriginalState(agentLoopState);

        // Execute BeforeParallelFunctionsAsync middleware hooks
        await _middlewarePipeline.ExecuteBeforeParallelFunctionsAsync(
            batchContext, cancellationToken).ConfigureAwait(false);

        // Apply any state updates from middleware
        if (batchContext.HasPendingStateUpdates)
        {
            agentLoopState = batchContext.GetPendingState() ?? agentLoopState;
        }

        // All tools will be processed - individual permission checks happen in ProcessFunctionCallsAsync
        // (those checks will use the BatchPermissionState populated by BeforeParallelFunctionsAsync)
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
        CancellationToken cancellationToken)
    {
        var resultMessages = new List<ChatMessage>();

        // Build function map per execution (Microsoft pattern for thread-safety)
        // This avoids shared mutable state and stale cache issues
        // Merge server-configured tools with request tools (request tools take precedence)
        var functionMap = FunctionMapBuilder.BuildMergedMap(_serverConfiguredTools, options?.Tools);

        // Process each function call through the unified middleware pipeline
        foreach (var functionCall in functionCallContents)
        {
            // Skip functions without names (safety check)
            if (string.IsNullOrEmpty(functionCall.Name))
                continue;

            // Resolve the function from the merged function map
            var function = FunctionMapBuilder.FindFunction(functionCall.Name, functionMap);

            // Extract scope information for middleware scoping
            string? pluginTypeName = null;
            if (function?.AdditionalProperties?.TryGetValue("ParentPlugin", out var parentPluginCtx) == true)
            {
                pluginTypeName = parentPluginCtx as string;
            }
            else if (function?.AdditionalProperties?.TryGetValue("PluginName", out var pluginNameProp) == true)
            {
                // For container functions, PluginName IS the plugin type
                pluginTypeName = pluginNameProp as string;
            }

            // Fallback: Try function-to-plugin mapping
            if (string.IsNullOrEmpty(pluginTypeName) && functionCall.Name != null)
            {
                // Plugin metadata comes from AIFunction.AdditionalProperties (set by source generator)
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

            // Create unified AgentMiddlewareContext for this function call
            var middlewareContext = new AgentMiddlewareContext
            {
                AgentName = agentLoopState.AgentName,
                ConversationId = agentLoopState.ConversationId,
                Iteration = agentLoopState.Iteration,
                Messages = messages,
                Options = options,
                Function = function,
                FunctionCallId = functionCall.CallId,
                FunctionArguments = functionCall.Arguments ?? new Dictionary<string, object?>(),
                PluginName = pluginTypeName,
                SkillName = skillName,
                IsSkillContainer = isSkillContainer,
                EventCoordinator = _eventCoordinator,
                CancellationToken = cancellationToken
            };
            middlewareContext.SetOriginalState(agentLoopState);

            // Store CallId in properties for extensibility
            middlewareContext.Properties["CallId"] = functionCall.CallId;

            // Check if function is unknown and TerminateOnUnknownCalls is enabled
            if (function == null && _agenticLoopConfig?.TerminateOnUnknownCalls == true)
            {
                // Terminate the loop - don't process this or any remaining functions
                // The function call will be returned to the caller for handling (e.g., multi-agent handoff)
                middlewareContext.Properties["IsTerminated"] = true;

                // Don't add any result message - let the caller handle the unknown function
                break;
            }

            // Execute BeforeSequentialFunctionAsync middleware hooks (permission check happens here)
            var shouldExecute = await _middlewarePipeline.ExecuteBeforeSequentialFunctionAsync(
                middlewareContext, cancellationToken).ConfigureAwait(false);

            // If middleware blocked execution (permission denied), record the denial and skip execution
            if (!shouldExecute || middlewareContext.BlockFunctionExecution)
            {
                var denialResult = middlewareContext.FunctionResult ?? "Permission denied";

                // Note: Function completion tracking is handled by caller using state updates

                var denialResultContent = new FunctionResultContent(functionCall.CallId, denialResult);
                var denialMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { denialResultContent });
                resultMessages.Add(denialMessage);
                continue; // Skip to next function call
            }

            // Permission approved - proceed with function execution
            Exception? executionException = null;
            try
            {
                // Handle function not found case
                if (middlewareContext.Function is null)
                {
                    // Generate basic error message
                    // Note: ToolScopingMiddleware may have already set a more detailed message in BeforeToolExecutionAsync
                    if (middlewareContext.FunctionResult == null)
                    {
                        middlewareContext.FunctionResult = $"Function '{functionCall.Name ?? "Unknown"}' not found.";
                    }
                }
                else
                {
                    // Execute the function through middleware pipeline
                    // This includes retry, timeout, and any custom middleware
                    middlewareContext.FunctionResult = await _middlewarePipeline.ExecuteFunctionAsync(
                        middlewareContext,
                        innerCall: async () =>
                        {
                            // Set AsyncLocal function invocation context for ambient access
                            Agent.CurrentFunctionContext = middlewareContext;

                            try
                            {
                                // THIS IS THE ACTUAL FUNCTION EXECUTION (innermost call)
                                var args = new AIFunctionArguments(middlewareContext.FunctionArguments ?? new Dictionary<string, object?>());
                                return await middlewareContext.Function.InvokeAsync(args, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                // Format error for LLM
                                return FormatErrorForLLM(ex, middlewareContext.Function.Name);
                            }
                            finally
                            {
                                // Always clear the context after function completes
                                Agent.CurrentFunctionContext = null;
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Emit error event before handling
                middlewareContext.Emit(new MiddlewareErrorEvent(
                    "FunctionExecution",
                    $"Error executing function '{functionCall.Name}': {ex.Message}",
                    ex));

                // Mark context as terminated and set error result
                middlewareContext.Properties["IsTerminated"] = true;
                middlewareContext.FunctionResult = $"Error executing function '{functionCall.Name}': {ex.Message}";
                executionException = ex;
            }

            // Update exception in context for AfterFunctionAsync
            middlewareContext.FunctionException = executionException;

            try
            {
                await _middlewarePipeline.ExecuteAfterFunctionAsync(
                    middlewareContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception afterEx)
            {
                // Log AfterFunction errors but don't fail the function execution
                middlewareContext.Emit(new MiddlewareErrorEvent(
                    "AfterFunctionMiddleware",
                    $"Error in AfterFunction middleware: {afterEx.Message}",
                    afterEx));
            }

            // Note: Function completion tracking is handled by caller using state updates

            var functionResult = new FunctionResultContent(functionCall.CallId, middlewareContext.FunctionResult);
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
    /// Loads thread history, merges options, adds system instructions, applies reduction (with caching), and Middlewares messages.
    /// </summary>
    /// <param name="thread">Conversation thread (null for stateless execution).</param>
    /// <param name="inputMessages">NEW messages from the caller (to be added to history).</param>
    /// <param name="options">Chat options to merge with defaults.</param>
    /// <param name="agentName">Agent name for logging/Middlewareing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>PreparedTurn with all state needed for execution.</returns>
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

#region Error Formatting Helper

/// <summary>
/// Helper class for formatting detailed error messages with provider-specific information.
/// </summary>
internal static class ErrorFormatter
{
    /// <summary>
    /// Formats an exception with detailed error information for display to users.
    /// Extracts provider-specific error details using the error handler.
    /// <summary>
    /// Builds a human-readable, detailed error message using provider-specific details when available.
    /// </summary>
    /// <param name="ex">The exception to format.</param>
    /// <param name="errorHandler">Optional provider error handler used to extract structured provider error details.</param>
    /// <returns>A formatted error string containing provider-specific fields when present (category, message, HTTP status, error code/type, request id, retry-after, raw details) and otherwise the exception type and message; includes inner exception message if available.</returns>
    internal static string FormatDetailedError(Exception ex, HPD.Providers.Core.IProviderErrorHandler? errorHandler)
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
#region BidirectionalEventCoordinator

/// <summary>
/// Manages bidirectional event coordination for request/response patterns.
/// Used by Middlewares (permissions, clarifications) and supports nested agent communication.
/// Thread-safe for concurrent event emission and response coordination.
/// </summary>
public class BidirectionalEventCoordinator : IEventCoordinator, IDisposable
{
    /// <summary>
    /// Shared event channel for all events.
    /// Unbounded to prevent blocking during event emission.
    /// Thread-safe: Multiple producers (Middlewares), single consumer (background drainer).
    /// </summary>
    private readonly Channel<AgentEvent> _eventChannel;

    /// <summary>
    /// Response coordination for bidirectional patterns.
    /// Maps requestId -> (TaskCompletionSource, CancellationTokenSource)
    /// Thread-safe: ConcurrentDictionary handles concurrent access.
    /// </summary>
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<AgentEvent>, CancellationTokenSource)>
        _responseWaiters = new();

    /// <summary>
    /// Parent coordinator for event bubbling in nested agent scenarios.
    /// Set via SetParent() when an agent is used as a tool by another agent.
    /// Events emitted to this coordinator will also bubble to the parent.
    /// </summary>
    private BidirectionalEventCoordinator? _parentCoordinator;

    /// <summary>
    /// Execution context for automatic attachment to events.
    /// Used to attach ExecutionContext to events that don't already have it.
    /// Decoupled from Agent for testability and clean architecture.
    /// Can be set after construction via SetExecutionContext() for lazy initialization.
    /// </summary>
    private AgentExecutionContext? _executionContext;

    /// <summary>
    /// Creates a new bidirectional event coordinator.
    /// </summary>
    /// <param name="executionContext">The execution context for event attribution (optional)</param>
    public BidirectionalEventCoordinator(AgentExecutionContext? executionContext = null)
    {
        _executionContext = executionContext;
        _eventChannel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleWriter = false,  // Multiple Middlewares can emit concurrently
            SingleReader = true,   // Only background drainer reads
            AllowSynchronousContinuations = false  // Performance & safety
        });
    }

    /// <summary>
    /// Sets the execution context for event attribution.
    /// Called when the execution context is lazily initialized (e.g., on first RunAsync).
    /// Thread-safe: Can be called from any thread.
    /// </summary>
    /// <param name="executionContext">The execution context to attach to events</param>
    public void SetExecutionContext(AgentExecutionContext executionContext)
    {
        _executionContext = executionContext ?? throw new ArgumentNullException(nameof(executionContext));
    }

    /// <summary>
    /// Gets the channel writer for event emission.
    /// Used by Middlewares and contexts to emit events directly to the channel.
    /// </summary>
    /// <remarks>
    /// Note: For most use cases, prefer Emit() method over direct channel access
    /// as it handles event bubbling to parent coordinators.
    /// </remarks>
    public ChannelWriter<AgentEvent> EventWriter => _eventChannel.Writer;

    /// <summary>
    /// Gets the channel reader for event consumption.
    /// Used by the agent's background drainer to read events.
    /// </summary>
    public ChannelReader<AgentEvent> EventReader => _eventChannel.Reader;

    /// <summary>
    /// Sets the parent coordinator for event bubbling in nested agent scenarios.
    /// When a parent is set, all events emitted via Emit() will bubble to the parent.
    /// </summary>
    /// <param name="parent">The parent coordinator to bubble events to</param>
    /// <exception cref="ArgumentNullException">If parent is null</exception>
    /// <exception cref="InvalidOperationException">If setting this parent would create a cycle</exception>
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
    public void Emit(AgentEvent evt)
    {
        if (evt == null)
            throw new ArgumentNullException(nameof(evt));

        // Auto-attach execution context if not already set
        // This enables event attribution in multi-agent systems
        var eventToEmit = evt;
        if (evt.ExecutionContext == null && _executionContext != null)
        {
            eventToEmit = evt with { ExecutionContext = _executionContext };
        }

        // Emit to local channel
        if (!_eventChannel.Writer.TryWrite(eventToEmit))
        {
            // Channel is closed or full - log but don't throw to avoid crashing the agent
            System.Diagnostics.Debug.WriteLine($"Failed to write event to channel: {eventToEmit.GetType().Name}");
        }

        // Bubble to parent coordinator (if nested agent)
        // This creates a chain: NestedAgent -> Orchestrator -> RootOrchestrator
        _parentCoordinator?.Emit(eventToEmit);
    }

    /// <summary>
    /// Sends a response to a Middleware waiting for a specific request.
    /// Called by external handlers when user provides input.
    /// Thread-safe: Can be called from any thread.
    /// </summary>
    /// <param name="requestId">The unique identifier for the request</param>
    /// <param name="response">The response event to deliver</param>
    /// <exception cref="ArgumentNullException">If response is null</exception>
    public void SendResponse(string requestId, AgentEvent response)
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
    public async Task<T> WaitForResponseAsync<T>(
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken) where T : AgentEvent
    {
        var tcs = new TaskCompletionSource<AgentEvent>();
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


#region Tool Execution Result Types
/// <summary>
/// Structured result from tool execution, replacing the 5-tuple return type.
/// Provides strongly-typed access to execution outcomes.
/// </summary>
internal record ToolExecutionResult(
    ChatMessage Message,
    HashSet<string> SuccessfulFunctions);

#endregion