
namespace HPD.Agent.Skills.DocumentStore;

/// <summary>
/// Reference to a document from a skill's perspective
/// </summary>
public record SkillDocumentReference
{
    public required string DocumentId { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// Description (skill-specific override or global default)
    /// </summary>
    public required string Description { get; init; }
}
