using Microsoft.Extensions.AI;

namespace HPD.Agent.Middleware;

//
// TURN LEVEL CONTEXTS
//

/// <summary>
/// Context for BeforeMessageTurn hook.
/// Available properties: UserMessage, ConversationHistory, RunOptions
/// </summary>
public sealed record BeforeMessageTurnContext : HookContext
{
    /// <summary>
    /// The user message that initiated this turn.
    ///   Can be NULL in continuation scenarios (when resuming from checkpoint with no new user input)
    /// </summary>
    public ChatMessage? UserMessage { get; init; }

    /// <summary>
    /// Complete conversation history prior to this turn.
    ///   Always available (never NULL)
    /// MUTABLE - middleware can modify history before processing
    /// </summary>
    public IList<ChatMessage> ConversationHistory { get; init; }

    /// <summary>
    /// Agent run options for this turn.
    ///   Always available (never NULL)
    /// Contains configuration like ClientToolInput, MaxIterations, etc.
    /// </summary>
    public AgentRunOptions RunOptions { get; init; }

    internal BeforeMessageTurnContext(
        AgentContext baseContext,
        ChatMessage? userMessage,
        IList<ChatMessage> conversationHistory,
        AgentRunOptions runOptions)
        : base(baseContext)
    {
        UserMessage = userMessage; // Can be null in continuation scenarios
        ConversationHistory = conversationHistory ?? throw new ArgumentNullException(nameof(conversationHistory));
        RunOptions = runOptions ?? throw new ArgumentNullException(nameof(runOptions));
    }
}

/// <summary>
/// Context for AfterMessageTurn hook.
/// Available properties: FinalResponse, TurnHistory, RunOptions
/// </summary>
public sealed record AfterMessageTurnContext : HookContext
{
    /// <summary>
    /// Final assistant response for this turn.
    ///   Always available (never NULL)
    /// </summary>
    public ChatResponse FinalResponse { get; init; }

    /// <summary>
    /// Messages that will be persisted to the thread after this turn completes.
    ///   Always available (never NULL)
    /// MUTABLE - middleware can filter/modify before persistence
    /// </summary>
    public List<ChatMessage> TurnHistory { get; init; }

    /// <summary>
    /// Original run options for this turn.
    ///   Always available (never NULL)
    /// READ-ONLY - represents the user's original intent for this run.
    /// Use for logging, metrics, and turn-level decisions based on user context.
    /// </summary>
    public AgentRunOptions RunOptions { get; init; }

    internal AfterMessageTurnContext(
        AgentContext baseContext,
        ChatResponse finalResponse,
        List<ChatMessage> turnHistory,
        AgentRunOptions runOptions)
        : base(baseContext)
    {
        FinalResponse = finalResponse ?? throw new ArgumentNullException(nameof(finalResponse));
        TurnHistory = turnHistory ?? throw new ArgumentNullException(nameof(turnHistory));
        RunOptions = runOptions ?? throw new ArgumentNullException(nameof(runOptions));
    }
}

//
// ITERATION LEVEL CONTEXTS
//

/// <summary>
/// Context for BeforeIteration hook.
/// Available properties: Iteration, Messages, Options, RunOptions
/// </summary>
public sealed record BeforeIterationContext : HookContext
{
    /// <summary>
    /// Current iteration number (0-based).
    ///   Always available
    /// </summary>
    public int Iteration { get; init; }

    /// <summary>
    /// Messages to send to the LLM for this iteration.
    ///   Always available (never NULL)
    /// MUTABLE - add context, modify history
    /// </summary>
    public List<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Chat options for this LLM call.
    ///   Always available (never NULL)
    /// MUTABLE - modify tools, instructions, temperature
    /// </summary>
    public ChatOptions Options { get; init; }

