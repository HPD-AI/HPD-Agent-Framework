using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Abstract base class for storing and managing dynamic memories (agent's working memory).
/// Follows the same pattern as ConversationThread/AgentThread.
/// Implementations can store memories in-memory, JSON files, SQL, Redis, etc.
/// </summary>
public abstract class DynamicMemoryStore
{
    /// <summary>
    /// Asynchronously retrieves all memories for a specific agent.
    /// </summary>
    /// <param name="agentName">The agent name to scope memories to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of memories ordered by LastAccessed (most recent first)</returns>
    public abstract Task<List<DynamicMemory>> GetMemoriesAsync(string agentName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves a specific memory by ID.
    /// </summary>
    /// <param name="agentName">The agent name to scope memories to</param>
    /// <param name="memoryId">The memory ID to retrieve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The memory if found, null otherwise</returns>
    public abstract Task<DynamicMemory?> GetMemoryAsync(string agentName, string memoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new memory for the agent.
    /// </summary>
    /// <param name="agentName">The agent name to scope memories to</param>
    /// <param name="title">Memory title</param>
    /// <param name="content">Memory content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created memory</returns>
    public abstract Task<DynamicMemory> CreateMemoryAsync(string agentName, string title, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing memory.
    /// </summary>
    /// <param name="agentName">The agent name to scope memories to</param>
    /// <param name="memoryId">The memory ID to update</param>
    /// <param name="title">New memory title</param>
    /// <param name="content">New memory content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated memory</returns>
    public abstract Task<DynamicMemory> UpdateMemoryAsync(string agentName, string memoryId, string title, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a memory.
    /// </summary>
    /// <param name="agentName">The agent name to scope memories to</param>
    /// <param name="memoryId">The memory ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public abstract Task DeleteMemoryAsync(string agentName, string memoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a callback to be invoked when the store's data changes.
    /// Used for cache invalidation in filters.
    /// </summary>
    /// <param name="callback">Callback to invoke on data changes</param>
    public abstract void RegisterInvalidationCallback(Action callback);

    /// <summary>
    /// Serialize this store to a snapshot for persistence.
    /// </summary>
    /// <returns>Serializable snapshot of the store's state</returns>
    public abstract DynamicMemoryStoreSnapshot SerializeToSnapshot();

    /// <summary>
    /// Deserialize a store from a snapshot.
    /// Factory method pattern - returns the appropriate implementation based on snapshot type.
    /// </summary>
    /// <param name="snapshot">The snapshot to deserialize from</param>
    /// <returns>Restored memory store</returns>
    public static DynamicMemoryStore Deserialize(DynamicMemoryStoreSnapshot snapshot)
    {
        return snapshot.StoreType switch
        {
            DynamicMemoryStoreType.InMemory => InMemoryDynamicMemoryStore.Deserialize(snapshot),
            DynamicMemoryStoreType.JsonFile => JsonDynamicMemoryStore.Deserialize(snapshot),
            _ => throw new NotSupportedException($"Store type {snapshot.StoreType} is not supported for deserialization.")
        };
    }
}

/// <summary>
/// Enum representing different types of dynamic memory stores.
/// Used for polymorphic deserialization.
/// </summary>
public enum DynamicMemoryStoreType
{
    InMemory,
    JsonFile,
    Sql,
    Redis,
    Custom
}

/// <summary>
/// Serializable snapshot of a DynamicMemoryStore for persistence.
/// Similar to ConversationThreadSnapshot pattern.
/// </summary>
public record DynamicMemoryStoreSnapshot
{
    /// <summary>Type of store for polymorphic deserialization</summary>
    public required DynamicMemoryStoreType StoreType { get; init; }

    /// <summary>All memories, keyed by agent name</summary>
    public required Dictionary<string, List<DynamicMemory>> Memories { get; init; }

    /// <summary>Optional configuration data specific to the store implementation</summary>
    public Dictionary<string, object>? Configuration { get; init; }
}
