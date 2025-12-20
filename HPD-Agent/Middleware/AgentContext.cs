using Microsoft.Extensions.AI;

namespace HPD.Agent.Middleware;

/// <summary>
/// Single unified context for the entire agent execution.
/// This is the core context object that flows through all middleware hooks.
/// </summary>
/// <remarks>
/// <para><b>Design Philosophy:</b></para>
/// <para>
/// AgentContext represents a single source of truth for agent execution.
/// Unlike the V1 architecture with separate turnContext and middlewareContext instances,
/// V2 uses a single context instance created at turn start and shared across all hooks.
/// </para>
/// <para><b>Key Improvements:</b></para>
/// <list type="bullet">
/// <item>✅ Single context instance - no manual synchronization needed</item>
/// <item>✅ Immediate state updates - updates visible to all subsequent hooks instantly</item>
/// <item>✅ Type-safe views - factory methods create typed contexts for each hook</item>
/// <item>✅ No scheduled updates - no awkward GetPendingState() pattern</item>
/// </list>
/// </remarks>
public sealed class AgentContext
{
    //
    // SHARED STATE (always synchronized, no manual sync needed)
    //

    private AgentLoopState _state;
    private readonly IEventCoordinator _events;
    private readonly CancellationToken _cancellationToken;

    //
    // INTERNAL ACCESS (for adapters)
    //

    /// <summary>
    /// Event coordinator (internal access for adapters).
    /// </summary>
    internal IEventCoordinator EventCoordinator => _events;

    //
    // IDENTITY (immutable)
    //

    /// <summary>
    /// Name of the agent executing this operation.
    /// </summary>
    public string AgentName { get; }

    /// <summary>
    /// Unique identifier for this conversation/session.
    /// Used for scoping permissions, memory, and other per-conversation state.
    /// </summary>
    public string? ConversationId { get; }

    //
    // STREAM MANAGEMENT (always available, may be null if not configured)
    //

    /// <summary>
    /// Stream registry for managing interruptible audio/streaming operations.
    /// Available when EventCoordinator is a BidirectionalEventCoordinator.
    /// </summary>
    /// <remarks>
    /// Used by audio middleware for stream interruption and priority streaming.
    /// Returns null if event coordination is not configured with stream support.
    /// </remarks>
    public IStreamRegistry? Streams => (_events as BidirectionalEventCoordinator)?.Streams;

    //
    // STATE ACCESS (always available)
    //

    /// <summary>
    /// Current agent loop state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// State is immutable. To update, use <see cref="UpdateState"/>.
    /// </para>
    /// <para>
    /// Includes: ActiveSkillInstructions, CompletedFunctions, MiddlewareStates,
    /// ExpandedSkillContainers, expandedCollapsedPluginContainers, etc.
    /// </para>
    /// </remarks>
    public AgentLoopState State => _state;

    /// <summary>
    /// Updates agent state immutably.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠️ CRITICAL: Updates are applied IMMEDIATELY</b></para>
    /// <para>
    /// Subsequent hooks see the updated state immediately. This is different from V1
    /// which scheduled updates for later. Middleware is responsible for validating
    /// updates before calling this method. There is no rollback mechanism.
    /// </para>
    /// <para><b>Example:</b></para>
    /// <code>
    /// // Update state
    /// context.UpdateState(s => s with
    /// {
    ///     MiddlewareState = s.MiddlewareState.WithErrorTracking(newState)
    /// });
    ///
    /// // Next middleware in chain sees updated state immediately!
    /// var errorCount = context.State.MiddlewareState.ErrorTracking.ConsecutiveFailures;
    /// </code>
    /// </remarks>
    /// <param name="transform">Function that transforms the current state to new state</param>
    public void UpdateState(Func<AgentLoopState, AgentLoopState> transform)
    {
        if (transform == null) throw new ArgumentNullException(nameof(transform));
        _state = transform(_state);
        // ✅ Updates visible to ALL subsequent hooks (same instance!)
        // ✅ No scheduled updates - no awkward GetPendingState() needed
    }

    /// <summary>
    /// Synchronizes the internal state with an external state object.
    /// Used by Agent.cs to sync state changes from the main loop.
    /// </summary>
    /// <param name="newState">The new state to synchronize</param>
    internal void SyncState(AgentLoopState newState)
    {
        _state = newState ?? throw new ArgumentNullException(nameof(newState));
    }

    //
    // EVENT EMISSION (always available)
    //

    /// <summary>
    /// Emits an event to the agent's event stream for external handling.
    /// Events are delivered immediately (not batched).
    /// </summary>
    /// <param name="evt">The event to emit</param>
    /// <exception cref="ArgumentNullException">If event is null</exception>
    public void Emit(AgentEvent evt)
    {
        if (evt == null) throw new ArgumentNullException(nameof(evt));
        _events.Emit(evt);
    }

