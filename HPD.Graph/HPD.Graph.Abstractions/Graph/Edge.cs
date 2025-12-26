namespace HPDAgent.Graph.Abstractions.Graph;

/// <summary>
/// Directed edge connecting two nodes.
/// Defines execution flow and optional conditional routing.
/// </summary>
public sealed record Edge
{
    /// <summary>
    /// Source node ID.
    /// </summary>
    public required string From { get; init; }

    /// <summary>
    /// Target node ID.
    /// </summary>
    public required string To { get; init; }

    /// <summary>
    /// Optional condition for traversing this edge.
    /// If null, edge is always traversed (unconditional).
    /// </summary>
    public EdgeCondition? Condition { get; init; }

    /// <summary>
    /// Additional metadata (labels, weights, etc.).
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}
