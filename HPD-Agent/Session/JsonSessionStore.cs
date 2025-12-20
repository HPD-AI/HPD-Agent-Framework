using System.Text.Json;


namespace HPD.Agent;

/// <summary>
/// File-based session store using JSON files.
/// Stores session snapshots and execution checkpoints separately for durability.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Directory Structure:</strong>
/// <code>
/// {basePath}/
/// ├── sessions/
/// │   └── {sessionId}.json            # SessionSnapshot (~20KB)
/// ├── checkpoints/
/// │   └── {sessionId}/
/// │       └── {checkpointId}.json     # ExecutionCheckpoint (~100KB)
/// └── pending/
///     └── {sessionId}_{checkpointId}.json
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
    private readonly string _sessionsPath;
    private readonly string _checkpointsPath;
    private readonly string _pendingPath;
    private readonly object _lock = new();

    /// <inheritdoc />
    public bool SupportsHistory => false;

    /// <inheritdoc />
    public bool SupportsPendingWrites => true;

    /// <summary>
    /// Creates a new JSON-based session store.
    /// </summary>
    /// <param name="basePath">Base directory for storing session files. Will be created if it doesn't exist.</param>
    public JsonSessionStore(string basePath)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));

        _sessionsPath = Path.Combine(_basePath, "sessions");
        _checkpointsPath = Path.Combine(_basePath, "checkpoints");
        _pendingPath = Path.Combine(_basePath, "pending");

        Directory.CreateDirectory(_sessionsPath);
        Directory.CreateDirectory(_checkpointsPath);
        Directory.CreateDirectory(_pendingPath);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SESSION PERSISTENCE (Snapshots)
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

        if (Directory.Exists(_sessionsPath))
        {
            var files = Directory.GetFiles(_sessionsPath, "*.json");
            sessionIds.AddRange(files.Select(f => Path.GetFileNameWithoutExtension(f)));
        }

        return Task.FromResult(sessionIds);
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_lock)
        {
            var sessionPath = GetSessionFilePath(sessionId);
            if (File.Exists(sessionPath))
                File.Delete(sessionPath);

            CleanupPendingWritesForSession(sessionId);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var checkpointDir = GetCheckpointDirectoryPath(sessionId);

        if (!Directory.Exists(checkpointDir))
            return Task.FromResult<ExecutionCheckpoint?>(null);

        lock (_lock)
        {
            // Find the most recently modified checkpoint file
            var files = Directory.GetFiles(checkpointDir, "*.json");
            if (files.Length == 0)
                return Task.FromResult<ExecutionCheckpoint?>(null);

            var latestFile = files
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .First();

            var json = File.ReadAllText(latestFile.FullName);
            var checkpoint = JsonSerializer.Deserialize(json, SessionJsonContext.Default.ExecutionCheckpoint);
            return Task.FromResult(checkpoint);
        }
    }

    public Task SaveCheckpointAsync(
        ExecutionCheckpoint checkpoint,
        CheckpointMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(metadata);

        var sessionId = checkpoint.SessionId;
        var checkpointId = checkpoint.ExecutionCheckpointId;
        var checkpointPath = GetCheckpointFilePath(sessionId, checkpointId);
        var json = JsonSerializer.Serialize(checkpoint, SessionJsonContext.Default.ExecutionCheckpoint);

        lock (_lock)
        {
            WriteAtomically(checkpointPath, json);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAllCheckpointsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var checkpointDir = GetCheckpointDirectoryPath(sessionId);

        lock (_lock)
        {
            // Delete all checkpoint files
            if (Directory.Exists(checkpointDir))
            {
                Directory.Delete(checkpointDir, recursive: true);
            }

            // Clean up pending writes
            CleanupPendingWritesForSession(sessionId);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionCheckpointId);

        var filePath = GetCheckpointFilePath(sessionId, executionCheckpointId);

        if (!File.Exists(filePath))
            return Task.FromResult<ExecutionCheckpoint?>(null);

        lock (_lock)
        {
            var json = File.ReadAllText(filePath);
            var checkpoint = JsonSerializer.Deserialize(json, SessionJsonContext.Default.ExecutionCheckpoint);
            return Task.FromResult(checkpoint);
        }
    }

    public Task<List<CheckpointManifestEntry>> GetCheckpointManifestAsync(
        string sessionId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        // No longer tracking checkpoint history without manifests
        // Return empty list for backwards compatibility
        return Task.FromResult(new List<CheckpointManifestEntry>());
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
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionCheckpointId);

        var filePath = GetPendingWritesFilePath(sessionId, executionCheckpointId);
        var writesList = writes.ToList();

        lock (_lock)
        {
            List<PendingWrite> existing = new();

            if (File.Exists(filePath))
            {
                var existingJson = File.ReadAllText(filePath);
                existing = JsonSerializer.Deserialize(existingJson, SessionJsonContext.Default.ListPendingWrite) ?? new();
            }

            existing.AddRange(writesList);

            var json = JsonSerializer.Serialize(existing, SessionJsonContext.Default.ListPendingWrite);
            WriteAtomically(filePath, json);
        }

        return Task.CompletedTask;
    }

    public Task<List<PendingWrite>> LoadPendingWritesAsync(
        string sessionId,
        string executionCheckpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionCheckpointId);

        var filePath = GetPendingWritesFilePath(sessionId, executionCheckpointId);

        if (!File.Exists(filePath))
            return Task.FromResult(new List<PendingWrite>());

        lock (_lock)
        {
            var json = File.ReadAllText(filePath);
            var writes = JsonSerializer.Deserialize(json, SessionJsonContext.Default.ListPendingWrite) ?? new();
            return Task.FromResult(writes);
        }
    }

    public Task DeletePendingWritesAsync(
        string sessionId,
        string executionCheckpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionCheckpointId);

        var filePath = GetPendingWritesFilePath(sessionId, executionCheckpointId);

        lock (_lock)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }

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
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var checkpointDir = GetCheckpointDirectoryPath(sessionId);

        if (!Directory.Exists(checkpointDir))
            return Task.CompletedTask;

        lock (_lock)
        {
            var files = Directory.GetFiles(checkpointDir, "*.json")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            if (files.Count <= keepLatest)
                return Task.CompletedTask;

            // Delete older checkpoints
            foreach (var file in files.Skip(keepLatest))
            {
                file.Delete();

                // Also clean up pending writes for this checkpoint
                var checkpointId = Path.GetFileNameWithoutExtension(file.Name);
                var pendingPath = GetPendingWritesFilePath(sessionId, checkpointId);
                if (File.Exists(pendingPath))
                    File.Delete(pendingPath);
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!Directory.Exists(_sessionsPath))
                return Task.CompletedTask;

            // Delete old session files
            var sessionFiles = Directory.GetFiles(_sessionsPath, "*.json");
            foreach (var sessionFile in sessionFiles)
            {
                var fileInfo = new FileInfo(sessionFile);
                if (fileInfo.LastWriteTimeUtc < cutoff)
                {
                    fileInfo.Delete();
                    var sessionId = Path.GetFileNameWithoutExtension(sessionFile);
                    CleanupPendingWritesForSession(sessionId);
                }
            }

            // Delete old checkpoints
            if (Directory.Exists(_checkpointsPath))
            {
                foreach (var sessionDir in Directory.GetDirectories(_checkpointsPath))
                {
                    var sessionId = Path.GetFileName(sessionDir);
                    var checkpointFiles = Directory.GetFiles(sessionDir, "*.json");
                    
                    foreach (var checkpointFile in checkpointFiles)
                    {
                        var fileInfo = new FileInfo(checkpointFile);
                        if (fileInfo.LastWriteTimeUtc < cutoff)
                        {
                            fileInfo.Delete();
                            var checkpointId = Path.GetFileNameWithoutExtension(checkpointFile);
                            var pendingPath = GetPendingWritesFilePath(sessionId, checkpointId);
                            if (File.Exists(pendingPath))
                                File.Delete(pendingPath);
                        }
                    }

                    // Delete empty checkpoint directories
                    if (!Directory.EnumerateFileSystemEntries(sessionDir).Any())
                        Directory.Delete(sessionDir);
                }
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
        var toDelete = new List<string>();

        lock (_lock)
        {
            if (!Directory.Exists(_sessionsPath))
                return Task.FromResult(0);

            var sessionFiles = Directory.GetFiles(_sessionsPath, "*.json");
            foreach (var sessionFile in sessionFiles)
            {
                var fileInfo = new FileInfo(sessionFile);
                if (fileInfo.LastWriteTimeUtc < cutoff)
                {
                    toDelete.Add(sessionFile);
                }
            }

            if (!dryRun)
            {
                foreach (var sessionFile in toDelete)
                {
                    var sessionId = Path.GetFileNameWithoutExtension(sessionFile);
                    File.Delete(sessionFile);
                    CleanupPendingWritesForSession(sessionId);
                    
                    // Also delete checkpoint directory if it exists
                    var checkpointDir = GetCheckpointDirectoryPath(sessionId);
                    if (Directory.Exists(checkpointDir))
                        Directory.Delete(checkpointDir, recursive: true);
                }
            }
        }

        return Task.FromResult(toDelete.Count);
    }

    public Task DeleteCheckpointsAsync(
        string sessionId,
        IEnumerable<string> checkpointIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var idsToDelete = checkpointIds.ToHashSet();
        if (idsToDelete.Count == 0)
            return Task.CompletedTask;

        lock (_lock)
        {
            foreach (var id in idsToDelete)
            {
                var checkpointPath = GetCheckpointFilePath(sessionId, id);
                if (File.Exists(checkpointPath))
                    File.Delete(checkpointPath);

                // Also clean up pending writes
                var pendingPath = GetPendingWritesFilePath(sessionId, id);
                if (File.Exists(pendingPath))
                    File.Delete(pendingPath);
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
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // PRIVATE HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════

    private string GetSessionFilePath(string sessionId)
        => Path.Combine(_sessionsPath, $"{sessionId}.json");

    private string GetCheckpointDirectoryPath(string sessionId)
        => Path.Combine(_checkpointsPath, sessionId);

    private string GetCheckpointFilePath(string sessionId, string checkpointId)
        => Path.Combine(GetCheckpointDirectoryPath(sessionId), $"{checkpointId}.json");

    private string GetPendingWritesFilePath(string sessionId, string checkpointId)
        => Path.Combine(_pendingPath, $"{sessionId}_{checkpointId}.json");

    private void WriteAtomically(string filePath, string content)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, filePath, overwrite: true);
    }

    private void CleanupPendingWritesForSession(string sessionId)
    {
        if (!Directory.Exists(_pendingPath))
            return;

        var pattern = $"{sessionId}_*.json";
        foreach (var file in Directory.GetFiles(_pendingPath, pattern))
        {
            File.Delete(file);
        }
    }
}
