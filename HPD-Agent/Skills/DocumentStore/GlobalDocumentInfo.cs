
namespace HPD.Agent.Skills.DocumentStore;

/// <summary>
/// Global document information (includes metadata + stats + version)
/// </summary>
public record GlobalDocumentInfo
{
    public required string DocumentId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public long SizeBytes { get; init; }
    public string ContentHash { get; init; } = string.Empty;

    // Version tracking
    public int Version { get; init; } = 1;
    public DateTime CreatedAt { get; init; }
    public DateTime LastModified { get; init; }
}
