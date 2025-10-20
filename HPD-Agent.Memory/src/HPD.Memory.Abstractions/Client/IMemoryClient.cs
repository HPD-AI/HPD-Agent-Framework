// Copyright (c) Einstein Essibu. All rights reserved.
// IMemoryClient v2: Fixed version addressing critical issues from review

namespace HPDAgent.Memory.Abstractions.Client;

/// <summary>
/// Universal interface for RAG (Retrieval-Augmented Generation) memory systems.
/// This version uses a SCOPED CLIENT pattern - each client is bound to a specific index.
/// </summary>
/// <remarks>
/// Design decisions (v2):
/// 1. SCOPED CLIENT: IMemoryClient is bound to an index at construction time
///    - No index parameters in methods (cleaner API)
///    - Matches ILogger pattern (scoped to category)
///    - Use IMemoryClientFactory to create clients for different indices
///
/// 2. STREAM-BASED: Uses Stream instead of byte[] for memory efficiency
///    - Avoids loading entire documents into memory
///    - Supports large files (GBs) without OOM
///    - Caller owns stream lifetime (using/dispose)
///
/// 3. GENERIC RESULTS: Results use dictionaries instead of specific properties
///    - Future-proof (new RAG systems don't break interface)
///    - Flexible (implementations report what they actually produce)
///    - Convention-based (standard keys documented)
///
/// 4. BATCH SUPPORT: Efficient multi-document operations
///    - Reduces round trips
///    - Enables transactional semantics
///    - Better performance
///
/// 5. DOCUMENT MANAGEMENT: Full CRUD + listing
///    - List documents with filtering
///    - Pagination support
///    - Document metadata queries
/// </remarks>
public interface IMemoryClient
{
    /// <summary>
    /// The index/collection this client is scoped to.
    /// All operations (ingest, retrieve, generate) operate on this index.
    /// </summary>
    string Index { get; }

    // ========================================
    // Document Ingestion
    // ========================================

    /// <summary>
    /// Ingest a single document into the memory system.
    /// </summary>
    /// <param name="request">Ingestion request containing document stream and metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing ingestion status and artifact counts</returns>
    /// <remarks>
    /// The caller owns the stream and must dispose it after this method completes.
    /// For efficient multi-document ingestion, use IngestBatchAsync instead.
    /// </remarks>
    Task<IIngestionResult> IngestAsync(
        IngestionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ingest multiple documents in a single operation (batch ingestion).
    /// </summary>
    /// <param name="requests">Collection of ingestion requests</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Batch result containing individual results and summary</returns>
    /// <remarks>
    /// Implementations should:
    /// - Process documents in parallel when possible
    /// - Use transactions when supported (all-or-nothing semantics)
    /// - Continue processing on individual failures (return partial results)
    /// - Report failures in individual results, not by throwing
    /// </remarks>
    Task<IBatchIngestionResult> IngestBatchAsync(
        IEnumerable<IngestionRequest> requests,
        CancellationToken cancellationToken = default);

    // ========================================
    // Knowledge Retrieval
    // ========================================

    /// <summary>
    /// Retrieve relevant knowledge/documents for a query.
    /// </summary>
    /// <param name="request">Retrieval request containing query and filters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing retrieved items ranked by relevance</returns>
    Task<IRetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default);

    // ========================================
    // RAG Generation
    // ========================================

