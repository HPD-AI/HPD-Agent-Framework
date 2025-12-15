using System.Collections.Concurrent;


namespace HPD.Agent.Session;

/// <summary>
/// In-memory session store for development and testing.
/// Data is lost on process restart.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe for concurrent access using ConcurrentDictionary.
/// In production, use a database-backed store like JsonSessionStore or a custom implementation.
/// </para>
/// <para>
/// Supports two distinct persistence concerns:
/// <list type="bullet">
/// <item><strong>Session Persistence:</strong> Snapshots stored in _sessions (conversation history)</item>
/// <item><strong>Execution Checkpoints:</strong> Stored in _checkpoints (crash recovery)</item>
/// </list>
/// </para>
/// </remarks>
public class InMemorySessionStore : ISessionStore
{
    // Session snapshots (conversation history) - keyed by sessionId
    private readonly ConcurrentDictionary<string, SessionSnapshot> _sessions = new();

    // Execution checkpoints (crash recovery) - keyed by sessionId, value is list ordered newest-first
    private readonly ConcurrentDictionary<string, List<ExecutionCheckpoint>> _checkpoints = new();

    // Checkpoint metadata - keyed by "{sessionId}:{checkpointId}"
    private readonly ConcurrentDictionary<string, CheckpointMetadata> _checkpointMetadata = new();

    // Pending writes: key = "{sessionId}:{checkpointId}"
    private readonly ConcurrentDictionary<string, List<PendingWrite>> _pendingWrites = new();

    private readonly bool _enableHistory;
    private readonly bool _enablePendingWrites;

    /// <inheritdoc />
    public bool SupportsHistory => _enableHistory;

    /// <inheritdoc />
    public bool SupportsPendingWrites => _enablePendingWrites;

    /// <summary>
    /// Creates a new InMemorySessionStore.
    /// </summary>
    /// <param name="enableHistory">Whether to store full checkpoint history (default: true)</param>
    /// <param name="enablePendingWrites">Whether to support pending writes for recovery (default: true)</param>
    public InMemorySessionStore(bool enableHistory = true, bool enablePendingWrites = true)
    {
        _enableHistory = enableHistory;
        _enablePendingWrites = enablePendingWrites;
    }

    // ═══════════════════════════════════════════════════════════════════
    // SESSION PERSISTENCE (Snapshots)
    // ═══════════════════════════════════════════════════════════════════