    /// <summary>
    /// Waits for a response event from external handlers.
    /// Used for interactive patterns: permissions, clarifications, approvals.
    /// </summary>
    /// <typeparam name="T">Type of response event expected</typeparam>
    /// <param name="requestId">Unique identifier for this request (must match response)</param>
    /// <param name="timeout">Maximum time to wait (default: 5 minutes)</param>
    /// <returns>The response event</returns>
    /// <exception cref="TimeoutException">If no response received within timeout</exception>
    /// <exception cref="OperationCanceledException">If operation was cancelled</exception>
    public async Task<T> WaitForResponseAsync<T>(
        string requestId,
        TimeSpan? timeout = null) where T : AgentEvent
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        return await _events.WaitForResponseAsync<T>(
            requestId,
            effectiveTimeout,
            _cancellationToken);
    }

    //
    // CONSTRUCTOR (internal - created by Agent.cs)
    //

    internal AgentContext(
        string agentName,
        string? conversationId,
        AgentLoopState initialState,
        IEventCoordinator eventCoordinator,
        CancellationToken cancellationToken)
    {
        AgentName = agentName ?? throw new ArgumentNullException(nameof(agentName));
        ConversationId = conversationId;
        _state = initialState ?? throw new ArgumentNullException(nameof(initialState));
        _events = eventCoordinator ?? throw new ArgumentNullException(nameof(eventCoordinator));
        _cancellationToken = cancellationToken;
    }

    //
    // FACTORY METHODS FOR TYPE-SAFE HOOK CONTEXTS
    //

    /// <summary>
    /// Creates a typed context for BeforeMessageTurn hook.
    /// </summary>
    internal BeforeMessageTurnContext AsBeforeMessageTurn(
        ChatMessage? userMessage,
        IList<ChatMessage> conversationHistory,
        AgentRunOptions runOptions)
        => new(this, userMessage, conversationHistory, runOptions);

    /// <summary>
    /// Creates a typed context for AfterMessageTurn hook.
    /// </summary>
    internal AfterMessageTurnContext AsAfterMessageTurn(
        ChatResponse finalResponse,
        List<ChatMessage> turnHistory,
        AgentRunOptions runOptions)
        => new(this, finalResponse, turnHistory, runOptions);

    /// <summary>
    /// Creates a typed context for BeforeIteration hook.
    /// </summary>
    internal BeforeIterationContext AsBeforeIteration(
        int iteration,
        List<ChatMessage> messages,
        ChatOptions options,
        AgentRunOptions runOptions)
        => new(this, iteration, messages, options, runOptions);

    /// <summary>
    /// Creates a typed context for BeforeToolExecution hook.
    /// </summary>
    internal BeforeToolExecutionContext AsBeforeToolExecution(
        ChatMessage response,
        IReadOnlyList<FunctionCallContent> toolCalls,
        AgentRunOptions runOptions)
        => new(this, response, toolCalls, runOptions);

    /// <summary>
    /// Creates a typed context for AfterIteration hook.
    /// </summary>
    internal AfterIterationContext AsAfterIteration(
        int iteration,
        IReadOnlyList<FunctionResultContent> toolResults,
        AgentRunOptions runOptions)
        => new(this, iteration, toolResults, runOptions);

    /// <summary>
    /// Creates a typed context for BeforeParallelBatch hook.
    /// </summary>
    internal BeforeParallelBatchContext AsBeforeParallelBatch(
        IReadOnlyList<ParallelFunctionInfo> parallelFunctions,
        AgentRunOptions runOptions)
        => new(this, parallelFunctions, runOptions);

    /// <summary>
    /// Creates a typed context for BeforeFunction hook.
    /// </summary>
    internal BeforeFunctionContext AsBeforeFunction(
        AIFunction? function,
        string callId,
        IReadOnlyDictionary<string, object?> arguments,
        AgentRunOptions runOptions,
        string? pluginName = null,
        string? skillName = null)
        => new(this, function, callId, arguments, pluginName, skillName, runOptions);

    /// <summary>
    /// Creates a typed context for AfterFunction hook.
    /// </summary>
    internal AfterFunctionContext AsAfterFunction(
        AIFunction? function,
        string callId,
        object? result,
        Exception? exception,
        AgentRunOptions runOptions,
        string? pluginName = null,
        string? skillName = null)
        => new(this, function, callId, result, exception, runOptions, pluginName, skillName);

    /// <summary>
    /// Creates a typed context for OnError hook.
    /// </summary>
    internal ErrorContext AsError(
        Exception error,
        ErrorSource source,
        int iteration)
        => new(this, error, source, iteration);
}