    /// <summary>
    /// Generate an answer to a question using RAG.
    /// Combines retrieval (finding relevant knowledge) with generation (producing an answer).
    /// </summary>
    /// <param name="request">Generation request containing question and configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing generated answer and citations</returns>
    Task<IGenerationResult> GenerateAsync(
        GenerationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate an answer with streaming (for real-time display).
    /// </summary>
    /// <param name="request">Generation request containing question and configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async stream of generation chunks (text, citations, metadata)</returns>
    /// <remarks>
    /// Implementations that don't support streaming should throw NotSupportedException.
    /// Check Capabilities.SupportsStreaming before calling.
    /// </remarks>
    IAsyncEnumerable<IGenerationChunk> GenerateStreamAsync(
        GenerationRequest request,
        CancellationToken cancellationToken = default);

    // ========================================
    // Document Management
    // ========================================

    /// <summary>
    /// List documents in this index with optional filtering and pagination.
    /// </summary>
    /// <param name="request">List request containing filters and pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing document list and pagination info</returns>
    Task<IDocumentListResult> ListDocumentsAsync(
        DocumentListRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get detailed information about a specific document.
    /// </summary>
    /// <param name="documentId">Document identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Document info if found, null otherwise</returns>
    Task<IDocumentInfo?> GetDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a document exists.
    /// </summary>
    /// <param name="documentId">Document identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if document exists, false otherwise</returns>
    Task<bool> DocumentExistsAsync(
        string documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a document and all its associated data.
    /// </summary>
    /// <param name="documentId">Document identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// Removes the document and all associated artifacts:
    /// - Vector embeddings
    /// - Graph entities/relationships
    /// - Cached data
    /// - Source files
    /// </remarks>
    Task DeleteDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update document metadata (tags, etc.) without re-ingesting.
    /// </summary>
    /// <param name="documentId">Document identifier</param>
    /// <param name="update">Updates to apply</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UpdateDocumentAsync(
        string documentId,
        DocumentUpdate update,
        CancellationToken cancellationToken = default);

    // ========================================
    // Capabilities
    // ========================================

    /// <summary>
    /// Get capabilities of this memory client implementation.
    /// </summary>
    IMemoryCapabilities Capabilities { get; }
}

/// <summary>
/// Factory for creating IMemoryClient instances scoped to different indices.
/// </summary>
/// <remarks>
/// Register this in DI:
/// services.AddSingleton&lt;IMemoryClientFactory, MyMemoryClientFactory&gt;();
///
/// Usage:
/// var client = factory.CreateClient("documents");
/// await client.IngestAsync(...);
/// </remarks>
public interface IMemoryClientFactory
{
    /// <summary>
    /// Create a memory client for a specific index.
    /// </summary>
    /// <param name="index">Index/collection name</param>
    /// <returns>Memory client scoped to the specified index</returns>
    IMemoryClient CreateClient(string index);

    /// <summary>
    /// Get the default index name.
    /// Used when creating clients without specifying an index.
    /// </summary>
    string DefaultIndex { get; }
}

/// <summary>
/// Document metadata update.
/// </summary>
public class DocumentUpdate
{
    /// <summary>
    /// Tags to add or update.
    /// If tag exists, values are merged (not replaced).
    /// </summary>
    public Dictionary<string, List<string>>? AddTags { get; init; }

    /// <summary>
    /// Tags to remove.
    /// </summary>
    public Dictionary<string, List<string>>? RemoveTags { get; init; }

    /// <summary>
    /// Custom metadata to add or update.
    /// </summary>
    public Dictionary<string, object>? UpdateMetadata { get; init; }
}

/// <summary>
/// Extension methods for IMemoryClient (convenience methods).
/// </summary>
public static class MemoryClientExtensions
{
    /// <summary>
    /// Quick ingest from file path.
    /// </summary>
    public static async Task<IIngestionResult> IngestFileAsync(
        this IMemoryClient client,
        string filePath,
        string? documentId = null,
        Dictionary<string, List<string>>? tags = null,
        CancellationToken cancellationToken = default)
    {
        using var request = await IngestionRequest.FromFileAsync(filePath, documentId, cancellationToken);
        if (tags != null)
        {
            foreach (var tag in tags)
                request.Tags[tag.Key] = tag.Value;
        }
        return await client.IngestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Quick search (simple query string).
    /// </summary>
    public static Task<IRetrievalResult> SearchAsync(
        this IMemoryClient client,
        string query,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        return client.RetrieveAsync(
            new RetrievalRequest { Query = query, MaxResults = maxResults },
            cancellationToken);
    }

    /// <summary>
    /// Quick ask (simple question string).
    /// </summary>
    public static Task<IGenerationResult> AskAsync(
        this IMemoryClient client,
        string question,
        CancellationToken cancellationToken = default)
    {
        return client.GenerateAsync(
            new GenerationRequest { Question = question },
            cancellationToken);
    }

    /// <summary>
    /// List all documents (no filtering).
    /// </summary>
    public static Task<IDocumentListResult> ListAllDocumentsAsync(
        this IMemoryClient client,
        int pageSize = 50,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        return client.ListDocumentsAsync(
            new DocumentListRequest { PageSize = pageSize, ContinuationToken = continuationToken },
            cancellationToken);
    }
}