    /// <summary>
    /// Original run options for this turn.
    ///   Always available (never NULL)
    /// READ-ONLY - represents the user's original intent for this run.
    /// Use for iteration-specific decisions based on user preferences and context.
    /// Examples: Adapt temperature, filter tools, access ContextOverrides for tenant/user info.
    /// </summary>
    public AgentRunOptions RunOptions { get; init; }

    //
    // CONTROL SIGNALS
    //

    /// <summary>
    /// Set to true to skip the LLM call.
    /// When skipping, populate OverrideResponse with the cached/computed response.
    /// </summary>
    public bool SkipLLMCall { get; set; }

    /// <summary>
    /// When SkipLLMCall is true, this provides the response to use instead.
    /// </summary>
    public ChatMessage? OverrideResponse { get; set; }

    //
    // HELPERS
    //

    /// <summary>
    /// True if this is the first iteration (before any tool calls).
    /// </summary>
    public bool IsFirstIteration => Iteration == 0;

    internal BeforeIterationContext(
        AgentContext baseContext,
        int iteration,
        List<ChatMessage> messages,
        ChatOptions options,
        AgentRunOptions runOptions)
        : base(baseContext)
    {
        Iteration = iteration;
        Messages = messages ?? throw new ArgumentNullException(nameof(messages));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        RunOptions = runOptions ?? throw new ArgumentNullException(nameof(runOptions));
    }
}

/// <summary>
/// Context for BeforeToolExecution hook.
/// Available properties: Response, ToolCalls, RunOptions
/// </summary>
public sealed record BeforeToolExecutionContext : HookContext
{
    /// <summary>
    /// LLM response for this iteration.
    ///   Always available (never NULL)
    /// </summary>
    public ChatMessage Response { get; init; }

    /// <summary>
    /// Tool calls requested by LLM in this iteration.
    ///   Always available (never NULL, but may be empty)
    /// </summary>
    public IReadOnlyList<FunctionCallContent> ToolCalls { get; init; }

    /// <summary>
    /// Original run options for this turn.
    ///   Always available (never NULL)
    /// READ-ONLY - represents the user's original intent for this run.
    /// Use for permission checks, dry-run mode (SkipTools), and tool-level validation.
    /// </summary>
    public AgentRunOptions RunOptions { get; init; }

    //
    // CONTROL SIGNALS
    //

    /// <summary>
    /// Set to true to skip ALL pending tool executions.
    /// When skipping, set OverrideResponse with an appropriate message.
    /// </summary>
    public bool SkipToolExecution { get; set; }

    /// <summary>
    /// When SkipToolExecution is true, this provides the response to use instead.
    /// </summary>
    public ChatMessage? OverrideResponse { get; set; }

    internal BeforeToolExecutionContext(
        AgentContext baseContext,
        ChatMessage response,
        IReadOnlyList<FunctionCallContent> toolCalls,
        AgentRunOptions runOptions)
        : base(baseContext)
    {
        Response = response ?? throw new ArgumentNullException(nameof(response));
        ToolCalls = toolCalls ?? throw new ArgumentNullException(nameof(toolCalls));
        RunOptions = runOptions ?? throw new ArgumentNullException(nameof(runOptions));
    }
}

/// <summary>
/// Context for AfterIteration hook.
/// Available properties: Iteration, ToolResults, RunOptions
/// </summary>
public sealed record AfterIterationContext : HookContext
{
    /// <summary>
    /// Current iteration number (0-based).
    ///   Always available
    /// </summary>
    public int Iteration { get; init; }

    /// <summary>
    /// Results from tool execution.
    ///   Always available (never NULL, but may be empty)
    /// </summary>
    public IReadOnlyList<FunctionResultContent> ToolResults { get; init; }

    /// <summary>
    /// Original run options for this turn.
    ///   Always available (never NULL)
    /// READ-ONLY - represents the user's original intent for this run.
    /// Use for error tracking, metrics collection, and iteration-level logging with user context.
    /// </summary>
    public AgentRunOptions RunOptions { get; init; }

    //
    // HELPERS
    //

