// Copyright (c) Einstein Essibu. All rights reserved.
// IngestionRequest v2: Fixed to use Stream instead of byte[] for memory efficiency

namespace HPDAgent.Memory.Abstractions.Client;

/// <summary>
/// Request to ingest a document into the memory system.
/// V2: Uses Stream instead of byte[] for memory efficiency with large files.
/// </summary>
/// <remarks>
/// IMPORTANT: The caller owns the Stream and must dispose it after IngestAsync completes.
///
/// Usage patterns:
///
/// Pattern 1 - File:
/// using var request = await IngestionRequest.FromFileAsync("doc.pdf");
/// await memory.IngestAsync(request);
///
/// Pattern 2 - Stream:
/// using var fileStream = File.OpenRead("doc.pdf");
/// using var request = IngestionRequest.FromStream(fileStream, "doc.pdf");
/// await memory.IngestAsync(request);
///
/// Pattern 3 - Byte array (for small content):
/// using var request = IngestionRequest.FromBytes(bytes, "doc.txt");
/// await memory.IngestAsync(request);
/// </remarks>
public class IngestionRequest : IDisposable
{
    private Stream? _contentStream;
    private bool _ownsStream;

    /// <summary>
    /// Unique identifier for the document.
    /// If null, a new ID will be generated.
    /// If provided and document exists, it will be updated (idempotent).
    /// </summary>
    public string? DocumentId { get; init; }

    /// <summary>
    /// File name of the document (for metadata).
    /// Used to determine content type if ContentType is not specified.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Document content as a stream.
    /// The stream will be read during ingestion.
    /// Caller must dispose the stream after IngestAsync completes.
    /// </summary>
    public required Stream ContentStream
    {
        get => _contentStream ?? throw new InvalidOperationException("ContentStream not initialized");
        init => _contentStream = value;
    }

    /// <summary>
    /// MIME type of the document (e.g., "application/pdf", "text/plain").
    /// If null, will be inferred from FileName.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Tags for organizing and filtering documents.
    /// </summary>
    public Dictionary<string, List<string>> Tags { get; init; } = new();

    /// <summary>
    /// Implementation-specific options.
    /// See StandardOptions for common option keys.
    /// </summary>
    public Dictionary<string, object> Options { get; init; } = new();

    /// <summary>
    /// Create an ingestion request from a file path.
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <param name="documentId">Optional document ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Ingestion request with opened file stream</returns>
    /// <remarks>
    /// This method opens the file stream and the request owns it (will dispose on Dispose()).
    /// Usage: using var request = await IngestionRequest.FromFileAsync("doc.pdf");
    /// </remarks>
    public static async Task<IngestionRequest> FromFileAsync(
        string filePath,
        string? documentId = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var fileName = Path.GetFileName(filePath);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var contentType = InferContentType(extension);

        // Open file stream asynchronously
        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        return new IngestionRequest
        {
            DocumentId = documentId,
            FileName = fileName,
            ContentStream = stream,
            ContentType = contentType,
            _ownsStream = true  // We opened it, we dispose it
        };
    }

    /// <summary>
    /// Create an ingestion request from a stream.
    /// </summary>
    /// <param name="stream">Content stream (caller owns and must dispose)</param>
    /// <param name="fileName">File name for metadata</param>
    /// <param name="documentId">Optional document ID</param>
    /// <param name="contentType">Optional content type (inferred from fileName if null)</param>
    /// <returns>Ingestion request</returns>
    /// <remarks>
    /// The caller owns the stream and must dispose it after IngestAsync completes.
    /// This request will NOT dispose the stream on Dispose().
    /// </remarks>
    public static IngestionRequest FromStream(
        Stream stream,
        string fileName,
        string? documentId = null,
        string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(stream));

        return new IngestionRequest
        {
            DocumentId = documentId,
            FileName = fileName,
            ContentStream = stream,
            ContentType = contentType ?? InferContentType(Path.GetExtension(fileName).ToLowerInvariant()),
            _ownsStream = false  // Caller owns it
        };
    }

    /// <summary>
    /// Create an ingestion request from a byte array.
    /// </summary>
    /// <param name="content">Content bytes</param>
    /// <param name="fileName">File name for metadata</param>
    /// <param name="documentId">Optional document ID</param>
    /// <param name="contentType">Optional content type</param>
    /// <returns>Ingestion request</returns>
    /// <remarks>
    /// This creates a MemoryStream from the byte array.
    /// The request owns this stream and will dispose it.
    /// For large content (>10MB), consider using FromStream with a FileStream instead.
    /// </remarks>
    public static IngestionRequest FromBytes(
        byte[] content,
        string fileName,
        string? documentId = null,
        string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var stream = new MemoryStream(content, writable: false);

        return new IngestionRequest
        {
            DocumentId = documentId,
            FileName = fileName,
            ContentStream = stream,
            ContentType = contentType ?? InferContentType(Path.GetExtension(fileName).ToLowerInvariant()),
            _ownsStream = true  // We created it, we dispose it
        };
    }

    /// <summary>
    /// Create an ingestion request from text content.
    /// </summary>
    /// <param name="text">Text content</param>
    /// <param name="fileName">File name for metadata</param>
    /// <param name="documentId">Optional document ID</param>
    /// <returns>Ingestion request</returns>
    public static IngestionRequest FromText(
        string text,
        string fileName,
        string? documentId = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return FromBytes(bytes, fileName, documentId, "text/plain");
    }

    /// <summary>
    /// Infer content type from file extension.
    /// </summary>
    private static string InferContentType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".html" or ".htm" => "text/html",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// Dispose the content stream if this request owns it.
    /// </summary>
    public void Dispose()
    {
        if (_ownsStream && _contentStream != null)
        {
            _contentStream.Dispose();
            _contentStream = null;
        }
    }
}

/// <summary>
/// Standard option keys for ingestion.
/// Implementations are not required to support these, but should use these keys when they do.
/// </summary>
public static class StandardIngestionOptions
{
    /// <summary>
    /// Chunk size in tokens (int). Default: 512
    /// </summary>
    public const string ChunkSize = "chunk_size";

    /// <summary>
    /// Chunk overlap in tokens (int). Default: 50
    /// </summary>
    public const string ChunkOverlap = "chunk_overlap";

    /// <summary>
    /// Embedding model name (string). Example: "text-embedding-3-small"
    /// </summary>
    public const string EmbeddingModel = "embedding_model";

    /// <summary>
    /// Extract entities for GraphRAG (bool). Default: false
    /// </summary>
    public const string ExtractEntities = "extract_entities";

    /// <summary>
    /// Extract relationships for GraphRAG (bool). Default: false
    /// </summary>
    public const string ExtractRelationships = "extract_relationships";

    /// <summary>
    /// Document language (string). Example: "en", "es", "fr"
    /// </summary>
    public const string Language = "language";

    /// <summary>
    /// Skip text extraction (bool). Default: false
    /// Use when document is already plain text.
    /// </summary>
    public const string SkipTextExtraction = "skip_text_extraction";

    /// <summary>
    /// Extract images from document (bool). Default: false
    /// For multi-modal RAG implementations.
    /// </summary>
    public const string ExtractImages = "extract_images";

    /// <summary>
    /// Generate document summary (bool). Default: false
    /// </summary>
    public const string GenerateSummary = "generate_summary";
}
