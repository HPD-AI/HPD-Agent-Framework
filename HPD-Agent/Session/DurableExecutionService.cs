using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Service for durable execution (auto-checkpointing + retention + pending writes).
/// Owns checkpoint creation policy and retention enforcement.
/// </summary>
/// <remarks>
/// <para>
/// This service encapsulates all checkpoint-related business logic.
/// It uses <see cref="ExecutionCheckpoint"/> for crash recovery (no message duplication).
/// </para>
/// <para>
/// <strong>Responsibilities:</strong>
/// <list type="bullet">
/// <item>Deciding WHEN to checkpoint (based on Frequency config)</item>
/// <item>Creating ExecutionCheckpoint from session + state</item>
/// <item>Enforcing retention policy AFTER save</item>
/// <item>Managing pending writes for partial failure recovery</item>
/// </list>
/// </para>
/// <para>
/// The store is DUMB (just CRUD). This service tells it what to save/delete.
/// </para>
/// </remarks>
public class DurableExecution
{
    private readonly ISessionStore _store;
    private readonly DurableExecutionConfig _config;

    /// <summary>
    /// Creates a new DurableExecutionService.
    /// </summary>
    /// <param name="store">The session store</param>
    /// <param name="config">Configuration for durable execution</param>
    public DurableExecution(ISessionStore store, DurableExecutionConfig config)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Gets whether durable execution is enabled.
    /// </summary>
    public bool IsEnabled => _config.Enabled;

    /// <summary>
    /// Gets the configured checkpoint frequency.
    /// </summary>
    public CheckpointFrequency Frequency => _config.Frequency;

    /// <summary>
    /// Gets the configured retention policy.
    /// </summary>
    public RetentionPolicy Retention => _config.Retention;