    /// <summary>
    /// True if all tool calls succeeded (no exceptions).
    /// </summary>
    public bool AllToolsSucceeded => ToolResults.All(r => r.Exception == null);

    /// <summary>
    /// True if any tool call failed (has exception).
    /// </summary>
    public bool AnyToolFailed => ToolResults.Any(r => r.Exception != null);

    internal AfterIterationContext(
        AgentContext baseContext,
        int iteration,
        IReadOnlyList<FunctionResultContent> toolResults,
        AgentRunOptions runOptions)
        : base(baseContext)
    {
        Iteration = iteration;
        ToolResults = toolResults ?? throw new ArgumentNullException(nameof(toolResults));
        RunOptions = runOptions ?? throw new ArgumentNullException(nameof(runOptions));
    }
}

//
// FUNCTION LEVEL CONTEXTS
//

/// <summary>
/// Context for BeforeParallelBatch hook.
/// Available properties: ParallelFunctions, RunOptions
/// </summary>
public sealed record BeforeParallelBatchContext : HookContext
{
    /// <summary>
    /// Information about functions being executed in parallel.
    ///   Always available (never NULL, always has at least 2 functions)
    /// </summary>
    public IReadOnlyList<ParallelFunctionInfo> ParallelFunctions { get; init; }

    /// <summary>
    /// Original run options for this turn.
    ///   Always available (never NULL)
    /// READ-ONLY - represents the user's original intent for this run.
    /// Use for rate limiting, batch-level validation, and parallel execution control based on user tier/context.
    /// </summary>
    public AgentRunOptions RunOptions { get; init; }

    internal BeforeParallelBatchContext(
        AgentContext baseContext,
        IReadOnlyList<ParallelFunctionInfo> parallelFunctions,
        AgentRunOptions runOptions)
        : base(baseContext)
    {
        ParallelFunctions = parallelFunctions ?? throw new ArgumentNullException(nameof(parallelFunctions));
        RunOptions = runOptions ?? throw new ArgumentNullException(nameof(runOptions));
    }
}

/// <summary>
/// Context for BeforeFunction hook.
/// Available properties: Function, FunctionCallId, Arguments, PluginName, SkillName, RunOptions
/// </summary>
public sealed record BeforeFunctionContext : HookContext
{
    /// <summary>
    /// The function being invoked.
    ///   Can be NULL when LLM calls an unknown/unavailable function (unless TerminateOnUnknownCalls is enabled)
    /// </summary>
    public AIFunction? Function { get; init; }

    /// <summary>
    /// Unique call ID for this function invocation.
    ///   Always available (never NULL)
    /// </summary>
    public string FunctionCallId { get; init; }

    /// <summary>
    /// Arguments passed to this function call.
    ///   Always available (never NULL, but may be empty)
    /// </summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; init; }

    /// <summary>
    /// Name of the plugin that contains this function, if any.
    /// May be NULL if function is not part of a plugin.
    /// </summary>
    public string? PluginName { get; init; }

    /// <summary>
    /// Name of the skill that referenced this function, if any.
    /// May be NULL if function is not part of a skill.
    /// </summary>
    public string? SkillName { get; init; }

    /// <summary>
    /// Original run options for this turn.
    ///   Always available (never NULL)
    /// READ-ONLY - represents the user's original intent for this run.
    /// Use for permission validation, dry-run mode (SkipTools), and function-level authorization.
    /// </summary>
    public AgentRunOptions RunOptions { get; init; }

    //
    // CONTROL SIGNALS
    //

    /// <summary>
    /// Set to true to block THIS function from executing.
    /// The function will not run; OverrideResult will be used as the result.
    /// </summary>
    public bool BlockExecution { get; set; }

    /// <summary>
    /// When BlockExecution is true, this provides the result to use instead.
    /// </summary>
    public object? OverrideResult { get; set; }

    //
    // HELPERS
    //

