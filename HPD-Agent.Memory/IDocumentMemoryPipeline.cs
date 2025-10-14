using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HPD_Agent.Memory;

/// <summary>
/// Interface for document ingestion and retrieval pipelines.
/// This abstraction allows HPD-Agent to integrate with any document memory system
/// without depending on specific pipeline implementations.
/// </summary>
public interface IDocumentMemoryPipeline
{
    /// <summary>
    /// Ingests a document into the memory system.
    /// </summary>
    /// <param name="documentPath">Path to the document file</param>
    /// <param name="index">The index/collection to store the document in</param>
    /// <param name="metadata">Optional metadata to associate with the document</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the ingestion operation</returns>
    Task<IngestionResult> IngestDocumentAsync(
        string documentPath,
        string index,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ingests raw text content into the memory system.
    /// </summary>
    /// <param name="content">Text content to ingest</param>
    /// <param name="index">The index/collection to store the content in</param>
    /// <param name="documentId">Unique identifier for this content</param>
    /// <param name="metadata">Optional metadata to associate with the content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the ingestion operation</returns>
    Task<IngestionResult> IngestTextAsync(
        string content,
        string index,
        string documentId,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves relevant content from the memory system based on a query.
    /// </summary>
    /// <param name="query">The search query</param>
    /// <param name="index">The index/collection to search in</param>
    /// <param name="maxResults">Maximum number of results to return</param>
    /// <param name="minRelevanceScore">Minimum relevance score threshold (0.0 to 1.0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Retrieved content with relevance scores</returns>
    Task<RetrievalResult> RetrieveAsync(
        string query,
        string index,
        int maxResults = 10,
        double minRelevanceScore = 0.0,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a document ingestion operation.
/// </summary>
public class IngestionResult
{
    /// <summary>Whether the ingestion was successful</summary>
    public bool Success { get; set; }

    /// <summary>Unique identifier for the ingested document</summary>
    public string? DocumentId { get; set; }

    /// <summary>Number of chunks created from the document</summary>
    public int ChunksCreated { get; set; }

    /// <summary>Error message if ingestion failed</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Additional metadata about the ingestion</summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Result of a retrieval operation.
/// </summary>
public class RetrievalResult
{
    /// <summary>Whether the retrieval was successful</summary>
    public bool Success { get; set; }

    /// <summary>Retrieved content items with relevance scores</summary>
    public List<RetrievedContent> Results { get; set; } = new();

    /// <summary>Error message if retrieval failed</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Query that was executed</summary>
    public string Query { get; set; } = string.Empty;
}

/// <summary>
/// A single piece of retrieved content with its relevance score.
/// </summary>
public class RetrievedContent
{
    /// <summary>The content text</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Relevance score (0.0 to 1.0)</summary>
    public double RelevanceScore { get; set; }

    /// <summary>Source document identifier</summary>
    public string? DocumentId { get; set; }

    /// <summary>Metadata associated with this content</summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}
