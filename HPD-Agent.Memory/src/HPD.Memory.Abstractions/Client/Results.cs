// Copyright (c) Einstein Essibu. All rights reserved.
// Result interfaces v2: Fixed to use generic artifact counts instead of specific properties

namespace HPDAgent.Memory.Abstractions.Client;

// ========================================
// Ingestion Results
// ========================================

/// <summary>
/// Result of a document ingestion operation.
/// V2: Uses ArtifactCounts dictionary instead of specific properties for flexibility.
/// </summary>
public interface IIngestionResult
{
    /// <summary>
    /// Document ID (assigned or provided).
    /// </summary>
    string DocumentId { get; }

    /// <summary>
    /// Index/collection where the document was ingested.
    /// </summary>
    string Index { get; }

    /// <summary>
    /// Whether ingestion succeeded.
    /// </summary>
    bool Success { get; }

    /// <summary>
    /// Error message if ingestion failed.
    /// </summary>
    string? ErrorMessage { get; }

    /// <summary>
    /// Counts of artifacts produced during ingestion.
    /// Standard keys (conventions):
    /// - "chunks": Number of text chunks created
    /// - "embeddings": Number of embeddings generated
    /// - "entities": Number of entities extracted (GraphRAG)
    /// - "relationships": Number of relationships extracted (GraphRAG)
    /// - "images": Number of images extracted (Multi-modal)
    /// - "summaries": Number of summaries generated
    /// - "tables": Number of tables extracted
    ///
    /// Implementations can add custom keys for their specific artifacts.
    /// </summary>
    IReadOnlyDictionary<string, int> ArtifactCounts { get; }

    /// <summary>
    /// Implementation-specific metadata about the ingestion.
    /// Examples:
    /// - "pipeline_id": string
    /// - "processing_time_ms": int
    /// - "extraction_method": string
    /// - "total_tokens": int
    /// - "model": string
    /// </summary>
    IReadOnlyDictionary<string, object> Metadata { get; }
}

/// <summary>
/// Result of a batch ingestion operation.
/// </summary>
public interface IBatchIngestionResult
{
    /// <summary>
    /// Individual results for each document in the batch.
    /// Includes both successful and failed ingestions.
    /// </summary>
    IReadOnlyList<IIngestionResult> Results { get; }

    /// <summary>
    /// Number of successful ingestions.
    /// </summary>
    int SuccessCount { get; }

    /// <summary>
    /// Number of failed ingestions.
    /// </summary>
    int FailureCount { get; }

    /// <summary>
    /// Total number of artifacts produced across all documents.
    /// Sum of all ArtifactCounts from successful results.
    /// </summary>
    IReadOnlyDictionary<string, int> TotalArtifactCounts { get; }

    /// <summary>
    /// Batch-level metadata.
    /// Examples:
    /// - "batch_processing_time_ms": int
    /// - "parallelism": int (number of parallel operations)
    /// - "transaction_id": string
    /// </summary>
    IReadOnlyDictionary<string, object> Metadata { get; }
}

/// <summary>
/// Default implementation of IIngestionResult.
/// </summary>
public record IngestionResult : IIngestionResult
{
    public required string DocumentId { get; init; }
    public required string Index { get; init; }
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyDictionary<string, int> ArtifactCounts { get; init; } =
        new Dictionary<string, int>();
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();

    /// <summary>
    /// Create a successful ingestion result.
    /// </summary>
    public static IngestionResult CreateSuccess(
        string documentId,
        string index,
        IReadOnlyDictionary<string, int>? artifactCounts = null,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        return new IngestionResult
        {
            DocumentId = documentId,
            Index = index,
            Success = true,
            ArtifactCounts = artifactCounts ?? new Dictionary<string, int>(),
            Metadata = metadata ?? new Dictionary<string, object>()
        };
    }

    /// <summary>
    /// Create a failed ingestion result.
    /// </summary>
    public static IngestionResult CreateFailure(
        string documentId,
        string index,
        string errorMessage,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        return new IngestionResult
        {
            DocumentId = documentId,
            Index = index,
            Success = false,
            ErrorMessage = errorMessage,
            ArtifactCounts = new Dictionary<string, int>(),
            Metadata = metadata ?? new Dictionary<string, object>()
        };
    }
}

/// <summary>
/// Default implementation of IBatchIngestionResult.
/// </summary>
public record BatchIngestionResult : IBatchIngestionResult
{
    public required IReadOnlyList<IIngestionResult> Results { get; init; }
    public int SuccessCount => Results.Count(r => r.Success);
    public int FailureCount => Results.Count(r => !r.Success);

    public IReadOnlyDictionary<string, int> TotalArtifactCounts
    {
        get
        {
            var totals = new Dictionary<string, int>();
            foreach (var result in Results.Where(r => r.Success))
            {
                foreach (var (key, count) in result.ArtifactCounts)
                {
                    totals[key] = totals.GetValueOrDefault(key, 0) + count;
                }
            }
            return totals;
        }
    }

    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}

// ========================================
// Document Management Results
// ========================================

