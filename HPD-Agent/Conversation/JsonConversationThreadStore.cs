using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Checkpointing;

/// <summary>
/// File-based checkpoint store using JSON files.
/// Stores full checkpoint history to disk for durability across process restarts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Directory Structure:</strong>
/// <code>
/// {basePath}/
/// ├── threads/
/// │   └── {threadId}/
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
/// <para>
/// For simple single-state storage without history, use <see cref="JsonThreadStore"/>.
/// </para>
/// </remarks>
public class JsonConversationThreadStore : ICheckpointStore
{
    private readonly string _basePath;
    private readonly string _threadsPath;
    private readonly string _pendingPath;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new JSON-based checkpoint store.
    /// </summary>
    /// <param name="basePath">Base directory for storing checkpoint files. Will be created if it doesn't exist.</param>
    public JsonConversationThreadStore(string basePath)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));

        _threadsPath = Path.Combine(_basePath, "threads");
        _pendingPath = Path.Combine(_basePath, "pending");

        Directory.CreateDirectory(_threadsPath);
        Directory.CreateDirectory(_pendingPath);
    }

    public Task<ConversationThread?> LoadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var threadDir = GetThreadDirectoryPath(threadId);
        var manifestPath = Path.Combine(threadDir, "manifest.json");

        if (!File.Exists(manifestPath))
            return Task.FromResult<ConversationThread?>(null);

        lock (_lock)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null || manifest.Checkpoints.Count == 0)
                return Task.FromResult<ConversationThread?>(null);

            // Get latest checkpoint (first in list, sorted newest first)
            var latest = manifest.Checkpoints[0];
            var checkpointPath = Path.Combine(threadDir, $"{latest.CheckpointId}.json");

            if (!File.Exists(checkpointPath))
                return Task.FromResult<ConversationThread?>(null);

            var json = File.ReadAllText(checkpointPath);
            var checkpoint = JsonSerializer.Deserialize(json, HPDJsonContext.Default.ExecutionCheckpoint);
            if (checkpoint == null)
                return Task.FromResult<ConversationThread?>(null);

            var thread = ConversationThread.FromExecutionCheckpoint(checkpoint);
            return Task.FromResult<ConversationThread?>(thread);
        }
    }

    public Task SaveThreadAsync(
        ConversationThread thread,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var threadDir = GetThreadDirectoryPath(thread.Id);
        Directory.CreateDirectory(threadDir);

        var checkpointId = Guid.NewGuid().ToString();
        var checkpointPath = Path.Combine(threadDir, $"{checkpointId}.json");

        // Use ExecutionCheckpoint for durable execution (includes ExecutionState)
        var checkpoint = thread.ToExecutionCheckpoint();
        var json = JsonSerializer.Serialize(checkpoint, HPDJsonContext.Default.ExecutionCheckpoint);

        lock (_lock)
        {
            var manifestPath = Path.Combine(threadDir, "manifest.json");
            var manifest = LoadOrCreateManifest(manifestPath);

            // If no checkpoints exist yet, create root checkpoint (messageIndex=-1)
            string? parentCheckpointId = null;
            if (manifest.Checkpoints.Count == 0)
            {
                parentCheckpointId = CreateRootCheckpoint(threadDir, thread.Id, manifest);
            }

            // Write checkpoint file
            WriteAtomically(checkpointPath, json);

            // Create manifest entry
            var entry = new CheckpointManifestEntry
            {
                CheckpointId = checkpointId,
                CreatedAt = DateTime.UtcNow,
                Step = thread.ExecutionState?.Iteration ?? -1,
                Source = thread.ExecutionState?.Metadata?.Source ?? CheckpointSource.Loop,
                ParentCheckpointId = parentCheckpointId,
                MessageIndex = thread.MessageCount,
                IsSnapshot = false  // Full checkpoint with ExecutionState
            };

            manifest.Checkpoints.Insert(0, entry);

            manifest.LastUpdated = DateTime.UtcNow;

            var manifestJson = JsonSerializer.Serialize(manifest, HPDJsonContext.Default.CheckpointManifest);
            WriteAtomically(manifestPath, manifestJson);
        }

        return Task.CompletedTask;
    }

    private string CreateRootCheckpoint(string threadDir, string threadId, CheckpointManifest manifest)
    {
        var rootCheckpointId = Guid.NewGuid().ToString();
        var rootEntry = new CheckpointManifestEntry
        {
            CheckpointId = rootCheckpointId,
            CreatedAt = DateTime.UtcNow,
            Step = -1,
            Source = CheckpointSource.Root,
            ParentCheckpointId = null,
            MessageIndex = -1,
            IsSnapshot = false
        };

        var rootSnapshot = new ConversationThreadSnapshot
        {
            Id = threadId,
            Messages = new List<ChatMessage>(),
            Metadata = new Dictionary<string, object>(),
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow
        };

        var rootJson = JsonSerializer.Serialize(rootSnapshot, HPDJsonContext.Default.ConversationThreadSnapshot);
        var rootPath = Path.Combine(threadDir, $"{rootCheckpointId}.json");
        WriteAtomically(rootPath, rootJson);

        manifest.Checkpoints.Add(rootEntry);
        return rootCheckpointId;
    }

    public Task<List<string>> ListThreadIdsAsync(CancellationToken cancellationToken = default)
    {
        var threadIds = new List<string>();

        if (Directory.Exists(_threadsPath))
        {
            var directories = Directory.GetDirectories(_threadsPath);
            threadIds.AddRange(directories.Select(d => Path.GetFileName(d)));
        }

        return Task.FromResult(threadIds);
    }

    public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        lock (_lock)
        {
            var threadDir = GetThreadDirectoryPath(threadId);
            if (Directory.Exists(threadDir))
                Directory.Delete(threadDir, recursive: true);

            CleanupPendingWritesForThread(threadId);
        }

        return Task.CompletedTask;
    }

    // ===== Checkpoint Access Methods =====

    public Task<ConversationThread?> LoadThreadAtCheckpointAsync(
        string threadId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);

        var threadDir = GetThreadDirectoryPath(threadId);
        var checkpointPath = Path.Combine(threadDir, $"{checkpointId}.json");

        if (!File.Exists(checkpointPath))
            return Task.FromResult<ConversationThread?>(null);

        lock (_lock)
        {
            var json = File.ReadAllText(checkpointPath);
            var checkpoint = JsonSerializer.Deserialize(json, HPDJsonContext.Default.ExecutionCheckpoint);
            if (checkpoint == null)
                return Task.FromResult<ConversationThread?>(null);

            var thread = ConversationThread.FromExecutionCheckpoint(checkpoint);
            return Task.FromResult<ConversationThread?>(thread);
        }
    }

