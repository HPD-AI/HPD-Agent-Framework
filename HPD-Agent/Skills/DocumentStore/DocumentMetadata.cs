namespace HPD_Agent.Skills.DocumentStore;

/// <summary>
/// Default metadata for a document in the global store
/// </summary>
public record DocumentMetadata
{
    /// <summary>
    /// Display name of the document
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what the document contains.
    /// This helps the agent understand the content and decide whether to read it.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Optional original source (URL, file path, etc.)
    /// </summary>
    public string? OriginalSource { get; init; }
}
