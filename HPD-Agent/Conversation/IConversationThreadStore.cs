using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HPD.Agent.Checkpointing;

/// <summary>
/// Mode for checkpoint retention.
/// </summary>
public enum CheckpointRetentionMode
{
    /// <summary>
    /// Keep only the latest checkpoint per thread (default).
    /// Minimizes storage, sufficient for crash recovery.
    /// </summary>
    LatestOnly,

    /// <summary>
    /// Keep all checkpoints (full history).
    /// Enables time-travel debugging, audit trails, replay.
    /// Requires more storage but provides complete execution history.
    /// </summary>
    FullHistory
}

/// <summary>
/// Basic interface for persisting and loading ConversationThread state.
/// Provides simple CRUD operations for conversation threads.
/// </summary>
/// <remarks>
/// Use this interface when you only need basic thread persistence without
/// advanced checkpoint management features like history, pruning, or pending writes.
/// </remarks>
public interface IThreadStore
{
    /// <summary>
    /// Load a thread from persistent storage by ID.
    /// Returns null if thread doesn't exist.
    /// </summary>
    Task<ConversationThread?> LoadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Save a thread to persistent storage.
    /// This should persist:
    /// - Thread metadata (Id, timestamps)
    /// - Messages (via MessageStore)
    /// - ExecutionState (AgentLoopState, if present)
    /// - Custom metadata
    /// </summary>
    /// <remarks>
    /// For <see cref="ICheckpointStore"/> implementations, this creates a new checkpoint
    /// with a unique ID. The first save MUST also create a root checkpoint
    /// (with <see cref="CheckpointSource.Root"/> and messageIndex=-1) to enable "edit first message".
    /// </remarks>
    Task SaveThreadAsync(
        ConversationThread thread,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List all thread IDs in storage.
    /// Useful for admin UIs, cleanup jobs, etc.
    /// </summary>
    Task<List<string>> ListThreadIdsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a thread from persistent storage.
    /// </summary>
    Task DeleteThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Extended interface for checkpoint management with advanced features.
/// Extends IThreadStore with checkpoint history, pruning, cleanup, and pending writes.
/// </summary>
/// <remarks>
/// <para>
/// This is a CRUD-only interface. The store is "dumb" - it just reads/writes data.
/// Business logic (retention policies) lives in services:
/// <list type="bullet">
/// <item><see cref="Services.DurableExecution"/> - checkpointing + retention</item>
/// </list>
/// </para>
/// <para>
/// <strong>Design Note:</strong> This interface always supports full checkpoint history.
/// For simple single-checkpoint storage, use <see cref="IThreadStore"/> directly.
/// The <see cref="Services.DurableExecution"/> controls retention via pruning.
/// </para>
/// </remarks>
public interface ICheckpointStore : IThreadStore
{

    // ===== CLEANUP METHODS =====

    /// <summary>
    /// Prune old checkpoints for a thread (FullHistory mode only).
    /// Keeps the N most recent checkpoints and deletes the rest.
    /// </summary>
    /// <param name="threadId">Thread to prune checkpoints for</param>
    /// <param name="keepLatest">Number of most recent checkpoints to keep (default: 10)</param>
    Task PruneCheckpointsAsync(
        string threadId,
        int keepLatest = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all checkpoints older than the specified cutoff date.
    /// Useful for compliance and storage management.
    /// </summary>
    Task DeleteOlderThanAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete threads that have been inactive for longer than the threshold.
    /// A thread is considered inactive if its LastActivity timestamp is older than threshold.
    /// </summary>
    /// <param name="inactivityThreshold">Threads inactive longer than this will be deleted</param>
    /// <param name="dryRun">If true, returns count of threads that would be deleted without deleting them</param>
    /// <returns>Number of threads deleted (or would be deleted in dry-run mode)</returns>
    Task<int> DeleteInactiveThreadsAsync(
        TimeSpan inactivityThreshold,
        bool dryRun = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete specific checkpoints by ID.
    /// Used by services for pruning operations.
    /// </summary>
    Task DeleteCheckpointsAsync(
        string threadId,
        IEnumerable<string> checkpointIds,
        CancellationToken cancellationToken = default);

    // ===== PENDING WRITES METHODS =====

    /// <summary>
    /// Save pending writes for a specific checkpoint.
    /// Pending writes are function call results saved before the iteration checkpoint completes.
    /// Used for partial failure recovery in parallel execution scenarios.
    /// </summary>
    /// <param name="threadId">Thread ID these writes belong to</param>
    /// <param name="checkpointId">Checkpoint ID (ETag) these writes are associated with</param>
    /// <param name="writes">Function call results to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SavePendingWritesAsync(
        string threadId,
        string checkpointId,
        IEnumerable<PendingWrite> writes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Load pending writes for a specific checkpoint.
    /// Returns empty list if no pending writes exist.
    /// </summary>
    /// <param name="threadId">Thread ID to load writes for</param>
    /// <param name="checkpointId">Checkpoint ID (ETag) to load writes for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of pending writes, or empty list if none exist</returns>
    Task<List<PendingWrite>> LoadPendingWritesAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete pending writes for a specific checkpoint.
    /// Called after a successful checkpoint save to clean up temporary data.
    /// </summary>
    /// <param name="threadId">Thread ID to delete writes for</param>
    /// <param name="checkpointId">Checkpoint ID (ETag) to delete writes for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeletePendingWritesAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default);

    // ===== CHECKPOINT ACCESS METHODS =====
    // Low-level checkpoint access for services to build on

    /// <summary>
    /// Load a specific checkpoint by ID (FullHistory mode only).
    /// Returns null if checkpoint doesn't exist.
    /// </summary>
    Task<ConversationThread?> LoadThreadAtCheckpointAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Save a thread at a specific checkpoint ID.
    /// Used by services for fork/branch operations.
    /// </summary>
    /// <param name="thread">Thread to save</param>
    /// <param name="checkpointId">Checkpoint ID to use</param>
    /// <param name="metadata">Checkpoint metadata (source, parent, etc.)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveThreadAtCheckpointAsync(
        ConversationThread thread,
        string checkpointId,
        CheckpointMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get checkpoint manifest entries for a thread (FullHistory mode only).
    /// Returns list of checkpoint metadata ordered by creation time (newest first).
    /// </summary>
    Task<List<CheckpointManifestEntry>> GetCheckpointManifestAsync(
        string threadId,
        int? limit = null,
        DateTime? before = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update checkpoint manifest entry (e.g., to change branch name).
    /// </summary>
    Task UpdateCheckpointManifestEntryAsync(
        string threadId,
        string checkpointId,
        Action<CheckpointManifestEntry> update,
        CancellationToken cancellationToken = default);

    // Snapshot storage removed - branching is now an application-level concern
    // Applications should use separate threads for branches instead of snapshots
}

/// <summary>
/// Represents a single checkpoint with metadata (FullHistory mode).
/// </summary>
public class CheckpointTuple
{
    public required string CheckpointId { get; set; }
    public required DateTime CreatedAt { get; set; }
    /// <summary>
    /// Execution state. Null for snapshots (lightweight branching) or root checkpoints.
    /// </summary>
    public AgentLoopState? State { get; set; }
    public required CheckpointMetadata Metadata { get; set; }
    public string? ParentCheckpointId { get; set; }

    /// <summary>
    /// Message index this checkpoint was created after.
    /// Enables "fork from message N" operations.
    /// </summary>
    public int MessageIndex { get; set; }
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
    /// Optional: Source thread ID when this checkpoint was created via Copy.
    /// Used for cross-thread lineage tracking (see <see cref="CheckpointSource.Copy"/>).
    /// </summary>
    public string? ParentThreadId { get; set; }

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
    public required string CheckpointId { get; set; }
    public required DateTime CreatedAt { get; set; }
    public int Step { get; set; }
    public CheckpointSource Source { get; set; }

    /// <summary>
    /// Parent checkpoint ID for tree structure.
    /// </summary>
    public string? ParentCheckpointId { get; set; }

    /// <summary>
    /// Message count at this checkpoint.
    /// </summary>
    public int MessageIndex { get; set; }

    /// <summary>
    /// Flag to distinguish lightweight snapshots from full checkpoints.
    /// true = Snapshot (~20KB), false = Full checkpoint (~120KB)
    /// </summary>
    public bool IsSnapshot { get; set; }
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
    /// Checkpoint created as a fork/branch of another checkpoint within the same thread.
    /// </summary>
    Fork,

    /// <summary>
    /// Checkpoint created by copying state from another thread to a new independent thread.
    /// Tracks lineage via ParentThreadId and ParentCheckpointId in CheckpointMetadata.
    /// </summary>
    Copy,

    /// <summary>
    /// Root checkpoint created automatically on first save in FullHistory mode.
    /// This checkpoint represents the empty conversation state at messageIndex=-1,
    /// enabling "edit first message" functionality by providing a fork point.
    /// </summary>
    Root
}

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

// Branch types and events are defined in HPD.Agent.Checkpointing.Services.BranchingTypes
