
namespace HPD.Agent.Skills.DocumentStore;

/// <summary>
/// Complete document with content
/// </summary>
public record SkillDocument
{
    public required string DocumentId { get; init; }
    public required string Name { get; init; }
    public required string Content { get; init; }
    public required string Description { get; init; }
}
