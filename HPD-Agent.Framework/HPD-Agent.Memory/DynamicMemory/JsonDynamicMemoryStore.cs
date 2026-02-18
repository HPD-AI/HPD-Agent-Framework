using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HPD.Agent.Memory;

#pragma warning disable RS1035, IL2026, IL3050 // Allow file IO and dynamic JSON code

/// <summary>
/// JSON file-based implementation of DynamicMemoryStore.
/// Stores each agent's memories in a separate JSON file.
/// Suitable for production use with persistent storage needs.
/// </summary>
public class JsonDynamicMemoryStore : DynamicMemoryStore
{
    private readonly string _storageDirectory;
    private readonly ILogger<JsonDynamicMemoryStore>? _logger;
    private readonly List<Action> _invalidationCallbacks = new();
    private readonly object _fileLock = new();

    /// <summary>
    /// Creates a new JSON file-based dynamic memory store.
    /// </summary>
    /// <param name="storageDirectory">Directory where JSON files will be stored</param>
    /// <param name="logger">Optional logger for diagnostic messages</param>
    public JsonDynamicMemoryStore(string storageDirectory, ILogger<JsonDynamicMemoryStore>? logger = null)
    {
        _storageDirectory = Path.GetFullPath(storageDirectory);
        _logger = logger;
        EnsureDirectoryExists();
    }

    public override async Task<DynamicMemory> UpdateMemoryAsync(string agentName, string memoryId, string title, string content, CancellationToken cancellationToken = default)
    {
        var memories = await GetMemoriesForAgentAsync(agentName, cancellationToken);
        var memory = memories.FirstOrDefault(m => m.Id == memoryId);

        if (memory == null)
        {
            throw new InvalidOperationException($"Memory with ID '{memoryId}' not found for agent '{agentName}'");
        }

        memory.Title = title;
        memory.Content = content;
        memory.LastUpdated = DateTime.UtcNow;
        memory.LastAccessed = DateTime.UtcNow;
        await SaveMemoriesAsync(agentName, memories, cancellationToken);
        InvokeInvalidation();
        return memory;
    }

    public override void RegisterInvalidationCallback(Action callback)
    {
        lock (_fileLock)
        {
            _invalidationCallbacks.Add(callback);
        }
    }

    public override DynamicMemoryStoreSnapshot SerializeToSnapshot()
    {
        // Load all agent memories from disk
        var allMemories = new Dictionary<string, List<DynamicMemory>>();

        if (Directory.Exists(_storageDirectory))
        {
            foreach (var file in Directory.GetFiles(_storageDirectory, "*.json"))
            {
                try
                {
                    var agentName = Path.GetFileNameWithoutExtension(file);
                    var json = File.ReadAllText(file);
                    var memories = JsonSerializer.Deserialize<List<DynamicMemory>>(json) ?? new List<DynamicMemory>();
                    allMemories[agentName] = memories;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to read memories from {File} during serialization", file);
                }
            }
        }

        return new DynamicMemoryStoreSnapshot
        {
            StoreType = DynamicMemoryStoreType.JsonFile,
            Memories = allMemories,
            Configuration = new Dictionary<string, object>
            {
                { "StorageDirectory", _storageDirectory }
            }
        };
    }

    /// <summary>
    /// Deserialize a JSON store from a snapshot.
    /// </summary>
    internal new static JsonDynamicMemoryStore Deserialize(DynamicMemoryStoreSnapshot snapshot)
    {
        // Extract storage directory from configuration
        var storageDirectory = snapshot.Configuration?.GetValueOrDefault("StorageDirectory") as string
            ?? "./agent-dynamic-memory";

        var store = new JsonDynamicMemoryStore(storageDirectory);

        // Write all memories to disk
        foreach (var (agentName, memories) in snapshot.Memories)
        {
            store.SaveMemoriesAsync(agentName, memories, CancellationToken.None).GetAwaiter().GetResult();
        }

        return store;
    }

