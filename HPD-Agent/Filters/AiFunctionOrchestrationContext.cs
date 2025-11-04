using Microsoft.Extensions.AI;

/// <summary>
/// Filter interface for function invocation pipeline.
/// Operates on FunctionInvocationContext which provides full orchestration capabilities
/// including bidirectional communication, event emission, and filter pipeline control.
/// </summary>
public interface IAiFunctionFilter
{
    Task InvokeAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next);
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
    /// Name of the agent executing in this run (optional).
    /// Used for telemetry, logging, and plugin context.
    /// </summary>
    public string? AgentName { get; set; }

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
    /// Set of tool call IDs that have been approved for execution in this run
    /// Used to prevent duplicate permission prompts in parallel execution
    /// </summary>
    private readonly HashSet<string> _approvedToolCalls = new();

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
    /// Tracks consecutive errors across iterations to prevent infinite error loops.
    /// Reset to 0 when a successful iteration occurs.
    /// </summary>
    public int ConsecutiveErrorCount { get; set; } = 0;

    /// <summary>
    /// Constructor for AgentRunContext
    /// </summary>
    public AgentRunContext(string runId, string conversationId, int maxIterations = 10, string? agentName = null)
    {
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        ConversationId = conversationId ?? throw new ArgumentNullException(nameof(conversationId));
        AgentName = agentName;
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
    /// Checks if a tool call has already been approved in this run
    /// </summary>
    public bool IsToolApproved(string callId) => _approvedToolCalls.Contains(callId);

    /// <summary>
    /// Marks a tool call as approved for execution in this run
    /// </summary>
    public void MarkToolApproved(string callId) => _approvedToolCalls.Add(callId);

    /// <summary>
    /// Gets the total elapsed time for this run
    /// </summary>
    public TimeSpan ElapsedTime => DateTime.UtcNow - StartTime;

    /// <summary>
    /// Checks if we've hit the maximum iteration limit
    /// </summary>
    public bool HasReachedMaxIterations => CurrentIteration >= MaxIterations;

    /// <summary>
    /// Records a successful iteration and resets consecutive error count
    /// </summary>
    public void RecordSuccess()
    {
        ConsecutiveErrorCount = 0;
    }

    /// <summary>
    /// Records an error and increments consecutive error count
    /// </summary>
    public void RecordError()
    {
        ConsecutiveErrorCount++;
    }

    /// <summary>
    /// Checks if consecutive errors have exceeded the maximum allowed limit
    /// </summary>
    /// <param name="maxConsecutiveErrors">Maximum allowed consecutive errors</param>
    /// <returns>True if limit exceeded, false otherwise</returns>
    public bool HasExceededErrorLimit(int maxConsecutiveErrors)
    {
        return ConsecutiveErrorCount > maxConsecutiveErrors;
    }

    /// <summary>
    /// Checks if execution is approaching a timeout threshold.
    /// </summary>
    /// <param name="threshold">Time buffer before timeout (e.g., 30 seconds)</param>
    /// <param name="maxDuration">Maximum allowed duration (defaults to 5 minutes)</param>
    /// <returns>True if elapsed time is within threshold of max duration</returns>
    public bool IsNearTimeout(TimeSpan threshold, TimeSpan? maxDuration = null)
    {
        var max = maxDuration ?? TimeSpan.FromMinutes(5);
        return ElapsedTime > (max - threshold);
    }

    /// <summary>
    /// Checks if execution is near the iteration limit.
    /// </summary>
    /// <param name="buffer">Number of iterations before limit (e.g., 2 means stop if 2 iterations remain)</param>
    /// <returns>True if current iteration is within buffer of max iterations</returns>
    public bool IsNearIterationLimit(int buffer = 2)
    {
        return CurrentIteration >= MaxIterations - buffer;
    }
}