    //──────────────────────────────────────────────────────────────────
    // CHECKPOINT OPERATIONS
    //──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Save an execution checkpoint for crash recovery.
    /// Uses the new ExecutionCheckpoint type (no message duplication).
    /// Enforces retention policy after save.
    /// </summary>
    /// <param name="session">The agent session</param>
    /// <param name="state">The agent loop state to checkpoint</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task SaveCheckpointAsync(
        AgentSession session,
        AgentLoopState state,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled) return;

        // Update session's execution state
        session.ExecutionState = state;

        // Create ExecutionCheckpoint (no message duplication - messages are inside state.CurrentMessages)
        var checkpointId = state.ETag ?? Guid.NewGuid().ToString();
        var checkpoint = new ExecutionCheckpoint
        {
            SessionId = session.Id,
            ExecutionCheckpointId = checkpointId,
            ExecutionState = state,
            CreatedAt = DateTime.UtcNow
        };

        var metadata = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = state.Iteration,
            MessageIndex = state.CurrentMessages.Count
        };

        await _store.SaveCheckpointAsync(checkpoint, metadata, cancellationToken);

        // Enforce retention policy AFTER save
        await EnforceRetentionAsync(session.Id, cancellationToken);

        // Cleanup pending writes after successful checkpoint
        if (_config.EnablePendingWrites && state.ETag != null)
        {
            await DeletePendingWritesAsync(session.Id, state.ETag, cancellationToken);
        }
    }

    /// <summary>
    /// Determines whether a checkpoint should be created based on current state.
    /// </summary>
    /// <param name="iteration">Current iteration number</param>
    /// <param name="turnComplete">Whether the turn has completed</param>
    /// <returns>True if a checkpoint should be created</returns>
    public bool ShouldCheckpoint(int iteration, bool turnComplete)
    {
        if (!_config.Enabled) return false;

        return _config.Frequency switch
        {
            CheckpointFrequency.PerIteration => true,
            CheckpointFrequency.PerTurn => turnComplete,
            CheckpointFrequency.Manual => false,
            _ => false
        };
    }

    /// <summary>
    /// Resume from the latest execution checkpoint (for crash recovery).
    /// </summary>
    /// <param name="sessionId">Session to resume</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The session restored from checkpoint, or null if no checkpoint exists</returns>
    public async Task<AgentSession?> ResumeFromLatestAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled) return null;

        var checkpoint = await _store.LoadCheckpointAsync(sessionId, cancellationToken);
        if (checkpoint == null)
            return null;

        return AgentSession.FromExecutionCheckpoint(checkpoint);
    }

    /// <summary>
    /// Delete all execution checkpoints for a session.
    /// Called after successful completion (checkpoints no longer needed for crash recovery).
    /// </summary>
    public async Task ClearCheckpointsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled) return;

        await _store.DeleteAllCheckpointsAsync(sessionId, cancellationToken);
    }

    /// <summary>
    /// Enforce the retention policy for a session.
    /// Called automatically after SaveCheckpointAsync.
    /// </summary>
    private async Task EnforceRetentionAsync(string sessionId, CancellationToken cancellationToken)
    {
        // Use polymorphic dispatch to apply the retention policy
        await _config.Retention.ApplyAsync(_store, sessionId, cancellationToken);
    }

    //──────────────────────────────────────────────────────────────────
    // PENDING WRITES (Partial Failure Recovery)
    //──────────────────────────────────────────────────────────────────
    // Pending writes save successful tool results BEFORE the iteration
    // checkpoint completes. On crash, we can restore these and avoid
    // re-executing tools that already succeeded.
    //
    // Flow:
    //   Tool A succeeds → SavePendingWriteAsync() [fire-and-forget]
    //   Tool B succeeds → SavePendingWriteAsync() [fire-and-forget]
    //   Tool C crashes  → 💥
    //   Resume          → LoadPendingWritesAsync() → Restore A & B results
    //   Checkpoint      → DeletePendingWritesAsync() [cleanup]
    //──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Save pending writes for successful function results (fire-and-forget).
    /// Called after each successful function execution, before the iteration checkpoint.
    /// </summary>
    /// <param name="sessionId">Session ID</param>
    /// <param name="checkpointId">Checkpoint ID (ETag)</param>
    /// <param name="toolResultMessage">Message containing function results</param>
    /// <param name="iteration">Current iteration number</param>
    public void SavePendingWriteFireAndForget(
        string sessionId,
        string checkpointId,
        ChatMessage toolResultMessage,
        int iteration)
    {
        if (!_config.EnablePendingWrites) return;

        // Extract successful function results
        var successfulResults = toolResultMessage.Contents
            .OfType<FunctionResultContent>()
            .Where(IsFunctionResultSuccessful)
            .ToList();

        if (successfulResults.Count == 0) return;

        // Create pending writes
        var pendingWrites = successfulResults.Select(result => new PendingWrite
        {
            CallId = result.CallId,
            FunctionName = result.CallId, // TODO: Get actual function name
            ResultJson = JsonSerializer.Serialize(result.Result),
            CompletedAt = DateTime.UtcNow,
            Iteration = iteration,
            SessionId = sessionId
        }).ToList();

        // Save in background (fire-and-forget)
        _ = Task.Run(async () =>
        {
            try
            {
                await _store.SavePendingWritesAsync(sessionId, checkpointId, pendingWrites);
            }
            catch
            {
                // Swallow errors - pending writes are optimization, not critical
            }
        });
    }

    /// <summary>
    /// Load pending writes for a specific checkpoint.
    /// Called during resume to restore successful function results.
    /// </summary>
    public async Task<ImmutableList<PendingWrite>> LoadPendingWritesAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        if (!_config.EnablePendingWrites)
            return ImmutableList<PendingWrite>.Empty;

        try
        {
            var writes = await _store.LoadPendingWritesAsync(sessionId, checkpointId, cancellationToken);
            return writes.ToImmutableList();
        }
        catch
        {
            // Swallow errors - if loading fails, just re-execute the functions
            return ImmutableList<PendingWrite>.Empty;
        }
    }

    /// <summary>
    /// Delete pending writes for a specific checkpoint.
    /// Called after a successful checkpoint save to clean up temporary data.
    /// </summary>
    public async Task DeletePendingWritesAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        if (!_config.EnablePendingWrites) return;

        try
        {
            await _store.DeletePendingWritesAsync(sessionId, checkpointId, cancellationToken);
        }
        catch
        {
            // Swallow errors - cleanup is best-effort
        }
    }

    /// <summary>
    /// Determines if a function result represents a successful execution.
    /// </summary>
    private static bool IsFunctionResultSuccessful(FunctionResultContent result)
    {
        // A result is successful if it doesn't indicate an error
        // This is a simplified heuristic - could be enhanced with error detection
        if (result.Result is string text)
        {
            // Check for common error patterns
            return !text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) &&
                   !text.StartsWith("Exception:", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }
}
