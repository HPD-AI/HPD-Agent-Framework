namespace HPDAgent.Graph.Abstractions.Execution;

/// <summary>
/// Additional metadata about node execution.
/// </summary>
public sealed record NodeExecutionMetadata
{
    /// <summary>
    /// Retry attempt number (0 = first try, 1 = first retry, etc.).
    /// </summary>
    public int AttemptNumber { get; init; }

    /// <summary>
    /// Custom metrics collected during execution.
    /// Example: tokens_used, api_calls, cache_hits, etc.
    /// </summary>
    public Dictionary<string, object>? CustomMetrics { get; init; }

    /// <summary>
    /// Why this node executed.
    /// Example: "Scheduled execution", "Fallback from node X", "Manual retry"
    /// </summary>
    public string? ExecutionReason { get; init; }
}
