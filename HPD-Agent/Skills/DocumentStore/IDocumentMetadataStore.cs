
namespace HPD.Agent.Skills.DocumentStore;

/// <summary>
/// Document metadata and listing operations
/// </summary>
public interface IDocumentMetadataStore
{
    /// <summary>
    /// Get document default metadata.
    /// Returns null if document does not exist.
    /// </summary>
    /// <param name="documentId">Document identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Document metadata or null if not found</returns>
    Task<GlobalDocumentInfo?> GetDocumentMetadataAsync(
        string documentId,
        CancellationToken ct = default);

    /// <summary>
    /// List all documents in global store
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of all documents with metadata</returns>
    Task<List<GlobalDocumentInfo>> ListAllDocumentsAsync(
        CancellationToken ct = default);
}
