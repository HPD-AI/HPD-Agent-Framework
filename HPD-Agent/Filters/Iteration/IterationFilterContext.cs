using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Context provided to iteration filters before each LLM call in the agentic loop.
/// Contains both input (messages, options, state) and output (response, tool calls).
/// Output properties are populated after next() returns.
/// </summary>
/// <remarks>
/// This context follows the pre/post invocation pattern:
/// - PRE-INVOKE (before next()): Messages, Options, and State are available for inspection/modification
/// - POST-INVOKE (after next()): Response, ToolCalls, and Exception are populated with LLM results
///
/// The State property is immutable (record type) and provides a snapshot of the agent's execution state.
/// To signal state changes, use the Properties dictionary to communicate with the agent loop.
/// </remarks>
public class IterationFilterContext
{
    // ═══════════════════════════════════════════════════════
    // METADATA (What iteration is this?)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Current iteration number in the agentic loop (0-based).
    /// Iteration 0 is the first LLM call, iteration 1 is after first set of tool calls, etc.
    /// </summary>
    public required int Iteration { get; init; }

    /// <summary>
    /// Name of the agent executing this iteration.
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// Cancellation token for this operation.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    // ═══════════════════════════════════════════════════════
    // INPUT - MUTABLE (Filters can modify before LLM call)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Messages to send to the LLM.
    /// MUTABLE: Filters can add, remove, or modify messages.
    /// Includes conversation history and tool results from previous iterations.
    /// </summary>
    public required IList<ChatMessage> Messages { get; set; }

    /// <summary>
    /// Chat options for this LLM call.
    /// MUTABLE: Filters can modify Instructions, Tools, Temperature, etc.
    /// Most common use case: Appending to Instructions property.
    /// </summary>
    public ChatOptions? Options { get; set; }

    // ═══════════════════════════════════════════════════════
    // STATE - READ-ONLY (Full agent state snapshot)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Current agent loop state (immutable snapshot).
    /// Provides context about the agent's execution state including:
    /// - ActiveSkillInstructions: Skills activated in this message turn
    /// - CompletedFunctions: Functions executed so far
    /// - ExpandedSkillContainers/expandedScopedPluginContainers: Scoping state
    /// - ConsecutiveFailures: Error tracking
    /// - Circuit breaker state
    /// - Full conversation history
    /// </summary>
    /// <remarks>
    /// This is a record type and cannot be modified. Filters observe state but
    /// cannot change it directly. To request state changes, use Properties to signal intent.
    /// </remarks>
    public required AgentLoopState State { get; init; }

    // ═══════════════════════════════════════════════════════
    // OUTPUT - POPULATED AFTER next() (LLM response)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// The assistant message returned by the LLM.
    /// NULL before next() is called (pre-invoke phase).
    /// POPULATED after next() returns (post-invoke phase).
    /// Contains text content, reasoning content, and tool call requests.
    /// </summary>
    public ChatMessage? Response { get; set; }

    /// <summary>
    /// Tool calls requested by the LLM in this iteration.
    /// EMPTY before next() is called (pre-invoke phase).
    /// POPULATED after next() returns (post-invoke phase).
    /// If empty after next(), this is likely the final iteration (no more tool calls).
    /// </summary>
    public IReadOnlyList<FunctionCallContent> ToolCalls { get; set; }
        = Array.Empty<FunctionCallContent>();

    /// <summary>
    /// Exception that occurred during LLM invocation, or null if successful.
    /// NULL before next() is called.
    /// POPULATED after next() returns if an error occurred.
    /// </summary>
    public Exception? Exception { get; set; }

    // ═══════════════════════════════════════════════════════
    // CONTROL (Filters can signal actions)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Set to true to skip the LLM call entirely.
    /// Useful for caching, short-circuiting, or conditional execution.
    /// If set before next() is called, the LLM invocation will be skipped.
    /// The filter that sets this flag should populate Response and ToolCalls with cached/computed values.
    /// </summary>
    public bool SkipLLMCall { get; set; }

    /// <summary>
    /// Extensible property bag for inter-filter communication and signaling.
    /// Use this to:
    /// - Pass data between filters in the pipeline
    /// - Signal cleanup actions to the agent loop
    /// - Store computed values for reuse
    /// Example: Properties["ShouldClearSkills"] = true;
    /// </summary>
    public Dictionary<string, object> Properties { get; init; } = new();

    // ═══════════════════════════════════════════════════════
    // BIDIRECTIONAL EVENTS (For interactive filters)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Reference to the agent instance (for event coordination).
    /// Used internally for bidirectional event communication.
    /// </summary>
    internal AgentCore? Agent { get; init; }

    /// <summary>
    /// Emits an event to the agent's event stream for external handling.
    /// Used for bidirectional communication patterns like permission requests.
    /// </summary>
    /// <param name="evt">The event to emit</param>
    /// <exception cref="InvalidOperationException">If Agent reference is not configured</exception>
    public void Emit(InternalAgentEvent evt)
    {
        if (evt == null)
            throw new ArgumentNullException(nameof(evt));

        if (Agent == null)
            throw new InvalidOperationException("Agent reference not configured for this context");

        Agent.EventCoordinator.Emit(evt);
    }

    /// <summary>
    /// Waits for a response event from external handlers (blocking operation).
    /// Used for interactive patterns like permission requests, clarifications, etc.
    /// </summary>
    /// <typeparam name="T">Type of response event expected</typeparam>
    /// <param name="requestId">Unique identifier for this request</param>
    /// <param name="timeout">Maximum time to wait for response</param>
    /// <returns>The response event</returns>
    /// <exception cref="TimeoutException">If no response received within timeout</exception>
    /// <exception cref="OperationCanceledException">If operation was cancelled</exception>
    /// <exception cref="InvalidOperationException">If Agent reference is not configured</exception>
    public async Task<T> WaitForResponseAsync<T>(
        string requestId,
        TimeSpan? timeout = null) where T : InternalAgentEvent
    {
        if (Agent == null)
            throw new InvalidOperationException("Agent reference not configured for this context");

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);

        return await Agent.WaitForFilterResponseAsync<T>(
            requestId,
            effectiveTimeout,
            CancellationToken);
    }

    // ═══════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Returns true if this is the first iteration (before any tool calls).
    /// </summary>
    public bool IsFirstIteration => Iteration == 0;

    /// <summary>
    /// Returns true if the LLM call succeeded (no exception).
    /// Only valid after next() returns.
    /// </summary>
    public bool IsSuccess => Exception == null && Response != null;

    /// <summary>
    /// Returns true if the LLM call failed (exception occurred).
    /// Only valid after next() returns.
    /// </summary>
    public bool IsFailure => Exception != null || Response == null;

    /// <summary>
    /// Returns true if this appears to be the final iteration.
    /// Determined by: IsSuccess AND no tool calls requested.
    /// Only valid after next() returns.
    /// </summary>
    public bool IsFinalIteration => IsSuccess && !ToolCalls.Any();
}
