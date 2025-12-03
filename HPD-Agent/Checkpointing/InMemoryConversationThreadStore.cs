using System.Collections.Concurrent;
using System.Text.Json;

namespace HPD.Agent.Checkpointing;

/// <summary>
/// In-memory conversation thread store for development and testing.
/// Data is lost on process restart.
/// Supports both LatestOnly (default) and FullHistory modes.
/// INTERNAL: Framework-level implementation for thread persistence and checkpointing.
/// </summary>
/// <remarks>
/// Thread-safe for concurrent access using ConcurrentDictionary.
/// In production, use a database-backed store (e.g., PostgresConversationThreadStore).
/// </remarks>
public class InMemoryConversationThreadStore : IConversationThreadStore
{
    // LatestOnly mode: single checkpoint per thread (stored as JSON element)
    private readonly ConcurrentDictionary<string, JsonElement> _checkpoints = new();

    // FullHistory mode: all checkpoints with unique IDs
    private readonly ConcurrentDictionary<string, List<CheckpointTuple>> _checkpointHistory = new();

    // Pending writes: key = "{threadId}:{checkpointId}"
    private readonly ConcurrentDictionary<string, List<PendingWrite>> _pendingWrites = new();

    public CheckpointRetentionMode RetentionMode { get; }

    public InMemoryConversationThreadStore(CheckpointRetentionMode retentionMode = CheckpointRetentionMode.LatestOnly)
    {
        RetentionMode = retentionMode;
    }

    public Task<ConversationThread?> LoadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (RetentionMode == CheckpointRetentionMode.LatestOnly)
        {
            // LatestOnly: Simple lookup
            if (_checkpoints.TryGetValue(threadId, out var snapshotJson))
            {
                var snapshot = JsonSerializer.Deserialize(snapshotJson.GetRawText(), HPDJsonContext.Default.ConversationThreadSnapshot);
                if (snapshot != null)
                {
                    var thread = ConversationThread.Deserialize(snapshot, null);
                    return Task.FromResult<ConversationThread?>(thread);
                }
            }
        }
        else
        {
            // FullHistory: Get latest checkpoint from history
            if (_checkpointHistory.TryGetValue(threadId, out var history) && history.Count > 0)
            {
                var latest = history[0]; // Already sorted newest first

                // Create a minimal thread and set execution state
                var thread = new ConversationThread
                {
                    ExecutionState = latest.State
                };

                return Task.FromResult<ConversationThread?>(thread);
            }
        }

