namespace HPD.Agent.Middleware;

/// <summary>
/// Abstract base class for all typed hook contexts.
/// Provides shared functionality: state access, event emission, identity properties.
/// </summary>
/// <remarks>
/// <para><b>Design Philosophy:</b></para>
/// <para>
/// Hook contexts are lightweight wrappers around <see cref="AgentContext"/> that expose
/// only the properties relevant to each specific hook. This provides compile-time safety
/// by preventing access to properties that aren't available in a given hook.
/// </para>
/// <para><b>Key Benefits:</b></para>
/// <list type="bullet">
/// <item>  Compile-time safety - only valid properties exposed</item>
/// <item>  Better IDE autocomplete - no NULL-able properties to check</item>
/// <item>  Clear documentation - hook signature shows exactly what's available</item>
/// <item>  Easier testing - mock only the properties that matter for each hook</item>
/// </list>
/// </remarks>
public abstract record HookContext
{
    /// <summary>
    /// The underlying agent context (shared across all hooks).
    /// </summary>
    internal AgentContext Base { get; init; }

    //
    // ALWAYS AVAILABLE (forwarded for convenience)
    //

    /// <summary>
    /// Name of the agent executing this operation.
    /// </summary>
    public string AgentName => Base.AgentName;

    /// <summary>
    /// Unique identifier for this conversation/session.
    /// </summary>
    public string? ConversationId => Base.ConversationId;

    /// <summary>
    /// Current agent loop state. Reflects any updates from earlier middlewares.
    /// </summary>
    public AgentLoopState State => Base.State;

    /// <summary>
    /// Updates agent state immutably.
    ///   CRITICAL: Updates are applied IMMEDIATELY - subsequent hooks see the updated state.
    /// </summary>
    /// <param name="transform">Function that transforms the current state to new state</param>
    public void UpdateState(Func<AgentLoopState, AgentLoopState> transform)
        => Base.UpdateState(transform);

    /// <summary>
    /// Emits an event to the agent's event stream for external handling.
    /// </summary>
    /// <param name="evt">The event to emit</param>
    /// <exception cref="InvalidOperationException">Thrown if EventCoordinator is not configured</exception>
    public void Emit(AgentEvent evt)
        => Base.Emit(evt);

    /// <summary>
    /// Attempts to emit an event to the agent's event stream.
    /// Silently does nothing if EventCoordinator is not configured.
    /// </summary>
    /// <param name="evt">The event to emit</param>
    /// <returns>True if event was emitted, false if EventCoordinator not configured</returns>
    public bool TryEmit(AgentEvent evt)
    {
        try
        {
            Base.Emit(evt);
            return true;
        }
        catch (InvalidOperationException)
        {
            // EventCoordinator not configured
            return false;
        }
    }

    /// <summary>
    /// Waits for a response event from external handlers.
    /// Used for interactive patterns: permissions, clarifications, approvals.
    /// </summary>
    /// <typeparam name="T">Type of response event expected</typeparam>
    /// <param name="requestId">Unique identifier for this request (must match response)</param>
    /// <param name="timeout">Maximum time to wait (default: 5 minutes)</param>
    /// <returns>The response event</returns>
    public Task<T> WaitForResponseAsync<T>(
        string requestId,
        TimeSpan? timeout = null) where T : AgentEvent
        => Base.WaitForResponseAsync<T>(requestId, timeout);

    /// <summary>
    /// Stream registry for managing interruptible operations.
    /// May be null if event coordination is not configured with stream support.
    /// </summary>
    /// <remarks>
    /// Used by audio middleware for stream interruption and priority streaming.
    /// Forwarded from the base AgentContext for convenience.
    /// </remarks>
    public IStreamRegistry? Streams => Base.Streams;

    //
    // CONSTRUCTOR
    //

    /// <summary>
    /// Initializes a new hook context wrapping the base agent context.
    /// </summary>
    /// <param name="baseContext">The underlying agent context</param>
    protected HookContext(AgentContext baseContext)
    {
        Base = baseContext ?? throw new ArgumentNullException(nameof(baseContext));
    }
}
