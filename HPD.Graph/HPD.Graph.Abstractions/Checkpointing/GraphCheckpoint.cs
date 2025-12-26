namespace HPDAgent.Graph.Abstractions.Checkpointing;

/// <summary>
/// Immutable snapshot of graph execution state.
/// Enables resume-from-failure and incremental execution.
/// </summary>
public sealed record GraphCheckpoint
{
    /// <summary>
    /// Unique identifier for this checkpoint.
    /// </summary>
    public required string CheckpointId { get; init; }

    /// <summary>
    /// The execution this checkpoint belongs to.
    /// </summary>
    public required string ExecutionId { get; init; }

    /// <summary>
    /// The graph being executed.
    /// </summary>
    public required string GraphId { get; init; }

    /// <summary>
    /// When this checkpoint was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Nodes that have successfully completed.
    /// Used to skip already-completed nodes on resume.
    /// </summary>
    public required IReadOnlySet<string> CompletedNodes { get; init; }

    /// <summary>
    /// Named outputs from completed nodes.
    /// Format: "nodeId.outputName" → value
    /// Used to reconstruct handler inputs on resume.
    /// </summary>
    public required IReadOnlyDictionary<string, object> NodeOutputs { get; init; }

    /// <summary>
    /// Full execution context (serialized).
    /// Includes channels, managed context, logs, etc.
    /// </summary>
    public required string ContextJson { get; init; }

    /// <summary>
    /// Optional metadata about this checkpoint.
    /// </summary>
    public CheckpointMetadata? Metadata { get; init; }

    /// <summary>
    /// Schema version for forward/backward compatibility.
    /// </summary>
    public string SchemaVersion { get; init; } = "1.0";
}

/// <summary>
/// Additional metadata about a checkpoint.
/// </summary>
public sealed record CheckpointMetadata
{
    /// <summary>
    /// What triggered this checkpoint.
    /// </summary>
    public required CheckpointTrigger Trigger { get; init; }

    /// <summary>
    /// The node that just completed (if Trigger = NodeCompleted).
    /// </summary>
    public string? CompletedNodeId { get; init; }

    /// <summary>
    /// The layer that just completed (if Trigger = LayerCompleted).
    /// </summary>
    public int? CompletedLayer { get; init; }

    /// <summary>
    /// Custom metadata (e.g., cost tracking, performance metrics).
    /// </summary>
    public IReadOnlyDictionary<string, object>? CustomMetadata { get; init; }
}

/// <summary>
/// What triggered a checkpoint to be saved.
/// </summary>
public enum CheckpointTrigger
{
    /// <summary>
    /// After a single node completed.
    /// Most granular, highest storage cost.
    /// </summary>
    NodeCompleted,

    /// <summary>
    /// After an entire layer completed.
    /// RECOMMENDED for most use cases.
    /// </summary>
    LayerCompleted,

    /// <summary>
    /// Manually triggered by user code.
    /// </summary>
    Manual,

    /// <summary>
    /// Triggered during graceful shutdown.
    /// </summary>
    Shutdown
}
