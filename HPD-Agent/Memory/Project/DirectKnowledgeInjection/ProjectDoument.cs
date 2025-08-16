/// <summary>
/// Represents a document uploaded to a project for context injection
/// </summary>
public class ProjectDocument
{
    /// <summary>Unique document identifier</summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>Original filename from upload</summary>
    public string FileName { get; set; } = string.Empty;
    
    /// <summary>Original file path or URL</summary>
    public string OriginalPath { get; set; } = string.Empty;
    
    /// <summary>Extracted text content for injection</summary>
    public string ExtractedText { get; set; } = string.Empty;
    
    /// <summary>MIME type detected by TextExtractionUtility</summary>
    public string MimeType { get; set; } = string.Empty;
    
    /// <summary>Original file size in bytes</summary>
    public long FileSize { get; set; }
    
    /// <summary>Character count of extracted text</summary>
    public int ExtractedTextLength => ExtractedText?.Length ?? 0;
    
    /// <summary>When document was uploaded to project</summary>
    public DateTime UploadedAt { get; set; }
    
    /// <summary>Last time document was accessed in conversation</summary>
    public DateTime LastAccessed { get; set; }
    
    /// <summary>User-provided description/notes</summary>
    public string Description { get; set; } = string.Empty;
}