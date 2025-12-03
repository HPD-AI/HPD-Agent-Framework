
namespace HPD.Agent.Skills.DocumentStore;

/// <summary>
/// Skill-specific metadata for a linked document
/// </summary>
public record SkillDocumentMetadata
{
    /// <summary>
    /// Skill-specific description override.
    /// If provided, this description is used instead of the global default when the skill references this document.
    /// </summary>
    public required string Description { get; init; }
}