        return Task.FromResult<ConversationThread?>(null);
    }

    public Task SaveThreadAsync(
        ConversationThread thread,
        CancellationToken cancellationToken = default)
    {
        if (RetentionMode == CheckpointRetentionMode.LatestOnly)
        {
            // LatestOnly: UPSERT (overwrite) - store as JsonElement
            var snapshot = thread.Serialize(null);
            var snapshotJson = System.Text.Json.JsonSerializer.SerializeToElement(snapshot, HPDJsonContext.Default.ConversationThreadSnapshot);
            _checkpoints[thread.Id] = snapshotJson;
        }
        else
        {
            // FullHistory: INSERT (new checkpoint with unique ID)
            var checkpointId = Guid.NewGuid().ToString(); // Unique ID
            var checkpoint = new CheckpointTuple
            {
                CheckpointId = checkpointId,
                CreatedAt = DateTime.UtcNow,
                State = thread.ExecutionState ?? throw new InvalidOperationException("Cannot checkpoint thread without ExecutionState"),
                Metadata = thread.ExecutionState.Metadata ?? new CheckpointMetadata()
            };

            _checkpointHistory.AddOrUpdate(
                thread.Id,
                _ => new List<CheckpointTuple> { checkpoint },
                (_, existing) =>
                {
                    lock (existing)
                    {
                        // Insert at beginning (newest first)
                        existing.Insert(0, checkpoint);
                    }
                    return existing;
                });
        }

        return Task.CompletedTask;
    }

    public Task<List<string>> ListThreadIdsAsync(
        CancellationToken cancellationToken = default)
    {
        var threadIds = RetentionMode == CheckpointRetentionMode.LatestOnly
            ? _checkpoints.Keys.ToList()
            : _checkpointHistory.Keys.ToList();
        return Task.FromResult(threadIds);
    }

    public Task DeleteThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (RetentionMode == CheckpointRetentionMode.LatestOnly)
        {
            _checkpoints.TryRemove(threadId, out _);
        }
        else
        {
            _checkpointHistory.TryRemove(threadId, out _);
        }

        return Task.CompletedTask;
    }

    // ===== FullHistory-only methods =====

    public Task<ConversationThread?> LoadThreadAtCheckpointAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        if (RetentionMode != CheckpointRetentionMode.FullHistory)
            throw new NotSupportedException($"LoadThreadAtCheckpointAsync requires RetentionMode.FullHistory");

        if (_checkpointHistory.TryGetValue(threadId, out var history))
        {
            CheckpointTuple? checkpoint;
            lock (history)
            {
                checkpoint = history.FirstOrDefault(c => c.CheckpointId == checkpointId);
            }

            if (checkpoint != null)
            {
                // Create a minimal thread and set execution state
                var thread = new ConversationThread
                {
                    ExecutionState = checkpoint.State
                };

                return Task.FromResult<ConversationThread?>(thread);
            }
        }

        return Task.FromResult<ConversationThread?>(null);
    }

    public Task<List<CheckpointTuple>> GetCheckpointHistoryAsync(
        string threadId,
        int? limit = null,
        DateTime? before = null,
        CancellationToken cancellationToken = default)
    {
        if (RetentionMode != CheckpointRetentionMode.FullHistory)
            throw new NotSupportedException($"GetCheckpointHistoryAsync requires RetentionMode.FullHistory");

        if (!_checkpointHistory.TryGetValue(threadId, out var history))
            return Task.FromResult(new List<CheckpointTuple>());

        List<CheckpointTuple> filtered;
        lock (history)
        {
            var query = history.AsEnumerable();

            if (before.HasValue)
                query = query.Where(c => c.CreatedAt < before.Value);

            if (limit.HasValue)
                query = query.Take(limit.Value);

            filtered = query.ToList();
        }

        return Task.FromResult(filtered);
    }

    public Task PruneCheckpointsAsync(
        string threadId,
        int keepLatest = 10,
        CancellationToken cancellationToken = default)
    {
        if (RetentionMode != CheckpointRetentionMode.FullHistory)
            return Task.CompletedTask; // No-op for LatestOnly

        if (_checkpointHistory.TryGetValue(threadId, out var history))
        {
            lock (history)
            {
                if (history.Count > keepLatest)
                {
                    // Remove oldest checkpoints (keep first N which are newest)
                    history.RemoveRange(keepLatest, history.Count - keepLatest);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteOlderThanAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default)
    {
        if (RetentionMode == CheckpointRetentionMode.LatestOnly)
        {
            // LatestOnly: Deserialize and check LastActivity timestamp from snapshot
            var toRemove = new List<string>();
            foreach (var kvp in _checkpoints)
            {
                var snapshot = JsonSerializer.Deserialize(kvp.Value.GetRawText(), HPDJsonContext.Default.ConversationThreadSnapshot);
                if (snapshot != null && snapshot.LastActivity < cutoff)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var threadId in toRemove)
            {
                _checkpoints.TryRemove(threadId, out _);
            }
        }
        else
        {
            // FullHistory: Remove old checkpoints
            foreach (var kvp in _checkpointHistory)
            {
                var history = kvp.Value;
                lock (history)
                {
                    history.RemoveAll(c => c.CreatedAt < cutoff);
                }

                // If no checkpoints remain, remove the thread entry
                if (history.Count == 0)
                {
                    _checkpointHistory.TryRemove(kvp.Key, out _);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteInactiveThreadsAsync(
        TimeSpan inactivityThreshold,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - inactivityThreshold;
        var count = 0;

        if (RetentionMode == CheckpointRetentionMode.LatestOnly)
        {
            var toRemove = new List<string>();
            foreach (var kvp in _checkpoints)
            {
                var snapshot = JsonSerializer.Deserialize(kvp.Value.GetRawText(), HPDJsonContext.Default.ConversationThreadSnapshot);
                if (snapshot != null && snapshot.LastActivity < cutoff)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            count = toRemove.Count;

            if (!dryRun)
            {
                foreach (var threadId in toRemove)
                {
                    _checkpoints.TryRemove(threadId, out _);
                }
            }
        }
        else
        {
            // FullHistory: Check if latest checkpoint is older than threshold
            var threadsToRemove = new List<string>();

            foreach (var kvp in _checkpointHistory)
            {
                var history = kvp.Value;
                DateTime latestActivity;

                lock (history)
                {
                    if (history.Count == 0)
                    {
                        threadsToRemove.Add(kvp.Key);
                        continue;
                    }

                    latestActivity = history[0].CreatedAt; // First is newest
                }

                if (latestActivity < cutoff)
                {
                    threadsToRemove.Add(kvp.Key);
                }
            }

            count = threadsToRemove.Count;

            if (!dryRun)
            {
                foreach (var threadId in threadsToRemove)
                {
                    _checkpointHistory.TryRemove(threadId, out _);
                }
            }
        }

        return Task.FromResult(count);
    }

    // ===== Pending Writes Methods =====

    public Task SavePendingWritesAsync(
        string threadId,
        string checkpointId,
        IEnumerable<PendingWrite> writes,
        CancellationToken cancellationToken = default)
    {
        var key = $"{threadId}:{checkpointId}";
        var writesList = writes.ToList();

        _pendingWrites.AddOrUpdate(
            key,
            _ => writesList,
            (_, existing) =>
            {
                // Append new writes to existing list
                lock (existing)
                {
                    existing.AddRange(writesList);
                }
                return existing;
            });

        return Task.CompletedTask;
    }

    public Task<List<PendingWrite>> LoadPendingWritesAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        var key = $"{threadId}:{checkpointId}";

        if (_pendingWrites.TryGetValue(key, out var writes))
        {
            // Return a copy to prevent external modification
            lock (writes)
            {
                return Task.FromResult(writes.ToList());
            }
        }

        return Task.FromResult(new List<PendingWrite>());
    }

    public Task DeletePendingWritesAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        var key = $"{threadId}:{checkpointId}";
        _pendingWrites.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
