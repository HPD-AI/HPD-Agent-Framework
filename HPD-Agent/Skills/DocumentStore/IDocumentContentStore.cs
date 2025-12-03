
namespace HPD.Agent.Skills.DocumentStore;

/// <summary>
/// Document content operations (upload, read, delete, exists)
/// 
/// ARCHITECTURE NOTE: File path resolution happens in AgentBuilder, not here.
/// This interface only cares about CONTENT persistence, not WHERE content comes from.
/// Callers are responsible for reading files and passing pre-extracted content.
/// </summary>
public interface IDocumentContentStore
{
    /// <summary>
    /// Upload document content from URL.
    /// Downloads and extracts text content from the URL.
    /// </summary>
    /// <param name="documentId">Unique document identifier</param>
    /// <param name="metadata">Document metadata (name, description, source)</param>
    /// <param name="url">URL to document</param>
    /// <param name="ct">Cancellation token</param>
    Task UploadFromUrlAsync(
        string documentId,
        DocumentMetadata metadata,
        string url,
        CancellationToken ct = default);

    /// <summary>
    /// Upload document content directly (pre-extracted text).
    /// Use this when you already have text content extracted.
    /// Idempotent - uses content hash to skip unchanged documents.
    /// 
    /// This is the PRIMARY method for uploading documents.
    /// AgentBuilder resolves file paths and reads content, then calls this method.
    /// </summary>
    /// <param name="documentId">Unique document identifier</param>
    /// <param name="metadata">Document metadata (name, description, source)</param>
    /// <param name="content">Pre-extracted text content</param>
    /// <param name="ct">Cancellation token</param>
    Task UploadFromContentAsync(
        string documentId,
        DocumentMetadata metadata,
        string content,
        CancellationToken ct = default);

    /// <summary>
    /// Check if document exists in global store
    /// </summary>
    /// <param name="documentId">Document identifier to check</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if document exists, false otherwise</returns>
    Task<bool> DocumentExistsAsync(
        string documentId,
        CancellationToken ct = default);

    /// <summary>
    /// Delete document from global store (also removes all skill links)
    /// </summary>
    /// <param name="documentId">Document identifier to delete</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteDocumentAsync(
        string documentId,
        CancellationToken ct = default);

    /// <summary>
    /// Read document content by ID (global, not skill-specific).
    /// Returns null if document does not exist.
    /// </summary>
    /// <param name="documentId">Document identifier to read</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Document content or null if not found</returns>
    Task<string?> ReadDocumentAsync(
        string documentId,
        CancellationToken ct = default);
}
