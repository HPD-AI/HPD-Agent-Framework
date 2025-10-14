using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Abstract base class for storing and managing static memory (agent's read-only knowledge/expertise).
/// Follows the same pattern as ConversationThread/AgentThread and DynamicMemoryStore.
/// Implementations can store knowledge in-memory, JSON files, databases, etc.
/// </summary>
public abstract class StaticMemoryStore
{
    /// <summary>
    /// Asynchronously retrieves all knowledge documents for a specific agent.
    /// </summary>
    /// <param name="agentName">The agent name to scope knowledge to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of documents ordered by LastAccessed (most recent first)</returns>
    public abstract Task<List<StaticMemoryDocument>> GetDocumentsAsync(string agentName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves a specific document by ID.
    /// </summary>
    /// <param name="agentName">The agent name to scope knowledge to</param>
    /// <param name="documentId">The document ID to retrieve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The document if found, null otherwise</returns>
    public abstract Task<StaticMemoryDocument?> GetDocumentAsync(string agentName, string documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new knowledge document for the agent.
    /// </summary>
    /// <param name="agentName">The agent name to scope knowledge to</param>
    /// <param name="document">The document to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The added document</returns>
    public abstract Task<StaticMemoryDocument> AddDocumentAsync(string agentName, StaticMemoryDocument document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a knowledge document.
    /// </summary>
    /// <param name="agentName">The agent name to scope knowledge to</param>
    /// <param name="documentId">The document ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public abstract Task DeleteDocumentAsync(string agentName, string documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets combined text from all knowledge documents up to a token limit.
    /// Used for FullTextInjection strategy.
    /// </summary>
    /// <param name="agentName">The agent name to scope knowledge to</param>
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
