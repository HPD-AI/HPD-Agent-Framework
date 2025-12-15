using System.Text.Json;


namespace HPD.Agent.Session;

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
/// │   └── {sessionId}/
/// │       ├── manifest.json           # Index of snapshots and checkpoints
/// │       ├── snapshots/
/// │       │   └── {snapshotId}.json   # SessionSnapshot (~20KB)
/// │       └── checkpoints/
/// │           └── {checkpointId}.json # ExecutionCheckpoint (~100KB)
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
    private readonly string _pendingPath;
    private readonly object _lock = new();

    /// <inheritdoc />
    public bool SupportsHistory => true;

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
        _pendingPath = Path.Combine(_basePath, "pending");

        Directory.CreateDirectory(_sessionsPath);
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

        var sessionDir = GetSessionDirectoryPath(sessionId);
        var manifestPath = Path.Combine(sessionDir, "manifest.json");

        if (!File.Exists(manifestPath))
            return Task.FromResult<AgentSession?>(null);

        lock (_lock)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null)
                return Task.FromResult<AgentSession?>(null);

            // Find the latest snapshot
            var latestSnapshot = manifest.Snapshots.FirstOrDefault();
            if (latestSnapshot == null)
                return Task.FromResult<AgentSession?>(null);

            var snapshotsDir = Path.Combine(sessionDir, "snapshots");
            var filePath = Path.Combine(snapshotsDir, $"{latestSnapshot.SnapshotId}.json");

            if (!File.Exists(filePath))
                return Task.FromResult<AgentSession?>(null);

            var json = File.ReadAllText(filePath);
            var snapshot = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionSnapshot);
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

        var sessionDir = GetSessionDirectoryPath(session.Id);
        var snapshotsDir = Path.Combine(sessionDir, "snapshots");
        Directory.CreateDirectory(snapshotsDir);

        var snapshotId = Guid.NewGuid().ToString();
        var snapshotPath = Path.Combine(snapshotsDir, $"{snapshotId}.json");

        var snapshot = session.ToSnapshot();
        var json = JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.SessionSnapshot);

        lock (_lock)
        {
            var manifestPath = Path.Combine(sessionDir, "manifest.json");
            var manifest = LoadOrCreateManifest(manifestPath, session.Id);

            // Write snapshot file
            WriteAtomically(snapshotPath, json);

            // Add to manifest (newest first)
            manifest.Snapshots.Insert(0, new SnapshotManifestEntry
            {
                SnapshotId = snapshotId,
                CreatedAt = DateTime.UtcNow,
                MessageIndex = session.MessageCount
            });
            manifest.LastUpdated = DateTime.UtcNow;

            var manifestJson = JsonSerializer.Serialize(manifest, SessionJsonContext.Default.SessionManifest);
            WriteAtomically(manifestPath, manifestJson);
        }

        return Task.CompletedTask;
    }

    public Task<List<string>> ListSessionIdsAsync(CancellationToken cancellationToken = default)
    {
        var sessionIds = new List<string>();

        if (Directory.Exists(_sessionsPath))
        {
            var directories = Directory.GetDirectories(_sessionsPath);
            sessionIds.AddRange(directories.Select(d => Path.GetFileName(d)));
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
                Directory.Delete(sessionDir, recursive: true);

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

        var sessionDir = GetSessionDirectoryPath(sessionId);
        var manifestPath = Path.Combine(sessionDir, "manifest.json");

        if (!File.Exists(manifestPath))
            return Task.FromResult<ExecutionCheckpoint?>(null);

        lock (_lock)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null)
                return Task.FromResult<ExecutionCheckpoint?>(null);

            // Find the latest checkpoint
            var latestCheckpoint = manifest.Checkpoints.FirstOrDefault();
            if (latestCheckpoint == null)
                return Task.FromResult<ExecutionCheckpoint?>(null);

            var checkpointsDir = Path.Combine(sessionDir, "checkpoints");
            var filePath = Path.Combine(checkpointsDir, $"{latestCheckpoint.ExecutionCheckpointId}.json");

            if (!File.Exists(filePath))
                return Task.FromResult<ExecutionCheckpoint?>(null);

            var json = File.ReadAllText(filePath);
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

        var sessionDir = GetSessionDirectoryPath(sessionId);
        var checkpointsDir = Path.Combine(sessionDir, "checkpoints");
        Directory.CreateDirectory(checkpointsDir);

        var checkpointPath = Path.Combine(checkpointsDir, $"{checkpointId}.json");
        var json = JsonSerializer.Serialize(checkpoint, SessionJsonContext.Default.ExecutionCheckpoint);

        lock (_lock)
        {
            WriteAtomically(checkpointPath, json);

            var manifestPath = Path.Combine(sessionDir, "manifest.json");
            var manifest = LoadOrCreateManifest(manifestPath, sessionId);

            // Add to manifest (newest first)
            manifest.Checkpoints.Insert(0, new CheckpointManifestEntry
            {
                ExecutionCheckpointId = checkpointId,
                CreatedAt = DateTime.UtcNow,
                Step = metadata.Step,
                Source = metadata.Source,
                ParentExecutionCheckpointId = metadata.ParentCheckpointId,
                MessageIndex = metadata.MessageIndex,
                IsSnapshot = false
            });
            manifest.LastUpdated = DateTime.UtcNow;

            var manifestJson = JsonSerializer.Serialize(manifest, SessionJsonContext.Default.SessionManifest);
            WriteAtomically(manifestPath, manifestJson);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAllCheckpointsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var sessionDir = GetSessionDirectoryPath(sessionId);
        var checkpointsDir = Path.Combine(sessionDir, "checkpoints");
        var manifestPath = Path.Combine(sessionDir, "manifest.json");

        lock (_lock)
        {
            // Delete all checkpoint files
            if (Directory.Exists(checkpointsDir))
            {
                Directory.Delete(checkpointsDir, recursive: true);
            }

            // Update manifest to clear checkpoints
            if (File.Exists(manifestPath))
            {
                var manifest = LoadManifest(manifestPath);
                if (manifest != null)
                {
                    manifest.Checkpoints.Clear();
                    manifest.LastUpdated = DateTime.UtcNow;

                    var manifestJson = JsonSerializer.Serialize(manifest, SessionJsonContext.Default.SessionManifest);
                    WriteAtomically(manifestPath, manifestJson);
                }
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

        var sessionDir = GetSessionDirectoryPath(sessionId);
        var checkpointsDir = Path.Combine(sessionDir, "checkpoints");
        var filePath = Path.Combine(checkpointsDir, $"{executionCheckpointId}.json");

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
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var sessionDir = GetSessionDirectoryPath(sessionId);
        var manifestPath = Path.Combine(sessionDir, "manifest.json");

        if (!File.Exists(manifestPath))
            return Task.FromResult(new List<CheckpointManifestEntry>());

        lock (_lock)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null)
                return Task.FromResult(new List<CheckpointManifestEntry>());

            var query = manifest.Checkpoints.AsEnumerable();

            if (limit.HasValue)
                query = query.Take(limit.Value);

            return Task.FromResult(query.ToList());
        }
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

        var sessionDir = GetSessionDirectoryPath(sessionId);
        var checkpointsDir = Path.Combine(sessionDir, "checkpoints");
        var manifestPath = Path.Combine(sessionDir, "manifest.json");

        if (!File.Exists(manifestPath))
            return Task.CompletedTask;

        lock (_lock)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null || manifest.Checkpoints.Count <= keepLatest)
                return Task.CompletedTask;

            var toDelete = manifest.Checkpoints.Skip(keepLatest).ToList();

            foreach (var entry in toDelete)
            {
                var checkpointPath = Path.Combine(checkpointsDir, $"{entry.ExecutionCheckpointId}.json");
                if (File.Exists(checkpointPath))
                    File.Delete(checkpointPath);

                // Also clean up pending writes for this checkpoint
                var pendingPath = GetPendingWritesFilePath(sessionId, entry.ExecutionCheckpointId);
                if (File.Exists(pendingPath))
                    File.Delete(pendingPath);
            }

            manifest.Checkpoints = manifest.Checkpoints.Take(keepLatest).ToList();
            manifest.LastUpdated = DateTime.UtcNow;

            var updatedManifestJson = JsonSerializer.Serialize(manifest, SessionJsonContext.Default.SessionManifest);
            WriteAtomically(manifestPath, updatedManifestJson);
        }

        return Task.CompletedTask;
    }

    public Task DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!Directory.Exists(_sessionsPath))
                return Task.CompletedTask;

            foreach (var sessionDir in Directory.GetDirectories(_sessionsPath))
            {
                var sessionId = Path.GetFileName(sessionDir);
                var manifestPath = Path.Combine(sessionDir, "manifest.json");
                if (!File.Exists(manifestPath))
                    continue;

                var manifest = LoadManifest(manifestPath);
                if (manifest == null)
                    continue;

                // Delete old checkpoints
                var checkpointsDir = Path.Combine(sessionDir, "checkpoints");
                var checkpointsToDelete = manifest.Checkpoints.Where(c => c.CreatedAt < cutoff).ToList();
                foreach (var entry in checkpointsToDelete)
                {
                    var checkpointPath = Path.Combine(checkpointsDir, $"{entry.ExecutionCheckpointId}.json");
                    if (File.Exists(checkpointPath))
                        File.Delete(checkpointPath);
                }
                manifest.Checkpoints = manifest.Checkpoints.Where(c => c.CreatedAt >= cutoff).ToList();

                // Delete old snapshots
                var snapshotsDir = Path.Combine(sessionDir, "snapshots");
                var snapshotsToDelete = manifest.Snapshots.Where(s => s.CreatedAt < cutoff).ToList();
                foreach (var entry in snapshotsToDelete)
                {
                    var snapshotPath = Path.Combine(snapshotsDir, $"{entry.SnapshotId}.json");
                    if (File.Exists(snapshotPath))
                        File.Delete(snapshotPath);
                }
                manifest.Snapshots = manifest.Snapshots.Where(s => s.CreatedAt >= cutoff).ToList();

                // If both are empty, delete the session directory
                if (manifest.Checkpoints.Count == 0 && manifest.Snapshots.Count == 0)
                {
                    Directory.Delete(sessionDir, recursive: true);
                    CleanupPendingWritesForSession(sessionId);
                }
                else
                {
                    manifest.LastUpdated = DateTime.UtcNow;
                    var updatedManifestJson = JsonSerializer.Serialize(manifest, SessionJsonContext.Default.SessionManifest);
                    WriteAtomically(manifestPath, updatedManifestJson);
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

            foreach (var sessionDir in Directory.GetDirectories(_sessionsPath))
            {
                var manifestPath = Path.Combine(sessionDir, "manifest.json");
                if (!File.Exists(manifestPath))
                    continue;

                var manifest = LoadManifest(manifestPath);
                if (manifest == null)
                {
                    toDelete.Add(sessionDir);
                    continue;
                }

                // Check latest activity from both snapshots and checkpoints
                var latestSnapshotTime = manifest.Snapshots.FirstOrDefault()?.CreatedAt ?? DateTime.MinValue;
                var latestCheckpointTime = manifest.Checkpoints.FirstOrDefault()?.CreatedAt ?? DateTime.MinValue;
                var latestActivity = latestSnapshotTime > latestCheckpointTime ? latestSnapshotTime : latestCheckpointTime;

                if (latestActivity < cutoff)
                {
                    toDelete.Add(sessionDir);
                }
            }

            if (!dryRun)
            {
                foreach (var sessionDir in toDelete)
                {
                    var sessionId = Path.GetFileName(sessionDir);
                    Directory.Delete(sessionDir, recursive: true);
                    CleanupPendingWritesForSession(sessionId);
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

        var sessionDir = GetSessionDirectoryPath(sessionId);
        var checkpointsDir = Path.Combine(sessionDir, "checkpoints");
        var manifestPath = Path.Combine(sessionDir, "manifest.json");

        if (!File.Exists(manifestPath))
            return Task.CompletedTask;

        lock (_lock)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null)
                return Task.CompletedTask;

            foreach (var id in idsToDelete)
            {
                var checkpointPath = Path.Combine(checkpointsDir, $"{id}.json");
                if (File.Exists(checkpointPath))
                    File.Delete(checkpointPath);

                // Also clean up pending writes
                var pendingPath = GetPendingWritesFilePath(sessionId, id);
                if (File.Exists(pendingPath))
                    File.Delete(pendingPath);
            }

            manifest.Checkpoints.RemoveAll(c => idsToDelete.Contains(c.ExecutionCheckpointId));
            manifest.LastUpdated = DateTime.UtcNow;

            var updatedJson = JsonSerializer.Serialize(manifest, SessionJsonContext.Default.SessionManifest);
            WriteAtomically(manifestPath, updatedJson);
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

    private string GetSessionDirectoryPath(string sessionId)
        => Path.Combine(_sessionsPath, sessionId);

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

    private SessionManifest? LoadManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionManifest);
    }

    private SessionManifest LoadOrCreateManifest(string manifestPath, string sessionId)
    {
        if (File.Exists(manifestPath))
        {
            return LoadManifest(manifestPath) ?? new SessionManifest { SessionId = sessionId };
        }
        return new SessionManifest { SessionId = sessionId };
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

/// <summary>
/// Manifest file tracking all snapshots and checkpoints for a session.
/// </summary>
public class SessionManifest
{
    public string SessionId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Session snapshots (conversation history) - newest first.
    /// </summary>
    public List<SnapshotManifestEntry> Snapshots { get; set; } = new();

    /// <summary>
    /// Execution checkpoints (crash recovery) - newest first.
    /// </summary>
    public List<CheckpointManifestEntry> Checkpoints { get; set; } = new();
}

/// <summary>
/// Entry in the snapshot manifest.
/// </summary>
public class SnapshotManifestEntry
{
    public required string SnapshotId { get; set; }
    public required DateTime CreatedAt { get; set; }
    public int MessageIndex { get; set; }
}
