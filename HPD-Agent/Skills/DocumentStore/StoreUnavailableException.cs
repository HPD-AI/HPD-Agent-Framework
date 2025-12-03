
namespace HPD.Agent.Skills.DocumentStore;

/// <summary>
/// Exception thrown when the document store is unavailable (health check failed)
/// </summary>
public class StoreUnavailableException : Exception
{
    public StoreUnavailableException(string message) : base(message) { }

    public StoreUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
