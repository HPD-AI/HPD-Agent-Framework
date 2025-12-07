using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Checkpointing;

/// <summary>
/// In-memory checkpoint store for development and testing.
/// Stores full checkpoint history per thread. Data is lost on process restart.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe for concurrent access using ConcurrentDictionary.
/// In production, use a database-backed store.
/// </para>
/// <para>
/// For simple single-state storage without history, use <see cref="InMemoryThreadStore"/>.
/// </para>
/// </remarks>
public class InMemoryConversationThreadStore : ICheckpointStore
{
    // All checkpoints with unique IDs, per thread
    private readonly ConcurrentDictionary<string, List<CheckpointTuple>> _checkpointHistory = new();

    // Pending writes: key = "{threadId}:{checkpointId}"
    private readonly ConcurrentDictionary<string, List<PendingWrite>> _pendingWrites = new();

    /// <summary>
    /// Creates a new InMemoryConversationThreadStore with full checkpoint history.
    /// </summary>
    public InMemoryConversationThreadStore()
    {
    }

    public Task<ConversationThread?> LoadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (_checkpointHistory.TryGetValue(threadId, out var history) && history.Count > 0)
        {
            var latest = history[0]; // Already sorted newest first

            var thread = new ConversationThread
            {
                ExecutionState = latest.State
            };

            // Populate messages from state
            if (latest.State?.CurrentMessages != null)
            {
                thread.AddMessages(latest.State.CurrentMessages);
            }

            return Task.FromResult<ConversationThread?>(thread);
        }

