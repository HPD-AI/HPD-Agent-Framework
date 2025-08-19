using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;


/// <summary>
/// Represents the type of vector store to use
/// </summary>
public enum VectorStoreType
{
    InMemory,
    Qdrant,
    AzureAISearch,
    Chroma,
    PostgreSQL // Future: pgvector support
}

/// <summary>
/// Defines fallback behavior when contextual function selection fails
/// </summary>
public enum FallbackMode
{
    UseAllFunctions,    // Safe fallback - agent works normally
    UseNoFunctions,     // Disable function calling for this turn
    ThrowException      // Fail fast for debugging
}

/// <summary>
/// Defines how to truncate context when it exceeds token limits
/// </summary>
public enum ContextTruncationStrategy
{
    KeepRecent,         // Keep most recent messages
    KeepRelevant,       // Keep messages with high keyword overlap (future)
    KeepImportant       // Keep system + user messages, summarize assistant (future)
}

/// <summary>
/// Distance metrics for vector similarity calculations
/// </summary>
public enum DistanceMetric
{
    Cosine,
    Euclidean,
    DotProduct
}

/// <summary>
/// Configuration for in-memory vector store
/// </summary>
public class InMemoryVectorStoreConfig
{
    public int? Dimensions { get; set; } // Auto-detected from embedding generator
    public DistanceMetric DistanceMetric { get; set; } = DistanceMetric.Cosine;
    public int InitialCapacity { get; set; } = 1000;
}

/// <summary>
/// Configuration for Qdrant vector store
/// </summary>
public class QdrantVectorStoreConfig
{
    public string ConnectionString { get; set; } = string.Empty;
    public string CollectionName { get; set; } = "agent-functions";
    public string? ApiKey { get; set; }
    public int? Dimensions { get; set; } // Auto-detected
}

/// <summary>
/// Configuration for Azure AI Search vector store
/// </summary>
public class AzureAISearchVectorStoreConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string IndexName { get; set; } = "agent-functions";
    public string? ApiKey { get; set; }
    public bool UseManagedIdentity { get; set; } = false;
    public int? Dimensions { get; set; } // Auto-detected
}

/// <summary>
/// Configuration for contextual function selection system
/// </summary>
public class ContextualFunctionConfig
{
    private Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>>? _embeddingGeneratorFactory;
    private bool _useRegisteredGenerator = false;
    private EmbeddingGeneratorConfigBuilder? _embeddingBuilderConfig;
    
    // === Embedding Generator Configuration ===
    
    /// <summary>
    /// Configure embedding generator with custom factory and middleware pipeline
    /// </summary>
    public EmbeddingGeneratorConfigBuilder WithEmbeddingGenerator(
        Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>> factory)
    {
        _embeddingGeneratorFactory = factory;
        _embeddingBuilderConfig = new EmbeddingGeneratorConfigBuilder(this);
        return _embeddingBuilderConfig;
    }
    
    /// <summary>
    /// Use embedding generator registered in DI container
    /// </summary>
    public ContextualFunctionConfig UseRegisteredEmbeddingGenerator()
    {
        _useRegisteredGenerator = true;
        return this;
    }
    
    // === Vector Store Configuration ===
    
    public ContextualFunctionConfig WithInMemoryVectorStore(
        Action<InMemoryVectorStoreConfig>? configure = null)
    {
        VectorStoreType = VectorStoreType.InMemory;
        InMemoryConfig = new InMemoryVectorStoreConfig();
        configure?.Invoke(InMemoryConfig);
        return this;
    }
    
    public ContextualFunctionConfig WithQdrantVectorStore(
        Action<QdrantVectorStoreConfig> configure)
    {
        VectorStoreType = VectorStoreType.Qdrant;
        QdrantConfig = new QdrantVectorStoreConfig();
        configure(QdrantConfig);
        return this;
    }
    
