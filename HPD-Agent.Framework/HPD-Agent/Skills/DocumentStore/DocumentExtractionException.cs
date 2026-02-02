
namespace HPD.Agent.Skills.DocumentStore;

/// <summary>
/// Exception thrown when document content extraction fails (e.g., corrupted PDF, invalid file)
/// </summary>
public class DocumentExtractionException : Exception
{
    public string FilePath { get; }

    public DocumentExtractionException(string message, string filePath) : base(message)
    {
        FilePath = filePath;
    }

    public DocumentExtractionException(string message, string filePath, Exception inner)
        : base(message, inner)
    {
        FilePath = filePath;
    }
}
