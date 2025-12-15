using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


namespace HPD.Agent.Session;

/// <summary>
/// Interface for persisting and loading AgentSession state.
/// Supports two distinct persistence concerns:
/// <list type="bullet">
/// <item><strong>Session Persistence:</strong> Snapshots for conversation history (after turn completes)</item>
/// <item><strong>Execution Checkpoints:</strong> For crash recovery during execution</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <strong>Design Note:</strong> This interface is a CRUD-only layer. The store is "dumb" -
/// it just reads/writes data. Business logic (retention policies, auto-save) lives in services.
/// </para>
/// <para>
/// <strong>Architecture:</strong> Based on LangGraph's separation of Checkpointer (execution state)
/// and Store (conversation history). This eliminates message duplication that occurred when
/// messages were stored in both SessionSnapshot and AgentLoopState.CurrentMessages.
/// </para>
/// </remarks>
public interface ISessionStore
{
    // ═══════════════════════════════════════════════════════════════════
    // SESSION PERSISTENCE (Snapshots - conversation history)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Load a session from persistent storage by ID.
    /// Returns the latest snapshot (conversation state, no ExecutionState).
    /// Returns null if session doesn't exist.
    /// </summary>
    Task<AgentSession?> LoadSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Save a session snapshot to persistent storage.
    /// This persists messages, metadata, and middleware persistent state.
    /// Does NOT require ExecutionState - use for normal conversation persistence.
    /// </summary>
    /// <remarks>
    /// For stores with history support, this creates a new snapshot entry.
    /// </remarks>
    Task SaveSessionAsync(
        AgentSession session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List all session IDs in storage.
    /// Useful for admin UIs, cleanup jobs, etc.
    /// </summary>
    Task<List<string>> ListSessionIdsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a session and all its checkpoints from persistent storage.
    /// </summary>
    Task DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    // ═══════════════════════════════════════════════════════════════════
    // EXECUTION CHECKPOINTS (Crash Recovery)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Load the latest execution checkpoint for crash recovery.
    /// Returns null if no checkpoint exists or session completed normally.
    /// </summary>
    /// <remarks>
    /// ExecutionCheckpoint contains AgentLoopState with messages inside
    /// ExecutionState.CurrentMessages (single source of truth during execution).
    /// </remarks>
    Task<ExecutionCheckpoint?> LoadCheckpointAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Save an execution checkpoint for crash recovery.
    /// Called during agent execution (DurableExecution).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ExecutionCheckpoint stores ONLY ExecutionState - messages are inside
    /// ExecutionState.CurrentMessages, eliminating duplication.
    /// </para>
    /// <para>
    /// Size: ~100KB (vs ~120KB with old SessionCheckpoint that duplicated messages).
    /// </para>
    /// </remarks>
    Task SaveCheckpointAsync(
        ExecutionCheckpoint checkpoint,
        CheckpointMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all execution checkpoints for a session.
    /// Called after successful completion (checkpoints no longer needed).
    /// </summary>
    Task DeleteAllCheckpointsAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    // ═══════════════════════════════════════════════════════════════════
    // CAPABILITY DETECTION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether this store supports checkpoint history (multiple checkpoints per session).
    /// When false, only the latest checkpoint is stored.
    /// </summary>
    bool SupportsHistory { get; }

    /// <summary>
    /// Whether this store supports pending writes for partial failure recovery.
    /// </summary>
    bool SupportsPendingWrites { get; }

    // ═══════════════════════════════════════════════════════════════════
    // PENDING WRITES (Partial Iteration Recovery)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Save pending writes for a specific execution checkpoint.
    /// Pending writes are function call results saved before the iteration checkpoint completes.
    /// Used for partial failure recovery in parallel execution scenarios.
    /// </summary>
    Task SavePendingWritesAsync(
        string sessionId,
        string executionCheckpointId,
        IEnumerable<PendingWrite> writes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Load pending writes for a specific execution checkpoint.
    /// Returns empty list if no pending writes exist.
    /// </summary>
    Task<List<PendingWrite>> LoadPendingWritesAsync(
        string sessionId,
        string executionCheckpointId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete pending writes for a specific execution checkpoint.
    /// Called after a successful checkpoint save to clean up temporary data.
    /// </summary>
    Task DeletePendingWritesAsync(
        string sessionId,
        string executionCheckpointId,
        CancellationToken cancellationToken = default);

    // ═══════════════════════════════════════════════════════════════════
    // CHECKPOINT HISTORY (Optional - check SupportsHistory first)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Load a specific execution checkpoint by ID (requires SupportsHistory).
    /// Returns null if checkpoint doesn't exist.
    /// </summary>
    Task<ExecutionCheckpoint?> LoadCheckpointAtAsync(
        string sessionId,
        string executionCheckpointId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get checkpoint manifest entries for a session (requires SupportsHistory).
    /// Returns list of checkpoint metadata ordered by creation time (newest first).
    /// </summary>
    Task<List<CheckpointManifestEntry>> GetCheckpointManifestAsync(
        string sessionId,
        int? limit = null,
        CancellationToken cancellationToken = default);

    // ═══════════════════════════════════════════════════════════════════
    // CLEANUP METHODS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Prune old checkpoints for a session.
    /// Keeps the N most recent checkpoints and deletes the rest.
    /// </summary>
    /// <param name="sessionId">Session to prune checkpoints for</param>
    /// <param name="keepLatest">Number of most recent checkpoints to keep (default: 10)</param>
    Task PruneCheckpointsAsync(
        string sessionId,
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
    /// Delete sessions that have been inactive for longer than the threshold.
    /// A session is considered inactive if its LastActivity timestamp is older than threshold.
    /// </summary>
    /// <param name="inactivityThreshold">Sessions inactive longer than this will be deleted</param>
    /// <param name="dryRun">If true, returns count of sessions that would be deleted without deleting them</param>
    /// <returns>Number of sessions deleted (or would be deleted in dry-run mode)</returns>
    Task<int> DeleteInactiveSessionsAsync(
        TimeSpan inactivityThreshold,
        bool dryRun = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete specific checkpoints by ID.
    /// Used by services for pruning operations.
    /// </summary>
    Task DeleteCheckpointsAsync(
        string sessionId,
        IEnumerable<string> checkpointIds,
        CancellationToken cancellationToken = default);

    // ═══════════════════════════════════════════════════════════════════
    // LEGACY METHODS (Deprecated - for backward compatibility)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// [DEPRECATED] Use LoadCheckpointAtAsync instead.
    /// Load a specific checkpoint by ID (requires SupportsHistory).
    /// </summary>
    [Obsolete("Use LoadCheckpointAtAsync for execution checkpoints")]
    Task<AgentSession?> LoadSessionAtCheckpointAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// [DEPRECATED] Use SaveCheckpointAsync instead.
    /// Save a session at a specific checkpoint ID.
    /// </summary>
    [Obsolete("Use SaveCheckpointAsync for execution checkpoints")]
    Task SaveSessionAtCheckpointAsync(
        AgentSession session,
        string checkpointId,
        CheckpointMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// [DEPRECATED] Use GetCheckpointManifestAsync instead.
    /// Update checkpoint manifest entry (e.g., to change branch name).
    /// </summary>
    [Obsolete("Use GetCheckpointManifestAsync for reading checkpoint history")]
    Task UpdateCheckpointManifestEntryAsync(
        string sessionId,
        string checkpointId,
        Action<CheckpointManifestEntry> update,
        CancellationToken cancellationToken = default);
}
