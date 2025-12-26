namespace HPDAgent.Graph.Abstractions.Graph;

/// <summary>
/// A node in the execution graph.
/// Nodes represent units of work (handlers) or control flow (START/END/Router).
/// Immutable after construction.
/// </summary>
public sealed record Node
{
    /// <summary>
    /// Unique node identifier within the graph.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable node name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Node type (Start, End, Handler, Router, SubGraph).
    /// </summary>
    public required NodeType Type { get; init; }

    /// <summary>
    /// Handler name for Handler and Router nodes.
    /// Must match a registered IGraphNodeHandler.HandlerName.
    /// Null for Start/End nodes.
    /// </summary>
    public string? HandlerName { get; init; }

    /// <summary>
    /// Node-specific configuration.
    /// Deserialized into handler-specific config types at runtime.
    /// </summary>
    public IReadOnlyDictionary<string, object> Config { get; init; } =
        new Dictionary<string, object>();

    /// <summary>
    /// Timeout for this node execution.
    /// Null = no timeout (use graph-level default).
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Retry policy for this node.
    /// Null = no retries.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Additional metadata (tags, labels, etc.).
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Enable checkpointing for this node.
    /// If true, node state is saved after execution.
    /// </summary>
    public bool EnableCheckpointing { get; init; } = true;

    /// <summary>
    /// Maximum number of times this node can execute (for loop detection).
    /// Null = unlimited.
    /// </summary>
    public int? MaxExecutions { get; init; }

    /// <summary>
    /// Sub-graph definition (for NodeType.SubGraph).
    /// Either embedded or referenced via SubGraphRef.
    /// </summary>
    public Graph? SubGraph { get; init; }

    /// <summary>
    /// Sub-graph reference (path or URI to graph definition).
    /// Alternative to inline SubGraph - load graph at runtime.
    /// </summary>
    public string? SubGraphRef { get; init; }

    /// <summary>
    /// Maximum input buffer size for backpressure management.
    /// When buffer is full, upstream nodes are throttled.
    /// Null = unbounded (default).
    /// Prevents memory overflow in high-throughput scenarios.
    /// </summary>
    public int? MaxInputBufferSize { get; init; }
}