/// <summary>
/// Request to list documents.
/// </summary>
public class DocumentListRequest
{
    /// <summary>
    /// Filter documents by metadata/tags.
    /// </summary>
    public MemoryFilter? Filter { get; init; }

    /// <summary>
    /// Number of documents per page.
    /// Default: 50, Max: 1000
    /// </summary>
    private int _pageSize = 50;
    public int PageSize
    {
        get => _pageSize;
        init
        {
            if (value < 1 || value > 1000)
                throw new ArgumentOutOfRangeException(nameof(PageSize), "PageSize must be between 1 and 1000");
            _pageSize = value;
        }
    }

    /// <summary>
    /// Continuation token from previous page (for pagination).
    /// Null for first page.
    /// </summary>
    public string? ContinuationToken { get; init; }

    /// <summary>
    /// Sort order for results.
    /// </summary>
    public DocumentSortOrder SortOrder { get; init; } = DocumentSortOrder.CreatedDescending;
}

/// <summary>
/// Sort order for document listings.
/// </summary>
public enum DocumentSortOrder
{
    /// <summary>
    /// Newest first (by creation date).
    /// </summary>
    CreatedDescending,

    /// <summary>
    /// Oldest first (by creation date).
    /// </summary>
    CreatedAscending,

    /// <summary>
    /// Most recently updated first.
    /// </summary>
    UpdatedDescending,

    /// <summary>
    /// Alphabetical by file name.
    /// </summary>
    NameAscending
}

/// <summary>
/// Result of a document listing operation.
/// </summary>
public interface IDocumentListResult
{
    /// <summary>
    /// Documents in this page.
    /// </summary>
    IReadOnlyList<IDocumentInfo> Documents { get; }

    /// <summary>
    /// Continuation token for next page.
    /// Null if this is the last page.
    /// </summary>
    string? ContinuationToken { get; }

    /// <summary>
    /// Total number of documents matching the filter (if known).
    /// Null if total count is not available (expensive to compute).
    /// </summary>
    int? TotalCount { get; }

    /// <summary>
    /// Metadata about the listing operation.
    /// </summary>
    IReadOnlyDictionary<string, object> Metadata { get; }
}

/// <summary>
/// Information about a document in the memory system.
/// </summary>
public interface IDocumentInfo
{
    /// <summary>
    /// Document identifier.
    /// </summary>
    string DocumentId { get; }

    /// <summary>
    /// File name.
    /// </summary>
    string FileName { get; }

    /// <summary>
    /// Content type (MIME type).
    /// </summary>
    string ContentType { get; }

    /// <summary>
    /// Document creation timestamp.
    /// </summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Document last update timestamp.
    /// </summary>
    DateTimeOffset UpdatedAt { get; }

    /// <summary>
    /// Document tags.
    /// </summary>
    IReadOnlyDictionary<string, List<string>> Tags { get; }

    /// <summary>
    /// Counts of artifacts for this document.
    /// Same keys as IIngestionResult.ArtifactCounts.
    /// </summary>
    IReadOnlyDictionary<string, int> ArtifactCounts { get; }

    /// <summary>
    /// Additional document metadata.
    /// </summary>
    IReadOnlyDictionary<string, object> Metadata { get; }
}

/// <summary>
/// Default implementation of IDocumentListResult.
/// </summary>
public record DocumentListResult : IDocumentListResult
{
    public required IReadOnlyList<IDocumentInfo> Documents { get; init; }
    public string? ContinuationToken { get; init; }
    public int? TotalCount { get; init; }
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}

/// <summary>
/// Default implementation of IDocumentInfo.
/// </summary>
public record DocumentInfo : IDocumentInfo
{
    public required string DocumentId { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public IReadOnlyDictionary<string, List<string>> Tags { get; init; } =
        new Dictionary<string, List<string>>();
    public IReadOnlyDictionary<string, int> ArtifactCounts { get; init; } =
        new Dictionary<string, int>();
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}

// ========================================
// Standard Artifact Keys
// ========================================

/// <summary>
/// Standard artifact count keys.
/// Implementations should use these keys when reporting artifact counts.
/// </summary>
public static class StandardArtifacts
{
    /// <summary>
    /// Number of text chunks created.
    /// </summary>
    public const string Chunks = "chunks";

    /// <summary>
    /// Number of embeddings generated.
    /// </summary>
    public const string Embeddings = "embeddings";

    /// <summary>
    /// Number of entities extracted (for GraphRAG).
    /// </summary>
    public const string Entities = "entities";

    /// <summary>
    /// Number of relationships extracted (for GraphRAG).
    /// </summary>
    public const string Relationships = "relationships";

    /// <summary>
    /// Number of images extracted.
    /// </summary>
    public const string Images = "images";

    /// <summary>
    /// Number of summaries generated.
    /// </summary>
    public const string Summaries = "summaries";

    /// <summary>
    /// Number of tables extracted.
    /// </summary>
    public const string Tables = "tables";

    /// <summary>
    /// Number of code blocks extracted.
    /// </summary>
    public const string CodeBlocks = "code_blocks";
}