    public ContextualFunctionConfig WithAzureAISearchVectorStore(
        Action<AzureAISearchVectorStoreConfig> configure)
    {
        VectorStoreType = VectorStoreType.AzureAISearch;
        AzureAISearchConfig = new AzureAISearchVectorStoreConfig();
        configure(AzureAISearchConfig);
        return this;
    }
    
    // === Core Selection Settings ===
    
    public int MaxRelevantFunctions { get; set; } = 5;
    public float SimilarityThreshold { get; set; } = 0.7f;
    public int RecentMessageWindow { get; set; } = 3;
    public int ReevaluateEveryNTurns { get; set; } = 1;
    
    // === Error Handling ===
    
    public FallbackMode OnEmbeddingFailure { get; set; } = FallbackMode.UseAllFunctions;
    public FallbackMode OnVectorStoreFailure { get; set; } = FallbackMode.UseAllFunctions;
    
    // === Context Management ===
    
    public int MaxContextTokens { get; set; } = 8000;
    public ContextTruncationStrategy TruncationStrategy { get; set; } = ContextTruncationStrategy.KeepRecent;
    
    // === Advanced Customization ===
    
    public Func<IEnumerable<ChatMessage>, string>? CustomContextBuilder { get; set; }
    public Func<AIFunction, string>? CustomFunctionDescriptor { get; set; }
    
    // Internal properties for configuration
    internal VectorStoreType VectorStoreType { get; private set; }
    internal InMemoryVectorStoreConfig? InMemoryConfig { get; private set; }
    internal QdrantVectorStoreConfig? QdrantConfig { get; private set; }
    internal AzureAISearchVectorStoreConfig? AzureAISearchConfig { get; private set; }
    internal Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>>? EmbeddingGeneratorFactory => _embeddingGeneratorFactory;
    internal bool UseRegisteredGenerator => _useRegisteredGenerator;
    internal EmbeddingGeneratorConfigBuilder? EmbeddingBuilderConfig => _embeddingBuilderConfig;
}

/// <summary>
/// Builder for configuring embedding generator middleware pipeline using Extensions.AI patterns
/// </summary>
public class EmbeddingGeneratorConfigBuilder
{
    private readonly ContextualFunctionConfig _config;
    private readonly List<Func<EmbeddingGeneratorBuilder<string, Embedding<float>>, EmbeddingGeneratorBuilder<string, Embedding<float>>>> _middlewareConfig = new();
    
    internal EmbeddingGeneratorConfigBuilder(ContextualFunctionConfig config)
    {
        _config = config;
    }
    
    /// <summary>
    /// Add distributed caching to embedding generation (Extensions.AI built-in)
    /// </summary>
    public EmbeddingGeneratorConfigBuilder UseDistributedCache(IDistributedCache? cache = null)
    {
        _middlewareConfig.Add(builder => builder.UseDistributedCache(cache));
        return this;
    }
    
    /// <summary>
    /// Add OpenTelemetry tracing to embedding generation (Extensions.AI built-in)
    /// </summary>
    public EmbeddingGeneratorConfigBuilder UseOpenTelemetry(
        string? sourceName = null, 
        Action<OpenTelemetryEmbeddingGenerator<string, Embedding<float>>>? configure = null)
    {
        _middlewareConfig.Add(builder => builder.UseOpenTelemetry(sourceName: sourceName, configure: configure));
        return this;
    }
    
    /// <summary>
    /// Add custom middleware using Extensions.AI patterns
    /// </summary>
    public EmbeddingGeneratorConfigBuilder Use(
        Func<IEmbeddingGenerator<string, Embedding<float>>, IEmbeddingGenerator<string, Embedding<float>>> middleware)
    {
        _middlewareConfig.Add(builder => builder.Use(middleware));
        return this;
    }
    
    /// <summary>
    /// Return to main configuration
    /// </summary>
    public ContextualFunctionConfig And() => _config;
    
    internal void ConfigureBuilder(EmbeddingGeneratorBuilder<string, Embedding<float>> builder)
    {
        foreach (var middleware in _middlewareConfig)
        {
            middleware(builder);
        }
    }
}
