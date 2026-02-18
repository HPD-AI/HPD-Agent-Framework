using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HPD.Agent.Memory;

/// <summary>
/// Abstract base class for storing and managing static memory (agent's read-only knowledge/expertise).
/// Follows the same pattern as ConversationThread/AgentThread and DynamicMemoryStore.
/// Implementations can store knowledge in-memory, JSON files, databases, etc.
/// </summary>
/// <remarks>
/// <para><b>IContentStore Integration (V2):</b></para>
/// <para>
/// StaticMemoryStore extends IContentStore for unified content operations. All IContentStore
/// methods (Put/Get/Delete/Query) use the scope parameter (agentName) for per-agent isolation.
/// </para>
/// <para><b>Unique Features:</b></para>
/// <para>
/// Beyond the base IContentStore methods, StaticMemoryStore provides specialized features:
/// - GetCombinedKnowledgeTextAsync: Combines all documents into a single text (for context injection)
/// </para>
/// <para><b>Example Usage:</b></para>
/// <code>
/// // Store knowledge document
/// var docId = await staticMemory.PutAsync(
///     scope: "agent1",
///     data: Encoding.UTF8.GetBytes(markdownText),
///     contentType: "text/markdown",
///     metadata: new ContentMetadata { Name = "API Docs" });
///
/// // Query agent's knowledge
/// var docs = await staticMemory.QueryAsync(scope: "agent1");
///
/// // Get combined knowledge text for context injection
/// var knowledge = await staticMemory.GetCombinedKnowledgeTextAsync("agent1", maxTokens: 4000);
/// </code>
/// </remarks>
public abstract class StaticMemoryStore : IContentStore
{
    /// <summary>
    /// Gets combined text from all knowledge documents up to a token limit.
    /// Used for FullTextInjection strategy.
    /// </summary>
    /// <param name="agentName">The agent name to Collapse knowledge to</param>
    /// <param name="maxTokens">Maximum tokens to include</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Combined knowledge text</returns>
    public abstract Task<string> GetCombinedKnowledgeTextAsync(string agentName, int maxTokens, CancellationToken cancellationToken = default);

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
    public abstract StaticMemoryStoreSnapshot SerializeToSnapshot();

    // ═══════════════════════════════════════════════════════════════════
    // IContentStore Implementation
    // ═══════════════════════════════════════════════════════════════════
    // Note: scope parameter = agentName for StaticMemoryStore

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
    public static StaticMemoryStore Deserialize(StaticMemoryStoreSnapshot snapshot)
    {
        return snapshot.StoreType switch
        {
            StaticMemoryStoreType.InMemory => InMemoryStaticMemoryStore.Deserialize(snapshot),
            StaticMemoryStoreType.JsonFile => JsonStaticMemoryStore.Deserialize(snapshot),
            _ => throw new NotSupportedException($"Store type {snapshot.StoreType} is not supported for deserialization.")
        };
    }
}

/// <summary>
/// Enum representing different types of static memory stores.
/// Used for polymorphic deserialization.
/// </summary>
public enum StaticMemoryStoreType
{
    InMemory,
    JsonFile,
    Sql,
    VectorDatabase,
    Custom
}

/// <summary>
/// Serializable snapshot of a StaticMemoryStore for persistence.
/// Similar to ConversationThreadSnapshot and DynamicMemoryStoreSnapshot patterns.
/// </summary>
public record StaticMemoryStoreSnapshot
{
    /// <summary>Type of store for polymorphic deserialization</summary>
    public required StaticMemoryStoreType StoreType { get; init; }

    /// <summary>All knowledge documents, keyed by agent name</summary>
    public required Dictionary<string, List<StaticMemoryDocument>> Documents { get; init; }

    /// <summary>Optional configuration data specific to the store implementation</summary>
    public Dictionary<string, object>? Configuration { get; init; }
}
