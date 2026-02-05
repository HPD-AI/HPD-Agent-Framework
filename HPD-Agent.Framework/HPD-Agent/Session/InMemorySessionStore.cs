using System.Collections.Concurrent;


namespace HPD.Agent;

/// <summary>
/// In-memory session store for development and testing.
/// Data is lost on process restart.
/// </summary>
/// <remarks>
/// Thread-safe for concurrent access using ConcurrentDictionary.
/// In production, use a database-backed store like JsonSessionStore or a custom implementation.
/// </remarks>
public class InMemorySessionStore : ISessionStore
{
    // Session snapshots (conversation history) - keyed by sessionId
    private readonly ConcurrentDictionary<string, SessionSnapshot> _sessions = new();

    // Uncommitted turns (crash recovery) - keyed by sessionId, at most one per session
    private readonly ConcurrentDictionary<string, UncommittedTurn> _uncommittedTurns = new();

    private readonly InMemoryAssetStore _assetStore;

    /// <summary>
    /// Creates a new InMemorySessionStore.
    /// </summary>
    public InMemorySessionStore()
    {
        _assetStore = new InMemoryAssetStore();
    }

    // Backward-compat constructor — ignores old parameters
    public InMemorySessionStore(bool enableHistory = true, bool enablePendingWrites = true) : this() { }

    /// <inheritdoc />
    public IAssetStore? GetAssetStore(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _assetStore;
    }

    // ═══════════════════════════════════════════════════════════════════
    // SESSION
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
        return Task.FromResult(_sessions.Keys.ToList());
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryRemove(sessionId, out _);
        _uncommittedTurns.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // UNCOMMITTED TURN (Crash Recovery)
    // ═══════════════════════════════════════════════════════════════════

    public Task<UncommittedTurn?> LoadUncommittedTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _uncommittedTurns.TryGetValue(sessionId, out var turn);
        return Task.FromResult(turn);
    }

    public Task SaveUncommittedTurnAsync(
        UncommittedTurn turn,
        CancellationToken cancellationToken = default)
    {
        _uncommittedTurns[turn.SessionId] = turn;
        return Task.CompletedTask;
    }

    public Task DeleteUncommittedTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _uncommittedTurns.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // CLEANUP
    // ═══════════════════════════════════════════════════════════════════

    public Task<int> DeleteInactiveSessionsAsync(
        TimeSpan inactivityThreshold,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - inactivityThreshold;
        var sessionsToRemove = new List<string>();

        foreach (var kvp in _sessions)
        {
            if (kvp.Value.LastActivity < cutoff)
            {
                sessionsToRemove.Add(kvp.Key);
            }
        }

        if (!dryRun)
        {
            foreach (var sessionId in sessionsToRemove)
            {
                _sessions.TryRemove(sessionId, out _);
                _uncommittedTurns.TryRemove(sessionId, out _);
            }
        }

        return Task.FromResult(sessionsToRemove.Count);
    }
}
