using System.Text.Json;


namespace HPD.Agent;

/// <summary>
/// File-based session store using JSON files.
/// Stores session snapshots and uncommitted turns for crash recovery.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Directory Structure:</strong>
/// <code>
/// {basePath}/
/// ├── {sessionId}/
/// │   ├── session.json          # SessionSnapshot (~20KB)
/// │   ├── uncommitted.json      # UncommittedTurn (crash recovery, ~10-20KB)
/// │   └── assets/
/// │       └── {assetId}.ext     # Binary assets
/// </code>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong>
/// Uses atomic writes (write to temp file, then rename) to prevent corruption.
/// File locking is used for concurrent access safety within the same process.
/// </para>
/// </remarks>
public class JsonSessionStore : ISessionStore
{
    private readonly string _basePath;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new JSON-based session store.
    /// </summary>
    /// <param name="basePath">Base directory for storing session files. Will be created if it doesn't exist.</param>
    public JsonSessionStore(string basePath)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        Directory.CreateDirectory(_basePath);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SESSION
    // ═══════════════════════════════════════════════════════════════════

    public Task<AgentSession?> LoadSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var sessionPath = GetSessionFilePath(sessionId);

        if (!File.Exists(sessionPath))
            return Task.FromResult<AgentSession?>(null);

        lock (_lock)
        {
            var json = File.ReadAllText(sessionPath);
            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(json, SessionJsonContext.CombinedOptions);
            if (snapshot == null)
                return Task.FromResult<AgentSession?>(null);

            return Task.FromResult<AgentSession?>(AgentSession.FromSnapshot(snapshot));
        }
    }

    public Task SaveSessionAsync(
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sessionPath = GetSessionFilePath(session.Id);
        var snapshot = session.ToSnapshot();
        var json = JsonSerializer.Serialize(snapshot, SessionJsonContext.CombinedOptions);

        lock (_lock)
        {
            WriteAtomically(sessionPath, json);
        }

        return Task.CompletedTask;
    }

    public Task<List<string>> ListSessionIdsAsync(CancellationToken cancellationToken = default)
    {
        var sessionIds = new List<string>();

        if (Directory.Exists(_basePath))
        {
            var sessionDirs = Directory.GetDirectories(_basePath);
            sessionIds.AddRange(sessionDirs.Select(d => Path.GetFileName(d)!));
        }

        return Task.FromResult(sessionIds);
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_lock)
        {
            var sessionDir = GetSessionDirectoryPath(sessionId);
            if (Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // UNCOMMITTED TURN (Crash Recovery)
    // ═══════════════════════════════════════════════════════════════════

    public Task<UncommittedTurn?> LoadUncommittedTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var filePath = GetUncommittedTurnFilePath(sessionId);

        if (!File.Exists(filePath))
            return Task.FromResult<UncommittedTurn?>(null);

        lock (_lock)
        {
            if (!File.Exists(filePath))
                return Task.FromResult<UncommittedTurn?>(null);

            var json = File.ReadAllText(filePath);
            var turn = JsonSerializer.Deserialize<UncommittedTurn>(json, SessionJsonContext.CombinedOptions);
            return Task.FromResult(turn);
        }
    }

    public Task SaveUncommittedTurnAsync(
        UncommittedTurn turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var filePath = GetUncommittedTurnFilePath(turn.SessionId);
        var json = JsonSerializer.Serialize(turn, SessionJsonContext.CombinedOptions);

        lock (_lock)
        {
            WriteAtomically(filePath, json);
        }

        return Task.CompletedTask;
    }

    public Task DeleteUncommittedTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var filePath = GetUncommittedTurnFilePath(sessionId);

        lock (_lock)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // ASSETS
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public IAssetStore? GetAssetStore(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var sessionPath = GetSessionDirectoryPath(sessionId);
        return new LocalFileAssetStore(sessionPath);
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
        var toDelete = new List<string>();

        lock (_lock)
        {
            if (!Directory.Exists(_basePath))
                return Task.FromResult(0);

            var sessionDirs = Directory.GetDirectories(_basePath);
            foreach (var sessionDir in sessionDirs)
            {
                var dirInfo = new DirectoryInfo(sessionDir);
                if (dirInfo.LastWriteTimeUtc < cutoff)
                {
                    toDelete.Add(sessionDir);
                }
            }

            if (!dryRun)
            {
                foreach (var sessionDir in toDelete)
                {
                    Directory.Delete(sessionDir, recursive: true);
                }
            }
        }

        return Task.FromResult(toDelete.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    // PRIVATE HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════

    private string GetSessionDirectoryPath(string sessionId)
        => Path.Combine(_basePath, sessionId);

    private string GetSessionFilePath(string sessionId)
        => Path.Combine(GetSessionDirectoryPath(sessionId), "session.json");

    private string GetUncommittedTurnFilePath(string sessionId)
        => Path.Combine(GetSessionDirectoryPath(sessionId), "uncommitted.json");

    private void WriteAtomically(string filePath, string content)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, filePath, overwrite: true);
    }
}
