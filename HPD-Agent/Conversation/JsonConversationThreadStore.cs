using System.Text.Json;

namespace HPD.Agent.Checkpointing;

/// <summary>
/// File-based conversation thread store using JSON files.
/// Persists conversation threads to disk for durability across process restarts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Directory Structure:</strong>
/// <code>
/// {basePath}/
/// ├── threads/
/// │   ├── {threadId}.json              (LatestOnly mode)
/// │   └── {threadId}/                  (FullHistory mode)
/// │       ├── manifest.json
/// │       ├── {checkpointId}.json
/// │       └── ...
/// └── pending/
///     └── {threadId}_{checkpointId}.json
/// </code>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong>
/// Uses atomic writes (write to temp file, then rename) to prevent corruption.
/// File locking is used for concurrent access safety within the same process.
/// </para>
/// </remarks>
public class JsonConversationThreadStore : ICheckpointStore
{
    private readonly string _basePath;
    private readonly string _threadsPath;
    private readonly string _pendingPath;
    private readonly object _lock = new();

    /// <inheritdoc />
    public CheckpointRetentionMode RetentionMode { get; }

    /// <summary>
    /// Creates a new JSON-based conversation thread store.
    /// </summary>
    /// <param name="basePath">Base directory for storing checkpoint files. Will be created if it doesn't exist.</param>
    /// <param name="retentionMode">Checkpoint retention mode (LatestOnly or FullHistory).</param>
    public JsonConversationThreadStore(
        string basePath,
        CheckpointRetentionMode retentionMode = CheckpointRetentionMode.LatestOnly)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        RetentionMode = retentionMode;

        _threadsPath = Path.Combine(_basePath, "threads");
        _pendingPath = Path.Combine(_basePath, "pending");

