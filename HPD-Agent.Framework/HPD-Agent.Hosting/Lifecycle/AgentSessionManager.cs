using System.Collections.Concurrent;
using HPD.Agent;

namespace HPD.Agent.Hosting.Lifecycle;

/// <summary>
/// Abstract base class for managing Agent lifecycle per session.
/// Platform-specific implementations provide agent building logic.
/// </summary>
/// <remarks>
/// This class handles:
/// - Agent caching (one agent per session)
/// - Async-safe per-session locking (prevents duplicate builds)
/// - Stream concurrency control (one stream per branch)
/// - Idle agent eviction (configurable timeout)
///
/// Platform implementations only need to implement:
/// - BuildAgentAsync() - how to create an agent for a session
/// - GetIdleTimeout() - how long before idle agents are evicted
/// </remarks>
public abstract class AgentSessionManager : IDisposable
{
    private readonly ISessionStore _store;
    private readonly ConcurrentDictionary<string, AgentEntry> _agents = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buildLocks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _streamLocks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly Timer _evictionTimer;
    private bool _disposed;

    protected AgentSessionManager(ISessionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _evictionTimer = new Timer(EvictIdleAgents, null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// The session store for this manager.
    /// </summary>
    public ISessionStore Store => _store;

    /// <summary>
    /// Create a new session and its default "main" branch directly in the store,
    /// without requiring a configured AI provider. Sessions are provider-agnostic
    /// containers; the agent/provider is only needed during streaming.
    /// </summary>
    public async Task<(Session session, Branch branch)> CreateSessionAsync(
        string? sessionId = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        var id = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString() : sessionId;
        var session = new Session(id);
        var branch = session.CreateBranch("main");
        session.Store = _store;

        if (metadata != null)
        {
            foreach (var kvp in metadata)
                session.AddMetadata(kvp.Key, kvp.Value);
        }

        await _store.SaveSessionAsync(session, ct);
        await _store.SaveBranchAsync(id, branch, ct);

        return (session, branch);
    }

    /// <summary>
    /// Get or create an Agent for the given session.
    /// Uses async-safe per-session locking to prevent duplicate builds.
    /// </summary>
    public async Task<Agent> GetOrCreateAgentAsync(
        string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        // Fast path: agent already exists
        if (_agents.TryGetValue(sessionId, out var entry))
        {
            entry.LastAccessed = DateTime.UtcNow;
            return entry.Agent;
        }

        // Slow path: build with per-session lock (async-safe, no sync-over-async)
        var buildLock = _buildLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await buildLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_agents.TryGetValue(sessionId, out entry))
            {
                entry.LastAccessed = DateTime.UtcNow;
                return entry.Agent;
            }

            var agent = await BuildAgentAsync(sessionId, ct);
            _agents[sessionId] = new AgentEntry(agent);
            return agent;
        }
        finally
        {
            buildLock.Release();
        }
    }

    /// <summary>
    /// Get a running agent for middleware responses. Returns null if not found or not actively running.
    /// An agent is considered "running" only if it's currently streaming (actively executing RunAsync).
    /// </summary>
    public Agent? GetRunningAgent(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _agents.TryGetValue(sessionId, out var entry) && entry.IsStreaming ? entry.Agent : null;
    }

    /// <summary>
    /// Remove an agent from the cache (e.g., when session is deleted).
    /// Also cleans up all stream lock semaphores for the session.
    /// </summary>
    public void RemoveAgent(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _agents.TryRemove(sessionId, out _);
        _buildLocks.TryRemove(sessionId, out _);
        _sessionLocks.TryRemove(sessionId, out _);

        var prefix = $"{sessionId}:";
        var keysToRemove = _streamLocks.Keys.Where(k => k.StartsWith(prefix)).ToList();
        foreach (var key in keysToRemove)
            if (_streamLocks.TryRemove(key, out var sem))
                sem.Dispose();
    }

