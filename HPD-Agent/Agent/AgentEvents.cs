using System.Collections.Immutable;


namespace HPD.Agent;
#region Internal Events
/// <summary>
/// Provides hierarchical context about which agent emitted an event.
/// Enables event attribution and filtering in multi-agent systems.
/// </summary>
public record AgentExecutionContext
{
    /// <summary>
    /// The immediate agent that emitted this event (e.g., "WeatherExpert")
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// Hierarchical agent ID showing full execution path.
    /// Format: "parent-abc12345-weatherExpert-def67890"
    /// </summary>
    public required string AgentId { get; init; }

    /// <summary>
    /// Parent agent ID (null if this is root orchestrator)
    /// </summary>
    public string? ParentAgentId { get; init; }

    /// <summary>
    /// Full agent chain from root to current.
    /// Example: ["Orchestrator", "DomainExpert", "WeatherExpert"]
    /// </summary>
    public IReadOnlyList<string> AgentChain { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Depth in the agent hierarchy (0 = root, 1 = direct SubAgent, etc.)
    /// </summary>
    public int Depth { get; init; }

    /// <summary>
    /// Is this event from a SubAgent (vs root orchestrator)?
    /// </summary>
    public bool IsSubAgent => Depth > 0;
}



#endregion

#region Priority and Direction Enums

/// <summary>
/// Priority levels for event processing.
/// Higher priority events are processed before lower priority events.
/// Events at the same priority level are processed in sequence order (FIFO).
/// </summary>
public enum EventPriority
{
    /// <summary>
    /// Highest priority. User-initiated control: stop, cancel, abort.
    /// These events are NEVER queued behind lower priority events.
    /// Use for: User cancellation, emergency stops, critical errors.
    /// </summary>
    Immediate = 0,

    /// <summary>
    /// System control signals: state changes, configuration updates, interruption acks.
    /// Processed before normal events but after immediate events.
    /// Use for: Interruption acknowledgments, state transitions, control flow.
    /// </summary>
    Control = 1,

    /// <summary>
    /// Standard data flow. Default priority for most events.
    /// Use for: Text deltas, tool results, normal agent responses.
    /// </summary>
    Normal = 2,

    /// <summary>
    /// Lowest priority. Background operations: metrics, telemetry, observability.
    /// Processed only when no higher priority events are pending.
    /// Use for: Logging events, metrics emission, non-critical notifications.
    /// </summary>
    Background = 3
}

/// <summary>
/// Direction of event flow through the agent pipeline.
/// </summary>
public enum EventDirection
{
    /// <summary>
    /// Normal flow: user input → agent processing → response output.
    /// Events flow through middleware in registration order.
    /// </summary>
    Downstream,

    /// <summary>
    /// Control flow: cancellation/interruption signals flowing back to source.
    /// Events flow through middleware in reverse order.
    /// Used for: Cancellation propagation, abort signals, upstream notifications.
    /// </summary>
    Upstream
}

#endregion

#region Interruption Types

/// <summary>
/// Source of an interruption request.
/// </summary>
public enum InterruptionSource
{
    /// <summary>User-initiated (clicked stop, pressed Ctrl+C, etc.)</summary>
    User,

    /// <summary>System-initiated (timeout, circuit breaker, error threshold)</summary>
    System,

    /// <summary>Parent agent aborting child agent</summary>
    Parent,

    /// <summary>Middleware-initiated (permission denied, validation failed)</summary>
    Middleware
}

/// <summary>
/// Requests interruption of active streams or operations.
/// </summary>
public record InterruptionRequestEvent(
    string? StreamId,
    string Reason,
    InterruptionSource Source) : AgentEvent;

#endregion

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
public abstract record AgentEvent
{
    /// <summary>
    /// Context about which agent emitted this event (optional for backwards compatibility).
    /// Automatically attached by BidirectionalEventCoordinator.Emit() if not already set.
    /// </summary>
    public AgentExecutionContext? ExecutionContext { get; init; }

    /// <summary>
    /// Processing priority for this event.
    /// Defaults to Normal for standard data flow events.
    /// </summary>
    public EventPriority Priority { get; init; } = EventPriority.Normal;

