/// <summary>
/// Chat providers for Microsoft.Extensions.AI IChatClient (used in AgentBuilder)
/// </summary>
public enum ChatProvider
{
    // Native Extensions.AI support
    OpenAI,
    AzureOpenAI,
    AzureAIInference, // For the unified Azure AI endpoint
    
    // Custom implementations for Extensions.AI
    OpenRouter,
    Anthropic,
    // AppleIntelligence removed
    Ollama,
    GoogleAI,   // For the simple Gemini API via API Key
    VertexAI,   // For Google Cloud Vertex AI via ADC
    HuggingFace, // For Serverless Inference API
    Bedrock,     // For AWS Bedrock
    OnnxRuntime, // For local ONNX models
    Mistral      // For Mistral AI API
}

/// <summary>
/// Embedding providers for Microsoft.Extensions.AI IEmbeddingGenerator
/// </summary>
public enum EmbeddingProvider
{
    // Native Extensions.AI support
    OpenAI,
    AzureOpenAI,

    // Custom or third-party implementations
    VoyageAI,
    Anthropic,
    Cohere,
    HuggingFace,
    ONNX,
    LocalEmbeddings
}

/// <summary>
/// Vector store providers supported by Kernel Memory
/// </summary>
public enum VectorStoreProvider
{
    // Native Kernel Memory support
    InMemory,
    SimpleVectorDb,
    AzureAISearch,
    Qdrant,
    Redis,
    Elasticsearch,
    Postgres,
    SqlServer,
    MongoDBAtlas,

    // Future potential additions
    Pinecone,
    Weaviate,
    Chroma,
    Milvus,
    DuckDB,
    SQLite,
    
    
}

/// <summary>
/// Storage types for Agent Memory (build-time, read-only, optimized)
/// </summary>
public enum AgentStorageType
{
    InMemory,
    SimpleVectorDb,
    Qdrant,
    AzureAISearch,
    Pinecone,
    Postgres,
    Redis,
    Elasticsearch,
    MongoDBAtlas
}