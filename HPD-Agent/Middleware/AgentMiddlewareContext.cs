using Microsoft.Extensions.AI;

namespace HPD.Agent.Middleware;

/// <summary>
/// Unified context for all middleware hooks.
/// Properties are populated progressively as the agent executes.
/// </summary>
/// <remarks>
/// <para><b>Property Availability by Hook:</b></para>
/// <list type="table">
/// <listheader>
///   <term>Hook</term>
///   <description>Available Properties</description>
/// </listheader>
/// <item>
///   <term>BeforeMessageTurn</term>
///   <description>AgentName, ConversationId, State, UserMessage, ConversationHistory</description>
/// </item>
/// <item>
///   <term>BeforeIteration</term>
///   <description>+ Iteration, Messages, Options</description>
/// </item>
/// <item>
///   <term>BeforeToolExecution</term>
///   <description>+ Response, ToolCalls</description>
/// </item>
/// <item>
///   <term>BeforeParallelFunctions</term>
///   <description>+ ParallelFunctions (list of all functions about to execute)</description>
/// </item>
/// <item>
///   <term>BeforeFunction</term>
///   <description>+ Function, FunctionCallId, FunctionArguments</description>
/// </item>
/// <item>
///   <term>AfterFunction</term>
///   <description>+ FunctionResult, FunctionException</description>
/// </item>
/// <item>
///   <term>AfterIteration</term>
///   <description>+ ToolResults</description>
/// </item>
/// <item>
///   <term>AfterMessageTurn</term>
///   <description>+ FinalResponse, TurnFunctionCalls</description>
/// </item>
/// </list>
///
/// <para><b>State Management:</b></para>
/// <para>
/// State is immutable. Use <c>UpdateState&lt;T&gt;(transform)</c> to schedule changes.
/// Updates are applied after the middleware chain completes.
/// </para>
/// </remarks>
public class AgentMiddlewareContext
{
    // ═══════════════════════════════════════════════════════════════
    // INTERNAL STATE TRACKING
    // ═══════════════════════════════════════════════════════════════

    private AgentLoopState _originalState = null!;
    private AgentLoopState? _pendingState;

    // ═══════════════════════════════════════════════════════════════
    // IDENTITY (Always available)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Name of the agent executing this operation.
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// Unique identifier for this conversation/session.
    /// Used for scoping permissions, memory, and other per-conversation state.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Cancellation token for this operation.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    // ═══════════════════════════════════════════════════════════════
    // STATE (Always available, immutable with scheduled updates)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Current agent loop state. Reflects any pending updates from earlier middlewares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// State is immutable. To update, use <see cref="UpdateState{TState}(Func{TState, TState})"/>
    /// which schedules changes to be applied after the middleware chain completes.
    /// </para>
    /// <para>
    /// Includes: ActiveSkillInstructions, CompletedFunctions, MiddlewareStates,
    /// ExpandedSkillContainers, expandedScopedPluginContainers, etc.
    /// </para>
    /// </remarks>
    public AgentLoopState State => _pendingState ?? _originalState;