#pragma warning disable CS0618 // BranchName is obsolete but used internally for backward compatibility
    public Task SaveThreadAtCheckpointAsync(
        ConversationThread thread,
        string checkpointId,
        CheckpointMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        ArgumentNullException.ThrowIfNull(metadata);

        var threadDir = GetThreadDirectoryPath(thread.Id);
        Directory.CreateDirectory(threadDir);

        var snapshot = thread.Serialize(null);
        var json = JsonSerializer.Serialize(snapshot, HPDJsonContext.Default.ConversationThreadSnapshot);
        var checkpointPath = Path.Combine(threadDir, $"{checkpointId}.json");

        lock (_lock)
        {
            WriteAtomically(checkpointPath, json);

            var manifestPath = Path.Combine(threadDir, "manifest.json");
            var manifest = LoadOrCreateManifest(manifestPath);

            var entry = new CheckpointManifestEntry
            {
                CheckpointId = checkpointId,
                CreatedAt = DateTime.UtcNow,
                Step = metadata.Step,
                Source = metadata.Source,
                ParentCheckpointId = metadata.ParentCheckpointId,
                MessageIndex = metadata.MessageIndex
            };

            manifest.Checkpoints.Insert(0, entry);
            manifest.LastUpdated = DateTime.UtcNow;

            var manifestJson = JsonSerializer.Serialize(manifest, HPDJsonContext.Default.CheckpointManifest);
            WriteAtomically(manifestPath, manifestJson);
        }

        return Task.CompletedTask;
    }

    public Task<List<CheckpointManifestEntry>> GetCheckpointManifestAsync(
        string threadId,
        int? limit = null,
        DateTime? before = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var threadDir = GetThreadDirectoryPath(threadId);
        var manifestPath = Path.Combine(threadDir, "manifest.json");

        if (!File.Exists(manifestPath))
            return Task.FromResult(new List<CheckpointManifestEntry>());

        lock (_lock)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null)
                return Task.FromResult(new List<CheckpointManifestEntry>());

            var query = manifest.Checkpoints.AsEnumerable();

            if (before.HasValue)
                query = query.Where(c => c.CreatedAt < before.Value);

            if (limit.HasValue)
                query = query.Take(limit.Value);

            return Task.FromResult(query.ToList());
        }
    }

    public Task UpdateCheckpointManifestEntryAsync(
        string threadId,
        string checkpointId,
        Action<CheckpointManifestEntry> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        ArgumentNullException.ThrowIfNull(update);

        var threadDir = GetThreadDirectoryPath(threadId);
        var manifestPath = Path.Combine(threadDir, "manifest.json");

        if (!File.Exists(manifestPath))
            return Task.CompletedTask;

        lock (_lock)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null)
                return Task.CompletedTask;

            var entry = manifest.Checkpoints.FirstOrDefault(c => c.CheckpointId == checkpointId);
            if (entry == null)
                return Task.CompletedTask;

            update(entry);

            manifest.LastUpdated = DateTime.UtcNow;
            var updatedJson = JsonSerializer.Serialize(manifest, HPDJsonContext.Default.CheckpointManifest);
            WriteAtomically(manifestPath, updatedJson);
        }

        return Task.CompletedTask;
    }

    // ===== Cleanup Methods =====

    public Task DeleteCheckpointsAsync(
        string threadId,
        IEnumerable<string> checkpointIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var idsToDelete = checkpointIds.ToHashSet();
        if (idsToDelete.Count == 0)
            return Task.CompletedTask;

        var threadDir = GetThreadDirectoryPath(threadId);
        var manifestPath = Path.Combine(threadDir, "manifest.json");

        if (!File.Exists(manifestPath))
            return Task.CompletedTask;

        lock (_lock)
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest == null)
                return Task.CompletedTask;

            foreach (var id in idsToDelete)
            {
                var checkpointPath = Path.Combine(threadDir, $"{id}.json");
                if (File.Exists(checkpointPath))
                    File.Delete(checkpointPath);
            }

            manifest.Checkpoints.RemoveAll(c => idsToDelete.Contains(c.CheckpointId));
            manifest.LastUpdated = DateTime.UtcNow;

            var updatedJson = JsonSerializer.Serialize(manifest, HPDJsonContext.Default.CheckpointManifest);
            WriteAtomically(manifestPath, updatedJson);
        }

        return Task.CompletedTask;
    }

    public Task PruneCheckpointsAsync(
        string threadId,
        int keepLatest = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var threadDir = GetThreadDirectoryPath(threadId);
        var manifestPath = Path.Combine(threadDir, "manifest.json");

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
                var checkpointPath = Path.Combine(threadDir, $"{entry.CheckpointId}.json");
                if (File.Exists(checkpointPath))
                    File.Delete(checkpointPath);
            }

            manifest.Checkpoints = manifest.Checkpoints.Take(keepLatest).ToList();
            manifest.LastUpdated = DateTime.UtcNow;

            var updatedManifestJson = JsonSerializer.Serialize(manifest, HPDJsonContext.Default.CheckpointManifest);
            WriteAtomically(manifestPath, updatedManifestJson);
        }

        return Task.CompletedTask;
    }

    public Task DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!Directory.Exists(_threadsPath))
                return Task.CompletedTask;

            foreach (var threadDir in Directory.GetDirectories(_threadsPath))
            {
                var manifestPath = Path.Combine(threadDir, "manifest.json");
                if (!File.Exists(manifestPath))
                    continue;

                var manifest = LoadManifest(manifestPath);
                if (manifest == null)
                    continue;

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
                    Directory.Delete(threadDir, recursive: true);
                }
                else
                {
                    manifest.LastUpdated = DateTime.UtcNow;
                    var updatedManifestJson = JsonSerializer.Serialize(manifest, HPDJsonContext.Default.CheckpointManifest);
                    WriteAtomically(manifestPath, updatedManifestJson);
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
        var toDelete = new List<string>();

        lock (_lock)
        {
            if (!Directory.Exists(_threadsPath))
                return Task.FromResult(0);

            foreach (var threadDir in Directory.GetDirectories(_threadsPath))
            {
                var manifestPath = Path.Combine(threadDir, "manifest.json");
                if (!File.Exists(manifestPath))
                    continue;

                var manifest = LoadManifest(manifestPath);
                if (manifest == null || manifest.Checkpoints.Count == 0)
                {
                    toDelete.Add(threadDir);
                    continue;
                }

                if (manifest.Checkpoints[0].CreatedAt < cutoff)
                {
                    toDelete.Add(threadDir);
                }
            }

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

        return Task.FromResult(toDelete.Count);
    }

    // ===== Pending Writes Methods =====

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

            if (File.Exists(filePath))
            {
                var existingJson = File.ReadAllText(filePath);
                existing = JsonSerializer.Deserialize(existingJson, HPDJsonContext.Default.ListPendingWrite) ?? new();
            }

            existing.AddRange(writesList);

            var json = JsonSerializer.Serialize(existing, HPDJsonContext.Default.ListPendingWrite);
            WriteAtomically(filePath, json);
        }

        return Task.CompletedTask;
    }

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
            var writes = JsonSerializer.Deserialize(json, HPDJsonContext.Default.ListPendingWrite) ?? new();
            return Task.FromResult(writes);
        }
    }

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

    // ===== Lightweight Snapshot Methods =====

    // Snapshot methods removed - branching is now an application-level concern

    // ===== Private Helper Methods =====

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

    private CheckpointManifest? LoadManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize(json, HPDJsonContext.Default.CheckpointManifest);
    }

    private CheckpointManifest LoadOrCreateManifest(string manifestPath)
    {
        if (File.Exists(manifestPath))
        {
            return LoadManifest(manifestPath) ?? new CheckpointManifest();
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
/// Manifest file tracking all checkpoints for a thread.
/// </summary>
public class CheckpointManifest
{
    public string ThreadId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public List<CheckpointManifestEntry> Checkpoints { get; set; } = new();
}
