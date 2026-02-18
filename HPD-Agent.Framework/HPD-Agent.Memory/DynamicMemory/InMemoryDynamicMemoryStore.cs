using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HPD.Agent.Memory;

/// <summary>
/// In-memory implementation of DynamicMemoryStore.
/// Stores all memories in a dictionary for fast access.
/// Suitable for development, testing, or scenarios where persistence is not required.
/// </summary>
public class InMemoryDynamicMemoryStore : DynamicMemoryStore
{
    private readonly Dictionary<string, List<DynamicMemory>> _memories = new();
    private readonly List<Action> _invalidationCallbacks = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new in-memory dynamic memory store.
    /// </summary>
    public InMemoryDynamicMemoryStore()
    {
    }

    /// <summary>
    /// Creates an in-memory store with pre-loaded memories (for deserialization).
    /// </summary>
    private InMemoryDynamicMemoryStore(Dictionary<string, List<DynamicMemory>> memories)
    {
        _memories = memories;
    }

    public override Task<DynamicMemory> UpdateMemoryAsync(string agentName, string memoryId, string title, string content, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_memories.TryGetValue(agentName, out var memories))
            {
                throw new InvalidOperationException($"No memories found for agent '{agentName}'");
            }

            var memory = memories.FirstOrDefault(m => m.Id == memoryId);
            if (memory == null)
            {
                throw new InvalidOperationException($"Memory with ID '{memoryId}' not found for agent '{agentName}'");
            }

            memory.Title = title;
            memory.Content = content;
            memory.LastUpdated = DateTime.UtcNow;
            memory.LastAccessed = DateTime.UtcNow;

            InvokeInvalidationCallbacks();

            return Task.FromResult(memory);
        }
    }

    public override void RegisterInvalidationCallback(Action callback)
    {
        lock (_lock)
        {
            _invalidationCallbacks.Add(callback);
        }
    }

    public override DynamicMemoryStoreSnapshot SerializeToSnapshot()
    {
        lock (_lock)
        {
            // Deep copy to avoid modification during serialization
            var memoriesCopy = _memories.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToList()
            );

            return new DynamicMemoryStoreSnapshot
            {
                StoreType = DynamicMemoryStoreType.InMemory,
                Memories = memoriesCopy
            };
        }
    }

    /// <summary>
    /// Deserialize an in-memory store from a snapshot.
    /// </summary>
    internal new static InMemoryDynamicMemoryStore Deserialize(DynamicMemoryStoreSnapshot snapshot)
    {
        // Deep copy from snapshot
        var memories = snapshot.Memories.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToList()
        );

        return new InMemoryDynamicMemoryStore(memories);
    }

    private void InvokeInvalidationCallbacks()
    {
        foreach (var callback in _invalidationCallbacks)
        {
            try
            {
                callback();
            }
            catch
            {
                // Ignore callback errors
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // IContentStore Implementation (V2)
    // ═══════════════════════════════════════════════════════════════════
    // scope = agentName for DynamicMemoryStore
    // If scope is null in QueryAsync, query across ALL agents

    /// <inheritdoc />
    public override Task<string> PutAsync(
        string? scope,
        byte[] data,
        string contentType,
        ContentMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var agentName = scope ?? throw new ArgumentNullException(nameof(scope), "Scope (agentName) is required for DynamicMemoryStore.PutAsync");

        var content = Encoding.UTF8.GetString(data);
        var title = metadata?.Name ?? $"Memory {DateTime.UtcNow:yyyyMMdd-HHmmss}";

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

        lock (_lock)
        {
            if (!_memories.TryGetValue(agentName, out var memories))
            {
                memories = new List<DynamicMemory>();
                _memories[agentName] = memories;
            }

            memories.Add(memory);
            InvokeInvalidationCallbacks();

            return Task.FromResult(memory.Id);
        }
    }

    /// <inheritdoc />
    public override Task<ContentData?> GetAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // If scope is provided, search only within that agent's memories
            if (scope != null)
            {
                if (_memories.TryGetValue(scope, out var memories))
                {
                    var memory = memories.FirstOrDefault(m => m.Id == contentId);
                    if (memory != null)
                    {
                        memory.LastAccessed = DateTime.UtcNow;
                        return Task.FromResult<ContentData?>(MapToContentData(memory));
                    }
                }
                return Task.FromResult<ContentData?>(null);
            }

            // If scope is null, search across ALL agents
            foreach (var (agentName, memories) in _memories)
            {
                var memory = memories.FirstOrDefault(m => m.Id == contentId);
                if (memory != null)
                {
                    memory.LastAccessed = DateTime.UtcNow;
                    return Task.FromResult<ContentData?>(MapToContentData(memory));
                }
            }

            return Task.FromResult<ContentData?>(null);
        }
    }

    /// <inheritdoc />
    public override Task DeleteAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // If scope is provided, delete only within that agent's memories
            if (scope != null)
            {
                if (_memories.TryGetValue(scope, out var memories))
                {
                    var removed = memories.RemoveAll(m => m.Id == contentId);
                    if (removed > 0)
                    {
                        InvokeInvalidationCallbacks();
                    }
                }
                return Task.CompletedTask;
            }

            // If scope is null, search across ALL agents and delete
            foreach (var (agentName, memories) in _memories)
            {
                var removed = memories.RemoveAll(m => m.Id == contentId);
                if (removed > 0)
                {
                    InvokeInvalidationCallbacks();
                    break;
                }
            }

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<ContentInfo>> QueryAsync(
        string? scope = null,
        ContentQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // If scope is provided, query only within that agent's memories
            IEnumerable<DynamicMemory> allMemories;

            if (scope != null)
            {
                allMemories = _memories.TryGetValue(scope, out var memories)
                    ? memories
                    : Enumerable.Empty<DynamicMemory>();
            }
            else
            {
                // If scope is null, query across ALL agents
                allMemories = _memories.Values.SelectMany(mems => mems);
            }

            // Apply filters
            if (query?.ContentType != null)
            {
                allMemories = allMemories.Where(m =>
                    "text/plain".Equals(query.ContentType, StringComparison.OrdinalIgnoreCase));
            }

            if (query?.CreatedAfter != null)
            {
                allMemories = allMemories.Where(m => m.Created >= query.CreatedAfter.Value);
            }

            // Map to ContentInfo
            var results = allMemories.Select(MapToContentInfo);

            // Apply limit
            if (query?.Limit != null)
            {
                results = results.Take(query.Limit.Value);
            }

            return Task.FromResult<IReadOnlyList<ContentInfo>>(results.ToList());
        }
    }

    /// <summary>
    /// Maps DynamicMemory to ContentData.
    /// </summary>
    private static ContentData MapToContentData(DynamicMemory memory)
    {
        var data = Encoding.UTF8.GetBytes(memory.Content);
        return new ContentData
        {
            Id = memory.Id,
            Data = data,
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