    /// <summary>
    /// Sets the original state. Called by Agent when creating the context.
    /// </summary>
    internal void SetOriginalState(AgentLoopState state)
    {
        _originalState = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>
    /// Schedules a state update. Updates are applied after the middleware chain completes.
    /// Multiple calls are composed (each transform sees the result of previous transforms).
    /// </summary>
    /// <param name="transform">Function that transforms the current state to new state</param>
    public void UpdateState(Func<AgentLoopState, AgentLoopState> transform)
    {
        if (transform == null) throw new ArgumentNullException(nameof(transform));
        _pendingState = transform(_pendingState ?? _originalState);
    }

    /// <summary>
    /// Gets pending state updates (called by Agent after middleware chain).
    /// Returns null if no updates were scheduled.
    /// </summary>
    internal AgentLoopState? GetPendingState() => _pendingState;

    /// <summary>
    /// Returns true if any state updates have been scheduled.
    /// </summary>
    internal bool HasPendingStateUpdates => _pendingState != null;

    // ═══════════════════════════════════════════════════════════════
    // BIDIRECTIONAL EVENTS (Always available)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Event coordinator for bidirectional communication patterns.
    /// Used for emitting events and waiting for responses (permissions, approvals, etc.)
    /// </summary>
    internal IEventCoordinator? EventCoordinator { get; init; }

    /// <summary>
    /// Emits an event to the agent's event stream for external handling.
    /// Events are delivered immediately (not batched).
    /// </summary>
    /// <param name="evt">The event to emit</param>
    /// <exception cref="ArgumentNullException">If event is null</exception>
    /// <exception cref="InvalidOperationException">If EventCoordinator is not configured</exception>
    public void Emit(AgentEvent evt)
    {
        if (evt == null)
            throw new ArgumentNullException(nameof(evt));

        if (EventCoordinator == null)
            throw new InvalidOperationException("Event coordination not configured for this context");

        EventCoordinator.Emit(evt);
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
    /// <exception cref="InvalidOperationException">If EventCoordinator is not configured</exception>
    public async Task<T> WaitForResponseAsync<T>(
        string requestId,
        TimeSpan? timeout = null) where T : AgentEvent
    {
        if (EventCoordinator == null)
            throw new InvalidOperationException("Event coordination not configured for this context");

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);

        return await EventCoordinator.WaitForResponseAsync<T>(
            requestId,
            effectiveTimeout,
            CancellationToken);
    }

    // ═══════════════════════════════════════════════════════════════
    // MESSAGE TURN LEVEL
    // Available in: BeforeMessageTurn, AfterMessageTurn
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The user message that initiated this turn.
    /// Available in BeforeMessageTurn and AfterMessageTurn.
    /// </summary>
    public ChatMessage? UserMessage { get; set; }

    /// <summary>
    /// Complete conversation history prior to this turn.
    /// Available in BeforeMessageTurn.
    /// </summary>
    public IList<ChatMessage>? ConversationHistory { get; set; }

    /// <summary>
    /// Final assistant response for this turn.
    /// Populated in AfterMessageTurn after all iterations complete.
    /// </summary>
    public ChatResponse? FinalResponse { get; set; }

    /// <summary>
    /// All function calls made during this turn, grouped by agent.
    /// Key: agent name, Value: list of function names called.
    /// Populated in AfterMessageTurn.
    /// </summary>
    public Dictionary<string, List<string>>? TurnFunctionCalls { get; set; }

    /// <summary>
    /// Messages that will be persisted to the thread after this turn completes.
    /// Available in AfterMessageTurn - middleware can filter/modify before persistence.
    /// MUTABLE - modify this list to control what gets saved (e.g., filter ephemeral messages).
    /// </summary>
    public List<ChatMessage>? TurnHistory { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // ITERATION LEVEL
    // Available in: BeforeIteration, BeforeToolExecution, AfterIteration
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Current iteration number (0-based). Iteration 0 = first LLM call.
    /// </summary>
    public int Iteration { get; set; }

    /// <summary>
    /// Messages to send to the LLM for this iteration.
    /// MUTABLE in BeforeIteration - add context, modify history.
    /// </summary>
    public IList<ChatMessage>? Messages { get; set; }

    /// <summary>
    /// Chat options for this LLM call.
    /// MUTABLE in BeforeIteration - modify tools, instructions, temperature.
    /// </summary>
    public ChatOptions? Options { get; set; }

    /// <summary>
    /// LLM response for this iteration.
    /// Populated AFTER LLM call completes (available in BeforeToolExecution, AfterIteration).
    /// </summary>
    public ChatMessage? Response { get; set; }

    /// <summary>
    /// Tool calls requested by LLM in this iteration.
    /// Populated AFTER LLM call (available in BeforeToolExecution, AfterIteration).
    /// </summary>
    public IReadOnlyList<FunctionCallContent> ToolCalls { get; set; }
        = Array.Empty<FunctionCallContent>();

    /// <summary>
    /// Results from tool execution.
    /// Populated AFTER tools execute (available in AfterIteration).
    /// </summary>
    public IReadOnlyList<FunctionResultContent> ToolResults { get; set; }
        = Array.Empty<FunctionResultContent>();

    /// <summary>
    /// Exception that occurred during LLM call, if any.
    /// </summary>
    public Exception? IterationException { get; set; }

    /// <summary>
    /// Set to true in BeforeIteration to skip the LLM call.
    /// When skipping, populate Response with the cached/computed response.
    /// </summary>
    public bool SkipLLMCall { get; set; }

    /// <summary>
    /// Set to true in BeforeToolExecution to skip ALL pending tool executions.
    /// When skipping, set Response with an appropriate message.
    /// </summary>
    public bool SkipToolExecution { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // FUNCTION LEVEL
    // Available in: BeforeParallelFunctions, BeforeFunction, AfterFunction
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Information about functions being executed in parallel.
    /// Populated ONLY in BeforeParallelFunctionsAsync when multiple functions execute in parallel.
    /// Null for sequential/single function execution.
    /// </summary>
    public IReadOnlyList<ParallelFunctionInfo>? ParallelFunctions { get; set; }

    /// <summary>
    /// The function being invoked.
    /// Available in BeforeFunction and AfterFunction.
    /// </summary>
    public AIFunction? Function { get; set; }

    /// <summary>
    /// Unique call ID for this function invocation.
    /// Used for correlation with FunctionResultContent.
    /// </summary>
    public string? FunctionCallId { get; set; }

    /// <summary>
    /// Arguments passed to this function call.
    /// </summary>
    public IDictionary<string, object?>? FunctionArguments { get; set; }

    /// <summary>
    /// Name of the plugin that contains this function, if any.
    /// Used by middleware to implement plugin-scoped logic.
    /// Available in BeforeFunction and AfterFunction.
    /// </summary>
    public string? PluginName { get; set; }

    /// <summary>
    /// Name of the skill that referenced this function, if any.
    /// Used by middleware to implement skill-scoped logic.
    /// Available in BeforeFunction and AfterFunction.
    /// </summary>
    public string? SkillName { get; set; }

    /// <summary>
    /// True if this function is itself a skill container (not a function called BY a skill).
    /// Used by middleware to distinguish skill containers from skill-invoked functions.
    /// Available in BeforeFunction and AfterFunction.
    /// </summary>
    public bool IsSkillContainer { get; set; }

    /// <summary>
    /// Result of the function execution.
    /// In BeforeFunction: Set this to provide a result WITHOUT executing.
    /// In AfterFunction: Contains actual result, can be modified.
    /// </summary>
    public object? FunctionResult { get; set; }

    /// <summary>
    /// Exception from function execution (if failed).
    /// Available in AfterFunction.
    /// </summary>
    public Exception? FunctionException { get; set; }

    /// <summary>
    /// Set to true in BeforeFunction to block THIS function from executing.
    /// The function will not run; FunctionResult will be used as the result.
    /// </summary>
    public bool BlockFunctionExecution { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // EXTENSIBILITY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Extensible property bag for inter-middleware communication.
    /// Use for: Passing data between middlewares, signaling, custom metadata.
    /// </summary>
    /// <example>
    /// <code>
    /// // Set a property in one middleware
    /// context.Properties["MyKey"] = myValue;
    ///
    /// // Read it in another middleware
    /// if (context.Properties.TryGetValue("MyKey", out var value))
    /// {
    ///     // Use value
    /// }
    /// </code>
    /// </example>
    public Dictionary<string, object> Properties { get; init; } = new();

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// True if this is the first iteration (before any tool calls).
    /// </summary>
    public bool IsFirstIteration => Iteration == 0;

    /// <summary>
    /// True if LLM returned no tool calls (likely final response).
    /// Only valid after LLM call completes.
    /// </summary>
    public bool IsFinalIteration => Response != null && ToolCalls.Count == 0;

    /// <summary>
    /// True if currently in a function-level hook (BeforeFunction/AfterFunction).
    /// </summary>
    public bool IsFunctionContext => Function != null;

    /// <summary>
    /// True if the LLM call succeeded (no exception, response populated).
    /// Only valid after LLM call completes.
    /// </summary>
    public bool IsIterationSuccess => IterationException == null && Response != null;

    /// <summary>
    /// True if the LLM call failed (exception occurred).
    /// Only valid after LLM call completes.
    /// </summary>
    public bool IsIterationFailure => IterationException != null;

    // ═══════════════════════════════════════════════════════════════
    // CONTEXT LIFECYCLE METHODS (Internal - called by Agent)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Prepares context for function-level hooks.
    /// Called by Agent before invoking BeforeFunction.
    /// </summary>
    internal void EnterFunctionContext(
        AIFunction function,
        string callId,
        IDictionary<string, object?> arguments)
    {
        Function = function;
        FunctionCallId = callId;
        FunctionArguments = arguments;
        FunctionResult = null;
        FunctionException = null;
        BlockFunctionExecution = false;
    }

    /// <summary>
    /// Clears function-level context after AfterFunction completes.
    /// Called by Agent after invoking AfterFunction.
    /// </summary>
    internal void ExitFunctionContext()
    {
        Function = null;
        FunctionCallId = null;
        FunctionArguments = null;
        FunctionResult = null;
        FunctionException = null;
        BlockFunctionExecution = false;
        PluginName = null;
        SkillName = null;
        IsSkillContainer = false;
    }

    /// <summary>
    /// Resets iteration-level state for a new iteration.
    /// Called by Agent at the start of each iteration.
    /// </summary>
    internal void ResetForNextIteration()
    {
        Response = null;
        ToolCalls = Array.Empty<FunctionCallContent>();
        ToolResults = Array.Empty<FunctionResultContent>();
        IterationException = null;
        SkipLLMCall = false;
        SkipToolExecution = false;
    }
}

/// <summary>
/// Information about a function that will execute in parallel.
/// Used in BeforeParallelFunctionsAsync to provide batch context.
/// </summary>
/// <param name="Function">The AI function definition</param>
/// <param name="CallId">Unique identifier for this function call</param>
/// <param name="Arguments">Arguments that will be passed to the function</param>
/// <param name="PluginName">Name of the plugin containing this function, if any</param>
/// <param name="SkillName">Name of the skill that invoked this function, if any</param>
public record ParallelFunctionInfo(
    AIFunction Function,
    string CallId,
    IDictionary<string, object?> Arguments,
    string? PluginName = null,
    string? SkillName = null)
{
    /// <summary>
    /// Convenience property for function name.
    /// </summary>
    public string Name => Function.Name;

    /// <summary>
    /// Convenience property for function description.
    /// </summary>
    public string? Description => Function.Description;
}
