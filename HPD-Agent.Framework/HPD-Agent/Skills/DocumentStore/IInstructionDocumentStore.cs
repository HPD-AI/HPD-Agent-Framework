
namespace HPD.Agent.Skills.DocumentStore;

/// <summary>
/// Complete instruction document store (composite interface for convenience).
/// Implements all three focused interfaces: content operations, metadata operations, and skill linking.
/// Also extends IContentStore for unified content operations.
///
/// This interface should be injected via DI (not accessed via singleton) for testability.
/// </summary>
/// <remarks>
/// <para><b>IContentStore Integration:</b></para>
/// <para>
/// IInstructionDocumentStore extends IContentStore for unified content operations. Unlike the
/// memory stores which are agent-scoped, this store is GLOBAL - all documents are system-wide.
/// The specialized methods (UploadFromContentAsync, ReadDocumentAsync, etc.) and IContentStore
/// methods operate on the same global document collection.
/// </para>
/// </remarks>
public interface IInstructionDocumentStore :
    IDocumentContentStore,
    IDocumentMetadataStore,
    ISkillDocumentLinker,
    IContentStore
{
    /// <summary>
    /// Health check - verifies store is accessible.
    /// Use this at startup to fail fast if store is unavailable.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if store is healthy and accessible, false otherwise</returns>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}
