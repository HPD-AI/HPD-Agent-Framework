namespace HPDAgent.Graph.Abstractions.Execution;

/// <summary>
/// Represents all possible outcomes of a node execution.
/// C# discriminated union via sealed record hierarchy.
/// Universal primitive for ANY workflow (not specific to any domain).
/// </summary>
public abstract record NodeExecutionResult
{
    /// <summary>
    /// Node executed successfully and produced outputs.
    /// </summary>
    public sealed record Success(
        Dictionary<string, object> Outputs,
        TimeSpan Duration,
        NodeExecutionMetadata? Metadata = null
    ) : NodeExecutionResult;

    /// <summary>
    /// Node failed with an error.
    /// </summary>
    public sealed record Failure(
        Exception Exception,
        ErrorSeverity Severity,
        bool IsTransient,
        TimeSpan Duration,
        string? ErrorCode = null,
        Dictionary<string, object>? PartialOutputs = null,
        NodeExecutionMetadata? Metadata = null
    ) : NodeExecutionResult;

    /// <summary>
    /// Node was skipped (dependency failed, condition not met, etc.).
    /// </summary>
    public sealed record Skipped(
        SkipReason Reason,
        string? Message = null,
        string? UpstreamFailedNode = null
    ) : NodeExecutionResult;

    /// <summary>
    /// Node execution suspended for external input.
    /// Human-in-the-loop, approval workflows, external decisions.
    /// </summary>
    public sealed record Suspended(
        string SuspendToken,
        object? ResumeValue = null,
        string? Message = null
    ) : NodeExecutionResult;

    /// <summary>
    /// Node was cancelled before or during execution.
    /// </summary>
    public sealed record Cancelled(
        CancellationReason Reason,
        string? Message = null
    ) : NodeExecutionResult;

    // Prevent external inheritance - only the nested records above
    private NodeExecutionResult() { }
}
