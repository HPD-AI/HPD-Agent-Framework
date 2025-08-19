/// <summary>
/// Represents a search result from a vector store
/// </summary>
public class VectorSearchResult
{
    public string Id { get; }
    public float Score { get; }
    public object? Metadata { get; }
    
    public VectorSearchResult(string id, float score, object? metadata = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Score = score;
        Metadata = metadata;
    }
}

/// <summary>
/// Defines the contract for vector storage and similarity search operations
/// </summary>
public interface IVectorStore : IDisposable
{
    /// <summary>
    /// Stores a vector with the specified ID and optional metadata
    /// </summary>
    /// <param name="id">Unique identifier for the vector</param>
    /// <param name="vector">The vector data</param>
    /// <param name="metadata">Optional metadata to associate with the vector</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task StoreAsync(string id, ReadOnlyMemory<float> vector, object? metadata = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Performs similarity search against stored vectors
    /// </summary>
    /// <param name="queryVector">The query vector to search for</param>
    /// <param name="limit">Maximum number of results to return</param>
    /// <param name="threshold">Minimum similarity score threshold (0.0 to 1.0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Search results ordered by similarity score (highest first)</returns>
    Task<VectorSearchResult[]> SearchAsync(
        ReadOnlyMemory<float> queryVector, 
        int limit = 10, 
        float threshold = 0.0f, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Removes a vector from the store
    /// </summary>
    /// <param name="id">ID of the vector to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Clears all vectors from the store
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ClearAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the count of stored vectors
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
