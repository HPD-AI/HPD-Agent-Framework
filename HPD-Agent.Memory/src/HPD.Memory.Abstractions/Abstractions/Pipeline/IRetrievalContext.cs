// Copyright (c) Einstein Essibu. All rights reserved.
// Inspired by Microsoft Kernel Memory, enhanced with modern patterns.

using HPD.Pipeline;

namespace HPDAgent.Memory.Abstractions.Pipeline;

/// <summary>
/// Marker interface for retrieval pipeline contexts.
/// Retrieval pipelines search and retrieve information from memory systems.
/// Use this interface for type constraints when creating retrieval-specific handlers.
/// </summary>
/// <remarks>
/// This interface extends IPipelineContext but doesn't add required members.
/// Concrete implementations will add retrieval-specific properties like:
/// - Search query
/// - Rewritten queries (for query expansion)
/// - Search results
/// - Filters (access control, metadata)
/// - Ranking/scoring information
///
/// Example:
/// <code>
/// public class SemanticSearchContext : IRetrievalContext
/// {
///     // IPipelineContext implementation...
///
///     // Retrieval-specific
///     public string Query { get; set; } = "";
///     public string? RewrittenQuery { get; set; }
///     public List&lt;SearchResult&gt; Results { get; set; } = new();
///     public Dictionary&lt;string, object&gt; Filters { get; set; } = new();
/// }
/// </code>
/// </remarks>
public interface IRetrievalContext : IPipelineContext
{
    // Marker interface - no additional required members
    // Concrete implementations add retrieval-specific properties
}
