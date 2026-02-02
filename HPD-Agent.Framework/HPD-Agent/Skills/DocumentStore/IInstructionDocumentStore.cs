
namespace HPD.Agent.Skills.DocumentStore;

/// <summary>
/// Complete instruction document store (composite interface for convenience).
/// Implements all three focused interfaces: content operations, metadata operations, and skill linking.
///
/// This interface should be injected via DI (not accessed via singleton) for testability.
/// </summary>
public interface IInstructionDocumentStore :
    IDocumentContentStore,
    IDocumentMetadataStore,
    ISkillDocumentLinker
{
    /// <summary>
    /// Health check - verifies store is accessible.
    /// Use this at startup to fail fast if store is unavailable.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if store is healthy and accessible, false otherwise</returns>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}
