using System.Collections.Concurrent;
using System.Text.Json;

namespace HPD.Agent.Checkpointing;

/// <summary>
/// Simple in-memory thread store for development and testing.
/// Stores only the latest state per thread (no checkpoint history).
/// Data is lost on process restart.
/// </summary>
/// <remarks>
/// <para>
/// Use this for simple scenarios where you only need crash recovery,
/// not full checkpoint history, branching, or time-travel debugging.
/// </para>
/// <para>
/// For full checkpoint capabilities, use <see cref="InMemoryConversationThreadStore"/>.
/// </para>
/// </remarks>
public class InMemoryThreadStore : IThreadStore
{
    private readonly ConcurrentDictionary<string, ExecutionCheckpoint> _threads = new();

    public Task<ConversationThread?> LoadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (_threads.TryGetValue(threadId, out var checkpoint))
        {
            var thread = ConversationThread.FromExecutionCheckpoint(checkpoint);
            return Task.FromResult<ConversationThread?>(thread);
        }

        return Task.FromResult<ConversationThread?>(null);
    }

    public Task SaveThreadAsync(
        ConversationThread thread,
        CancellationToken cancellationToken = default)
    {
        // Only save if ExecutionState is set (don't save empty threads)
        if (thread.ExecutionState != null)
        {
            var checkpoint = thread.ToExecutionCheckpoint();
            _threads[thread.Id] = checkpoint;
        }
        return Task.CompletedTask;
    }

    public Task<List<string>> ListThreadIdsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_threads.Keys.ToList());
    }

    public Task DeleteThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        _threads.TryRemove(threadId, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Delete threads that have been inactive for longer than the threshold.
    /// </summary>
    public Task<int> DeleteInactiveThreadsAsync(
        TimeSpan inactivityThreshold,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - inactivityThreshold;
        var toRemove = new List<string>();

        foreach (var kvp in _threads)
        {
            if (kvp.Value.LastActivity < cutoff)
            {
                toRemove.Add(kvp.Key);
            }
        }

        if (!dryRun)
        {
            foreach (var threadId in toRemove)
            {
                _threads.TryRemove(threadId, out _);
            }
        }

        return Task.FromResult(toRemove.Count);
    }
}
