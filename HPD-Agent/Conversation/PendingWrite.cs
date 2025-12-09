namespace HPD.Agent.Checkpointing;

/// <summary>
/// Represents a function call result saved before iteration checkpoint.
/// Used for partial failure recovery in parallel execution scenarios.
/// </summary>
/// <remarks>
/// <para>
/// Pending writes are created when a function call completes successfully during
/// an iteration, but before the full checkpoint is saved. This allows the system
/// to avoid re-executing successful operations if a crash occurs.
/// </para>
/// <para>
/// <strong>Lifecycle:</strong>
/// <list type="number">
/// <item>Function completes → Pending write saved immediately (fire-and-forget)</item>
/// <item>Iteration checkpoint saves → Pending writes deleted (no longer needed)</item>
/// <item>On resume → Pending writes loaded and restored as tool messages</item>
/// </list>
/// </para>
/// <para>
/// <strong>Example:</strong>
/// <code>
/// // During execution (3 parallel calls)
/// GetWeather() → Success → Save pending write
/// GetNews() → Success → Save pending write
/// AnalyzeData() → CRASH ❌
///
/// // On resume
/// Load pending writes → Restore GetWeather and GetNews results
/// Only re-execute AnalyzeData()
/// </code>
/// </para>
/// </remarks>
public sealed record PendingWrite
{
    /// <summary>
    /// Unique identifier for the function call (from FunctionCallContent.CallId).
    /// Used to match restored results with expected function calls.
    /// </summary>
    public required string CallId { get; init; }

    /// <summary>
    /// Name of the function that was called.
    /// Used for telemetry and debugging.
    /// </summary>
    public required string FunctionName { get; init; }

    /// <summary>
    /// The result returned by the function (serialized as JSON).
    /// Will be deserialized and restored as a FunctionResultContent on resume.
    /// </summary>
    public required string ResultJson { get; init; }

    /// <summary>
    /// When this function completed successfully (UTC).
    /// Used for cleanup and telemetry.
    /// </summary>
    public required DateTime CompletedAt { get; init; }

    /// <summary>
    /// Iteration number when this function was called.
    /// Used to associate pending writes with specific checkpoints.
    /// </summary>
    public required int Iteration { get; init; }

    /// <summary>
    /// Thread ID this write belongs to.
    /// Used for Collapsing pending writes to specific conversation threads.
    /// </summary>
    public required string ThreadId { get; init; }
}
