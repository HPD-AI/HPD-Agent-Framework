namespace HPD.Agent.Session;

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
