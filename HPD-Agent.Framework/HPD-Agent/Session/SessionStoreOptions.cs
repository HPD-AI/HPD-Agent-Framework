namespace HPD.Agent;

/// <summary>
/// Options for configuring session persistence behavior.
/// Controls when and how sessions are saved and retained.
/// </summary>
public class SessionStoreOptions
{
    /// <summary>
    /// Whether to automatically save session snapshot after each turn completes.
    /// When false, you must call SaveSessionAsync() manually.
    /// Default: false (manual save).
    /// </summary>
    /// <remarks>
    /// This is separate from durable execution checkpoints:
    /// <list type="bullet">
    /// <item><strong>PersistAfterTurn:</strong> Saves SessionSnapshot after turn completes (conversation history)</item>
    /// <item><strong>DurableExecution:</strong> Saves ExecutionCheckpoint during execution (crash recovery)</item>
    /// </list>
    /// You can use either or both features independently.
    /// </remarks>
    public bool PersistAfterTurn { get; set; } = false;

    /// <summary>
    /// [DEPRECATED] Use PersistAfterTurn instead.
    /// </summary>
    [Obsolete("Use PersistAfterTurn instead")]
    public bool AutoSave
    {
        get => PersistAfterTurn;
        set => PersistAfterTurn = value;
    }

    /// <summary>
    /// How frequently to create checkpoints during agent execution.
    /// Default: PerTurn (checkpoint after each message turn completes).
    /// </summary>
    public CheckpointFrequency Frequency { get; set; } = CheckpointFrequency.PerTurn;

    /// <summary>
    /// How many checkpoints to retain.
    /// Default: LastN(3) - covers undo, crash recovery, without unbounded growth.
    /// </summary>
    public RetentionPolicy Retention { get; set; } = RetentionPolicy.LastN(3);

    /// <summary>
    /// Whether to enable pending writes for partial failure recovery.
    /// When true, successful function results are saved before the iteration checkpoint,
    /// allowing recovery without re-executing successful operations.
    /// Default: false.
    /// </summary>
    public bool EnablePendingWrites { get; set; } = false;
}

/// <summary>
/// Policy for how many checkpoints to retain.
/// </summary>
public abstract record RetentionPolicy
{
    /// <summary>
    /// Keep only the latest checkpoint per session (default).
    /// Minimizes storage, sufficient for crash recovery.
    /// </summary>
    public static readonly RetentionPolicy LatestOnly = new LatestOnlyPolicy();

    /// <summary>
    /// Keep all checkpoints (full history).
    /// Enables time-travel debugging and audit trails.
    /// </summary>
    public static readonly RetentionPolicy FullHistory = new FullHistoryPolicy();

    /// <summary>
    /// Keep the last N checkpoints.
    /// Balance between storage and history depth.
    /// </summary>
    public static RetentionPolicy LastN(int n) => new LastNPolicy(n);

    /// <summary>
    /// Keep checkpoints from the last specified duration.
    /// Good for compliance requirements (e.g., keep last 30 days).
    /// </summary>
    public static RetentionPolicy TimeBased(TimeSpan duration) => new TimeBasedPolicy(duration);

    /// <summary>
    /// Apply retention policy to prune checkpoints.
    /// </summary>
    /// <param name="store">The session store</param>
    /// <param name="sessionId">Session to apply policy to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    internal virtual Task ApplyAsync(ISessionStore store, string sessionId, CancellationToken cancellationToken)
    {
        // Base implementation does nothing (for FullHistory)
        return Task.CompletedTask;
    }

    private sealed record LatestOnlyPolicy : RetentionPolicy
    {
        internal override async Task ApplyAsync(ISessionStore store, string sessionId, CancellationToken cancellationToken)
        {
            // Prune to keep only the latest checkpoint
            await store.PruneCheckpointsAsync(sessionId, keepLatest: 1, cancellationToken);
        }
    }

    private sealed record FullHistoryPolicy : RetentionPolicy
    {
        // Don't prune anything - use base implementation
    }

    internal sealed record LastNPolicy(int N) : RetentionPolicy
    {
        internal override async Task ApplyAsync(ISessionStore store, string sessionId, CancellationToken cancellationToken)
        {
            await store.PruneCheckpointsAsync(sessionId, keepLatest: N, cancellationToken);
        }
    }

    internal sealed record TimeBasedPolicy(TimeSpan Duration) : RetentionPolicy
    {
        internal override async Task ApplyAsync(ISessionStore store, string sessionId, CancellationToken cancellationToken)
        {
            await store.DeleteOlderThanAsync(DateTime.UtcNow - Duration, cancellationToken);
        }
    }
}
