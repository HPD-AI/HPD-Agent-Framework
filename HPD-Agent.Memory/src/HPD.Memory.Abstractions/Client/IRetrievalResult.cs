// Copyright (c) Einstein Essibu. All rights reserved.
// Result contracts for IMemoryClient retrieval operations

namespace HPDAgent.Memory.Abstractions.Client;

/// <summary>
/// Result of a knowledge retrieval operation.
/// </summary>
public interface IRetrievalResult
{
    /// <summary>
    /// Original query that was executed.
    /// </summary>
    string Query { get; }

    /// <summary>
    /// Retrieved items, ranked by relevance.
    /// </summary>
    IReadOnlyList<IRetrievedItem> Items { get; }

    /// <summary>
    /// Implementation-specific metadata about the retrieval.
    /// Examples:
    /// - "total_results": int (before MaxResults limit)
    /// - "query_rewritten_to": string (if query was rewritten)
    /// - "search_duration_ms": int
    /// - "retrieval_strategy": string (e.g., "vector", "graph", "hybrid")
    /// - "graph_hops": int (for GraphRAG)
    /// - "entities_traversed": int (for GraphRAG)
    /// - "reranked": bool
    /// </summary>
    IReadOnlyDictionary<string, object> Metadata { get; }
}

/// <summary>
/// A single retrieved item (text chunk, entity, image, etc.).
/// </summary>
public interface IRetrievedItem
{
    /// <summary>
    /// Content of the item.
    /// - For text chunks: The actual text
    /// - For entities: Entity name/description
    /// - For images: Image description or base64 data
    /// - For relationships: Relationship description
    /// </summary>
    string Content { get; }

    /// <summary>
    /// Relevance score (0.0 to 1.0).
    /// Higher = more relevant to the query.
    /// </summary>
    double Score { get; }

    /// <summary>
    /// Type of content in this item.
    /// Standard types (conventions):
    /// - "text_chunk": Plain text chunk from document
    /// - "entity": Graph entity
    /// - "relationship": Graph relationship
    /// - "image": Image content
    /// - "table": Structured table data
    /// - "code": Code snippet
    ///
    /// Implementations can define custom types.
    /// </summary>
    string ContentType { get; }

    /// <summary>
    /// Metadata about this item.
    /// Common metadata (conventions):
    /// - "document_id": string
    /// - "document_name": string (file name)
    /// - "chunk_id": string
    /// - "partition_number": int
    /// - "page_number": int
    /// - "created_at": DateTimeOffset
    /// - "entity_type": string (for entities: "Person", "Organization", etc.)
    /// - "relationship_type": string (for relationships: "cites", "authored_by", etc.)
    /// - "source_location": string (URL, file path, etc.)
    /// </summary>
    IReadOnlyDictionary<string, object> Metadata { get; }
}

/// <summary>
/// Default implementation of IRetrievalResult.
/// </summary>
public record RetrievalResult : IRetrievalResult
{
    public required string Query { get; init; }
    public required IReadOnlyList<IRetrievedItem> Items { get; init; }
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}

/// <summary>
/// Default implementation of IRetrievedItem.
/// </summary>
public record RetrievedItem : IRetrievedItem
{
    public required string Content { get; init; }
    public required double Score { get; init; }
    public required string ContentType { get; init; }
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();

    /// <summary>
    /// Create a text chunk item.
    /// </summary>
    public static RetrievedItem CreateTextChunk(
        string content,
        double score,
        string documentId,
        string? documentName = null,
        string? chunkId = null,
        int? partitionNumber = null,
        IReadOnlyDictionary<string, object>? additionalMetadata = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["document_id"] = documentId
        };

        if (documentName != null)
            metadata["document_name"] = documentName;
        if (chunkId != null)
            metadata["chunk_id"] = chunkId;
        if (partitionNumber != null)
            metadata["partition_number"] = partitionNumber;

        if (additionalMetadata != null)
        {
            foreach (var kvp in additionalMetadata)
                metadata[kvp.Key] = kvp.Value;
        }

        return new RetrievedItem
        {
            Content = content,
            Score = score,
            ContentType = "text_chunk",
            Metadata = metadata
        };
    }

    /// <summary>
    /// Create a graph entity item.
    /// </summary>
    public static RetrievedItem CreateEntity(
        string content,
        double score,
        string entityType,
        string? entityId = null,
        IReadOnlyDictionary<string, object>? additionalMetadata = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["entity_type"] = entityType
        };

        if (entityId != null)
            metadata["entity_id"] = entityId;

        if (additionalMetadata != null)
        {
            foreach (var kvp in additionalMetadata)
                metadata[kvp.Key] = kvp.Value;
        }

        return new RetrievedItem
        {
            Content = content,
            Score = score,
            ContentType = "entity",
            Metadata = metadata
        };
    }
}
