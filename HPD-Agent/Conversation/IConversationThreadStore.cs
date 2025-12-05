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
/// Use this interface when you need:
/// - Checkpoint history (time-travel debugging)
/// - Checkpoint pruning and cleanup
/// - Pending writes for partial failure recovery
/// - Age-based or inactivity-based cleanup
/// </remarks>
public interface ICheckpointStore : IThreadStore
{
    /// <summary>
    /// Gets the checkpoint retention mode for this store.
    /// </summary>
    CheckpointRetentionMode RetentionMode { get; }

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

    // ===== FULL HISTORY METHODS =====
    // These methods are only meaningful when RetentionMode == FullHistory

    /// <summary>
    /// Load a specific checkpoint by ID (FullHistory mode only).
    /// Returns null if checkpoint doesn't exist.
    /// </summary>
    Task<ConversationThread?> LoadThreadAtCheckpointAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get checkpoint history for a thread (FullHistory mode only).
    /// Returns list of checkpoints ordered by creation time (newest first).
    /// </summary>
    Task<List<CheckpointTuple>> GetCheckpointHistoryAsync(
        string threadId,
        int? limit = null,
        DateTime? before = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Combined interface for backward compatibility.
/// Equivalent to ICheckpointStore - use IThreadStore or ICheckpointStore directly for new code.
/// </summary>
[Obsolete("Use IThreadStore for simple persistence or ICheckpointStore for advanced features")]
public interface IConversationThreadStore : ICheckpointStore
{
}

/// <summary>
/// Represents a single checkpoint with metadata (FullHistory mode).
/// </summary>
public class CheckpointTuple
{
    public required string CheckpointId { get; set; }
    public required DateTime CreatedAt { get; set; }
    public required AgentLoopState State { get; set; }
    public required CheckpointMetadata Metadata { get; set; }
    public string? ParentCheckpointId { get; set; }
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
    /// Optional: Parent checkpoint ID for tracking lineage.
    /// Used for forking and branching execution paths.
    /// </summary>
    public string? ParentCheckpointId { get; set; }
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
    /// Checkpoint created as a fork/copy of another checkpoint.
    /// </summary>
    Fork
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
