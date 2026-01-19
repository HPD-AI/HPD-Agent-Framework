# HPD.MRAG (Modular RAG) - Design Proposal

**Date:** January 19, 2026
**Status:** Draft Proposal
**Version:** 1.0
**Author:** Claude (consolidated from discussion)

---

## Executive Summary

HPD.MRAG is a **modular retrieval-augmented generation framework** built on HPD.Graph that competes with Haystack. It leverages:

- **Microsoft.Extensions.AI** (`IChatClient`, `IEmbeddingGenerator`) for LLM/embedding abstractions
- **Microsoft.Extensions.VectorData** (`IVectorStore`) for vector database abstraction
- **HPD.Graph** for orchestration (cycles, parallelism, caching, artifacts, partitioning)
- **Hybrid configuration** with Provider Aliases for DRY + serializable configs

**Key Differentiators from Haystack:**
- First-class incremental processing (only re-embed changed documents)
- Artifact lineage tracking (document → chunks → embeddings provenance)
- Native partitioning (multi-tenant RAG out of the box)
- Content-addressable caching (massive cost savings)
- Demand-driven materialization (`MaterializeAsync`)
- Type-safe C# with compile-time validation

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Core Abstractions](#2-core-abstractions)
3. [Configuration System](#3-configuration-system)
4. [Provider System](#4-provider-system)
5. [Handler Implementations](#5-handler-implementations)
6. [Builder APIs](#6-builder-apis)
7. [HPD.Graph Feature Mapping](#7-hpdgraph-feature-mapping)
8. [Preset System](#8-preset-system)
9. [Usage Examples](#9-usage-examples)
10. [Package Structure](#10-package-structure)
11. [Implementation Roadmap](#11-implementation-roadmap)

---

## 1. Architecture Overview

### 1.1 Layered Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         USER CODE                                │
│  - Presets (BasicRAG.Create(), CorrectiveRAG.Create())          │
│  - Config files (JSON)                                          │
│  - Builder APIs (IngestionPipelineBuilder, RetrievalPipelineBuilder)
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                      HPD.MRAG.Core                               │
│  ┌────────────────┐  ┌────────────────┐  ┌────────────────┐    │
│  │ Configuration  │  │   Builders     │  │   Handlers     │    │
│  │ - Hybrid config│  │ - Ingestion    │  │ - Converter    │    │
│  │ - Validation   │  │ - Retrieval    │  │ - Splitter     │    │
│  │ - Aliases      │  │ - Wraps Graph  │  │ - Embedder     │    │
│  └────────────────┘  └────────────────┘  │ - Retriever    │    │
│  ┌────────────────┐  ┌────────────────┐  │ - Reranker     │    │
│  │   Providers    │  │    Stores      │  │ - Generator    │    │
│  │ - Factory reg  │  │ - IDocStore    │  └────────────────┘    │
│  │ - Alias reg    │  │ - IGraphStore  │                        │
│  └────────────────┘  └────────────────┘                        │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                        HPD.Graph                                 │
│  - Orchestration (DAG, cycles, parallel execution)              │
│  - Artifact registry & lineage tracking                         │
│  - Partitioning (multi-tenant, time-based)                      │
│  - Content-addressable caching                                  │
│  - Incremental execution (change detection)                     │
│  - Demand-driven materialization                                │
│  - Temporal operators (delays, schedules)                       │
│  - Checkpointing & suspension                                   │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                   External Abstractions                          │
│  ┌─────────────────────┐  ┌─────────────────────┐              │
│  │ Microsoft.Extensions│  │ Microsoft.Extensions│              │
│  │ .AI                 │  │ .VectorData         │              │
│  │ - IChatClient       │  │ - IVectorStore      │              │
│  │ - IEmbeddingGen     │  │                     │              │
│  └─────────────────────┘  └─────────────────────┘              │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                  Provider Implementations                        │
│  HPD.MRAG.Providers.OpenAI    (embedders, generators)           │
│  HPD.MRAG.Providers.Cohere    (embedders, rerankers)            │
│  HPD.MRAG.Providers.Qdrant    (vector store)                    │
│  HPD.MRAG.Providers.Pinecone  (vector store)                    │
│  HPD.MRAG.Converters          (PDF, DOCX, HTML)                 │
│  HPD.MRAG.Splitters           (recursive, semantic)             │
└─────────────────────────────────────────────────────────────────┘
```

### 1.2 Two Pipeline Types

RAG systems have two distinct lifecycles:

| Pipeline | Purpose | Lifecycle | Key Operations |
|----------|---------|-----------|----------------|
| **Ingestion** | Index documents | Batch, scheduled | Convert → Split → Embed → Index |
| **Retrieval** | Answer queries | Real-time, on-demand | Embed Query → Retrieve → Rerank → Generate |

```
INGESTION PIPELINE (batch)
┌──────────┐   ┌──────────┐   ┌──────────┐   ┌──────────┐
│ Convert  │ → │  Split   │ → │  Embed   │ → │  Index   │
│ PDF→Doc  │   │ Chunking │   │ Vectors  │   │ VectorDB │
└──────────┘   └──────────┘   └──────────┘   └──────────┘

RETRIEVAL PIPELINE (real-time)
┌──────────┐   ┌──────────┐   ┌──────────┐   ┌──────────┐
│  Embed   │ → │ Retrieve │ → │ Rerank   │ → │ Generate │
│  Query   │   │ Top-K    │   │ Top-N    │   │ Answer   │
└──────────┘   └──────────┘   └──────────┘   └──────────┘
```

---

## 2. Core Abstractions

### 2.1 From Microsoft (Use Directly)

```csharp
// Microsoft.Extensions.AI
namespace Microsoft.Extensions.AI;

public interface IChatClient
{
    Task<ChatCompletion> CompleteAsync(
        IList<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface IEmbeddingGenerator<TInput, TEmbedding>
{
    Task<GeneratedEmbeddings<TEmbedding>> GenerateAsync(
        IEnumerable<TInput> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default);
}

// Microsoft.Extensions.VectorData
namespace Microsoft.Extensions.VectorData;

public interface IVectorStore
{
    IVectorStoreRecordCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name,
        VectorStoreRecordDefinition? vectorStoreRecordDefinition = null);
}
```

### 2.2 HPD.MRAG Abstractions

```csharp
namespace HPD.MRAG.Abstractions;

//
// DOCUMENT MODEL
//

/// <summary>
/// Represents a document in the RAG system.
/// </summary>
public record Document
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public string? ContentType { get; init; }  // "application/pdf", "text/plain"
    public string? SourceUri { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Represents a chunk of a document after splitting.
/// </summary>
public record DocumentChunk
{
    public required string Id { get; init; }
    public required string DocumentId { get; init; }  // Parent document
    public required string Content { get; init; }
    public int ChunkIndex { get; init; }
    public int StartOffset { get; init; }
    public int EndOffset { get; init; }
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Represents a chunk with its embedding vector.
/// </summary>
public record EmbeddedChunk
{
    public required DocumentChunk Chunk { get; init; }
    public required ReadOnlyMemory<float> Embedding { get; init; }
}

/// <summary>
/// Represents a retrieved result with relevance score.
/// </summary>
public record RetrievedDocument
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public required float Score { get; init; }
    public string? DocumentId { get; init; }
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

//
// STORE INTERFACES
//

/// <summary>
/// Store for raw documents (before processing).
/// </summary>
public interface IDocumentStore
{
    Task<Document?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> GetManyAsync(IEnumerable<string> ids, CancellationToken ct = default);
    Task UpsertAsync(Document document, CancellationToken ct = default);
    Task UpsertManyAsync(IEnumerable<Document> documents, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    IAsyncEnumerable<Document> ListAsync(DocumentFilter? filter = null, CancellationToken ct = default);
    Task<bool> ExistsAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// Store for knowledge graphs (GraphRAG scenarios).
/// </summary>
public interface IGraphStore
{
    Task<IReadOnlyList<GraphNode>> GetNodesAsync(string query, int limit = 10, CancellationToken ct = default);
    Task<IReadOnlyList<GraphEdge>> GetEdgesAsync(string nodeId, CancellationToken ct = default);
    Task UpsertNodeAsync(GraphNode node, CancellationToken ct = default);
    Task UpsertEdgeAsync(GraphEdge edge, CancellationToken ct = default);
    Task<IReadOnlyList<GraphPath>> TraverseAsync(string startNodeId, int maxDepth = 3, CancellationToken ct = default);
}

public record GraphNode(string Id, string Label, IReadOnlyDictionary<string, object>? Properties = null);
public record GraphEdge(string Id, string FromNodeId, string ToNodeId, string Relationship, IReadOnlyDictionary<string, object>? Properties = null);
public record GraphPath(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);

//
// PROCESSING INTERFACES
//

/// <summary>
/// Converts documents from one format to another (e.g., PDF → text).
/// </summary>
public interface IConverter
{
    string[] SupportedContentTypes { get; }
    Task<Document> ConvertAsync(Stream input, string contentType, string? sourceUri = null, CancellationToken ct = default);
    Task<Document> ConvertAsync(Document document, CancellationToken ct = default);
}

/// <summary>
/// Splits documents into chunks.
/// </summary>
public interface ISplitter
{
    Task<IReadOnlyList<DocumentChunk>> SplitAsync(Document document, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentChunk>> SplitAsync(string content, string documentId, CancellationToken ct = default);
}

/// <summary>
/// Reranks retrieved documents by relevance.
/// </summary>
public interface IReranker
{
    Task<IReadOnlyList<RetrievedDocument>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedDocument> documents,
        int topN,
        CancellationToken ct = default);
}

/// <summary>
/// Transforms queries (e.g., HyDE, multi-query expansion).
/// </summary>
public interface IQueryTransformer
{
    Task<IReadOnlyList<string>> TransformAsync(string query, CancellationToken ct = default);
}

/// <summary>
/// Post-processes retrieved results (filter, dedupe, augment).
/// </summary>
public interface IPostProcessor
{
    Task<IReadOnlyList<RetrievedDocument>> ProcessAsync(
        IReadOnlyList<RetrievedDocument> documents,
        CancellationToken ct = default);
}
```

---

## 3. Configuration System

### 3.1 Design Principles

The configuration system addresses the **5-dimensional configuration problem**:

| Dimension | Solution |
|-----------|----------|
| Handler Type | Role-based configs (`EmbedderConfig`, `RetrieverConfig`) |
| Provider | `Provider` field + factory registry |
| Provider Config | `ProviderAlias` (DRY) or `ProviderOptionsJson` (inline) |
| Handler Config | Service-agnostic fields on each config |
| Node Config | HPD.Graph integration (transparent) |

### 3.2 Provider Resolution

```
┌─────────────────────────────────────────────────────────────────┐
│                  PROVIDER ALIAS REGISTRY                         │
│  (DI-level, holds secrets, NOT serialized)                      │
│                                                                  │
│  "embedding-prod" → { Provider: "openai",                       │
│                       Options: { ApiKey: "sk-...", ... } }      │
│  "vector-prod"    → { Provider: "qdrant",                       │
│                       Options: { Url: "http://...", ... } }     │
└─────────────────────────────────────────────────────────────────┘
                              ↓ (lookup by alias)
┌─────────────────────────────────────────────────────────────────┐
│                     HANDLER CONFIG                               │
│  (Serializable to JSON, secrets-free)                           │
│                                                                  │
│  Provider = "openai"              ← Factory key                 │
│  ProviderAlias = "embedding-prod" ← Optional: pre-registered    │
│  ProviderOptionsJson = "..."      ← Optional: inline (for JSON) │
│  + Service-agnostic: BatchSize, Dimensions, ModelId...          │
└─────────────────────────────────────────────────────────────────┘
                              ↓ (resolved options)
┌─────────────────────────────────────────────────────────────────┐
│                  PROVIDER FACTORY REGISTRY                       │
│  (ModuleInitializer, creates instances)                         │
│                                                                  │
│  "openai" → OpenAIEmbedderFactory.CreateAsync(options)          │
│  "cohere" → CohereEmbedderFactory.CreateAsync(options)          │
└─────────────────────────────────────────────────────────────────┘
```

**Resolution Order:**
1. If `ProviderAlias` set → Lookup options from alias registry
2. Else if `ProviderOptionsJson` set → Deserialize inline options
3. Create provider instance via factory using `Provider` key

### 3.3 Top-Level Configs

```csharp
namespace HPD.MRAG.Configuration;

/// <summary>
/// Configuration for ingestion pipelines.
/// </summary>
public class IngestionPipelineConfig
{
    //
    // HANDLER CONFIGS (nullable - only configure what you need)
    //

    public ConverterConfig? Converter { get; set; }
    public SplitterConfig? Splitter { get; set; }
    public EmbedderConfig? Embedder { get; set; }
    public IndexerConfig? Indexer { get; set; }

    //
    // PIPELINE-LEVEL SETTINGS
    //

    /// <summary>
    /// Enable incremental processing (only process changed documents).
    /// Leverages HPD.Graph's content-addressable caching.
    /// </summary>
    public bool EnableIncremental { get; set; } = true;

    /// <summary>
    /// Enable artifact tracking for lineage (document → chunks → embeddings).
    /// </summary>
    public bool EnableArtifactTracking { get; set; } = true;

    /// <summary>
    /// Partition key for multi-tenant scenarios.
    /// </summary>
    public string? PartitionKey { get; set; }

    /// <summary>
    /// Maximum parallel documents to process.
    /// </summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>
    /// Global execution timeout.
    /// </summary>
    public TimeSpan? ExecutionTimeout { get; set; }

    //
    // ORCHESTRATION (for JSON configs)
    //

    public List<EdgeDefinition>? Edges { get; set; }
}

/// <summary>
/// Configuration for retrieval pipelines.
/// </summary>
public class RetrievalPipelineConfig
{
    //
    // HANDLER CONFIGS
    //

    public EmbedderConfig? QueryEmbedder { get; set; }
    public RetrieverConfig? Retriever { get; set; }
    public RerankerConfig? Reranker { get; set; }
    public GeneratorConfig? Generator { get; set; }
    public QueryTransformerConfig? QueryTransformer { get; set; }

    //
    // PIPELINE-LEVEL SETTINGS
    //

    /// <summary>
    /// Default top-K for retrieval.
    /// </summary>
    public int TopK { get; set; } = 10;

    /// <summary>
    /// Default top-N after reranking.
    /// </summary>
    public int TopN { get; set; } = 5;

    /// <summary>
    /// Score threshold for filtering results.
    /// </summary>
    public float? ScoreThreshold { get; set; }

    /// <summary>
    /// Maximum iterations for corrective RAG loops.
    /// </summary>
    public int MaxIterations { get; set; } = 3;

    /// <summary>
    /// Partition key for multi-tenant retrieval.
    /// </summary>
    public string? PartitionKey { get; set; }

    /// <summary>
    /// Execution timeout for the query.
    /// </summary>
    public TimeSpan? ExecutionTimeout { get; set; }

    //
    // ORCHESTRATION
    //

    public List<EdgeDefinition>? Edges { get; set; }
}
```

### 3.4 Handler Configs

```csharp
namespace HPD.MRAG.Configuration;

/// <summary>
/// Base class for all handler configurations.
/// </summary>
public abstract class HandlerConfigBase
{
    /// <summary>
    /// Provider type key (e.g., "openai", "cohere", "qdrant").
    /// Used to lookup the factory in the provider registry.
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Optional: Reference to a pre-registered provider alias.
    /// If set, provider options are loaded from the alias registry.
    /// This keeps secrets out of serialized configs.
    /// </summary>
    public string? ProviderAlias { get; set; }

    /// <summary>
    /// Optional: Inline provider-specific options as JSON.
    /// Used when ProviderAlias is not set (e.g., for JSON config files).
    /// </summary>
    public string? ProviderOptionsJson { get; set; }

    /// <summary>
    /// Validates the configuration.
    /// </summary>
    public virtual void Validate()
    {
        if (string.IsNullOrWhiteSpace(Provider))
            throw new ArgumentException("Provider is required", nameof(Provider));
    }
}

/// <summary>
/// Configuration for embedding operations.
/// </summary>
public class EmbedderConfig : HandlerConfigBase
{
    //
    // SERVICE-AGNOSTIC SETTINGS (work with ALL providers)
    //

    /// <summary>
    /// Model identifier (provider-specific).
    /// Examples: "text-embedding-3-small", "embed-english-v3.0"
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Batch size for processing multiple texts.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Output embedding dimensions.
    /// If null, uses provider's default.
    /// </summary>
    public int? Dimensions { get; set; }

    /// <summary>
    /// Normalize embeddings to unit length.
    /// </summary>
    public bool NormalizeEmbeddings { get; set; } = true;

    /// <summary>
    /// Text prefix (e.g., "query: " vs "passage: ").
    /// </summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Maximum text length before truncation.
    /// </summary>
    public int? MaxTextLength { get; set; }
}

/// <summary>
/// Configuration for document conversion.
/// </summary>
public class ConverterConfig : HandlerConfigBase
{
    /// <summary>
    /// Supported content types to convert.
    /// </summary>
    public string[]? SupportedContentTypes { get; set; }

    /// <summary>
    /// Extract images from documents.
    /// </summary>
    public bool ExtractImages { get; set; } = false;

    /// <summary>
    /// Extract tables from documents.
    /// </summary>
    public bool ExtractTables { get; set; } = true;

    /// <summary>
    /// OCR for scanned documents.
    /// </summary>
    public bool EnableOcr { get; set; } = false;
}

/// <summary>
/// Configuration for document splitting.
/// </summary>
public class SplitterConfig : HandlerConfigBase
{
    /// <summary>
    /// Target chunk size (characters or tokens based on provider).
    /// </summary>
    public int ChunkSize { get; set; } = 512;

    /// <summary>
    /// Overlap between chunks.
    /// </summary>
    public int ChunkOverlap { get; set; } = 50;

    /// <summary>
    /// Splitting strategy: "recursive", "sentence", "paragraph", "semantic"
    /// </summary>
    public string Strategy { get; set; } = "recursive";

    /// <summary>
    /// Custom separators for recursive splitting.
    /// </summary>
    public string[]? Separators { get; set; }

    /// <summary>
    /// Preserve document metadata in chunks.
    /// </summary>
    public bool PreserveMetadata { get; set; } = true;
}

/// <summary>
/// Configuration for vector retrieval.
/// </summary>
public class RetrieverConfig : HandlerConfigBase
{
    /// <summary>
    /// Collection/index name in the vector store.
    /// </summary>
    public string? CollectionName { get; set; }

    /// <summary>
    /// Number of results to retrieve.
    /// </summary>
    public int TopK { get; set; } = 10;

    /// <summary>
    /// Minimum score threshold.
    /// </summary>
    public float? ScoreThreshold { get; set; }

    /// <summary>
    /// Include metadata in results.
    /// </summary>
    public bool IncludeMetadata { get; set; } = true;

    /// <summary>
    /// Metadata filters for the query.
    /// </summary>
    public Dictionary<string, object>? Filters { get; set; }
}

/// <summary>
/// Configuration for reranking.
/// </summary>
public class RerankerConfig : HandlerConfigBase
{
    /// <summary>
    /// Model identifier for reranking.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Number of documents to return after reranking.
    /// </summary>
    public int TopN { get; set; } = 5;

    /// <summary>
    /// Maximum documents to send to reranker.
    /// </summary>
    public int? MaxDocuments { get; set; }
}

/// <summary>
/// Configuration for LLM generation.
/// </summary>
public class GeneratorConfig : HandlerConfigBase
{
    /// <summary>
    /// Model identifier.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Temperature for generation.
    /// </summary>
    public float Temperature { get; set; } = 0.7f;

    /// <summary>
    /// Maximum tokens to generate.
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// System prompt for the LLM.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Prompt template with placeholders: {query}, {context}
    /// </summary>
    public string? PromptTemplate { get; set; }

    /// <summary>
    /// Enable streaming responses.
    /// </summary>
    public bool EnableStreaming { get; set; } = false;
}

/// <summary>
/// Configuration for indexing to vector store.
/// </summary>
public class IndexerConfig : HandlerConfigBase
{
    /// <summary>
    /// Collection/index name.
    /// </summary>
    public string? CollectionName { get; set; }

    /// <summary>
    /// Create collection if it doesn't exist.
    /// </summary>
    public bool CreateIfNotExists { get; set; } = true;

    /// <summary>
    /// Batch size for indexing.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Upsert mode (update if exists).
    /// </summary>
    public bool Upsert { get; set; } = true;
}

/// <summary>
/// Configuration for query transformation.
/// </summary>
public class QueryTransformerConfig : HandlerConfigBase
{
    /// <summary>
    /// Transformation strategy: "hyde", "multi-query", "step-back"
    /// </summary>
    public string Strategy { get; set; } = "none";

    /// <summary>
    /// Number of query variations to generate.
    /// </summary>
    public int NumQueries { get; set; } = 3;

    /// <summary>
    /// Model for query generation.
    /// </summary>
    public string? ModelId { get; set; }
}
```

---

## 4. Provider System

### 4.1 Provider Alias Registry

```csharp
namespace HPD.MRAG.Providers;

/// <summary>
/// Registry for provider aliases.
/// Enables DRY configuration by registering provider options once.
/// </summary>
public interface IProviderAliasRegistry
{
    void Register<TOptions>(string alias, string provider, TOptions options)
        where TOptions : class;

    bool TryGet(string alias, out ProviderAliasEntry entry);

    IReadOnlyList<string> GetAliases();
}

public record ProviderAliasEntry(
    string Provider,
    object Options
);

/// <summary>
/// Default implementation using ConcurrentDictionary.
/// </summary>
public class ProviderAliasRegistry : IProviderAliasRegistry
{
    private readonly ConcurrentDictionary<string, ProviderAliasEntry> _aliases = new();

    public void Register<TOptions>(string alias, string provider, TOptions options)
        where TOptions : class
    {
        _aliases[alias] = new ProviderAliasEntry(provider, options);
    }

    public bool TryGet(string alias, out ProviderAliasEntry entry)
    {
        return _aliases.TryGetValue(alias, out entry!);
    }

    public IReadOnlyList<string> GetAliases() => _aliases.Keys.ToList();
}

/// <summary>
/// Extension methods for DI registration.
/// </summary>
public static class ProviderAliasExtensions
{
    public static IServiceCollection AddMRAGProviderAliases(
        this IServiceCollection services,
        Action<IProviderAliasRegistry> configure)
    {
        var registry = new ProviderAliasRegistry();
        configure(registry);
        services.AddSingleton<IProviderAliasRegistry>(registry);
        return services;
    }
}
```

### 4.2 Provider Factory Registry

```csharp
namespace HPD.MRAG.Providers;

/// <summary>
/// Factory interface for creating embedder instances.
/// </summary>
public interface IEmbedderFactory
{
    string ProviderKey { get; }

    Task<IEmbeddingGenerator<string, Embedding<float>>> CreateAsync(
        EmbedderConfig config,
        object? providerOptions,
        CancellationToken ct = default);
}

/// <summary>
/// Factory interface for creating retriever instances.
/// </summary>
public interface IRetrieverFactory
{
    string ProviderKey { get; }

    Task<IVectorStoreRecordCollection<string, VectorRecord>> CreateAsync(
        RetrieverConfig config,
        object? providerOptions,
        CancellationToken ct = default);
}

/// <summary>
/// Factory interface for creating reranker instances.
/// </summary>
public interface IRerankerFactory
{
    string ProviderKey { get; }

    Task<IReranker> CreateAsync(
        RerankerConfig config,
        object? providerOptions,
        CancellationToken ct = default);
}

/// <summary>
/// Factory interface for creating generator (LLM) instances.
/// </summary>
public interface IGeneratorFactory
{
    string ProviderKey { get; }

    Task<IChatClient> CreateAsync(
        GeneratorConfig config,
        object? providerOptions,
        CancellationToken ct = default);
}

/// <summary>
/// Central registry for all provider factories.
/// Uses ModuleInitializer for auto-registration.
/// </summary>
public static class ProviderFactoryRegistry
{
    private static readonly ConcurrentDictionary<string, IEmbedderFactory> _embedders = new();
    private static readonly ConcurrentDictionary<string, IRetrieverFactory> _retrievers = new();
    private static readonly ConcurrentDictionary<string, IRerankerFactory> _rerankers = new();
    private static readonly ConcurrentDictionary<string, IGeneratorFactory> _generators = new();

    public static void RegisterEmbedder(IEmbedderFactory factory) =>
        _embedders[factory.ProviderKey] = factory;

    public static void RegisterRetriever(IRetrieverFactory factory) =>
        _retrievers[factory.ProviderKey] = factory;

    public static void RegisterReranker(IRerankerFactory factory) =>
        _rerankers[factory.ProviderKey] = factory;

    public static void RegisterGenerator(IGeneratorFactory factory) =>
        _generators[factory.ProviderKey] = factory;

    public static IEmbedderFactory GetEmbedderFactory(string provider) =>
        _embedders.TryGetValue(provider, out var f) ? f
            : throw new InvalidOperationException($"Embedder provider '{provider}' not found");

    public static IRetrieverFactory GetRetrieverFactory(string provider) =>
        _retrievers.TryGetValue(provider, out var f) ? f
            : throw new InvalidOperationException($"Retriever provider '{provider}' not found");

    public static IRerankerFactory GetRerankerFactory(string provider) =>
        _rerankers.TryGetValue(provider, out var f) ? f
            : throw new InvalidOperationException($"Reranker provider '{provider}' not found");

    public static IGeneratorFactory GetGeneratorFactory(string provider) =>
        _generators.TryGetValue(provider, out var f) ? f
            : throw new InvalidOperationException($"Generator provider '{provider}' not found");
}
```

### 4.3 Provider Options Resolution

```csharp
namespace HPD.MRAG.Providers;

/// <summary>
/// Resolves provider options from alias or inline JSON.
/// </summary>
public class ProviderOptionsResolver
{
    private readonly IProviderAliasRegistry _aliasRegistry;

    public ProviderOptionsResolver(IProviderAliasRegistry aliasRegistry)
    {
        _aliasRegistry = aliasRegistry;
    }

    /// <summary>
    /// Resolves provider options for a handler config.
    /// </summary>
    public (string Provider, object? Options) Resolve<TConfig>(TConfig config)
        where TConfig : HandlerConfigBase
    {
        // Priority 1: Alias lookup
        if (!string.IsNullOrWhiteSpace(config.ProviderAlias))
        {
            if (_aliasRegistry.TryGet(config.ProviderAlias, out var entry))
            {
                return (entry.Provider, entry.Options);
            }
            throw new InvalidOperationException(
                $"Provider alias '{config.ProviderAlias}' not found in registry");
        }

        // Priority 2: Inline JSON
        if (!string.IsNullOrWhiteSpace(config.ProviderOptionsJson))
        {
            // Provider factory will deserialize based on its expected type
            return (config.Provider, config.ProviderOptionsJson);
        }

        // Priority 3: No options (provider may have defaults)
        return (config.Provider, null);
    }
}
```

---

## 5. Handler Implementations

### 5.1 MRAG Context

```csharp
namespace HPD.MRAG.Handlers;

/// <summary>
/// Execution context for MRAG handlers.
/// Extends GraphContext with MRAG-specific functionality.
/// </summary>
public class MRAGContext : GraphContext
{
    private readonly IProviderAliasRegistry _aliasRegistry;
    private readonly ProviderOptionsResolver _optionsResolver;

    // Cached provider instances (created on first use)
    private readonly ConcurrentDictionary<string, object> _providerCache = new();

    public MRAGContext(
        string executionId,
        Graph graph,
        IServiceProvider services,
        IProviderAliasRegistry aliasRegistry,
        IGraphChannelSet? channels = null,
        IManagedContext? managed = null)
        : base(executionId, graph, services, channels, managed, enableSharedData: true)
    {
        _aliasRegistry = aliasRegistry;
        _optionsResolver = new ProviderOptionsResolver(aliasRegistry);
    }

    /// <summary>
    /// Resolves provider options for a config.
    /// </summary>
    public (string Provider, object? Options) ResolveProviderOptions<TConfig>(TConfig config)
        where TConfig : HandlerConfigBase
    {
        return _optionsResolver.Resolve(config);
    }

    /// <summary>
    /// Gets or creates a cached provider instance.
    /// </summary>
    public async Task<T> GetOrCreateProviderAsync<T>(
        string cacheKey,
        Func<Task<T>> factory)
        where T : class
    {
        if (_providerCache.TryGetValue(cacheKey, out var cached))
        {
            return (T)cached;
        }

        var instance = await factory();
        _providerCache[cacheKey] = instance;
        return instance;
    }

    public override IGraphContext CreateIsolatedCopy()
    {
        var copy = new MRAGContext(
            ExecutionId,
            Graph,
            Services,
            _aliasRegistry,
            CloneChannelsInternal(),
            Managed)
        {
            CurrentLayerIndex = CurrentLayerIndex,
            EventCoordinator = EventCoordinator
        };

        // Copy execution state
        foreach (var nodeId in CompletedNodes)
        {
            copy.MarkNodeComplete(nodeId);
        }

        // Share provider cache (providers are thread-safe)
        foreach (var kvp in _providerCache)
        {
            copy._providerCache[kvp.Key] = kvp.Value;
        }

        // Copy shared data
        if (SharedData != null && copy.SharedData != null)
        {
            foreach (var kvp in SharedData)
            {
                copy.SharedData[kvp.Key] = kvp.Value;
            }
        }

        return copy;
    }
}
```

### 5.2 Embedder Handler

```csharp
namespace HPD.MRAG.Handlers;

/// <summary>
/// Handler for embedding text using configured provider.
/// Supports both single text and batch embedding.
/// </summary>
public class EmbedderHandler : IGraphNodeHandler<MRAGContext>
{
    public string HandlerName => "MRAGEmbedderHandler";

    public async Task<NodeExecutionResult> ExecuteAsync(
        MRAGContext context,
        HandlerInputs inputs,
        CancellationToken ct)
    {
        try
        {
            // 1. Get config from node
            var config = inputs.GetConfig<EmbedderConfig>("config")
                ?? throw new InvalidOperationException("EmbedderConfig not found");

            config.Validate();

            // 2. Resolve provider options
            var (provider, options) = context.ResolveProviderOptions(config);

            // 3. Get or create embedder instance
            var cacheKey = $"embedder:{context.CurrentNodeId}";
            var embedder = await context.GetOrCreateProviderAsync(cacheKey, async () =>
            {
                var factory = ProviderFactoryRegistry.GetEmbedderFactory(provider);
                return await factory.CreateAsync(config, options, ct);
            });

            // 4. Get input - support multiple input types
            List<string> textsToEmbed;

            if (inputs.TryGetOutput<string>("query", out var query))
            {
                // Single query embedding
                textsToEmbed = new List<string> { ApplyPrefixSuffix(query, config) };
            }
            else if (inputs.TryGetOutput<List<DocumentChunk>>("chunks", out var chunks))
            {
                // Batch chunk embedding
                textsToEmbed = chunks.Select(c => ApplyPrefixSuffix(c.Content, config)).ToList();
            }
            else if (inputs.TryGetOutput<List<string>>("texts", out var texts))
            {
                // Generic batch
                textsToEmbed = texts.Select(t => ApplyPrefixSuffix(t, config)).ToList();
            }
            else
            {
                throw new InvalidOperationException(
                    "EmbedderHandler requires 'query' (string), 'chunks' (List<DocumentChunk>), or 'texts' (List<string>)");
            }

            // 5. Embed in batches
            var allEmbeddings = new List<Embedding<float>>();

            for (int i = 0; i < textsToEmbed.Count; i += config.BatchSize)
            {
                var batch = textsToEmbed.Skip(i).Take(config.BatchSize);
                var result = await embedder.GenerateAsync(batch, cancellationToken: ct);
                allEmbeddings.AddRange(result);
            }

            // 6. Write output
            if (textsToEmbed.Count == 1)
            {
                // Single embedding
                context.Channels.Write("embedding", allEmbeddings[0].Vector);
            }
            else
            {
                // Batch embeddings
                var embeddedChunks = chunks!.Zip(allEmbeddings, (chunk, emb) =>
                    new EmbeddedChunk { Chunk = chunk, Embedding = emb.Vector }).ToList();
                context.Channels.Write("embeddedChunks", embeddedChunks);
            }

            return NodeExecutionResult.Success();
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Failure(ex);
        }
    }

    private static string ApplyPrefixSuffix(string text, EmbedderConfig config)
    {
        var result = text;
        if (!string.IsNullOrEmpty(config.Prefix))
            result = config.Prefix + result;
        return result;
    }
}
```

### 5.3 Retriever Handler

```csharp
namespace HPD.MRAG.Handlers;

/// <summary>
/// Handler for vector retrieval.
/// </summary>
public class RetrieverHandler : IGraphNodeHandler<MRAGContext>
{
    public string HandlerName => "MRAGRetrieverHandler";

    public async Task<NodeExecutionResult> ExecuteAsync(
        MRAGContext context,
        HandlerInputs inputs,
        CancellationToken ct)
    {
        try
        {
            var config = inputs.GetConfig<RetrieverConfig>("config")
                ?? throw new InvalidOperationException("RetrieverConfig not found");

            config.Validate();

            var (provider, options) = context.ResolveProviderOptions(config);

            var cacheKey = $"retriever:{context.CurrentNodeId}";
            var collection = await context.GetOrCreateProviderAsync(cacheKey, async () =>
            {
                var factory = ProviderFactoryRegistry.GetRetrieverFactory(provider);
                return await factory.CreateAsync(config, options, ct);
            });

            // Get query embedding
            var queryEmbedding = inputs.GetOutput<ReadOnlyMemory<float>>("embedding");

            // Search
            var searchOptions = new VectorSearchOptions
            {
                Top = config.TopK,
                Filter = config.Filters != null
                    ? new VectorSearchFilter(config.Filters)
                    : null
            };

            var results = await collection.VectorizedSearchAsync(
                queryEmbedding,
                searchOptions,
                ct);

            // Convert to RetrievedDocument
            var documents = new List<RetrievedDocument>();
            await foreach (var result in results.Results)
            {
                if (config.ScoreThreshold.HasValue && result.Score < config.ScoreThreshold)
                    continue;

                documents.Add(new RetrievedDocument
                {
                    Id = result.Record.Key,
                    Content = result.Record.Content,
                    Score = (float)result.Score!,
                    DocumentId = result.Record.DocumentId,
                    Metadata = result.Record.Metadata
                });
            }

            context.Channels.Write("documents", documents);

            // Write quality indicator for routing (e.g., Corrective RAG)
            var avgScore = documents.Any() ? documents.Average(d => d.Score) : 0f;
            context.Channels.Write("retrievalQuality", avgScore > 0.8f ? "high" : "low");

            return NodeExecutionResult.Success();
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Failure(ex);
        }
    }
}
```

### 5.4 Reranker Handler

```csharp
namespace HPD.MRAG.Handlers;

/// <summary>
/// Handler for reranking retrieved documents.
/// </summary>
public class RerankerHandler : IGraphNodeHandler<MRAGContext>
{
    public string HandlerName => "MRAGRerankerHandler";

    public async Task<NodeExecutionResult> ExecuteAsync(
        MRAGContext context,
        HandlerInputs inputs,
        CancellationToken ct)
    {
        try
        {
            var config = inputs.GetConfig<RerankerConfig>("config")
                ?? throw new InvalidOperationException("RerankerConfig not found");

            config.Validate();

            var (provider, options) = context.ResolveProviderOptions(config);

            var cacheKey = $"reranker:{context.CurrentNodeId}";
            var reranker = await context.GetOrCreateProviderAsync(cacheKey, async () =>
            {
                var factory = ProviderFactoryRegistry.GetRerankerFactory(provider);
                return await factory.CreateAsync(config, options, ct);
            });

            var query = inputs.GetOutput<string>("query");
            var documents = inputs.GetOutput<List<RetrievedDocument>>("documents");

            // Limit input to reranker if configured
            var docsToRerank = config.MaxDocuments.HasValue
                ? documents.Take(config.MaxDocuments.Value).ToList()
                : documents;

            var reranked = await reranker.RerankAsync(query, docsToRerank, config.TopN, ct);

            context.Channels.Write("documents", reranked);

            return NodeExecutionResult.Success();
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Failure(ex);
        }
    }
}
```

### 5.5 Generator Handler

```csharp
namespace HPD.MRAG.Handlers;

/// <summary>
/// Handler for LLM-based answer generation.
/// </summary>
public class GeneratorHandler : IGraphNodeHandler<MRAGContext>
{
    public string HandlerName => "MRAGGeneratorHandler";

    private const string DefaultPromptTemplate = """
        You are a helpful assistant. Answer the question based only on the provided context.
        If the context doesn't contain relevant information, say so.

        Context:
        {context}

        Question: {query}

        Answer:
        """;

    public async Task<NodeExecutionResult> ExecuteAsync(
        MRAGContext context,
        HandlerInputs inputs,
        CancellationToken ct)
    {
        try
        {
            var config = inputs.GetConfig<GeneratorConfig>("config")
                ?? throw new InvalidOperationException("GeneratorConfig not found");

            config.Validate();

            var (provider, options) = context.ResolveProviderOptions(config);

            var cacheKey = $"generator:{context.CurrentNodeId}";
            var chatClient = await context.GetOrCreateProviderAsync(cacheKey, async () =>
            {
                var factory = ProviderFactoryRegistry.GetGeneratorFactory(provider);
                return await factory.CreateAsync(config, options, ct);
            });

            var query = inputs.GetOutput<string>("query");
            var documents = inputs.GetOutput<List<RetrievedDocument>>("documents");

            // Build context from documents
            var contextText = string.Join("\n\n", documents.Select((d, i) =>
                $"[Document {i + 1}] (Score: {d.Score:F2})\n{d.Content}"));

            // Build prompt
            var template = config.PromptTemplate ?? DefaultPromptTemplate;
            var prompt = template
                .Replace("{context}", contextText)
                .Replace("{query}", query);

            // Build messages
            var messages = new List<ChatMessage>();

            if (!string.IsNullOrWhiteSpace(config.SystemPrompt))
            {
                messages.Add(new ChatMessage(ChatRole.System, config.SystemPrompt));
            }

            messages.Add(new ChatMessage(ChatRole.User, prompt));

            // Generate
            var chatOptions = new ChatOptions
            {
                Temperature = config.Temperature,
                MaxOutputTokens = config.MaxTokens
            };

            var response = await chatClient.CompleteAsync(messages, chatOptions, ct);
            var answer = response.Message.Text ?? string.Empty;

            context.Channels.Write("answer", answer);

            // Also write sources for citation
            var sources = documents.Select(d => new { d.Id, d.DocumentId, d.Score }).ToList();
            context.Channels.Write("sources", sources);

            return NodeExecutionResult.Success();
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Failure(ex);
        }
    }
}
```

---

## 6. Builder APIs

### 6.1 Ingestion Pipeline Builder

```csharp
namespace HPD.MRAG.Builders;

/// <summary>
/// Fluent builder for creating document ingestion pipelines.
/// </summary>
public class IngestionPipelineBuilder
{
    private readonly GraphBuilder _graphBuilder;
    private readonly IngestionPipelineConfig _config;

    private readonly Dictionary<string, ConverterConfig> _converters = new();
    private readonly Dictionary<string, SplitterConfig> _splitters = new();
    private readonly Dictionary<string, EmbedderConfig> _embedders = new();
    private readonly Dictionary<string, IndexerConfig> _indexers = new();

    private readonly List<(string From, string To, EdgeCondition? Condition)> _edges = new();
    private bool _hasExplicitOrchestration = false;

    public IngestionPipelineBuilder()
    {
        _config = new IngestionPipelineConfig();
        _graphBuilder = new GraphBuilder();
    }

    public IngestionPipelineBuilder(IngestionPipelineConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _graphBuilder = new GraphBuilder();
        InitializeFromConfig();
    }

    //
    // HANDLER REGISTRATION
    //

    public IngestionPipelineBuilder AddConverter(string name, Action<ConverterConfig> configure)
    {
        var config = new ConverterConfig();
        configure(config);
        config.Validate();
        _converters[name] = config;
        return this;
    }

    public IngestionPipelineBuilder AddSplitter(string name, Action<SplitterConfig> configure)
    {
        var config = new SplitterConfig();
        configure(config);
        config.Validate();
        _splitters[name] = config;
        return this;
    }

    public IngestionPipelineBuilder AddEmbedder(string name, Action<EmbedderConfig> configure)
    {
        var config = new EmbedderConfig();
        configure(config);
        config.Validate();
        _embedders[name] = config;
        return this;
    }

    public IngestionPipelineBuilder AddIndexer(string name, Action<IndexerConfig> configure)
    {
        var config = new IndexerConfig();
        configure(config);
        config.Validate();
        _indexers[name] = config;
        return this;
    }

    //
    // CONVENIENCE METHODS (provider-specific shortcuts)
    //

    public IngestionPipelineBuilder AddRecursiveSplitter(
        string name = "splitter",
        int chunkSize = 512,
        int chunkOverlap = 50,
        string[]? separators = null)
    {
        return AddSplitter(name, cfg =>
        {
            cfg.Provider = "recursive";
            cfg.ChunkSize = chunkSize;
            cfg.ChunkOverlap = chunkOverlap;
            cfg.Strategy = "recursive";
            cfg.Separators = separators ?? new[] { "\n\n", "\n", ". ", " " };
        });
    }

    public IngestionPipelineBuilder AddOpenAIEmbedder(
        string name = "embedder",
        string? providerAlias = null,
        string model = "text-embedding-3-small",
        int batchSize = 100,
        int? dimensions = null)
    {
        return AddEmbedder(name, cfg =>
        {
            cfg.Provider = "openai";
            cfg.ProviderAlias = providerAlias;
            cfg.ModelId = model;
            cfg.BatchSize = batchSize;
            cfg.Dimensions = dimensions;
            cfg.Prefix = "passage: ";  // For asymmetric embeddings
        });
    }

    public IngestionPipelineBuilder AddQdrantIndexer(
        string name = "indexer",
        string? providerAlias = null,
        string? collectionName = null)
    {
        return AddIndexer(name, cfg =>
        {
            cfg.Provider = "qdrant";
            cfg.ProviderAlias = providerAlias;
            cfg.CollectionName = collectionName;
            cfg.CreateIfNotExists = true;
        });
    }

    //
    // ORCHESTRATION
    //

    public IngestionEdgeBuilder From(params string[] sourceNodes)
    {
        _hasExplicitOrchestration = true;
        return new IngestionEdgeBuilder(this, sourceNodes);
    }

    internal void AddEdge(string from, string to, EdgeCondition? condition = null)
    {
        _edges.Add((from, to, condition));
    }

    //
    // PIPELINE SETTINGS
    //

    public IngestionPipelineBuilder WithIncremental(bool enable = true)
    {
        _config.EnableIncremental = enable;
        return this;
    }

    public IngestionPipelineBuilder WithArtifactTracking(bool enable = true)
    {
        _config.EnableArtifactTracking = enable;
        return this;
    }

    public IngestionPipelineBuilder WithPartition(string partitionKey)
    {
        _config.PartitionKey = partitionKey;
        return this;
    }

    public IngestionPipelineBuilder WithMaxParallelism(int maxParallelism)
    {
        _config.MaxParallelism = maxParallelism;
        return this;
    }

    public IngestionPipelineBuilder WithTimeout(TimeSpan timeout)
    {
        _config.ExecutionTimeout = timeout;
        return this;
    }

    //
    // BUILD
    //

    public Graph Build()
    {
        _graphBuilder.WithName("IngestionPipeline");

        if (_config.ExecutionTimeout.HasValue)
            _graphBuilder.WithExecutionTimeout(_config.ExecutionTimeout.Value);

        // Add START/END
        _graphBuilder.AddStartNode();
        _graphBuilder.AddEndNode();

        // Add handler nodes
        AddHandlerNodes();

        // Add edges
        if (_hasExplicitOrchestration)
        {
            AddExplicitEdges();
        }
        else
        {
            AddDefaultSequentialEdges();
        }

        return _graphBuilder.Build();
    }

    private void AddHandlerNodes()
    {
        foreach (var (name, config) in _converters)
        {
            _graphBuilder.AddHandlerNode(name, $"Convert_{name}", "MRAGConverterHandler", node =>
            {
                node.WithConfig("config", config);
            });
        }

        foreach (var (name, config) in _splitters)
        {
            _graphBuilder.AddHandlerNode(name, $"Split_{name}", "MRAGSplitterHandler", node =>
            {
                node.WithConfig("config", config);
            });
        }

        foreach (var (name, config) in _embedders)
        {
            _graphBuilder.AddHandlerNode(name, $"Embed_{name}", "MRAGEmbedderHandler", node =>
            {
                node.WithConfig("config", config);

                // Enable artifact tracking if configured
                if (_config.EnableArtifactTracking)
                {
                    node.ProducesArtifact($"embeddings:{{documentId}}");
                    node.RequiresArtifacts($"chunks:{{documentId}}");
                }
            });
        }

        foreach (var (name, config) in _indexers)
        {
            _graphBuilder.AddHandlerNode(name, $"Index_{name}", "MRAGIndexerHandler", node =>
            {
                node.WithConfig("config", config);
            });
        }
    }

    private void AddDefaultSequentialEdges()
    {
        var stages = new List<string>();

        if (_converters.Any())
            stages.AddRange(_converters.Keys);
        if (_splitters.Any())
            stages.AddRange(_splitters.Keys);
        if (_embedders.Any())
            stages.AddRange(_embedders.Keys);
        if (_indexers.Any())
            stages.AddRange(_indexers.Keys);

        if (stages.Count == 0)
            throw new InvalidOperationException("No handlers registered");

        _graphBuilder.AddEdge("START", stages[0]);

        for (int i = 0; i < stages.Count - 1; i++)
        {
            _graphBuilder.AddEdge(stages[i], stages[i + 1]);
        }

        _graphBuilder.AddEdge(stages[^1], "END");
    }

    private void AddExplicitEdges()
    {
        foreach (var (from, to, condition) in _edges)
        {
            if (condition != null)
                _graphBuilder.AddEdge(from, to, e => e.WithCondition(condition));
            else
                _graphBuilder.AddEdge(from, to);
        }
    }

    private void InitializeFromConfig()
    {
        if (_config.Converter != null) _converters["converter"] = _config.Converter;
        if (_config.Splitter != null) _splitters["splitter"] = _config.Splitter;
        if (_config.Embedder != null) _embedders["embedder"] = _config.Embedder;
        if (_config.Indexer != null) _indexers["indexer"] = _config.Indexer;

        if (_config.Edges?.Any() == true)
        {
            _hasExplicitOrchestration = true;
            foreach (var edge in _config.Edges)
            {
                _edges.Add((edge.From, edge.To, edge.ToCondition()));
            }
        }
    }
}
```

### 6.2 Retrieval Pipeline Builder

```csharp
namespace HPD.MRAG.Builders;

/// <summary>
/// Fluent builder for creating retrieval pipelines.
/// </summary>
public class RetrievalPipelineBuilder
{
    private readonly GraphBuilder _graphBuilder;
    private readonly RetrievalPipelineConfig _config;

    private readonly Dictionary<string, EmbedderConfig> _embedders = new();
    private readonly Dictionary<string, RetrieverConfig> _retrievers = new();
    private readonly Dictionary<string, RerankerConfig> _rerankers = new();
    private readonly Dictionary<string, GeneratorConfig> _generators = new();
    private readonly Dictionary<string, QueryTransformerConfig> _queryTransformers = new();

    private readonly List<(string From, string To, EdgeCondition? Condition)> _edges = new();
    private bool _hasExplicitOrchestration = false;

    public RetrievalPipelineBuilder()
    {
        _config = new RetrievalPipelineConfig();
        _graphBuilder = new GraphBuilder();
    }

    public RetrievalPipelineBuilder(RetrievalPipelineConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _graphBuilder = new GraphBuilder();
        InitializeFromConfig();
    }

    //
    // HANDLER REGISTRATION
    //

    public RetrievalPipelineBuilder AddQueryEmbedder(string name, Action<EmbedderConfig> configure)
    {
        var config = new EmbedderConfig();
        configure(config);
        config.Validate();
        _embedders[name] = config;
        return this;
    }

    public RetrievalPipelineBuilder AddRetriever(string name, Action<RetrieverConfig> configure)
    {
        var config = new RetrieverConfig();
        configure(config);
        config.Validate();
        _retrievers[name] = config;
        return this;
    }

    public RetrievalPipelineBuilder AddReranker(string name, Action<RerankerConfig> configure)
    {
        var config = new RerankerConfig();
        configure(config);
        config.Validate();
        _rerankers[name] = config;
        return this;
    }

    public RetrievalPipelineBuilder AddGenerator(string name, Action<GeneratorConfig> configure)
    {
        var config = new GeneratorConfig();
        configure(config);
        config.Validate();
        _generators[name] = config;
        return this;
    }

    //
    // CONVENIENCE METHODS
    //

    public RetrievalPipelineBuilder AddOpenAIQueryEmbedder(
        string name = "query-embedder",
        string? providerAlias = null,
        string model = "text-embedding-3-small")
    {
        return AddQueryEmbedder(name, cfg =>
        {
            cfg.Provider = "openai";
            cfg.ProviderAlias = providerAlias;
            cfg.ModelId = model;
            cfg.Prefix = "query: ";  // For asymmetric embeddings
        });
    }

    public RetrievalPipelineBuilder AddQdrantRetriever(
        string name = "retriever",
        string? providerAlias = null,
        string? collectionName = null,
        int topK = 20)
    {
        return AddRetriever(name, cfg =>
        {
            cfg.Provider = "qdrant";
            cfg.ProviderAlias = providerAlias;
            cfg.CollectionName = collectionName;
            cfg.TopK = topK;
        });
    }

    public RetrievalPipelineBuilder AddCohereReranker(
        string name = "reranker",
        string? providerAlias = null,
        string model = "rerank-english-v3.0",
        int topN = 5)
    {
        return AddReranker(name, cfg =>
        {
            cfg.Provider = "cohere";
            cfg.ProviderAlias = providerAlias;
            cfg.ModelId = model;
            cfg.TopN = topN;
        });
    }

    public RetrievalPipelineBuilder AddOpenAIGenerator(
        string name = "generator",
        string? providerAlias = null,
        string model = "gpt-4o",
        float temperature = 0.7f)
    {
        return AddGenerator(name, cfg =>
        {
            cfg.Provider = "openai";
            cfg.ProviderAlias = providerAlias;
            cfg.ModelId = model;
            cfg.Temperature = temperature;
        });
    }

    //
    // PARALLEL RETRIEVAL (uses HPD.Graph's execution layers)
    //

    public RetrievalPipelineBuilder AddParallelRetrievers(
        params (string Name, Action<RetrieverConfig> Configure)[] retrievers)
    {
        foreach (var (name, configure) in retrievers)
        {
            AddRetriever(name, configure);
        }
        return this;
    }

    //
    // ORCHESTRATION
    //

    public RetrievalEdgeBuilder From(params string[] sourceNodes)
    {
        _hasExplicitOrchestration = true;
        return new RetrievalEdgeBuilder(this, sourceNodes);
    }

    internal void AddEdge(string from, string to, EdgeCondition? condition = null)
    {
        _edges.Add((from, to, condition));
    }

    //
    // PIPELINE SETTINGS
    //

    public RetrievalPipelineBuilder WithTopK(int topK)
    {
        _config.TopK = topK;
        return this;
    }

    public RetrievalPipelineBuilder WithTopN(int topN)
    {
        _config.TopN = topN;
        return this;
    }

    public RetrievalPipelineBuilder WithMaxIterations(int maxIterations)
    {
        _config.MaxIterations = maxIterations;
        _graphBuilder.WithMaxIterations(maxIterations);
        return this;
    }

    public RetrievalPipelineBuilder WithPartition(string partitionKey)
    {
        _config.PartitionKey = partitionKey;
        return this;
    }

    public RetrievalPipelineBuilder WithTimeout(TimeSpan timeout)
    {
        _config.ExecutionTimeout = timeout;
        return this;
    }

    //
    // BUILD
    //

    public Graph Build()
    {
        _graphBuilder.WithName("RetrievalPipeline");

        if (_config.ExecutionTimeout.HasValue)
            _graphBuilder.WithExecutionTimeout(_config.ExecutionTimeout.Value);

        _graphBuilder.AddStartNode();
        _graphBuilder.AddEndNode();

        AddHandlerNodes();

        if (_hasExplicitOrchestration)
        {
            AddExplicitEdges();
        }
        else
        {
            AddDefaultSequentialEdges();
        }

        return _graphBuilder.Build();
    }

    private void AddHandlerNodes()
    {
        foreach (var (name, config) in _queryTransformers)
        {
            _graphBuilder.AddHandlerNode(name, $"Transform_{name}", "MRAGQueryTransformerHandler", node =>
            {
                node.WithConfig("config", config);
            });
        }

        foreach (var (name, config) in _embedders)
        {
            _graphBuilder.AddHandlerNode(name, $"Embed_{name}", "MRAGEmbedderHandler", node =>
            {
                node.WithConfig("config", config);
            });
        }

        foreach (var (name, config) in _retrievers)
        {
            _graphBuilder.AddHandlerNode(name, $"Retrieve_{name}", "MRAGRetrieverHandler", node =>
            {
                node.WithConfig("config", config);
            });
        }

        foreach (var (name, config) in _rerankers)
        {
            _graphBuilder.AddHandlerNode(name, $"Rerank_{name}", "MRAGRerankerHandler", node =>
            {
                node.WithConfig("config", config);
            });
        }

        foreach (var (name, config) in _generators)
        {
            _graphBuilder.AddHandlerNode(name, $"Generate_{name}", "MRAGGeneratorHandler", node =>
            {
                node.WithConfig("config", config);
            });
        }
    }

    private void AddDefaultSequentialEdges()
    {
        var stages = new List<string>();

        if (_queryTransformers.Any())
            stages.AddRange(_queryTransformers.Keys);
        if (_embedders.Any())
            stages.AddRange(_embedders.Keys);
        if (_retrievers.Any())
            stages.AddRange(_retrievers.Keys);
        if (_rerankers.Any())
            stages.AddRange(_rerankers.Keys);
        if (_generators.Any())
            stages.AddRange(_generators.Keys);

        if (stages.Count == 0)
            throw new InvalidOperationException("No handlers registered");

        _graphBuilder.AddEdge("START", stages[0]);

        for (int i = 0; i < stages.Count - 1; i++)
        {
            _graphBuilder.AddEdge(stages[i], stages[i + 1]);
        }

        _graphBuilder.AddEdge(stages[^1], "END");
    }

    private void AddExplicitEdges()
    {
        foreach (var (from, to, condition) in _edges)
        {
            if (condition != null)
                _graphBuilder.AddEdge(from, to, e => e.WithCondition(condition));
            else
                _graphBuilder.AddEdge(from, to);
        }
    }

    private void InitializeFromConfig()
    {
        if (_config.QueryEmbedder != null) _embedders["query-embedder"] = _config.QueryEmbedder;
        if (_config.Retriever != null) _retrievers["retriever"] = _config.Retriever;
        if (_config.Reranker != null) _rerankers["reranker"] = _config.Reranker;
        if (_config.Generator != null) _generators["generator"] = _config.Generator;
        if (_config.QueryTransformer != null) _queryTransformers["query-transformer"] = _config.QueryTransformer;

        if (_config.Edges?.Any() == true)
        {
            _hasExplicitOrchestration = true;
            foreach (var edge in _config.Edges)
            {
                _edges.Add((edge.From, edge.To, edge.ToCondition()));
            }
        }
    }
}
```

### 6.3 Edge Builders

```csharp
namespace HPD.MRAG.Builders;

/// <summary>
/// Edge builder for retrieval pipelines with domain-specific conditions.
/// </summary>
public class RetrievalEdgeBuilder
{
    private readonly RetrievalPipelineBuilder _builder;
    private readonly string[] _sourceNodes;

    internal RetrievalEdgeBuilder(RetrievalPipelineBuilder builder, string[] sourceNodes)
    {
        _builder = builder;
        _sourceNodes = sourceNodes;
    }

    public RetrievalPipelineBuilder To(params string[] targetNodes)
    {
        foreach (var source in _sourceNodes)
        foreach (var target in targetNodes)
        {
            _builder.AddEdge(source, target);
        }
        return _builder;
    }

    public RetrievalConditionalEdgeBuilder To(string targetNode)
    {
        return new RetrievalConditionalEdgeBuilder(_builder, _sourceNodes, targetNode);
    }
}

public class RetrievalConditionalEdgeBuilder
{
    private readonly RetrievalPipelineBuilder _builder;
    private readonly string[] _sourceNodes;
    private readonly string _targetNode;

    internal RetrievalConditionalEdgeBuilder(
        RetrievalPipelineBuilder builder,
        string[] sourceNodes,
        string targetNode)
    {
        _builder = builder;
        _sourceNodes = sourceNodes;
        _targetNode = targetNode;
    }

    /// <summary>
    /// Traverse when retrieval quality is high (score > 0.8).
    /// </summary>
    public RetrievalPipelineBuilder WhenQualityHigh()
    {
        return When("retrievalQuality", "high");
    }

    /// <summary>
    /// Traverse when retrieval quality is low (score <= 0.8).
    /// </summary>
    public RetrievalPipelineBuilder WhenQualityLow()
    {
        return When("retrievalQuality", "low");
    }

    /// <summary>
    /// Generic condition: field equals value.
    /// </summary>
    public RetrievalPipelineBuilder When(string field, object value)
    {
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = field,
            Value = value
        };

        foreach (var source in _sourceNodes)
        {
            _builder.AddEdge(source, _targetNode, condition);
        }

        return _builder;
    }

    /// <summary>
    /// Default fallback edge.
    /// </summary>
    public RetrievalPipelineBuilder AsDefault()
    {
        var condition = new EdgeCondition { Type = ConditionType.Default };

        foreach (var source in _sourceNodes)
        {
            _builder.AddEdge(source, _targetNode, condition);
        }

        return _builder;
    }
}
```

---

## 7. HPD.Graph Feature Mapping

### 7.1 Feature-to-Use-Case Mapping

| HPD.Graph Feature | MRAG Use Case |
|-------------------|---------------|
| **Artifact Registry** | Document → Chunks → Embeddings lineage tracking |
| **Partitioning** | Multi-tenant RAG, time-based document batches |
| **MaterializeAsync** | On-demand embedding: "get embeddings for document X" |
| **Content-Addressable Cache** | Skip re-embedding unchanged documents ($$$ savings) |
| **Incremental Execution** | Only process new/changed documents in ingestion |
| **Port-Based Routing** | Splitter outputs N chunks from 1 document |
| **Map Nodes** | Parallel document/chunk processing |
| **Channels (Append)** | Collect retrieved documents from parallel retrievers |
| **Channels (Barrier)** | Wait for all embeddings before bulk indexing |
| **Polling/Sensors** | Wait for vector index to sync before retrieval |
| **Temporal Operators** | Schedule re-indexing, rate-limit API calls |
| **Cycles + MaxIterations** | Corrective RAG (quality check → reformulate → retry) |
| **SubGraphs** | Reusable ingestion/retrieval components |
| **Checkpointing** | Resume long-running ingestion after failure |

### 7.2 Advanced Integration Examples

#### Artifact-Based Lineage

```csharp
// Track document → chunks → embeddings provenance
builder.AddSplitter("split", cfg => { ... })
    .ProducesArtifact("chunks:{documentId}");

builder.AddEmbedder("embed", cfg => { ... })
    .RequiresArtifacts("chunks:{documentId}")
    .ProducesArtifact("embeddings:{documentId}");

// Query lineage
var lineage = await artifactRegistry.GetLineageAsync(
    new ArtifactKey("embeddings:doc-123"),
    version: "latest");
// Returns: { "chunks:doc-123": "v2", "document:doc-123": "v1" }
```

#### Partitioned Multi-Tenant Ingestion

```csharp
var ingestion = new IngestionPipelineBuilder()
    .AddSplitter("split", ...)
    .AddEmbedder("embed", ...)
    .AddIndexer("index", ...)
    .WithPartition("tenant-{tenantId}")  // Per-tenant isolation
    .Build();

// Ingest for specific tenant
await orchestrator.ExecuteAsync(context with {
    CurrentPartition = new PartitionKey("tenant-acme")
});
```

#### Demand-Driven Embedding

```csharp
// Only compute embeddings when needed (lazy evaluation)
var embeddings = await orchestrator.MaterializeAsync(
    graphId: "ingestion-pipeline",
    artifactKey: new ArtifactKey("embeddings:doc-123"),
    partition: new PartitionKey("tenant-acme")
);
// If cached and unchanged → returns immediately
// If stale → computes minimal subgraph
```

#### Parallel Hybrid Retrieval

```csharp
var retrieval = new RetrievalPipelineBuilder()
    .AddOpenAIQueryEmbedder("embed-query")
    .AddParallelRetrievers(
        ("qdrant", cfg => { cfg.Provider = "qdrant"; cfg.TopK = 10; }),
        ("pinecone", cfg => { cfg.Provider = "pinecone"; cfg.TopK = 10; }),
        ("bm25", cfg => { cfg.Provider = "elasticsearch"; cfg.TopK = 10; })
    )
    .AddReranker("fusion", cfg => {
        cfg.Provider = "reciprocal-rank-fusion";
        cfg.TopN = 10;
    })
    .AddCohereReranker("rerank", topN: 5)
    .AddOpenAIGenerator("generate")

    // Orchestration
    .From("START").To("embed-query")
    .From("embed-query").To("qdrant", "pinecone", "bm25")  // PARALLEL!
    .From("qdrant", "pinecone", "bm25").To("fusion")       // Merge results
    .From("fusion").To("rerank")
    .From("rerank").To("generate")
    .From("generate").To("END")
    .Build();
```

#### Corrective RAG with Cycles

```csharp
var retrieval = new RetrievalPipelineBuilder()
    .AddOpenAIQueryEmbedder("embed")
    .AddQdrantRetriever("retrieve", topK: 20)
    .AddCustomHandler("quality-check", new QualityCheckerConfig())
    .AddCustomHandler("reformulate", new QueryReformulatorConfig())
    .AddCohereReranker("rerank")
    .AddOpenAIGenerator("generate")

    // Corrective RAG pattern
    .From("START").To("embed")
    .From("embed").To("retrieve")
    .From("retrieve").To("quality-check")
    .From("quality-check")
        .To("rerank").WhenQualityHigh()       // Good results → continue
        .To("reformulate").WhenQualityLow()   // Bad results → reformulate
    .From("reformulate").To("embed")          // CYCLE: retry with new query
    .From("rerank").To("generate")
    .From("generate").To("END")

    .WithMaxIterations(3)  // Limit refinement loops
    .Build();
```

---

## 8. Preset System

```csharp
namespace HPD.MRAG.Presets;

/// <summary>
/// Pre-built pipeline configurations for common RAG patterns.
/// </summary>
public static class MRAGPresets
{
    /// <summary>
    /// Basic RAG: Embed Query → Retrieve → Generate
    /// </summary>
    public static RetrievalPipelineBuilder BasicRAG(
        string? embeddingAlias = null,
        string? vectorStoreAlias = null,
        string? generatorAlias = null,
        int topK = 5)
    {
        return new RetrievalPipelineBuilder()
            .AddOpenAIQueryEmbedder("embed", providerAlias: embeddingAlias)
            .AddQdrantRetriever("retrieve", providerAlias: vectorStoreAlias, topK: topK)
            .AddOpenAIGenerator("generate", providerAlias: generatorAlias);
    }

    /// <summary>
    /// RAG with Reranking: Embed → Retrieve (top 20) → Rerank (top 5) → Generate
    /// </summary>
    public static RetrievalPipelineBuilder RerankingRAG(
        string? embeddingAlias = null,
        string? vectorStoreAlias = null,
        string? rerankerAlias = null,
        string? generatorAlias = null,
        int retrieveTopK = 20,
        int rerankTopN = 5)
    {
        return new RetrievalPipelineBuilder()
            .AddOpenAIQueryEmbedder("embed", providerAlias: embeddingAlias)
            .AddQdrantRetriever("retrieve", providerAlias: vectorStoreAlias, topK: retrieveTopK)
            .AddCohereReranker("rerank", providerAlias: rerankerAlias, topN: rerankTopN)
            .AddOpenAIGenerator("generate", providerAlias: generatorAlias);
    }

    /// <summary>
    /// Corrective RAG (CRAG): Quality check → Reformulate if needed → Retry
    /// </summary>
    public static RetrievalPipelineBuilder CorrectiveRAG(
        string? embeddingAlias = null,
        string? vectorStoreAlias = null,
        string? generatorAlias = null,
        int maxIterations = 3)
    {
        return new RetrievalPipelineBuilder()
            .AddOpenAIQueryEmbedder("embed", providerAlias: embeddingAlias)
            .AddQdrantRetriever("retrieve", providerAlias: vectorStoreAlias)
            .AddCustomHandler("quality-check", new QualityCheckerConfig { MinScore = 0.7f })
            .AddCustomHandler("reformulate", new QueryReformulatorConfig())
            .AddOpenAIGenerator("generate", providerAlias: generatorAlias)

            .From("START").To("embed")
            .From("embed").To("retrieve")
            .From("retrieve").To("quality-check")
            .From("quality-check")
                .To("generate").WhenQualityHigh()
                .To("reformulate").WhenQualityLow()
            .From("reformulate").To("embed")
            .From("generate").To("END")

            .WithMaxIterations(maxIterations);
    }

    /// <summary>
    /// Basic ingestion: Split → Embed → Index
    /// </summary>
    public static IngestionPipelineBuilder BasicIngestion(
        string? embeddingAlias = null,
        string? vectorStoreAlias = null,
        int chunkSize = 512,
        int chunkOverlap = 50)
    {
        return new IngestionPipelineBuilder()
            .AddRecursiveSplitter("split", chunkSize, chunkOverlap)
            .AddOpenAIEmbedder("embed", providerAlias: embeddingAlias)
            .AddQdrantIndexer("index", providerAlias: vectorStoreAlias)
            .WithIncremental(true)
            .WithArtifactTracking(true);
    }
}
```

---

## 9. Usage Examples

### 9.1 Beginner: Basic RAG with Presets

```csharp
// Register provider aliases (secrets management)
services.AddMRAGProviderAliases(aliases =>
{
    aliases.Register("openai-prod", "openai", new OpenAIProviderOptions
    {
        ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")!
    });

    aliases.Register("qdrant-prod", "qdrant", new QdrantProviderOptions
    {
        Url = "http://localhost:6333",
        CollectionName = "documents"
    });
});

// Build pipeline using preset
var pipeline = MRAGPresets.BasicRAG(
    embeddingAlias: "openai-prod",
    vectorStoreAlias: "qdrant-prod",
    generatorAlias: "openai-prod",
    topK: 5
).Build();

// Execute
var context = new MRAGContext(...);
context.Channels.Write("query", "What is RAG?");

var result = await orchestrator.ExecuteAsync(pipeline, context);
var answer = result.Channels.Get<string>("answer");
```

### 9.2 Intermediate: JSON Configuration

```json
{
  "queryEmbedder": {
    "provider": "openai",
    "providerAlias": "openai-prod",
    "modelId": "text-embedding-3-small",
    "prefix": "query: "
  },
  "retriever": {
    "provider": "qdrant",
    "providerAlias": "qdrant-prod",
    "topK": 20
  },
  "reranker": {
    "provider": "cohere",
    "providerAlias": "cohere-prod",
    "topN": 5
  },
  "generator": {
    "provider": "openai",
    "providerAlias": "openai-prod",
    "modelId": "gpt-4o",
    "temperature": 0.7
  },
  "maxIterations": 3,
  "executionTimeout": "00:02:00"
}
```

```csharp
// Load and build from JSON
var config = await JsonSerializer.DeserializeAsync<RetrievalPipelineConfig>(
    File.OpenRead("rag-config.json"));
var pipeline = new RetrievalPipelineBuilder(config).Build();
```

### 9.3 Advanced: Custom Pipeline with Parallel Retrieval

```csharp
var pipeline = new RetrievalPipelineBuilder()
    // Query processing
    .AddOpenAIQueryEmbedder("embed", providerAlias: "openai-prod")

    // Parallel retrieval from multiple sources
    .AddQdrantRetriever("qdrant", providerAlias: "qdrant-prod", topK: 10)
    .AddRetriever("pinecone", cfg =>
    {
        cfg.Provider = "pinecone";
        cfg.ProviderAlias = "pinecone-prod";
        cfg.TopK = 10;
    })

    // Merge and rerank
    .AddCustomHandler("merge", new DocumentMergerConfig())
    .AddCohereReranker("rerank", providerAlias: "cohere-prod", topN: 5)

    // Generate
    .AddOpenAIGenerator("generate", providerAlias: "openai-prod", model: "gpt-4o")

    // Orchestration
    .From("START").To("embed")
    .From("embed").To("qdrant", "pinecone")  // Parallel!
    .From("qdrant", "pinecone").To("merge")
    .From("merge").To("rerank")
    .From("rerank").To("generate")
    .From("generate").To("END")

    .WithTimeout(TimeSpan.FromMinutes(2))
    .Build();
```

---

## 10. Package Structure

```
HPD.MRAG/
├── HPD.MRAG.Abstractions/
│   ├── Documents/
│   │   ├── Document.cs
│   │   ├── DocumentChunk.cs
│   │   ├── EmbeddedChunk.cs
│   │   └── RetrievedDocument.cs
│   ├── Stores/
│   │   ├── IDocumentStore.cs
│   │   └── IGraphStore.cs
│   ├── Processing/
│   │   ├── IConverter.cs
│   │   ├── ISplitter.cs
│   │   ├── IReranker.cs
│   │   ├── IQueryTransformer.cs
│   │   └── IPostProcessor.cs
│   └── Configuration/
│       ├── HandlerConfigBase.cs
│       ├── EmbedderConfig.cs
│       ├── RetrieverConfig.cs
│       ├── RerankerConfig.cs
│       ├── GeneratorConfig.cs
│       └── ... (other configs)
│
├── HPD.MRAG.Core/
│   ├── Configuration/
│   │   ├── IngestionPipelineConfig.cs
│   │   └── RetrievalPipelineConfig.cs
│   ├── Providers/
│   │   ├── IProviderAliasRegistry.cs
│   │   ├── ProviderAliasRegistry.cs
│   │   ├── ProviderFactoryRegistry.cs
│   │   ├── ProviderOptionsResolver.cs
│   │   └── Factories/
│   │       ├── IEmbedderFactory.cs
│   │       ├── IRetrieverFactory.cs
│   │       ├── IRerankerFactory.cs
│   │       └── IGeneratorFactory.cs
│   ├── Handlers/
│   │   ├── MRAGContext.cs
│   │   ├── EmbedderHandler.cs
│   │   ├── RetrieverHandler.cs
│   │   ├── RerankerHandler.cs
│   │   ├── GeneratorHandler.cs
│   │   ├── SplitterHandler.cs
│   │   ├── IndexerHandler.cs
│   │   └── ConverterHandler.cs
│   ├── Builders/
│   │   ├── IngestionPipelineBuilder.cs
│   │   ├── RetrievalPipelineBuilder.cs
│   │   ├── IngestionEdgeBuilder.cs
│   │   └── RetrievalEdgeBuilder.cs
│   ├── Presets/
│   │   └── MRAGPresets.cs
│   └── Stores/
│       ├── InMemoryDocumentStore.cs
│       └── FileSystemDocumentStore.cs
│
├── HPD.MRAG.Providers.OpenAI/
│   ├── OpenAIEmbedderFactory.cs
│   ├── OpenAIGeneratorFactory.cs
│   ├── OpenAIProviderOptions.cs
│   └── OpenAIModule.cs (ModuleInitializer)
│
├── HPD.MRAG.Providers.Cohere/
│   ├── CohereEmbedderFactory.cs
│   ├── CohereRerankerFactory.cs
│   ├── CohereProviderOptions.cs
│   └── CohereModule.cs
│
├── HPD.MRAG.Providers.Qdrant/
│   ├── QdrantRetrieverFactory.cs
│   ├── QdrantIndexerFactory.cs
│   ├── QdrantProviderOptions.cs
│   └── QdrantModule.cs
│
├── HPD.MRAG.Converters/
│   ├── PdfConverter.cs
│   ├── DocxConverter.cs
│   ├── HtmlConverter.cs
│   └── MarkdownConverter.cs
│
└── HPD.MRAG.Splitters/
    ├── RecursiveSplitter.cs
    ├── SentenceSplitter.cs
    ├── SemanticSplitter.cs
    └── MarkdownSplitter.cs
```

---

## 11. Implementation Roadmap

### Phase 1: Core Infrastructure (Week 1-2)
- [ ] HPD.MRAG.Abstractions (documents, stores, processing interfaces)
- [ ] Configuration types with hybrid provider resolution
- [ ] Provider alias registry
- [ ] Provider factory registry
- [ ] MRAGContext extending GraphContext
- [ ] JSON serialization with source generation

### Phase 2: Handlers (Week 2-3)
- [ ] EmbedderHandler
- [ ] RetrieverHandler
- [ ] RerankerHandler
- [ ] GeneratorHandler
- [ ] SplitterHandler
- [ ] IndexerHandler
- [ ] ConverterHandler

### Phase 3: Builders (Week 3-4)
- [ ] IngestionPipelineBuilder
- [ ] RetrievalPipelineBuilder
- [ ] Edge builders with domain-specific conditions
- [ ] HPD.Graph feature integration (artifacts, partitioning)

### Phase 4: Providers (Week 4-6)
- [ ] HPD.MRAG.Providers.OpenAI
- [ ] HPD.MRAG.Providers.Cohere
- [ ] HPD.MRAG.Providers.Qdrant
- [ ] HPD.MRAG.Providers.Pinecone
- [ ] HPD.MRAG.Converters (PDF, DOCX, HTML)
- [ ] HPD.MRAG.Splitters (recursive, semantic)

### Phase 5: Presets & Examples (Week 6-7)
- [ ] BasicRAG, RerankingRAG, CorrectiveRAG presets
- [ ] BasicIngestion preset
- [ ] Example projects
- [ ] Documentation

### Phase 6: Testing & Polish (Week 7-8)
- [ ] Unit tests for all handlers
- [ ] Integration tests for presets
- [ ] Performance benchmarks
- [ ] API documentation

---

## 12. Key Differentiators vs Haystack

| Aspect | Haystack | HPD.MRAG |
|--------|----------|----------|
| **Incremental Processing** | Manual, external | Built-in with HPD.Graph fingerprinting |
| **Data Lineage** | None | Full artifact provenance |
| **Caching** | Basic, time-based | Content-addressable, hierarchical |
| **Multi-tenancy** | DIY | Native partitioning |
| **Demand-Driven** | No | `MaterializeAsync` for lazy evaluation |
| **Parallel Execution** | Explicit | Automatic via execution layers |
| **Cycles (CRAG)** | Complex | Native with `MaxIterations` |
| **Type Safety** | Python dicts | C# records + compile-time validation |
| **Configuration** | Scattered, repeated | Hybrid: aliases (DRY) + inline (JSON) |
| **Secrets Management** | In config | Provider aliases (separate) |
| **Observability** | Basic logging | Event system + progress tracking |
| **Error Handling** | Try/catch | Configurable policies, circuit breakers |
| **Checkpointing** | Limited | Native suspension/resume |

---

## 13. Conclusion

HPD.MRAG leverages HPD.Graph's sophisticated orchestration capabilities to provide a modular RAG framework that is:

1. **Easier to configure** - Hybrid provider system (aliases for DRY, JSON for serialization)
2. **More efficient** - Content-addressable caching, incremental processing
3. **More observable** - Artifact lineage, event system
4. **More scalable** - Native partitioning, parallel execution
5. **More resilient** - Checkpointing, configurable error policies
6. **Type-safe** - C# with compile-time validation

By building on HPD.Graph rather than reinventing orchestration, HPD.MRAG focuses entirely on what makes RAG unique while getting world-class workflow capabilities for free.
