/// <summary>
/// Represents a document in an agent's knowledge base.
/// This is the agent's static, read-only expertise (e.g., Python docs, design patterns).
/// </summary>
public class StaticMemoryDocument
{
    /// <summary>Unique document identifier</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Original filename from upload</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Original file path or URL</summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>Extracted text content for injection or indexing</summary>
    public string ExtractedText { get; set; } = string.Empty;

    /// <summary>MIME type detected by TextExtractionUtility</summary>
    public string MimeType { get; set; } = string.Empty;

    /// <summary>Original file size in bytes</summary>
    public long FileSize { get; set; }

    /// <summary>Character count of extracted text</summary>
    public int ExtractedTextLength => ExtractedText?.Length ?? 0;

    /// <summary>When document was added to agent knowledge</summary>
    public DateTime AddedAt { get; set; }

    /// <summary>Last time document was accessed during agent operation</summary>
    public DateTime LastAccessed { get; set; }

    /// <summary>User-provided description/notes about this knowledge document</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Optional tags for categorizing knowledge (e.g., "api", "patterns", "security")</summary>
    public List<string> Tags { get; set; } = new();
}
