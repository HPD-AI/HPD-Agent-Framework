using System;
using System.Collections.Generic;
using System.Linq;
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

    public override Task<List<DynamicMemory>> GetMemoriesAsync(string agentName, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_memories.TryGetValue(agentName, out var memories))
            {
                return Task.FromResult(new List<DynamicMemory>());
            }

            // Return copy, sorted by LastAccessed (most recent first)
            return Task.FromResult(memories.OrderByDescending(m => m.LastAccessed).ToList());
        }
    }

    public override Task<DynamicMemory?> GetMemoryAsync(string agentName, string memoryId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_memories.TryGetValue(agentName, out var memories))
            {
                return Task.FromResult<DynamicMemory?>(null);
            }

            var memory = memories.FirstOrDefault(m => m.Id == memoryId);
            return Task.FromResult(memory);
        }
    }

    public override Task<DynamicMemory> CreateMemoryAsync(string agentName, string title, string content, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
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

            if (!_memories.TryGetValue(agentName, out var memories))
            {
                memories = new List<DynamicMemory>();
                _memories[agentName] = memories;
            }

            memories.Add(memory);
            InvokeInvalidationCallbacks();

            return Task.FromResult(memory);
        }
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

    public override Task DeleteMemoryAsync(string agentName, string memoryId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_memories.TryGetValue(agentName, out var memories))
            {
                memories.RemoveAll(m => m.Id == memoryId);
                InvokeInvalidationCallbacks();
            }

            return Task.CompletedTask;
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
}
