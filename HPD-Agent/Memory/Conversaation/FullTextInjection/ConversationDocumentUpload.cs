
    /// <summary>
    /// Represents the result of processing a document upload for conversation injection
    /// </summary>
    public class ConversationDocumentUpload
    {
        /// <summary>Original filename from upload</summary>
        public string FileName { get; set; } = string.Empty;
        
        /// <summary>Extracted text content to append to message</summary>
        public string ExtractedText { get; set; } = string.Empty;
    
    /// <summary>MIME type detected by TextExtractionUtility</summary>
    public string MimeType { get; set; } = string.Empty;
    
    /// <summary>Original file size in bytes</summary>
    public long FileSize { get; set; }
    
    /// <summary>Character count of extracted text</summary>
    public int ExtractedTextLength => ExtractedText?.Length ?? 0;
    
    /// <summary>Processing timestamp</summary>
    public DateTime ProcessedAt { get; set; }
    
    /// <summary>Whether extraction succeeded</summary>
    public bool Success { get; set; }
    
    /// <summary>Error message if extraction failed</summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>Decoder used for extraction</summary>
    public string? DecoderUsed { get; set; }
}
