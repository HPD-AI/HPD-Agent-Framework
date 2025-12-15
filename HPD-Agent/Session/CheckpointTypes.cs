// Core checkpoint types used across the codebase.
// These types are shared by Session stores, Agent, and middleware.

namespace HPD.Agent.Session;

/// <summary>
/// Checkpoint frequency configuration.
/// </summary>
public enum CheckpointFrequency
{
    /// <summary>
    /// Checkpoint after each message turn completes (recommended).
    /// Balances durability with performance.
    /// </summary>
    PerTurn,

    /// <summary>
    /// Checkpoint after each agent iteration.
    /// More frequent but higher overhead.
    /// Use for long-running agents (>10 iterations).
    /// </summary>
    PerIteration,

    /// <summary>
    /// Only checkpoint when explicitly requested.
    /// Lowest overhead but least durable.
    /// </summary>
    Manual
}

/// <summary>
/// How a checkpoint was created.
/// </summary>
public enum CheckpointSource
{
    /// <summary>
    /// Checkpoint created from initial user input (before execution starts).
    /// </summary>
    Input,

    /// <summary>
    /// Checkpoint created during normal agent loop execution.
    /// </summary>
    Loop,

    /// <summary>
    /// Checkpoint created from manual state update (e.g., user editing state).
    /// </summary>
    Update,

    /// <summary>
    /// Checkpoint created as a fork/branch of another checkpoint within the same session.
    /// </summary>
    Fork,

    /// <summary>
    /// Checkpoint created by copying state from another session to a new independent session.
    /// Tracks lineage via ParentSessionId and ParentCheckpointId in CheckpointMetadata.
    /// </summary>
    Copy,

    /// <summary>
    /// Root checkpoint created automatically on first save in FullHistory mode.
    /// This checkpoint represents the empty conversation state at messageIndex=-1,
    /// enabling "edit first message" functionality by providing a fork point.
    /// </summary>
    Root,

    /// <summary>
    /// Lightweight snapshot created after successful turn completion.
    /// Contains messages + metadata but no ExecutionState.
    /// Used for normal session persistence (not crash recovery).
    /// </summary>
    Snapshot,

    /// <summary>
    /// Checkpoint created by explicit user request via SaveCheckpointAsync.
    /// Used when CheckpointFrequency.Manual is configured.
    /// </summary>
    Manual
}

/// <summary>
/// Metadata about how and when a checkpoint was created.
/// Used for time-travel debugging and audit trails.
/// </summary>
public class CheckpointMetadata
{
    /// <summary>
    /// Source of this checkpoint.
    /// </summary>
    public CheckpointSource Source { get; set; }

    /// <summary>
    /// Step number (iteration) when this checkpoint was created.
    /// -1 for initial input checkpoint, 0+ for loop checkpoints.
    /// </summary>
    public int Step { get; set; }

    /// <summary>
    /// Optional: Parent checkpoint ID for tracking lineage within the same thread.
    /// Used for forking and branching execution paths.
    /// </summary>
    public string? ParentCheckpointId { get; set; }

    /// <summary>
    /// Optional: Source session ID when this checkpoint was created via Copy.
    /// Used for cross-session lineage tracking (see <see cref="CheckpointSource.Copy"/>).
    /// </summary>
    public string? ParentSessionId { get; set; }

    /// <summary>
    /// Message count at this checkpoint.
    /// </summary>
    public int MessageIndex { get; set; }
}

/// <summary>
/// Entry in the checkpoint manifest (lightweight metadata for listing).
/// </summary>
public class CheckpointManifestEntry
{
    /// <summary>
    /// Unique identifier for this execution checkpoint.
    /// </summary>
    public required string ExecutionCheckpointId { get; set; }

    /// <summary>
    /// When this checkpoint was created (UTC).
    /// </summary>
    public required DateTime CreatedAt { get; set; }

    /// <summary>
    /// Iteration number when this checkpoint was created.
    /// </summary>
    public int Step { get; set; }

    /// <summary>
    /// What triggered this checkpoint (Loop, Turn, Manual, etc.).
    /// </summary>
    public CheckpointSource Source { get; set; }

    /// <summary>
    /// Parent checkpoint ID for tree structure (time-travel branching).
    /// </summary>
    public string? ParentExecutionCheckpointId { get; set; }

    /// <summary>
    /// Message count at this checkpoint.
    /// </summary>
    public int MessageIndex { get; set; }

    /// <summary>
    /// Flag to distinguish lightweight snapshots from full checkpoints.
    /// true = Snapshot (~20KB), false = Full checkpoint (~100KB)
    /// </summary>
    public bool IsSnapshot { get; set; }

    // Legacy property for backward compatibility during transition
    [Obsolete("Use ExecutionCheckpointId instead")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string CheckpointId
    {
        get => ExecutionCheckpointId;
        set => ExecutionCheckpointId = value;
    }
}

/// <summary>
/// Represents a single checkpoint or snapshot with metadata (FullHistory mode).
/// </summary>
/// <remarks>
/// A CheckpointTuple can hold either:
/// <list type="bullet">
/// <item>A full checkpoint (State is set) - for crash recovery during execution</item>
/// <item>A snapshot (Snapshot is set) - for normal session persistence after completion</item>
/// </list>
/// </remarks>
public class CheckpointTuple
{
    /// <summary>
    /// Unique identifier for this execution checkpoint.
    /// </summary>
    public required string ExecutionCheckpointId { get; set; }

    /// <summary>
    /// When this checkpoint was created (UTC).
    /// </summary>
    public required DateTime CreatedAt { get; set; }

    /// <summary>
    /// Execution state for full checkpoints. Null for snapshots or root checkpoints.
    /// </summary>
    public AgentLoopState? State { get; set; }

    /// <summary>
    /// Snapshot data for lightweight saves. Null for full checkpoints.
    /// When set, this is a snapshot (messages + metadata only, no ExecutionState).
    /// </summary>
    public SessionSnapshot? Snapshot { get; set; }

    public required CheckpointMetadata Metadata { get; set; }

    /// <summary>
    /// Parent checkpoint ID for tree structure (time-travel branching).
    /// </summary>
    public string? ParentExecutionCheckpointId { get; set; }

    /// <summary>
    /// Message index this checkpoint was created after.
    /// Enables "fork from message N" operations.
    /// </summary>
    public int MessageIndex { get; set; }

    /// <summary>
    /// Whether this is a snapshot (lightweight) or full checkpoint.
    /// </summary>
    public bool IsSnapshot => Snapshot != null;

    // Legacy property for backward compatibility during transition
    [Obsolete("Use ExecutionCheckpointId instead")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string CheckpointId
    {
        get => ExecutionCheckpointId;
        set => ExecutionCheckpointId = value;
    }

    // Legacy property for backward compatibility during transition
    [Obsolete("Use ParentExecutionCheckpointId instead")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ParentCheckpointId
    {
        get => ParentExecutionCheckpointId;
        set => ParentExecutionCheckpointId = value;
    }
}
