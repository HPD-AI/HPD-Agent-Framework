using System.Collections.Generic;

/// <summary>
/// Represents a result from memory retrieval.
/// </summary>
public record RetrievalResult(
    string Id,
    string Title,
    string Content,
    string Source = "Unknown",
    float Relevance = 0.0f,
    Dictionary<string, object>? Metadata = null
);