        // Ensure directories exist
        Directory.CreateDirectory(_threadsPath);
        Directory.CreateDirectory(_pendingPath);
    }

    /// <inheritdoc />
    public Task<ConversationThread?> LoadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        if (RetentionMode == CheckpointRetentionMode.LatestOnly)
        {
            var filePath = GetThreadFilePath(threadId);

            if (!File.Exists(filePath))
                return Task.FromResult<ConversationThread?>(null);

            lock (_lock)
            {
                var json = File.ReadAllText(filePath);
                var snapshot = JsonSerializer.Deserialize(json, HPDJsonContext.Default.ConversationThreadSnapshot);

                if (snapshot == null)
                    return Task.FromResult<ConversationThread?>(null);

                var thread = ConversationThread.Deserialize(snapshot, null);
                return Task.FromResult<ConversationThread?>(thread);
            }
        }
        else
        {
            // FullHistory: Load manifest and get latest checkpoint
            var threadDir = GetThreadDirectoryPath(threadId);
            var manifestPath = Path.Combine(threadDir, "manifest.json");

            if (!File.Exists(manifestPath))
                return Task.FromResult<ConversationThread?>(null);

            lock (_lock)
            {
                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<CheckpointManifest>(manifestJson);

                if (manifest == null || manifest.Checkpoints.Count == 0)
                    return Task.FromResult<ConversationThread?>(null);

                // Get latest checkpoint (first in list, sorted newest first)
                var latestId = manifest.Checkpoints[0].CheckpointId;
                var checkpointPath = Path.Combine(threadDir, $"{latestId}.json");

                if (!File.Exists(checkpointPath))
                    return Task.FromResult<ConversationThread?>(null);

                var json = File.ReadAllText(checkpointPath);
                var snapshot = JsonSerializer.Deserialize(json, HPDJsonContext.Default.ConversationThreadSnapshot);

                if (snapshot == null)
                    return Task.FromResult<ConversationThread?>(null);

                var thread = ConversationThread.Deserialize(snapshot, null);
                return Task.FromResult<ConversationThread?>(thread);
            }
        }
    }

    /// <inheritdoc />
    public Task SaveThreadAsync(
        ConversationThread thread,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var snapshot = thread.Serialize(null);
        var json = JsonSerializer.Serialize(snapshot, HPDJsonContext.Default.ConversationThreadSnapshot);

        if (RetentionMode == CheckpointRetentionMode.LatestOnly)
        {
            var filePath = GetThreadFilePath(thread.Id);
            WriteAtomically(filePath, json);
        }
        else
        {
            // FullHistory: Create new checkpoint file and update manifest
            var threadDir = GetThreadDirectoryPath(thread.Id);
            Directory.CreateDirectory(threadDir);

            var checkpointId = Guid.NewGuid().ToString();
            var checkpointPath = Path.Combine(threadDir, $"{checkpointId}.json");

            lock (_lock)
            {
                // Write checkpoint file
                WriteAtomically(checkpointPath, json);

                // Update manifest
                var manifestPath = Path.Combine(threadDir, "manifest.json");
                var manifest = LoadOrCreateManifest(manifestPath);

                var entry = new CheckpointManifestEntry
                {
                    CheckpointId = checkpointId,
                    CreatedAt = DateTime.UtcNow,
                    Step = thread.ExecutionState?.Metadata?.Step ?? -1,
                    Source = thread.ExecutionState?.Metadata?.Source ?? CheckpointSource.Loop
                };

                // Insert at beginning (newest first)
                manifest.Checkpoints.Insert(0, entry);
                manifest.LastUpdated = DateTime.UtcNow;

                var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                WriteAtomically(manifestPath, manifestJson);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<List<string>> ListThreadIdsAsync(CancellationToken cancellationToken = default)
    {
        var threadIds = new List<string>();

        if (RetentionMode == CheckpointRetentionMode.LatestOnly)
        {
            if (Directory.Exists(_threadsPath))
            {
                var files = Directory.GetFiles(_threadsPath, "*.json");
                threadIds.AddRange(files.Select(f => Path.GetFileNameWithoutExtension(f)));
            }
        }
        else
        {
            if (Directory.Exists(_threadsPath))
            {
                var directories = Directory.GetDirectories(_threadsPath);
                threadIds.AddRange(directories.Select(d => Path.GetFileName(d)));
            }
        }

        return Task.FromResult(threadIds);
    }

    /// <inheritdoc />
    public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        lock (_lock)
        {
            if (RetentionMode == CheckpointRetentionMode.LatestOnly)
            {
                var filePath = GetThreadFilePath(threadId);
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            else
            {
                var threadDir = GetThreadDirectoryPath(threadId);
                if (Directory.Exists(threadDir))
                    Directory.Delete(threadDir, recursive: true);
            }

            // Also clean up any pending writes for this thread
            CleanupPendingWritesForThread(threadId);
        }

        return Task.CompletedTask;
    }

    // ===== Full History Methods =====

    /// <inheritdoc />
    public Task<ConversationThread?> LoadThreadAtCheckpointAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        if (RetentionMode != CheckpointRetentionMode.FullHistory)
            throw new NotSupportedException("LoadThreadAtCheckpointAsync requires RetentionMode.FullHistory");

        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);

        var threadDir = GetThreadDirectoryPath(threadId);
        var checkpointPath = Path.Combine(threadDir, $"{checkpointId}.json");

        if (!File.Exists(checkpointPath))
            return Task.FromResult<ConversationThread?>(null);

        lock (_lock)
        {
            var json = File.ReadAllText(checkpointPath);
            var snapshot = JsonSerializer.Deserialize(json, HPDJsonContext.Default.ConversationThreadSnapshot);

            if (snapshot == null)
                return Task.FromResult<ConversationThread?>(null);

            var thread = ConversationThread.Deserialize(snapshot, null);
            return Task.FromResult<ConversationThread?>(thread);
        }
    }

    /// <inheritdoc />
    public Task<List<CheckpointTuple>> GetCheckpointHistoryAsync(
        string threadId,
        int? limit = null,
        DateTime? before = null,
        CancellationToken cancellationToken = default)
    {
        if (RetentionMode != CheckpointRetentionMode.FullHistory)
            throw new NotSupportedException("GetCheckpointHistoryAsync requires RetentionMode.FullHistory");

        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var threadDir = GetThreadDirectoryPath(threadId);
        var manifestPath = Path.Combine(threadDir, "manifest.json");

        if (!File.Exists(manifestPath))
            return Task.FromResult(new List<CheckpointTuple>());

        lock (_lock)
        {
            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<CheckpointManifest>(manifestJson);

            if (manifest == null)
                return Task.FromResult(new List<CheckpointTuple>());

            var query = manifest.Checkpoints.AsEnumerable();

            if (before.HasValue)
                query = query.Where(c => c.CreatedAt < before.Value);

            if (limit.HasValue)
                query = query.Take(limit.Value);

            // Load full checkpoint data for each entry
            var result = new List<CheckpointTuple>();
            foreach (var entry in query)
            {
                var checkpointPath = Path.Combine(threadDir, $"{entry.CheckpointId}.json");
                if (!File.Exists(checkpointPath))
                    continue;

                var json = File.ReadAllText(checkpointPath);
                var snapshot = JsonSerializer.Deserialize(json, HPDJsonContext.Default.ConversationThreadSnapshot);

                if (snapshot == null)
                    continue;

                var thread = ConversationThread.Deserialize(snapshot, null);

                if (thread.ExecutionState == null)
                    continue;

                result.Add(new CheckpointTuple
                {
                    CheckpointId = entry.CheckpointId,
                    CreatedAt = entry.CreatedAt,
                    State = thread.ExecutionState,
                    Metadata = thread.ExecutionState.Metadata ?? new CheckpointMetadata
                    {
                        Source = entry.Source,
                        Step = entry.Step
                    }
                });
            }

            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task PruneCheckpointsAsync(
        string threadId,
        int keepLatest = 10,
        CancellationToken cancellationToken = default)
    {
        if (RetentionMode != CheckpointRetentionMode.FullHistory)
            return Task.CompletedTask;

        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var threadDir = GetThreadDirectoryPath(threadId);
        var manifestPath = Path.Combine(threadDir, "manifest.json");

        if (!File.Exists(manifestPath))
            return Task.CompletedTask;

        lock (_lock)
        {
            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<CheckpointManifest>(manifestJson);

            if (manifest == null || manifest.Checkpoints.Count <= keepLatest)
                return Task.CompletedTask;

            // Get checkpoints to delete (oldest ones beyond keepLatest)
            var toDelete = manifest.Checkpoints.Skip(keepLatest).ToList();

            foreach (var entry in toDelete)
            {
                var checkpointPath = Path.Combine(threadDir, $"{entry.CheckpointId}.json");
                if (File.Exists(checkpointPath))
                    File.Delete(checkpointPath);
            }

            // Update manifest
            manifest.Checkpoints = manifest.Checkpoints.Take(keepLatest).ToList();
            manifest.LastUpdated = DateTime.UtcNow;

            var updatedManifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            WriteAtomically(manifestPath, updatedManifestJson);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (RetentionMode == CheckpointRetentionMode.LatestOnly)
            {
                if (!Directory.Exists(_threadsPath))
                    return Task.CompletedTask;

                foreach (var filePath in Directory.GetFiles(_threadsPath, "*.json"))
                {
                    var json = File.ReadAllText(filePath);
                    var snapshot = JsonSerializer.Deserialize(json, HPDJsonContext.Default.ConversationThreadSnapshot);

                    if (snapshot != null && snapshot.LastActivity < cutoff)
                    {
                        File.Delete(filePath);
                    }
                }
            }
            else
            {
                if (!Directory.Exists(_threadsPath))
                    return Task.CompletedTask;

                foreach (var threadDir in Directory.GetDirectories(_threadsPath))
                {
                    var manifestPath = Path.Combine(threadDir, "manifest.json");
                    if (!File.Exists(manifestPath))
                        continue;

                    var manifestJson = File.ReadAllText(manifestPath);
                    var manifest = JsonSerializer.Deserialize<CheckpointManifest>(manifestJson);

                    if (manifest == null)
                        continue;

                    // Remove checkpoints older than cutoff
                    var toDelete = manifest.Checkpoints.Where(c => c.CreatedAt < cutoff).ToList();

                    foreach (var entry in toDelete)
                    {
                        var checkpointPath = Path.Combine(threadDir, $"{entry.CheckpointId}.json");
                        if (File.Exists(checkpointPath))
                            File.Delete(checkpointPath);
                    }

                    manifest.Checkpoints = manifest.Checkpoints.Where(c => c.CreatedAt >= cutoff).ToList();

                    if (manifest.Checkpoints.Count == 0)
                    {
                        // No checkpoints left, delete the whole thread directory
                        Directory.Delete(threadDir, recursive: true);
                    }
                    else
                    {
                        // Update manifest
                        manifest.LastUpdated = DateTime.UtcNow;
                        var updatedManifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                        WriteAtomically(manifestPath, updatedManifestJson);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> DeleteInactiveThreadsAsync(
        TimeSpan inactivityThreshold,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - inactivityThreshold;
        var count = 0;

        lock (_lock)
        {
            if (RetentionMode == CheckpointRetentionMode.LatestOnly)
            {
                if (!Directory.Exists(_threadsPath))
                    return Task.FromResult(0);

                var toDelete = new List<string>();

                foreach (var filePath in Directory.GetFiles(_threadsPath, "*.json"))
                {
                    var json = File.ReadAllText(filePath);
                    var snapshot = JsonSerializer.Deserialize(json, HPDJsonContext.Default.ConversationThreadSnapshot);

                    if (snapshot != null && snapshot.LastActivity < cutoff)
                    {
                        toDelete.Add(filePath);
                    }
                }

                count = toDelete.Count;

                if (!dryRun)
                {
                    foreach (var filePath in toDelete)
                    {
                        File.Delete(filePath);

                        // Clean up pending writes
                        var threadId = Path.GetFileNameWithoutExtension(filePath);
                        CleanupPendingWritesForThread(threadId);
                    }
                }
            }
            else
            {
                if (!Directory.Exists(_threadsPath))
                    return Task.FromResult(0);

                var toDelete = new List<string>();

                foreach (var threadDir in Directory.GetDirectories(_threadsPath))
                {
                    var manifestPath = Path.Combine(threadDir, "manifest.json");
                    if (!File.Exists(manifestPath))
                        continue;

                    var manifestJson = File.ReadAllText(manifestPath);
                    var manifest = JsonSerializer.Deserialize<CheckpointManifest>(manifestJson);

                    if (manifest == null || manifest.Checkpoints.Count == 0)
                    {
                        toDelete.Add(threadDir);
                        continue;
                    }

                    // Check latest checkpoint (first in list)
                    if (manifest.Checkpoints[0].CreatedAt < cutoff)
                    {
                        toDelete.Add(threadDir);
                    }
                }

                count = toDelete.Count;

                if (!dryRun)
                {
                    foreach (var threadDir in toDelete)
                    {
                        var threadId = Path.GetFileName(threadDir);
                        Directory.Delete(threadDir, recursive: true);
                        CleanupPendingWritesForThread(threadId);
                    }
                }
            }
        }

        return Task.FromResult(count);
    }

    // ===== Pending Writes Methods =====

    /// <inheritdoc />
    public Task SavePendingWritesAsync(
        string threadId,
        string checkpointId,
        IEnumerable<PendingWrite> writes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);

        var filePath = GetPendingWritesFilePath(threadId, checkpointId);
        var writesList = writes.ToList();

        lock (_lock)
        {
            List<PendingWrite> existing = new();

            // Load existing pending writes if any
            if (File.Exists(filePath))
            {
                var existingJson = File.ReadAllText(filePath);
                existing = JsonSerializer.Deserialize<List<PendingWrite>>(existingJson) ?? new();
            }

            // Append new writes
            existing.AddRange(writesList);

            var json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
            WriteAtomically(filePath, json);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<List<PendingWrite>> LoadPendingWritesAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);

        var filePath = GetPendingWritesFilePath(threadId, checkpointId);

        if (!File.Exists(filePath))
            return Task.FromResult(new List<PendingWrite>());

        lock (_lock)
        {
            var json = File.ReadAllText(filePath);
            var writes = JsonSerializer.Deserialize<List<PendingWrite>>(json) ?? new();
            return Task.FromResult(writes);
        }
    }

    /// <inheritdoc />
    public Task DeletePendingWritesAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);

        var filePath = GetPendingWritesFilePath(threadId, checkpointId);

        lock (_lock)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }

        return Task.CompletedTask;
    }

    // ===== Private Helpers =====

    private string GetThreadFilePath(string threadId)
        => Path.Combine(_threadsPath, $"{threadId}.json");

    private string GetThreadDirectoryPath(string threadId)
        => Path.Combine(_threadsPath, threadId);

    private string GetPendingWritesFilePath(string threadId, string checkpointId)
        => Path.Combine(_pendingPath, $"{threadId}_{checkpointId}.json");

    private void WriteAtomically(string filePath, string content)
    {
        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, filePath, overwrite: true);
    }

    private CheckpointManifest LoadOrCreateManifest(string manifestPath)
    {
        if (File.Exists(manifestPath))
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<CheckpointManifest>(json) ?? new CheckpointManifest();
        }

        return new CheckpointManifest();
    }

    private void CleanupPendingWritesForThread(string threadId)
    {
        if (!Directory.Exists(_pendingPath))
            return;

        var pattern = $"{threadId}_*.json";
        foreach (var file in Directory.GetFiles(_pendingPath, pattern))
        {
            File.Delete(file);
        }
    }
}

/// <summary>
/// Manifest file tracking all checkpoints for a thread (FullHistory mode).
/// </summary>
internal class CheckpointManifest
{
    public string ThreadId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public List<CheckpointManifestEntry> Checkpoints { get; set; } = new();
}

/// <summary>
/// Entry in the checkpoint manifest (lightweight metadata).
/// </summary>
internal class CheckpointManifestEntry
{
    public required string CheckpointId { get; set; }
    public required DateTime CreatedAt { get; set; }
    public int Step { get; set; }
    public CheckpointSource Source { get; set; }
}
