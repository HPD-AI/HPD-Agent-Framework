using Microsoft.Extensions.AI;

/// <summary>
/// Represents the context of an orchestration step where a tool may be invoked.
/// Native AOT compatible - does not inherit from FunctionInvocationContext.
/// </summary>
public class AiFunctionContext :  FunctionInvocationContext

{
    /// <summary>
    /// The raw tool call request from the Language Model.
    /// </summary>
    public ToolCallRequest ToolCallRequest { get; }

    /// <summary>
    /// The name of the agent executing this function
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// Context about the current agent run/turn
    /// </summary>
    public AgentRunContext? RunContext { get; set; }

    /// <summary>
    /// A flag to allow a filter to terminate the pipeline.
    /// </summary>
    public bool IsTerminated { get; set; } = false;

    /// <summary>
    /// The result of the function invocation, to be set by the final step.
    /// </summary>
    public object? Result { get; set; }

    /// <summary>
    /// The AI function being invoked (if available).
    /// </summary>
    public new AIFunction? Function { get; set; }



    /// <summary>
    /// Arguments for the function call (AOT-safe access).
    /// </summary>
    public new AIFunctionArguments Arguments { get; }

    public AiFunctionContext(ToolCallRequest toolCallRequest)
    {
        ToolCallRequest = toolCallRequest ?? throw new ArgumentNullException(nameof(toolCallRequest));
        Arguments = new AIFunctionArguments(toolCallRequest.Arguments);
    }
}


/// <summary>
/// The filter interface remains the same, but it will now operate
/// on the new, richer AiFunctionContext.
/// </summary>
public interface IAiFunctionFilter
{
    Task InvokeAsync(
        AiFunctionContext context,
        Func<AiFunctionContext, Task> next);
}

/// <summary>
/// Represents a function call request from the LLM.
/// </summary>
public class ToolCallRequest
{
    public required string FunctionName { get; set; }
    public required IDictionary<string, object?> Arguments { get; set; }
}

/// <summary>
/// Represents the context of an entire agent run/turn, providing
/// cross-function-call state and statistics
/// </summary>
public class AgentRunContext
{
    /// <summary>
    /// Unique identifier for this agent run
    /// </summary>
    public string RunId { get; }

    /// <summary>
    /// Conversation ID for this agent run
    /// </summary>
    public string ConversationId { get; }

    /// <summary>
    /// When this agent run started
    /// </summary>
    public DateTime StartTime { get; }

    /// <summary>
    /// Current iteration/function call number (0-based)
    /// </summary>
    public int CurrentIteration { get; set; } = 0;

    /// <summary>
    /// Maximum allowed function calls for this run
    /// </summary>
    public int MaxIterations { get; set; } = 10;

    /// <summary>
    /// List of function names that have been completed in this run
    /// </summary>
    public List<string> CompletedFunctions { get; } = new();

    /// <summary>
    /// Additional metadata for this run
    /// </summary>
    public Dictionary<string, object> Metadata { get; } = new();

    /// <summary>
    /// Whether this run has been terminated early
    /// </summary>
    public bool IsTerminated { get; set; } = false;

    /// <summary>
    /// Reason for termination (if terminated)
    /// </summary>
    public string? TerminationReason { get; set; }

    /// <summary>
    /// Constructor for AgentRunContext
    /// </summary>
    public AgentRunContext(string runId, string conversationId, int maxIterations = 10)
    {
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        ConversationId = conversationId ?? throw new ArgumentNullException(nameof(conversationId));
        StartTime = DateTime.UtcNow;
        MaxIterations = maxIterations;
    }

    /// <summary>
    /// Marks a function as completed
    /// </summary>
    public void CompleteFunction(string functionName)
    {
        CompletedFunctions.Add(functionName);
    }

    /// <summary>
    /// Gets the total elapsed time for this run
    /// </summary>
    public TimeSpan ElapsedTime => DateTime.UtcNow - StartTime;

    /// <summary>
    /// Checks if we've hit the maximum iteration limit
    /// </summary>
    public bool HasReachedMaxIterations => CurrentIteration >= MaxIterations;
}
