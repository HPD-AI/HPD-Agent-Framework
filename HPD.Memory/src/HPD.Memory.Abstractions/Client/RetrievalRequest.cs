// Copyright (c) Einstein Essibu. All rights reserved.
// Request contracts for IMemoryClient retrieval operations

namespace HPDAgent.Memory.Abstractions.Client;

/// <summary>
/// Request to retrieve relevant knowledge/documents for a query.
/// </summary>
public class RetrievalRequest
{
    /// <summary>
    /// The search query.
    /// Examples: "What is RAG?", "How do I configure authentication?", "Show me recent sales reports"
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Index/collection to search in.
    /// If null, searches the default index.
    /// </summary>
    public string? Index { get; init; }

    /// <summary>
    /// Maximum number of items to retrieve.
    /// Default: 10
    /// </summary>
    public int MaxResults { get; init; } = 10;

    /// <summary>
    /// Minimum relevance score (0.0 to 1.0).
    /// Items with score below this threshold are filtered out.
    /// Default: null (no filtering)
    /// </summary>
    public double? MinRelevanceScore { get; init; }

    /// <summary>
    /// Filter documents by metadata/tags.
    /// Examples:
    /// - Filter by tag: { "category": ["technical"] }
    /// - Filter by date range: { "created_after": "2024-01-01" }
    /// - Complex filter: { "department": ["engineering"], "status": ["published"] }
    /// </summary>
    public MemoryFilter? Filter { get; init; }

    /// <summary>
    /// Implementation-specific options.
    /// Common options (conventions, not required):
    ///
    /// Query enhancement:
    /// - "query_rewrite": bool (rewrite query for better retrieval)
    /// - "multi_query": bool (generate multiple query variations)
    /// - "hyde": bool (generate hypothetical document for query)
    ///
    /// Graph-specific (GraphRAG):
    /// - "max_hops": int (maximum graph traversal depth, e.g., 2)
    /// - "relationship_types": string[] (filter by relationship types)
    /// - "include_entities": bool (include graph entities in results)
    /// - "include_relationships": bool (include relationships in results)
    ///
    /// Ranking/Reranking:
    /// - "rerank": bool (use reranking model)
    /// - "rerank_model": string (reranking model to use)
    /// - "diversity": bool (enforce diversity in results)
    ///
    /// Agentic:
    /// - "max_iterations": int (for iterative retrieval)
    /// - "tools": string[] (tools available to agent)
    /// </summary>
    public Dictionary<string, object> Options { get; init; } = new();
}

/// <summary>
/// Filter for memory retrieval based on metadata and tags.
/// </summary>
public class MemoryFilter
{
    /// <summary>
    /// Filter by document tags.
    /// Only documents with ALL specified tags will be included (AND logic).
    /// Example: { "category": ["technical"], "status": ["published"] }
    /// → Only documents tagged with category=technical AND status=published
    /// </summary>
    public Dictionary<string, List<string>>? Tags { get; init; }

    /// <summary>
    /// Filter by document IDs.
    /// If specified, only documents in this list are searched.
    /// </summary>
    public List<string>? DocumentIds { get; init; }

    /// <summary>
    /// Filter by date range (created date).
    /// </summary>
    public DateTimeOffset? CreatedAfter { get; init; }

    /// <summary>
    /// Filter by date range (created date).
    /// </summary>
    public DateTimeOffset? CreatedBefore { get; init; }

    /// <summary>
    /// Custom filters (implementation-specific).
    /// Examples:
    /// - { "file_type": "pdf" }
    /// - { "author": "John Doe" }
    /// - { "department": "engineering" }
    /// </summary>
    public Dictionary<string, object>? CustomFilters { get; init; }
}