    private string GetFilePath(string agentName)
    {
        // Sanitize agent name for file system
        var safeAgentName = string.Join("_", agentName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_storageDirectory, safeAgentName + ".json");
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
        }
    }

    private Task SaveMemoriesAsync(string agentName, List<DynamicMemory> memories, CancellationToken cancellationToken)
    {
        EnsureDirectoryExists();
        var file = GetFilePath(agentName);

        lock (_fileLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(memories, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(file, json);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to write memories to {File}", file);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    private async Task<List<DynamicMemory>> GetMemoriesForAgentAsync(string agentName, CancellationToken cancellationToken = default)
    {
        var file = GetFilePath(agentName);
        if (!File.Exists(file))
        {
            return new List<DynamicMemory>();
        }

        try
        {
            using var stream = File.OpenRead(file);
            var memories = await JsonSerializer.DeserializeAsync<List<DynamicMemory>>(stream, cancellationToken: cancellationToken)
                ?? new List<DynamicMemory>();
            return memories.OrderByDescending(m => m.LastAccessed).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read memories from {File}", file);
            return new List<DynamicMemory>();
        }
    }

    private void InvokeInvalidation()
    {
        foreach (var cb in _invalidationCallbacks)
        {
            try { cb(); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // IContentStore Implementation (V2)
    // ═══════════════════════════════════════════════════════════════════
    // scope = agentName for DynamicMemoryStore
    // If scope is null in QueryAsync, query across ALL agents

    /// <inheritdoc />
    public override async Task<string> PutAsync(
        string? scope,
        byte[] data,
        string contentType,
        ContentMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var agentName = scope ?? throw new ArgumentNullException(nameof(scope), "Scope (agentName) is required for DynamicMemoryStore.PutAsync");

        var content = Encoding.UTF8.GetString(data);
        var title = metadata?.Name ?? $"Memory {DateTime.UtcNow:yyyyMMdd-HHmmss}";

        var memories = await GetMemoriesForAgentAsync(agentName, cancellationToken);
        var now = DateTime.UtcNow;
        var memory = new DynamicMemory
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 6),
            Title = title,
            Content = content,
            Created = now,
            LastUpdated = now,
            LastAccessed = now
        };

        memories.Add(memory);
        await SaveMemoriesAsync(agentName, memories, cancellationToken);
        InvokeInvalidation();

        return memory.Id;
    }

    /// <inheritdoc />
    public override async Task<ContentData?> GetAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default)
    {
        // If scope is provided, search only within that agent's memories
        if (scope != null)
        {
            var memories = await GetMemoriesForAgentAsync(scope, cancellationToken);
            var memory = memories.FirstOrDefault(m => m.Id == contentId);
            if (memory != null)
            {
                memory.LastAccessed = DateTime.UtcNow;
                await SaveMemoriesAsync(scope, memories, cancellationToken);
                return MapToContentData(memory);
            }
            return null;
        }

        // If scope is null, search across ALL agent files
        if (!Directory.Exists(_storageDirectory))
        {
            return null;
        }

        foreach (var file in Directory.GetFiles(_storageDirectory, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var memories = await JsonSerializer.DeserializeAsync<List<DynamicMemory>>(stream, cancellationToken: cancellationToken)
                    ?? new List<DynamicMemory>();

                var memory = memories.FirstOrDefault(m => m.Id == contentId);
                if (memory != null)
                {
                    // Update last accessed
                    memory.LastAccessed = DateTime.UtcNow;

                    // Save updated access time back to file
                    var agentName = Path.GetFileNameWithoutExtension(file);
                    await SaveMemoriesAsync(agentName, memories, cancellationToken);

                    return MapToContentData(memory);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read memories from {File} during GetAsync", file);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public override async Task DeleteAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default)
    {
        // If scope is provided, delete only within that agent's memories
        if (scope != null)
        {
            var memories = await GetMemoriesForAgentAsync(scope, cancellationToken);
            var removed = memories.RemoveAll(m => m.Id == contentId);
            if (removed > 0)
            {
                await SaveMemoriesAsync(scope, memories, cancellationToken);
                InvokeInvalidation();
            }
            return;
        }

        // If scope is null, search across ALL agent files and delete
        if (!Directory.Exists(_storageDirectory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_storageDirectory, "*.json"))
        {
            try
            {
                var agentName = Path.GetFileNameWithoutExtension(file);
                var memories = await GetMemoriesForAgentAsync(agentName, cancellationToken);

                var removed = memories.RemoveAll(m => m.Id == contentId);
                if (removed > 0)
                {
                    await SaveMemoriesAsync(agentName, memories, cancellationToken);
                    InvokeInvalidation();
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete memory from {File}", file);
            }
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ContentInfo>> QueryAsync(
        string? scope = null,
        ContentQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var allMemories = new List<DynamicMemory>();

        // If scope is provided, query only within that agent's memories
        if (scope != null)
        {
            allMemories = await GetMemoriesForAgentAsync(scope, cancellationToken);
        }
        else
        {
            // If scope is null, query across ALL agent files
            if (Directory.Exists(_storageDirectory))
            {
                foreach (var file in Directory.GetFiles(_storageDirectory, "*.json"))
                {
                    try
                    {
                        using var stream = File.OpenRead(file);
                        var memories = await JsonSerializer.DeserializeAsync<List<DynamicMemory>>(stream, cancellationToken: cancellationToken)
                            ?? new List<DynamicMemory>();
                        allMemories.AddRange(memories);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to read memories from {File} during QueryAsync", file);
                    }
                }
            }
        }

        // Apply filters
        IEnumerable<DynamicMemory> filteredMemories = allMemories;

        if (query?.ContentType != null)
        {
            filteredMemories = filteredMemories.Where(m =>
                "text/plain".Equals(query.ContentType, StringComparison.OrdinalIgnoreCase));
        }

        if (query?.CreatedAfter != null)
        {
            filteredMemories = filteredMemories.Where(m => m.Created >= query.CreatedAfter.Value);
        }

        // Map to ContentInfo
        var results = filteredMemories.Select(MapToContentInfo);

        // Apply limit
        if (query?.Limit != null)
        {
            results = results.Take(query.Limit.Value);
        }

        return results.ToList();
    }

    /// <summary>
    /// Maps DynamicMemory to ContentData.
    /// </summary>
    private static ContentData MapToContentData(DynamicMemory memory)
    {
        var dataBytes = Encoding.UTF8.GetBytes(memory.Content);
        return new ContentData
        {
            Id = memory.Id,
            Data = dataBytes,
            ContentType = "text/plain",
            Info = MapToContentInfo(memory)
        };
    }

    /// <summary>
    /// Maps DynamicMemory to ContentInfo.
    /// </summary>
    private static ContentInfo MapToContentInfo(DynamicMemory memory)
    {
        return new ContentInfo
        {
            Id = memory.Id,
            Name = memory.Title,
            ContentType = "text/plain",
            SizeBytes = Encoding.UTF8.GetByteCount(memory.Content),
            CreatedAt = memory.Created,
            LastModified = memory.LastUpdated,
            LastAccessed = memory.LastAccessed,
            Origin = ContentSource.Agent, // Memories are agent-created
            ExtendedMetadata = new Dictionary<string, object>
            {
                ["title"] = memory.Title
            }
        };
    }
}

#pragma warning restore RS1035, IL2026, IL3050