    /// <summary>
    /// Monotonically increasing sequence number assigned by the coordinator.
    /// Used for ordering events within the same priority level.
    /// </summary>
    public long SequenceNumber { get; internal set; }

    /// <summary>
    /// Direction of event flow through the middleware pipeline.
    /// </summary>
    public EventDirection Direction { get; init; } = EventDirection.Downstream;

    /// <summary>
    /// Stream ID for grouping related interruptible events.
    /// Null if this event is not part of a managed stream.
    /// </summary>
    public string? StreamId { get; init; }

    /// <summary>
    /// Whether this event can be dropped on stream interruption.
    /// True for: Data chunks, progress updates, intermediate results.
    /// False for: Completion markers, error reports, cleanup events.
    /// </summary>
    public bool CanInterrupt { get; init; } = true;
}

#region Message Turn Events (Entire User Interaction)

/// <summary>
/// Emitted when a message turn starts (user sends message, agent begins processing)
/// This represents the START of the entire multi-step agent execution.
/// </summary>
public record MessageTurnStartedEvent(
    string MessageTurnId,
    string ConversationId,
    string AgentName,
    DateTimeOffset Timestamp) : AgentEvent;

/// <summary>
/// Emitted when a message turn completes successfully
/// This represents the END of the entire agent execution for this user message.
/// </summary>
public record MessageTurnFinishedEvent(
    string MessageTurnId,
    string ConversationId,
    string AgentName,
    TimeSpan Duration,
    DateTimeOffset Timestamp) : AgentEvent;

/// <summary>
/// Emitted when an error occurs during message turn execution
/// </summary>
public record MessageTurnErrorEvent(string Message, Exception? Exception = null) : AgentEvent;

#endregion

#region Agent Turn Events (Single LLM Call Within Message Turn)

/// <summary>
/// Emitted when an agent turn starts (single LLM call within the agentic loop)
/// An agent turn represents one iteration where the LLM processes messages and responds.
/// Multiple agent turns may occur in one message turn when tools are called.
/// </summary>
public record AgentTurnStartedEvent(int Iteration) : AgentEvent;

/// <summary>
/// Emitted when an agent turn completes
/// </summary>
public record AgentTurnFinishedEvent(int Iteration) : AgentEvent;

/// <summary>
/// Emitted during agent execution to expose internal state for testing/debugging.
/// NOT intended for production use - only for characterization tests and debugging.
/// </summary>
public record StateSnapshotEvent(
    int CurrentIteration,
    int MaxIterations,
    bool IsTerminated,
    string? TerminationReason,
    int ConsecutiveErrorCount,
    List<string> CompletedFunctions,
    string AgentName,
    DateTimeOffset Timestamp) : AgentEvent;

#endregion

#region Content Events (Within an Agent Turn)

/// <summary>
/// Emitted when the agent starts producing text content
/// </summary>
public record TextMessageStartEvent(string MessageId, string Role) : AgentEvent;

/// <summary>
/// Emitted when the agent produces text content (streaming delta)
/// </summary>
public record TextDeltaEvent(string Text, string MessageId) : AgentEvent;

/// <summary>
/// Emitted when the agent finishes producing text content
/// </summary>
public record TextMessageEndEvent(string MessageId) : AgentEvent;

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
public record Reasoning(
    ReasoningPhase Phase,
    string MessageId,
    string? Role = null,
    string? Text = null
) : AgentEvent;

#endregion

#region Tool Events

/// <summary>
/// Emitted when the agent requests a tool call
/// </summary>
public record ToolCallStartEvent(
    string CallId,
    string Name,
    string MessageId) : AgentEvent;

/// <summary>
/// Emitted when a tool call's arguments are fully available
/// </summary>
public record ToolCallArgsEvent(string CallId, string ArgsJson) : AgentEvent;

/// <summary>
/// Emitted when a tool call completes execution
/// </summary>
public record ToolCallEndEvent(string CallId) : AgentEvent;

/// <summary>
/// Emitted when a tool call result is available
/// </summary>
public record ToolCallResultEvent(
    string CallId,
    string Result) : AgentEvent;

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
/// Handler should prompt user and send PermissionResponseEvent.
/// </summary>
public record PermissionRequestEvent(
    string PermissionId,
    string SourceName,
    string FunctionName,
    string? Description,
    string CallId,
    IDictionary<string, object?>? Arguments) : AgentEvent, IPermissionEvent;

/// <summary>
/// Response to permission request.
/// Sent by external handler back to waiting Middleware.
/// </summary>
public record PermissionResponseEvent(
    string PermissionId,
    string SourceName,
    bool Approved,
    string? Reason = null,
    PermissionChoice Choice = PermissionChoice.Ask) : AgentEvent, IPermissionEvent;

/// <summary>
/// Emitted after permission is approved (for observability).
/// </summary>
public record PermissionApprovedEvent(
    string PermissionId,
    string SourceName) : AgentEvent, IPermissionEvent;

/// <summary>
/// Emitted after permission is denied (for observability).
/// </summary>
public record PermissionDeniedEvent(
    string PermissionId,
    string SourceName,
    string Reason) : AgentEvent, IPermissionEvent;

/// <summary>
/// Middleware requests permission to continue beyond max iterations.
/// </summary>
public record ContinuationRequestEvent(
    string ContinuationId,
    string SourceName,
    int CurrentIteration,
    int MaxIterations) : AgentEvent, IPermissionEvent
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
public record ContinuationResponseEvent(
    string ContinuationId,
    string SourceName,
    bool Approved,
    int ExtensionAmount = 0) : AgentEvent, IPermissionEvent
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
/// Handler should prompt user and send ClarificationResponseEvent.
/// </summary>
public record ClarificationRequestEvent(
    string RequestId,
    string SourceName,
    string Question,
    string? AgentName = null,
    string[]? Options = null) : AgentEvent, IClarificationEvent;

/// <summary>
/// Response to clarification request.
/// Sent by external handler back to waiting agent/plugin.
/// </summary>
public record ClarificationResponseEvent(
    string RequestId,
    string SourceName,
    string Question,
    string Answer) : AgentEvent, IClarificationEvent;

/// <summary>
/// Middleware reports progress (one-way, no response needed).
/// </summary>
public record MiddlewareProgressEvent(
    string SourceName,
    string Message,
    int? PercentComplete = null) : AgentEvent, IBidirectionalEvent;

/// <summary>
/// Middleware reports an error (one-way, no response needed).
/// </summary>
public record MiddlewareErrorEvent(
    string SourceName,
    string ErrorMessage,
    Exception? Exception = null) : AgentEvent, IBidirectionalEvent;

#endregion

#region Observability Events (Internal diagnostics)

/// <summary>
/// Marker interface to distinguish observability events from protocol events.
/// Observability events are designed for logging, metrics, and monitoring.
/// They are processed by IAgentEventObserver implementations.
/// </summary>
public interface IObservabilityEvent { }

/// <summary>
/// Emitted when Collapsed tools visibility is determined for an iteration.
/// Contains full snapshot of what tools the LLM can see.
/// </summary>
public record CollapsedToolsVisibleEvent(
    string AgentName,
    int Iteration,
    IReadOnlyList<string> VisibleToolNames,
    ImmutableHashSet<string> ExpandedPlugins,
    ImmutableHashSet<string> ExpandedSkills,
    int TotalToolCount,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when a plugin or skill container is expanded.
/// </summary>
public record ContainerExpandedEvent(
    string ContainerName,
    ContainerType Type,
    IReadOnlyList<string> UnlockedFunctions,
    int Iteration,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

public enum ContainerType { Plugin, Skill }

/// <summary>
/// Emitted when Middleware pipeline execution starts.
/// </summary>
public record MiddlewarePipelineStartEvent(
    string FunctionName,
    int MiddlewareCount,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when Middleware pipeline execution completes.
/// </summary>
public record MiddlewarePipelineEndEvent(
    string FunctionName,
    TimeSpan Duration,
    bool Success,
    string? ErrorMessage,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when a permission check occurs.
/// </summary>
public record PermissionCheckEvent(
    string FunctionName,
    bool IsApproved,
    string? DenialReason,
    string AgentName,
    int Iteration,
    TimeSpan Duration,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when an iteration starts with full state snapshot.
/// </summary>
public record IterationStartEvent(
    string AgentName,
    int Iteration,
    int MaxIterations,
    int CurrentMessageCount,
    int HistoryMessageCount,
    int TurnHistoryMessageCount,
    int CompletedFunctionsCount,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when circuit breaker is triggered.
/// </summary>
public record CircuitBreakerTriggeredEvent(
    string AgentName,
    string FunctionName,
    int ConsecutiveCount,
    int Iteration,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when history reduction cache is checked.
/// </summary>
public record HistoryReductionCacheEvent(
    string AgentName,
    bool IsHit,
    DateTime? ReductionCreatedAt,
    int? SummarizedUpToIndex,
    int CurrentMessageCount,
    int? TokenSavings,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

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
public record CheckpointEvent(
    CheckpointOperation Operation,
    string SessionId,
    DateTimeOffset Timestamp,
    TimeSpan? Duration = null,
    int? Iteration = null,
    int? WriteCount = null,
    int? SizeBytes = null,
    int? MessageCount = null,
    bool? Success = null,
    string? ErrorMessage = null
) : AgentEvent, IObservabilityEvent;

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
) : AgentEvent, IObservabilityEvent;

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
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when a function execution is being retried due to an error.
/// Emitted by FunctionRetryMiddleware for observability.
/// </summary>
/// <param name="FunctionName">The name of the function being retried</param>
/// <param name="Attempt">The current retry attempt number (1-based)</param>
/// <param name="MaxRetries">Maximum number of retries allowed</param>
/// <param name="Delay">Time to wait before retrying</param>
/// <param name="Exception">The exception that caused the retry</param>
/// <param name="ExceptionType">The type name of the exception</param>
/// <param name="ErrorMessage">The error message from the exception</param>
public record FunctionRetryEvent(
    string FunctionName,
    int Attempt,
    int MaxRetries,
    TimeSpan Delay,
    Exception Exception,
    string ExceptionType,
    string ErrorMessage
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when delta sending is activated.
/// </summary>
public record DeltaSendingActivatedEvent(
    string AgentName,
    int MessageCountSent,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when plan mode is activated.
/// </summary>
public record PlanModeActivatedEvent(
    string AgentName,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when a nested agent is invoked.
/// </summary>
public record NestedAgentInvokedEvent(
    string OrchestratorName,
    string ChildAgentName,
    int NestingDepth,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when document processing occurs.
/// </summary>
public record DocumentProcessedEvent(
    string AgentName,
    string DocumentPath,
    long SizeBytes,
    TimeSpan Duration,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when message preparation completes.
/// </summary>
public record InternalMessagePreparedEvent(
    string AgentName,
    int Iteration,
    int FinalMessageCount,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when a bidirectional event is processed.
/// </summary>
public record BidirectionalEventProcessedEvent(
    string AgentName,
    string EventType,
    bool RequiresResponse,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when agent makes a decision.
/// </summary>
public record AgentDecisionEvent(
    string AgentName,
    string DecisionType,
    int Iteration,
    int ConsecutiveFailures,
    int CompletedFunctionsCount,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when agent completes successfully.
/// </summary>
public record AgentCompletionEvent(
    string AgentName,
    int TotalIterations,
    TimeSpan Duration,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when iteration messages are logged.
/// </summary>
public record IterationMessagesEvent(
    string AgentName,
    int Iteration,
    int MessageCount,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;

/// <summary>
/// Emitted when middleware schema changes are detected during checkpoint restoration.
/// Used for monitoring, alerting, and audit trails.
/// </summary>
public record SchemaChangedEvent(
    string? OldSignature,
    string NewSignature,
    IReadOnlyList<string> RemovedTypes,
    IReadOnlyList<string> AddedTypes,
    bool IsUpgrade,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent
{
    public SchemaChangedEvent(
        string? oldSignature,
        string newSignature,
        DateTimeOffset timestamp)
        : this(
            OldSignature: oldSignature,
            NewSignature: newSignature,
            RemovedTypes: Array.Empty<string>(),
            AddedTypes: Array.Empty<string>(),
            IsUpgrade: oldSignature == null,
            Timestamp: timestamp)
    {
    }
}

/// <summary>
/// Emitted by ToolCollapsingMiddleware at iteration start to report Collapsing state.
/// Tracks how many plugins and skills have been expanded.
/// </summary>
public record CollapsingStateEvent(
    string AgentName,
    int Iteration,
    int ExpandedPluginsCount,
    int ExpandedSkillsCount,
    DateTimeOffset Timestamp
) : AgentEvent, IObservabilityEvent;




#endregion

/// <summary>
/// Abstraction for bidirectional event coordination.
/// Enables middlewares to emit events and wait for responses
/// without knowing about Agent internals.
/// </summary>
/// <remarks>
/// <para>
/// This interface decouples middleware from Agent, enabling:
/// - Clean middleware architecture (no agent reference needed)
/// - Easy unit testing (mock the interface)
/// - Future implementations (e.g., distributed event coordination)
/// </para>
/// <para>
/// <b>Threading:</b> All methods must be thread-safe. Multiple middlewares
/// can emit events concurrently.
/// </para>
/// <para>
/// <b>Event Bubbling:</b> Implementations should support event bubbling
/// for nested agent scenarios (child agent events visible to parent).
/// </para>
/// </remarks>
public interface IEventCoordinator
{
    /// <summary>
    /// Emits an event to handlers. Fire-and-forget.
    /// Events bubble to parent coordinators in nested agent scenarios.
    /// </summary>
    /// <param name="evt">The event to emit</param>
    /// <exception cref="ArgumentNullException">If event is null</exception>
    /// <remarks>
    /// <para>
    /// This is the primary way for middlewares to communicate with external handlers.
    /// Events are written to a channel and processed asynchronously.
    /// </para>
    /// <para>
    /// <b>Thread-safe:</b> Can be called from any thread.
    /// </para>
    /// <para>
    /// <b>Non-blocking:</b> Returns immediately (unbounded channel).
    /// </para>
    /// </remarks>
    void Emit(AgentEvent evt);

    /// <summary>
    /// Sends a response to a waiting request.
    /// Called by handlers when user provides input.
    /// </summary>
    /// <param name="requestId">The unique identifier for the request</param>
    /// <param name="response">The response event to deliver</param>
    /// <exception cref="ArgumentNullException">If response is null</exception>
    /// <remarks>
    /// <para>
    /// If requestId is not found (e.g., timeout already occurred),
    /// the call is silently ignored. This is intentional to avoid
    /// race conditions between timeout and response.
    /// </para>
    /// <para>
    /// <b>Thread-safe:</b> Can be called from any thread.
    /// </para>
    /// </remarks>
    void SendResponse(string requestId, AgentEvent response);

    /// <summary>
    /// Waits for a response to a previously emitted request.
    /// Used for request/response patterns (permissions, clarifications).
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
    /// <para>
    /// This method is used by middlewares that need bidirectional communication:
    /// </para>
    /// <list type="number">
    /// <item>Middleware emits request event (e.g., PermissionRequestEvent)</item>
    /// <item>Middleware calls WaitForResponseAsync() - BLOCKS HERE</item>
    /// <item>Handler receives request event (via agent's event loop)</item>
    /// <item>User provides input</item>
    /// <item>Handler calls SendResponse()</item>
    /// <item>Middleware receives response and continues</item>
    /// </list>
    /// <para>
    /// <b>Timeout vs. Cancellation:</b>
    /// - TimeoutException: No response received within the specified timeout
    /// - OperationCanceledException: External cancellation (e.g., user stopped agent)
    /// </para>
    /// </remarks>
    Task<T> WaitForResponseAsync<T>(
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken) where T : AgentEvent;
}
