using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Checkpointing.Services;

/// <summary>
/// Service for durable execution (auto-checkpointing + retention + pending writes).
/// Owns checkpoint creation policy and retention enforcement.
/// </summary>
/// <remarks>
/// <para>
/// This service encapsulates all checkpoint-related business logic that was previously
/// scattered in Agent.cs and duplicated across store implementations.
/// </para>
/// <para>
/// <strong>Responsibilities:</strong>
/// <list type="bullet">
/// <item>Deciding WHEN to checkpoint (based on Frequency config)</item>
/// <item>Creating checkpoint data from thread + state</item>
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
    private readonly ICheckpointStore _store;
    private readonly DurableExecutionConfig _config;

    /// <summary>
    /// Creates a new DurableExecutionService.
    /// </summary>
    /// <param name="store">The checkpoint store</param>
    /// <param name="config">Configuration for durable execution</param>
    public DurableExecution(ICheckpointStore store, DurableExecutionConfig config)
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
    /// Save a checkpoint for the given thread and state.
    /// Enforces retention policy after save.
    /// </summary>
    /// <param name="thread">The conversation thread</param>
    /// <param name="state">The agent loop state to checkpoint</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task SaveCheckpointAsync(
        ConversationThread thread,
        AgentLoopState state,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled) return;

        // Update thread's execution state
        thread.ExecutionState = state;

        // Save through the store (which handles checkpoint creation)
        await _store.SaveThreadAsync(thread, cancellationToken);

        // Enforce retention policy AFTER save
        await EnforceRetentionAsync(thread.Id, cancellationToken);

        // Cleanup pending writes after successful checkpoint
        if (_config.EnablePendingWrites && state.ETag != null)
        {
            await DeletePendingWritesAsync(thread.Id, state.ETag, cancellationToken);
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
    /// Resume from the latest checkpoint (for crash recovery).
    /// </summary>
    /// <param name="threadId">Thread to resume</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The thread at the latest checkpoint, or null if no checkpoint exists</returns>
    public async Task<ConversationThread?> ResumeFromLatestAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled) return null;

        return await _store.LoadThreadAsync(threadId, cancellationToken);
    }

    /// <summary>
    /// Enforce the retention policy for a thread.
    /// Called automatically after SaveCheckpointAsync.
    /// </summary>
    private async Task EnforceRetentionAsync(string threadId, CancellationToken cancellationToken)
    {
        // Use polymorphic dispatch to apply the retention policy
        await _config.Retention.ApplyAsync(_store, threadId, cancellationToken);
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
    /// <param name="threadId">Thread ID</param>
    /// <param name="checkpointId">Checkpoint ID (ETag)</param>
    /// <param name="toolResultMessage">Message containing function results</param>
    /// <param name="iteration">Current iteration number</param>
    public void SavePendingWriteFireAndForget(
        string threadId,
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
            ThreadId = threadId
        }).ToList();

        // Save in background (fire-and-forget)
        _ = Task.Run(async () =>
        {
            try
            {
                await _store.SavePendingWritesAsync(threadId, checkpointId, pendingWrites);
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
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        if (!_config.EnablePendingWrites)
            return ImmutableList<PendingWrite>.Empty;

        try
        {
            var writes = await _store.LoadPendingWritesAsync(threadId, checkpointId, cancellationToken);
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
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        if (!_config.EnablePendingWrites) return;

        try
        {
            await _store.DeletePendingWritesAsync(threadId, checkpointId, cancellationToken);
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
