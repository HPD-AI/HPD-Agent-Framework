
namespace HPD.Agent.Skills.DocumentStore;

/// <summary>
/// Skill-document linking operations
/// </summary>
public interface ISkillDocumentLinker
{
    /// <summary>
    /// Link a skill to a document with skill-specific metadata.
    /// This creates an association between a skill and a document,
    /// allowing skill-specific description overrides.
    /// </summary>
    /// <param name="skillNamespace">Skill namespace (format: "PluginName.SkillName")</param>
    /// <param name="documentId">Document identifier to link</param>
    /// <param name="metadata">Skill-specific metadata (description override)</param>
    /// <param name="ct">Cancellation token</param>
    Task LinkDocumentToSkillAsync(
        string skillNamespace,
        string documentId,
        SkillDocumentMetadata metadata,
        CancellationToken ct = default);

    /// <summary>
    /// Get all documents linked to a skill.
    /// Returns documents with skill-specific descriptions if overridden,
    /// otherwise uses global default descriptions.
    /// </summary>
    /// <param name="skillNamespace">Skill namespace (format: "PluginName.SkillName")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of document references for this skill</returns>
    Task<List<SkillDocumentReference>> GetSkillDocumentsAsync(
        string skillNamespace,
        CancellationToken ct = default);

    /// <summary>
    /// Read document for specific skill (returns skill-specific metadata + content).
    /// Returns null if document does not exist or is not linked to the skill.
    /// </summary>
    /// <param name="skillNamespace">Skill namespace (format: "PluginName.SkillName")</param>
    /// <param name="documentId">Document identifier to read</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Complete document with content and metadata, or null if not found</returns>
    Task<SkillDocument?> ReadSkillDocumentAsync(
        string skillNamespace,
        string documentId,
        CancellationToken ct = default);
}
