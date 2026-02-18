using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace HPD.Agent.Memory;
/// <summary>
/// Abstract base class for storing and managing dynamic memories (agent's working memory).
/// Follows the same pattern as ConversationThread/AgentThread and StaticMemoryStore.
/// Implementations can store memories in-memory, JSON files, SQL, Redis, etc.
/// </summary>
/// <remarks>
/// <para><b>IContentStore Integration (V2):</b></para>
/// <para>
/// DynamicMemoryStore extends IContentStore for unified content operations. All IContentStore
/// methods (Put/Get/Delete/Query) use the scope parameter (agentName) for per-agent isolation.
/// </para>
/// <para><b>Unique Features:</b></para>
/// <para>
/// Beyond the base IContentStore methods, DynamicMemoryStore provides:
/// - UpdateMemoryAsync: Updates memory with structured title + content (avoids full content replacement)
/// </para>
/// <para><b>Example Usage:</b></para>
/// <code>
/// // Create memory
/// var memoryData = Encoding.UTF8.GetBytes($"Title: {title}\n\n{content}");
/// var memoryId = await dynamicMemory.PutAsync(
///     scope: "agent1",
///     data: memoryData,
///     contentType: "text/plain",
///     metadata: new ContentMetadata { Name = title });
///
/// // Update memory (specialized method)
/// await dynamicMemory.UpdateMemoryAsync("agent1", memoryId, "New Title", "New Content");
///
/// // Query agent's memories
/// var memories = await dynamicMemory.QueryAsync(scope: "agent1");
/// </code>
/// </remarks>
public abstract class DynamicMemoryStore : IContentStore
{
    /// <summary>
    /// Updates an existing memory with structured title + content.
    /// This is a specialized operation that avoids full content replacement.
    /// </summary>
    /// <param name="agentName">The agent name to Collapse memories to</param>
    /// <param name="memoryId">The memory ID to update</param>
    /// <param name="title">New memory title</param>
    /// <param name="content">New memory content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated memory</returns>
    public abstract Task<DynamicMemory> UpdateMemoryAsync(string agentName, string memoryId, string title, string content, CancellationToken cancellationToken = default);

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

    // ═══════════════════════════════════════════════════════════════════
    // IContentStore Implementation
    // ═══════════════════════════════════════════════════════════════════
    // Note: scope parameter = agentName for DynamicMemoryStore

    /// <inheritdoc />
    public abstract Task<string> PutAsync(
        string? scope,
        byte[] data,
        string contentType,
        ContentMetadata? metadata = null,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<ContentData?> GetAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task DeleteAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<ContentInfo>> QueryAsync(
        string? scope = null,
        ContentQuery? query = null,
        CancellationToken cancellationToken = default);

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