    /// <summary>
    /// True if this function is part of a skill.
    /// </summary>
    public bool IsSkillFunction => SkillName != null;

    /// <summary>
    /// True if this function is part of a plugin.
    /// </summary>
    public bool IsPluginFunction => PluginName != null;

    internal BeforeFunctionContext(
        AgentContext baseContext,
        AIFunction? function,
        string callId,
        IReadOnlyDictionary<string, object?> arguments,
        string? pluginName,
        string? skillName,
        AgentRunOptions runOptions)
        : base(baseContext)
    {
        Function = function; // Can be null for unknown functions
        FunctionCallId = callId ?? throw new ArgumentNullException(nameof(callId));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        PluginName = pluginName;
        SkillName = skillName;
        RunOptions = runOptions ?? throw new ArgumentNullException(nameof(runOptions));
    }
}

/// <summary>
/// Context for AfterFunction hook.
/// Available properties: Function, FunctionCallId, Result, Exception, PluginName, SkillName, RunOptions
/// </summary>
public sealed record AfterFunctionContext : HookContext
{
    /// <summary>
    /// The function that was invoked.
    ///   Can be NULL when an unknown function was called
    /// </summary>
    public AIFunction? Function { get; init; }

    /// <summary>
    /// Unique call ID for this function invocation.
    ///   Always available (never NULL)
    /// </summary>
    public string FunctionCallId { get; init; }

    /// <summary>
    /// Result of the function execution (if successful).
    /// NULL if function threw an exception.
    /// MUTABLE - middleware can transform the result
    /// </summary>
    public object? Result { get; set; }

    /// <summary>
    /// Exception from function execution (if failed).
    /// NULL if function succeeded.
    /// MUTABLE - middleware can transform/wrap exceptions
    /// </summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// Name of the plugin that contains this function, if any.
    /// May be NULL if function is not part of a plugin.
    /// </summary>
    public string? PluginName { get; init; }

    /// <summary>
    /// Name of the skill that referenced this function, if any.
    /// May be NULL if function is not part of a skill.
    /// </summary>
    public string? SkillName { get; init; }

    /// <summary>
    /// Original run options for this turn.
    ///   Always available (never NULL)
    /// READ-ONLY - represents the user's original intent for this run.
    /// Use for audit logging, metrics, and result transformation based on user context.
    /// </summary>
    public AgentRunOptions RunOptions { get; init; }

    //
    // HELPERS
    //

    /// <summary>
    /// True if the function succeeded (no exception).
    /// </summary>
    public bool IsSuccess => Exception == null;

    /// <summary>
    /// True if the function failed (has exception).
    /// </summary>
    public bool IsFailure => Exception != null;

    /// <summary>
    /// True if this function is part of a skill.
    /// </summary>
    public bool IsSkillFunction => SkillName != null;

    /// <summary>
    /// True if this function is part of a plugin.
    /// </summary>
    public bool IsPluginFunction => PluginName != null;

    internal AfterFunctionContext(
        AgentContext baseContext,
        AIFunction? function,
        string callId,
        object? result,
        Exception? exception,
        AgentRunOptions runOptions,
        string? pluginName = null,
        string? skillName = null)
        : base(baseContext)
    {
        Function = function; // Can be null for unknown functions
        FunctionCallId = callId ?? throw new ArgumentNullException(nameof(callId));
        Result = result;
        Exception = exception;
        PluginName = pluginName;
        SkillName = skillName;
        RunOptions = runOptions ?? throw new ArgumentNullException(nameof(runOptions));
    }
}

//
// HELPER TYPES
//

/// <summary>
/// Information about a function being executed in parallel.
/// </summary>
public sealed record ParallelFunctionInfo(
    AIFunction Function,
    string CallId,
    IReadOnlyDictionary<string, object?> Arguments)
{
    /// <summary>
    /// Name of the function being called.
    /// </summary>
    public string FunctionName => Function.Name;
}
