

/// <summary>
/// Exception thrown when duplicate document uploads are detected at initialization
/// </summary>
public class DuplicateDocumentException : Exception
{
    public DuplicateDocumentException(string message) : base(message) { }
}
