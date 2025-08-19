using System.Collections.Concurrent;


/// <summary>
/// Stored vector entry with metadata
/// </summary>
internal class VectorEntry
{
    public string Id { get; }
    public ReadOnlyMemory<float> Vector { get; }
    public object? Metadata { get; }
    
    public VectorEntry(string id, ReadOnlyMemory<float> vector, object? metadata)
    {
        Id = id;
        Vector = vector;
        Metadata = metadata;
    }
}

/// <summary>
/// In-memory implementation of vector store using cosine similarity
/// Thread-safe and suitable for moderate-sized function sets (< 1000 functions)
/// </summary>
public class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<string, VectorEntry> _vectors = new();
    private readonly DistanceMetric _distanceMetric;
    private readonly object _lockObject = new();
    private bool _disposed;
    
    public InMemoryVectorStore(InMemoryVectorStoreConfig config)
    {
        _distanceMetric = config.DistanceMetric;
    }
    
    /// <inheritdoc />
    public Task StoreAsync(string id, ReadOnlyMemory<float> vector, object? metadata = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("Vector ID cannot be null or empty", nameof(id));
        
        if (vector.IsEmpty)
            throw new ArgumentException("Vector cannot be empty", nameof(vector));
        
        var entry = new VectorEntry(id, vector, metadata);
        _vectors.AddOrUpdate(id, entry, (key, oldValue) => entry);
        
        return Task.CompletedTask;
    }
    
    /// <inheritdoc />
    public Task<VectorSearchResult[]> SearchAsync(
        ReadOnlyMemory<float> queryVector, 
        int limit = 10, 
        float threshold = 0.0f, 
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (queryVector.IsEmpty)
            throw new ArgumentException("Query vector cannot be empty", nameof(queryVector));
        
        if (limit <= 0)
            throw new ArgumentException("Limit must be positive", nameof(limit));
        
        if (threshold < 0.0f || threshold > 1.0f)
            throw new ArgumentException("Threshold must be between 0.0 and 1.0", nameof(threshold));
        
        var results = new List<VectorSearchResult>();
        var querySpan = queryVector.Span;
        
        // Calculate similarity with all stored vectors
        foreach (var entry in _vectors.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var similarity = CalculateSimilarity(querySpan, entry.Vector.Span, _distanceMetric);
            
            if (similarity >= threshold)
            {
                results.Add(new VectorSearchResult(entry.Id, similarity, entry.Metadata));
            }
        }
        
        // Sort by similarity (highest first) and take top results
        var topResults = results
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToArray();
        
        return Task.FromResult(topResults);
    }
    
    /// <inheritdoc />
    public Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("Vector ID cannot be null or empty", nameof(id));
        
        _vectors.TryRemove(id, out _);
        return Task.CompletedTask;
    }
    
    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _vectors.Clear();
        return Task.CompletedTask;
    }
    
    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.FromResult(_vectors.Count);
    }
    
    /// <summary>
    /// Calculates similarity between two vectors based on the specified distance metric
    /// </summary>
    private static float CalculateSimilarity(ReadOnlySpan<float> vector1, ReadOnlySpan<float> vector2, DistanceMetric metric)
    {
        if (vector1.Length != vector2.Length)
            throw new ArgumentException("Vectors must have the same dimensions");
        
        return metric switch
        {
            DistanceMetric.Cosine => CalculateCosineSimilarity(vector1, vector2),
            DistanceMetric.Euclidean => CalculateEuclideanSimilarity(vector1, vector2),
            DistanceMetric.DotProduct => CalculateDotProductSimilarity(vector1, vector2),
            _ => throw new ArgumentException($"Unsupported distance metric: {metric}")
        };
    }
    
    /// <summary>
    /// Calculates cosine similarity between two vectors (0 to 1, where 1 is most similar)
    /// </summary>
    private static float CalculateCosineSimilarity(ReadOnlySpan<float> vector1, ReadOnlySpan<float> vector2)
    {
        float dotProduct = 0f;
        float magnitude1 = 0f;
        float magnitude2 = 0f;
        
        for (int i = 0; i < vector1.Length; i++)
        {
            dotProduct += vector1[i] * vector2[i];
            magnitude1 += vector1[i] * vector1[i];
            magnitude2 += vector2[i] * vector2[i];
        }
        
        var magnitude = MathF.Sqrt(magnitude1) * MathF.Sqrt(magnitude2);
        
        // Avoid division by zero
        if (magnitude == 0f)
            return 0f;
        
        // Convert from [-1, 1] to [0, 1] range
        var cosineSimilarity = dotProduct / magnitude;
        return (cosineSimilarity + 1f) / 2f;
    }
    
    /// <summary>
    /// Calculates Euclidean similarity (converted to 0-1 range where 1 is most similar)
    /// </summary>
    private static float CalculateEuclideanSimilarity(ReadOnlySpan<float> vector1, ReadOnlySpan<float> vector2)
    {
        float sumSquaredDifferences = 0f;
        
        for (int i = 0; i < vector1.Length; i++)
        {
            var diff = vector1[i] - vector2[i];
            sumSquaredDifferences += diff * diff;
        }
        
        var distance = MathF.Sqrt(sumSquaredDifferences);
        
        // Convert distance to similarity (closer = more similar)
        // Using 1 / (1 + distance) to map [0, ∞) to (0, 1]
        return 1f / (1f + distance);
    }
    
    /// <summary>
    /// Calculates dot product similarity (normalized to 0-1 range)
    /// </summary>
    private static float CalculateDotProductSimilarity(ReadOnlySpan<float> vector1, ReadOnlySpan<float> vector2)
    {
        float dotProduct = 0f;
        
        for (int i = 0; i < vector1.Length; i++)
        {
            dotProduct += vector1[i] * vector2[i];
        }
        
        // Normalize to 0-1 range (assuming vectors are normalized)
        return Math.Max(0f, dotProduct);
    }
    
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InMemoryVectorStore));
    }
    
    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _vectors.Clear();
            _disposed = true;
        }
    }
}