    /// <summary>
    /// Remove and dispose the stream lock for a single branch (called after branch delete).
    /// Must be called AFTER ReleaseStreamLock, never before.
    /// </summary>
    public void RemoveBranchStreamLock(string sessionId, string branchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);

        var key = $"{sessionId}:{branchId}";
        if (_streamLocks.TryRemove(key, out var sem))
            sem.Dispose();
    }

    /// <summary>
    /// Try to acquire the stream lock for a branch. Returns false if already streaming.
    /// </summary>
    /// <remarks>
    /// Prevents concurrent streams on the same branch, which would cause race conditions
    /// on shared Session/Branch state. Used by streaming endpoints to return 409 Conflict.
    /// </remarks>
    public bool TryAcquireStreamLock(string sessionId, string branchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);

        var key = $"{sessionId}:{branchId}";
        var semaphore = _streamLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        return semaphore.Wait(0);
    }

    /// <summary>
    /// Release the stream lock for a branch.
    /// </summary>
    public void ReleaseStreamLock(string sessionId, string branchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);

        var key = $"{sessionId}:{branchId}";
        if (_streamLocks.TryGetValue(key, out var semaphore))
        {
            try { semaphore.Release(); } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>
    /// Mark an agent as actively streaming (prevents eviction).
    /// </summary>
    public void SetStreaming(string sessionId, bool isStreaming)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (_agents.TryGetValue(sessionId, out var entry))
        {
            entry.IsStreaming = isStreaming;
            entry.LastAccessed = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Execute an action with exclusive session-level lock.
    /// Used for safe concurrent metadata updates to avoid race conditions.
    /// </summary>
    public async Task<T> WithSessionLockAsync<T>(
        string sessionId,
        Func<Task<T>> action,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(action);

        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Execute a void action with exclusive session-level lock.
    /// </summary>
    public async Task WithSessionLockAsync(
        string sessionId,
        Func<Task> action,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(action);

        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Whether recursive branch deletion is permitted on this manager.
    /// Platform implementations override this to read from their options.
    /// Default: false (safe — prevents accidental subtree deletion).
    /// </summary>
    public virtual bool AllowRecursiveBranchDelete => false;

    /// <summary>
    /// Platform-specific agent building logic.
    /// Called when an agent needs to be created for a session.
    /// </summary>
    protected abstract Task<Agent> BuildAgentAsync(
        string sessionId, CancellationToken ct);

    /// <summary>
    /// Get the idle timeout for agent eviction.
    /// Platform implementations should return their configured timeout.
    /// </summary>
    protected abstract TimeSpan GetIdleTimeout();

    private void EvictIdleAgents(object? state)
    {
        if (_disposed) return;

        var cutoff = DateTime.UtcNow - GetIdleTimeout();

        foreach (var kvp in _agents)
        {
            if (kvp.Value.LastAccessed < cutoff && !kvp.Value.IsStreaming)
            {
                _agents.TryRemove(kvp.Key, out _);
                _buildLocks.TryRemove(kvp.Key, out _);
                _sessionLocks.TryRemove(kvp.Key, out _);

                var prefix = $"{kvp.Key}:";
                var keysToRemove = _streamLocks.Keys.Where(k => k.StartsWith(prefix)).ToList();
                foreach (var key in keysToRemove)
                    if (_streamLocks.TryRemove(key, out var sem))
                        sem.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _evictionTimer.Dispose();

        // Cleanup locks
        foreach (var kvp in _buildLocks)
        {
            kvp.Value.Dispose();
        }
        foreach (var kvp in _streamLocks)
        {
            kvp.Value.Dispose();
        }
        foreach (var kvp in _sessionLocks)
        {
            kvp.Value.Dispose();
        }
    }

    private sealed class AgentEntry
    {
        public Agent Agent { get; }
        public DateTime LastAccessed { get; set; }
        public bool IsStreaming { get; set; }

        public AgentEntry(Agent agent)
        {
            Agent = agent ?? throw new ArgumentNullException(nameof(agent));
            LastAccessed = DateTime.UtcNow;
            IsStreaming = false;
        }
    }
}