        return Task.FromResult<ConversationThread?>(null);
    }

    public Task SaveThreadAsync(
        ConversationThread thread,
        CancellationToken cancellationToken = default)
    {
        var checkpointId = Guid.NewGuid().ToString();

        _checkpointHistory.AddOrUpdate(
            thread.Id,
            _ => CreateInitialHistory(thread, checkpointId),
            (_, existing) => AddCheckpoint(existing, thread, checkpointId));

        return Task.CompletedTask;
    }

    private List<CheckpointTuple> CreateInitialHistory(ConversationThread thread, string checkpointId)
    {
        var list = new List<CheckpointTuple>();

        // Create root checkpoint first (messageIndex=-1)
        var rootCheckpointId = Guid.NewGuid().ToString();
        var emptyState = AgentLoopState.Initial(
            messages: Array.Empty<Microsoft.Extensions.AI.ChatMessage>(),
            runId: "root",
            conversationId: thread.Id,
            agentName: "root");

        var rootCheckpoint = new CheckpointTuple
        {
            CheckpointId = rootCheckpointId,
            CreatedAt = DateTime.UtcNow,
            State = emptyState,
            Metadata = new CheckpointMetadata
            {
                Source = CheckpointSource.Root,
                Step = -1,
                ParentCheckpointId = null
            },
            ParentCheckpointId = null,
            MessageIndex = -1
        };
        list.Add(rootCheckpoint);

        // Add the actual checkpoint
        var executionState = thread.ExecutionState
            ?? throw new InvalidOperationException("Cannot checkpoint thread without ExecutionState");

        var metadata = CloneMetadata(executionState);
        var checkpoint = new CheckpointTuple
        {
            CheckpointId = checkpointId,
            CreatedAt = DateTime.UtcNow,
            State = executionState,
            Metadata = metadata,
            ParentCheckpointId = rootCheckpointId,
            MessageIndex = thread.MessageCount
        };
        list.Insert(0, checkpoint); // Newest first

        return list;
    }

    private List<CheckpointTuple> AddCheckpoint(List<CheckpointTuple> existing, ConversationThread thread, string checkpointId)
    {
        lock (existing)
        {
            var executionState = thread.ExecutionState
                ?? throw new InvalidOperationException("Cannot checkpoint thread without ExecutionState");

            var metadata = CloneMetadata(executionState);
            var checkpoint = new CheckpointTuple
            {
                CheckpointId = checkpointId,
                CreatedAt = DateTime.UtcNow,
                State = executionState,
                Metadata = metadata,
                MessageIndex = thread.MessageCount
            };
            existing.Insert(0, checkpoint); // Newest first
        }
        return existing;
    }

    private static CheckpointMetadata CloneMetadata(AgentLoopState state)
    {
        var source = state.Metadata ?? new CheckpointMetadata();
        return new CheckpointMetadata
        {
            Source = source.Source,
            Step = state.Iteration,
            ParentCheckpointId = source.ParentCheckpointId,
            ParentThreadId = source.ParentThreadId,
            MessageIndex = source.MessageIndex
        };
    }

    public Task<List<string>> ListThreadIdsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_checkpointHistory.Keys.ToList());
    }

    public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        _checkpointHistory.TryRemove(threadId, out _);
        return Task.CompletedTask;
    }

    // ===== CHECKPOINT ACCESS METHODS =====

    public Task<ConversationThread?> LoadThreadAtCheckpointAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        if (_checkpointHistory.TryGetValue(threadId, out var history))
        {
            CheckpointTuple? checkpoint;
            lock (history)
            {
                checkpoint = history.FirstOrDefault(c => c.CheckpointId == checkpointId);
            }

            if (checkpoint != null)
            {
                var thread = new ConversationThread
                {
                    ExecutionState = checkpoint.State
                };

                if (checkpoint.State?.CurrentMessages != null)
                {
                    thread.AddMessages(checkpoint.State.CurrentMessages);
                }

                return Task.FromResult<ConversationThread?>(thread);
            }
        }

        return Task.FromResult<ConversationThread?>(null);
    }

    public Task SaveThreadAtCheckpointAsync(
        ConversationThread thread,
        string checkpointId,
        CheckpointMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = new CheckpointTuple
        {
            CheckpointId = checkpointId,
            CreatedAt = DateTime.UtcNow,
            State = thread.ExecutionState
                ?? throw new InvalidOperationException("Cannot checkpoint thread without ExecutionState"),
            Metadata = metadata,
            ParentCheckpointId = metadata.ParentCheckpointId,
            MessageIndex = metadata.MessageIndex
        };

        _checkpointHistory.AddOrUpdate(
            thread.Id,
            _ => new List<CheckpointTuple> { checkpoint },
            (_, existing) =>
            {
                lock (existing) { existing.Insert(0, checkpoint); }
                return existing;
            });

        return Task.CompletedTask;
    }

    public Task<List<CheckpointManifestEntry>> GetCheckpointManifestAsync(
        string threadId,
        int? limit = null,
        DateTime? before = null,
        CancellationToken cancellationToken = default)
    {
        var allEntries = new List<CheckpointManifestEntry>();

        // Add full checkpoints
        if (_checkpointHistory.TryGetValue(threadId, out var history))
        {
            lock (history)
            {
                foreach (var c in history)
                {
                    allEntries.Add(new CheckpointManifestEntry
                    {
                        CheckpointId = c.CheckpointId,
                        CreatedAt = c.CreatedAt,
                        Step = c.Metadata?.Step ?? 0,
                        Source = c.Metadata?.Source ?? CheckpointSource.Loop,
                        ParentCheckpointId = c.ParentCheckpointId,
                        MessageIndex = c.MessageIndex,
                        IsSnapshot = false
                    });
                }
            }
        }

        // Sort by creation time (newest first)
        var query = allEntries.OrderByDescending(e => e.CreatedAt).AsEnumerable();

        if (before.HasValue)
            query = query.Where(c => c.CreatedAt < before.Value);

        if (limit.HasValue)
            query = query.Take(limit.Value);

        return Task.FromResult(query.ToList());
    }

    public Task UpdateCheckpointManifestEntryAsync(
        string threadId,
        string checkpointId,
        Action<CheckpointManifestEntry> update,
        CancellationToken cancellationToken = default)
    {
        if (!_checkpointHistory.TryGetValue(threadId, out var history))
            return Task.CompletedTask;

        lock (history)
        {
            var checkpoint = history.FirstOrDefault(c => c.CheckpointId == checkpointId);
            if (checkpoint == null)
                return Task.CompletedTask;

            var tempEntry = new CheckpointManifestEntry
            {
                CheckpointId = checkpoint.CheckpointId,
                CreatedAt = checkpoint.CreatedAt,
                Step = checkpoint.Metadata?.Step ?? 0,
                Source = checkpoint.Metadata?.Source ?? CheckpointSource.Loop,
                ParentCheckpointId = checkpoint.ParentCheckpointId,
                MessageIndex = checkpoint.MessageIndex
            };

            update(tempEntry);
        }

        return Task.CompletedTask;
    }

    // ===== CLEANUP METHODS =====

    public Task DeleteCheckpointsAsync(
        string threadId,
        IEnumerable<string> checkpointIds,
        CancellationToken cancellationToken = default)
    {
        if (!_checkpointHistory.TryGetValue(threadId, out var history))
            return Task.CompletedTask;

        var idsToDelete = checkpointIds.ToHashSet();

        lock (history)
        {
            history.RemoveAll(c => idsToDelete.Contains(c.CheckpointId));
        }

        return Task.CompletedTask;
    }

    public Task PruneCheckpointsAsync(
        string threadId,
        int keepLatest = 10,
        CancellationToken cancellationToken = default)
    {
        if (_checkpointHistory.TryGetValue(threadId, out var history))
        {
            lock (history)
            {
                if (history.Count > keepLatest)
                {
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
        foreach (var kvp in _checkpointHistory)
        {
            var history = kvp.Value;
            lock (history)
            {
                history.RemoveAll(c => c.CreatedAt < cutoff);
            }

            if (history.Count == 0)
            {
                _checkpointHistory.TryRemove(kvp.Key, out _);
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
                latestActivity = history[0].CreatedAt;
            }

            if (latestActivity < cutoff)
            {
                threadsToRemove.Add(kvp.Key);
            }
        }

        if (!dryRun)
        {
            foreach (var threadId in threadsToRemove)
            {
                _checkpointHistory.TryRemove(threadId, out _);
            }
        }

        return Task.FromResult(threadsToRemove.Count);
    }

    // ===== PENDING WRITES =====

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
                lock (existing) { existing.AddRange(writesList); }
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

    // Snapshot methods removed - branching is now an application-level concern
}
