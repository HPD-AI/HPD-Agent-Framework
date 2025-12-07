using HPD.Agent.Checkpointing;

namespace HPD.Agent.Checkpointing.Services;

/// <summary>
/// Configuration for the DurableExecutionService.
/// Controls when and how checkpoints are created and retained.
/// </summary>
public class DurableExecutionConfig
{
    /// <summary>
    /// Whether durable execution is enabled.
    /// When false, all checkpoint operations are no-ops.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How frequently to create checkpoints.
    /// </summary>
    public CheckpointFrequency Frequency { get; set; } = CheckpointFrequency.PerTurn;

    /// <summary>
    /// How many checkpoints to retain.
    /// </summary>
    public RetentionPolicy Retention { get; set; } = RetentionPolicy.LatestOnly;

    /// <summary>
    /// Whether to enable pending writes for partial failure recovery.
    /// When true, successful function results are saved before the iteration checkpoint,
    /// allowing recovery without re-executing successful operations.
    /// </summary>
    public bool EnablePendingWrites { get; set; } = false;
}

/// <summary>
/// Policy for how many checkpoints to retain.
/// </summary>
public abstract record RetentionPolicy
{
    /// <summary>
    /// Keep only the latest checkpoint per thread (default).
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
    /// <param name="store">The checkpoint store</param>
    /// <param name="threadId">Thread to apply policy to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    internal virtual Task ApplyAsync(ICheckpointStore store, string threadId, CancellationToken cancellationToken)
    {
        // Base implementation does nothing (for FullHistory)
        return Task.CompletedTask;
    }

    private sealed record LatestOnlyPolicy : RetentionPolicy
    {
        internal override async Task ApplyAsync(ICheckpointStore store, string threadId, CancellationToken cancellationToken)
        {
            // Prune to keep only the latest checkpoint
            await store.PruneCheckpointsAsync(threadId, keepLatest: 1, cancellationToken);
        }
    }

    private sealed record FullHistoryPolicy : RetentionPolicy
    {
        // Don't prune anything - use base implementation
    }

    internal sealed record LastNPolicy(int N) : RetentionPolicy
    {
        internal override async Task ApplyAsync(ICheckpointStore store, string threadId, CancellationToken cancellationToken)
        {
            await store.PruneCheckpointsAsync(threadId, keepLatest: N, cancellationToken);
        }
    }

    internal sealed record TimeBasedPolicy(TimeSpan Duration) : RetentionPolicy
    {
        internal override async Task ApplyAsync(ICheckpointStore store, string threadId, CancellationToken cancellationToken)
        {
            await store.DeleteOlderThanAsync(DateTime.UtcNow - Duration, cancellationToken);
        }
    }
}