    public Task<AgentSession?> LoadSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(sessionId, out var snapshot))
        {
            return Task.FromResult<AgentSession?>(AgentSession.FromSnapshot(snapshot));
        }

        return Task.FromResult<AgentSession?>(null);
    }

    public Task SaveSessionAsync(
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        var snapshot = session.ToSnapshot();
        _sessions[session.Id] = snapshot;
        return Task.CompletedTask;
    }

    public Task<List<string>> ListSessionIdsAsync(CancellationToken cancellationToken = default)
    {
        // Combine session IDs from both snapshots and checkpoints
        var allIds = new HashSet<string>(_sessions.Keys);
        foreach (var checkpointSessionId in _checkpoints.Keys)
        {
            allIds.Add(checkpointSessionId);
        }
        return Task.FromResult(allIds.ToList());
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryRemove(sessionId, out _);
        _checkpoints.TryRemove(sessionId, out _);

        // Clean up metadata for this session
        var metadataKeysToRemove = _checkpointMetadata.Keys
            .Where(k => k.StartsWith($"{sessionId}:"))
            .ToList();
        foreach (var key in metadataKeysToRemove)
        {
            _checkpointMetadata.TryRemove(key, out _);
        }

        // Clean up pending writes for this session
        var pendingWriteKeysToRemove = _pendingWrites.Keys
            .Where(k => k.StartsWith($"{sessionId}:"))
            .ToList();
        foreach (var key in pendingWriteKeysToRemove)
        {
            _pendingWrites.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // EXECUTION CHECKPOINTS (Crash Recovery)
    // ═══════════════════════════════════════════════════════════════════

    public Task<ExecutionCheckpoint?> LoadCheckpointAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (_checkpoints.TryGetValue(sessionId, out var checkpoints) && checkpoints.Count > 0)
        {
            lock (checkpoints)
            {
                // Return the latest checkpoint (newest first)
                return Task.FromResult<ExecutionCheckpoint?>(checkpoints[0]);
            }
        }

        return Task.FromResult<ExecutionCheckpoint?>(null);
    }

    public Task SaveCheckpointAsync(
        ExecutionCheckpoint checkpoint,
        CheckpointMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var sessionId = checkpoint.SessionId;
        var checkpointId = checkpoint.ExecutionCheckpointId;

        // Store metadata
        _checkpointMetadata[$"{sessionId}:{checkpointId}"] = metadata;

        if (_enableHistory)
        {
            _checkpoints.AddOrUpdate(
                sessionId,
                _ => new List<ExecutionCheckpoint> { checkpoint },
                (_, existing) =>
                {
                    lock (existing)
                    {
                        existing.Insert(0, checkpoint); // Newest first
                    }
                    return existing;
                });
        }
        else
        {
            // Without history, just keep the latest
            _checkpoints[sessionId] = new List<ExecutionCheckpoint> { checkpoint };
        }

        return Task.CompletedTask;
    }

    public Task DeleteAllCheckpointsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _checkpoints.TryRemove(sessionId, out _);

        // Clean up metadata for this session
        var metadataKeysToRemove = _checkpointMetadata.Keys
            .Where(k => k.StartsWith($"{sessionId}:"))
            .ToList();
        foreach (var key in metadataKeysToRemove)
        {
            _checkpointMetadata.TryRemove(key, out _);
        }

        // Clean up pending writes for this session
        var pendingWriteKeysToRemove = _pendingWrites.Keys
            .Where(k => k.StartsWith($"{sessionId}:"))
            .ToList();
        foreach (var key in pendingWriteKeysToRemove)
        {
            _pendingWrites.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // CHECKPOINT HISTORY
    // ═══════════════════════════════════════════════════════════════════

    public Task<ExecutionCheckpoint?> LoadCheckpointAtAsync(
        string sessionId,
        string executionCheckpointId,
        CancellationToken cancellationToken = default)
    {
        if (!_enableHistory)
            return Task.FromResult<ExecutionCheckpoint?>(null);

        if (_checkpoints.TryGetValue(sessionId, out var checkpoints))
        {
            lock (checkpoints)
            {
                var checkpoint = checkpoints.FirstOrDefault(c => c.ExecutionCheckpointId == executionCheckpointId);
                return Task.FromResult(checkpoint);
            }
        }

        return Task.FromResult<ExecutionCheckpoint?>(null);
    }

    public Task<List<CheckpointManifestEntry>> GetCheckpointManifestAsync(
        string sessionId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (!_enableHistory)
            return Task.FromResult(new List<CheckpointManifestEntry>());

        var entries = new List<CheckpointManifestEntry>();

        if (_checkpoints.TryGetValue(sessionId, out var checkpoints))
        {
            lock (checkpoints)
            {
                foreach (var checkpoint in checkpoints)
                {
                    var metadataKey = $"{sessionId}:{checkpoint.ExecutionCheckpointId}";
                    _checkpointMetadata.TryGetValue(metadataKey, out var metadata);

                    entries.Add(new CheckpointManifestEntry
                    {
                        ExecutionCheckpointId = checkpoint.ExecutionCheckpointId,
                        CreatedAt = checkpoint.CreatedAt,
                        Step = metadata?.Step ?? checkpoint.ExecutionState.Iteration,
                        Source = metadata?.Source ?? CheckpointSource.Loop,
                        ParentExecutionCheckpointId = metadata?.ParentCheckpointId,
                        MessageIndex = metadata?.MessageIndex ?? checkpoint.ExecutionState.CurrentMessages.Count,
                        IsSnapshot = false // ExecutionCheckpoints are never snapshots
                    });
                }
            }
        }

        // Sort by creation time (newest first)
        var query = entries.OrderByDescending(e => e.CreatedAt).AsEnumerable();

        if (limit.HasValue)
            query = query.Take(limit.Value);

        return Task.FromResult(query.ToList());
    }

    // ═══════════════════════════════════════════════════════════════════
    // PENDING WRITES
    // ═══════════════════════════════════════════════════════════════════

    public Task SavePendingWritesAsync(
        string sessionId,
        string executionCheckpointId,
        IEnumerable<PendingWrite> writes,
        CancellationToken cancellationToken = default)
    {
        if (!_enablePendingWrites)
            return Task.CompletedTask;

        var key = $"{sessionId}:{executionCheckpointId}";
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
        string sessionId,
        string executionCheckpointId,
        CancellationToken cancellationToken = default)
    {
        if (!_enablePendingWrites)
            return Task.FromResult(new List<PendingWrite>());

        var key = $"{sessionId}:{executionCheckpointId}";

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
        string sessionId,
        string executionCheckpointId,
        CancellationToken cancellationToken = default)
    {
        if (!_enablePendingWrites)
            return Task.CompletedTask;

        var key = $"{sessionId}:{executionCheckpointId}";
        _pendingWrites.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // CLEANUP METHODS
    // ═══════════════════════════════════════════════════════════════════

    public Task PruneCheckpointsAsync(
        string sessionId,
        int keepLatest = 10,
        CancellationToken cancellationToken = default)
    {
        if (!_enableHistory)
            return Task.CompletedTask;

        if (_checkpoints.TryGetValue(sessionId, out var checkpoints))
        {
            lock (checkpoints)
            {
                if (checkpoints.Count > keepLatest)
                {
                    // Get IDs of checkpoints to remove
                    var toRemove = checkpoints.Skip(keepLatest).ToList();
                    foreach (var checkpoint in toRemove)
                    {
                        var metadataKey = $"{sessionId}:{checkpoint.ExecutionCheckpointId}";
                        _checkpointMetadata.TryRemove(metadataKey, out _);

                        var pendingKey = $"{sessionId}:{checkpoint.ExecutionCheckpointId}";
                        _pendingWrites.TryRemove(pendingKey, out _);
                    }

                    checkpoints.RemoveRange(keepLatest, checkpoints.Count - keepLatest);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteOlderThanAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default)
    {
        // Clean up old checkpoints
        foreach (var kvp in _checkpoints)
        {
            var sessionId = kvp.Key;
            var checkpoints = kvp.Value;
            lock (checkpoints)
            {
                var toRemove = checkpoints.Where(c => c.CreatedAt < cutoff).ToList();
                foreach (var checkpoint in toRemove)
                {
                    checkpoints.Remove(checkpoint);
                    _checkpointMetadata.TryRemove($"{sessionId}:{checkpoint.ExecutionCheckpointId}", out _);
                    _pendingWrites.TryRemove($"{sessionId}:{checkpoint.ExecutionCheckpointId}", out _);
                }
            }

            if (checkpoints.Count == 0)
            {
                _checkpoints.TryRemove(kvp.Key, out _);
            }
        }

        // Clean up old sessions
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.LastActivity < cutoff)
            {
                _sessions.TryRemove(kvp.Key, out _);
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteInactiveSessionsAsync(
        TimeSpan inactivityThreshold,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - inactivityThreshold;
        var sessionsToRemove = new List<string>();

        // Check sessions
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.LastActivity < cutoff)
            {
                sessionsToRemove.Add(kvp.Key);
            }
        }

        // Check checkpoints (session might only have checkpoints, no snapshot)
        foreach (var kvp in _checkpoints)
        {
            var sessionId = kvp.Key;
            if (sessionsToRemove.Contains(sessionId))
                continue;

            var checkpoints = kvp.Value;
            DateTime latestActivity;

            lock (checkpoints)
            {
                if (checkpoints.Count == 0)
                {
                    sessionsToRemove.Add(sessionId);
                    continue;
                }
                latestActivity = checkpoints[0].CreatedAt;
            }

            if (latestActivity < cutoff)
            {
                sessionsToRemove.Add(sessionId);
            }
        }

        if (!dryRun)
        {
            foreach (var sessionId in sessionsToRemove)
            {
                _sessions.TryRemove(sessionId, out _);
                _checkpoints.TryRemove(sessionId, out _);

                // Clean up metadata and pending writes
                var keysToRemove = _checkpointMetadata.Keys
                    .Where(k => k.StartsWith($"{sessionId}:"))
                    .ToList();
                foreach (var key in keysToRemove)
                {
                    _checkpointMetadata.TryRemove(key, out _);
                }

                var pendingKeysToRemove = _pendingWrites.Keys
                    .Where(k => k.StartsWith($"{sessionId}:"))
                    .ToList();
                foreach (var key in pendingKeysToRemove)
                {
                    _pendingWrites.TryRemove(key, out _);
                }
            }
        }

        return Task.FromResult(sessionsToRemove.Count);
    }

    public Task DeleteCheckpointsAsync(
        string sessionId,
        IEnumerable<string> checkpointIds,
        CancellationToken cancellationToken = default)
    {
        if (!_enableHistory)
            return Task.CompletedTask;

        if (!_checkpoints.TryGetValue(sessionId, out var checkpoints))
            return Task.CompletedTask;

        var idsToDelete = checkpointIds.ToHashSet();

        lock (checkpoints)
        {
            var toRemove = checkpoints.Where(c => idsToDelete.Contains(c.ExecutionCheckpointId)).ToList();
            foreach (var checkpoint in toRemove)
            {
                checkpoints.Remove(checkpoint);
                _checkpointMetadata.TryRemove($"{sessionId}:{checkpoint.ExecutionCheckpointId}", out _);
                _pendingWrites.TryRemove($"{sessionId}:{checkpoint.ExecutionCheckpointId}", out _);
            }
        }

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // LEGACY METHODS (Deprecated)
    // ═══════════════════════════════════════════════════════════════════

    [Obsolete("Use LoadCheckpointAtAsync for execution checkpoints")]
    public async Task<AgentSession?> LoadSessionAtCheckpointAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await LoadCheckpointAtAsync(sessionId, checkpointId, cancellationToken);
        if (checkpoint == null)
            return null;

        return AgentSession.FromExecutionCheckpoint(checkpoint);
    }

    [Obsolete("Use SaveCheckpointAsync for execution checkpoints")]
    public Task SaveSessionAtCheckpointAsync(
        AgentSession session,
        string checkpointId,
        CheckpointMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (session.ExecutionState == null)
            throw new InvalidOperationException("Cannot save checkpoint without ExecutionState");

        var checkpoint = session.ToExecutionCheckpoint(checkpointId);
        return SaveCheckpointAsync(checkpoint, metadata, cancellationToken);
    }

    [Obsolete("Use GetCheckpointManifestAsync for reading checkpoint history")]
    public Task UpdateCheckpointManifestEntryAsync(
        string sessionId,
        string checkpointId,
        Action<CheckpointManifestEntry> update,
        CancellationToken cancellationToken = default)
    {
        // This method was used to update metadata - now we just return without doing anything
        // since the new architecture doesn't support in-place metadata updates
        return Task.CompletedTask;
    }
}
