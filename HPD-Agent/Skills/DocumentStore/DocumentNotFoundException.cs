namespace HPD_Agent.Skills.DocumentStore;

/// <summary>
/// Exception thrown when a requested document does not exist in the store
/// </summary>
public class DocumentNotFoundException : Exception
{
    public string DocumentId { get; }

    public DocumentNotFoundException(string message, string documentId) : base(message)
    {
        DocumentId = documentId;
    }
}
