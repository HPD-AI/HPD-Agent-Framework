# AI Provider Setup Guide

Complete guide for setting up chat providers, embedding generators, and vector stores with HPD-Agent.Memory.

---

## Overview

HPD-Agent.Memory uses **Microsoft.Extensions.AI** and **Microsoft.Extensions.VectorData** for AI services. This provides:

✅ **Provider-agnostic interfaces** - Switch between OpenAI, Azure, local models, etc.
✅ **Standard abstractions** - No vendor lock-in
✅ **Modern patterns** - Built-in DI, logging, telemetry
✅ **Production-ready** - Used by Microsoft in production

---

## Required Packages

### Core Memory System

```xml
<PackageReference Include="Microsoft.Extensions.AI" Version="9.7.1" />
<PackageReference Include="Microsoft.Extensions.VectorData.Abstractions" Version="9.7.0" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.7" />
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.7" />
```

### AI Provider Packages (Choose Your Provider)

#### Option 1: OpenAI
```xml
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="9.7.1" />
```

#### Option 2: Azure OpenAI
```xml
<PackageReference Include="Microsoft.Extensions.AI.AzureAIInference" Version="9.7.1" />
```

#### Option 3: Ollama (Local Models)
```xml
<PackageReference Include="Microsoft.Extensions.AI.Ollama" Version="9.7.1" />
```

#### Option 4: Anthropic (Claude)
```xml
<!-- Note: Check NuGet for latest Anthropic connector -->
<PackageReference Include="Microsoft.Extensions.AI.Anthropic" Version="9.7.1" />
```

### Vector Store Packages (Choose Your Store)

#### Option 1: Azure AI Search
```xml
<PackageReference Include="Microsoft.Extensions.VectorData.Azure.AISearch" Version="9.7.0" />
```

#### Option 2: Qdrant
```xml
<PackageReference Include="Microsoft.Extensions.VectorData.Qdrant" Version="9.7.0" />
```

#### Option 3: In-Memory (Testing Only)
```xml
<!-- Built into VectorData.Abstractions -->
```

---

## Complete Setup Examples

### Example 1: OpenAI + Azure AI Search (Production)

```csharp
using HPDAgent.Memory.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;

var services = new ServiceCollection();

// ========================================
// Logging
// ========================================
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// ========================================
// Chat Client (OpenAI GPT-4)
// ========================================
services.AddChatClient(builder =>
{
    builder.UseOpenAI(
        apiKey: Environment.GetEnvironmentVariable("OPENAI_API_KEY")!,
        modelId: "gpt-4-turbo-preview"
    );
});

// ========================================
// Embedding Generator (OpenAI text-embedding-3-small)
// ========================================
services.AddEmbeddingGenerator<string, Embedding<float>>(builder =>
{
    builder.UseOpenAI(
        apiKey: Environment.GetEnvironmentVariable("OPENAI_API_KEY")!,
        modelId: "text-embedding-3-small",
        dimensions: 1536 // Embedding size
    );
});

// ========================================
// Vector Store (Azure AI Search)
// ========================================
services.AddVectorStore(builder =>
{
    builder.UseAzureAISearch(
        endpoint: Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")!,
        apiKey: Environment.GetEnvironmentVariable("AZURE_SEARCH_KEY")!
    );
});

// ========================================
// HPD-Agent Memory System
// ========================================
services.AddHPDAgentMemory("/var/lib/hpd-agent/data");

// Build service provider
var serviceProvider = services.BuildServiceProvider();

// ========================================
// Usage
// ========================================
var chatClient = serviceProvider.GetRequiredService<IChatClient>();
var embedder = serviceProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var vectorStore = serviceProvider.GetRequiredService<IVectorStore>();

Console.WriteLine("✅ All AI providers configured successfully!");
```

### Example 2: Ollama + Qdrant (Self-Hosted)

Perfect for running everything locally without external dependencies!

```csharp
using HPDAgent.Memory.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;

var services = new ServiceCollection();

services.AddLogging(builder => builder.AddConsole());

// ========================================
// Chat Client (Ollama - Local Llama 3)
// ========================================
services.AddChatClient(builder =>
{
    builder.UseOllama(
        endpoint: new Uri("http://localhost:11434"),
        modelId: "llama3.2:latest"
    );
});

// ========================================
// Embedding Generator (Ollama - nomic-embed-text)
// ========================================
services.AddEmbeddingGenerator<string, Embedding<float>>(builder =>
{
    builder.UseOllama(
        endpoint: new Uri("http://localhost:11434"),
        modelId: "nomic-embed-text:latest",
        dimensions: 768
    );
});

// ========================================
// Vector Store (Qdrant - Local Instance)
// ========================================
services.AddVectorStore(builder =>
{
    builder.UseQdrant(
        endpoint: new Uri("http://localhost:6333")
    );
});

// ========================================
// HPD-Agent Memory
// ========================================
services.AddHPDAgentMemory("./data");

var serviceProvider = services.BuildServiceProvider();

Console.WriteLine("✅ Local AI stack ready!");
Console.WriteLine("   - Ollama (Llama 3.2) at localhost:11434");
Console.WriteLine("   - Qdrant at localhost:6333");
Console.WriteLine("   - No external APIs needed!");
```

### Example 3: Azure OpenAI + Azure AI Search (Enterprise)

```csharp
using Azure.Identity;
using HPDAgent.Memory.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;

var services = new ServiceCollection();

services.AddLogging(builder => builder.AddConsole());

// ========================================
// Azure OpenAI with Managed Identity
// ========================================
var credential = new DefaultAzureCredential();

services.AddChatClient(builder =>
{
    builder.UseAzureOpenAI(
        endpoint: new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
        credential: credential,
        deploymentName: "gpt-4-deployment"
    );
});

services.AddEmbeddingGenerator<string, Embedding<float>>(builder =>
{
    builder.UseAzureOpenAI(
        endpoint: new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
        credential: credential,
        deploymentName: "text-embedding-ada-002-deployment",
        dimensions: 1536
    );
});

// ========================================
// Azure AI Search with Managed Identity
// ========================================
services.AddVectorStore(builder =>
{
    builder.UseAzureAISearch(
        endpoint: new Uri(Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")!),
        credential: credential
    );
});

services.AddHPDAgentMemory("/mnt/azure-files/hpd-agent");

var serviceProvider = services.BuildServiceProvider();

Console.WriteLine("✅ Azure AI infrastructure ready!");
```

### Example 4: Mixed Providers (Best of Both Worlds)

Use different providers for different purposes:

```csharp
using HPDAgent.Memory.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddLogging(builder => builder.AddConsole());

// ========================================
// OpenAI for Chat (Best quality)
// ========================================
services.AddChatClient("openai-chat", builder =>
{
    builder.UseOpenAI(
        apiKey: Environment.GetEnvironmentVariable("OPENAI_API_KEY")!,
        modelId: "gpt-4-turbo-preview"
    );
});

// ========================================
// Ollama for Embeddings (Cost savings - local)
// ========================================
services.AddEmbeddingGenerator<string, Embedding<float>>("ollama-embeddings", builder =>
{
    builder.UseOllama(
        endpoint: new Uri("http://localhost:11434"),
        modelId: "nomic-embed-text:latest",
        dimensions: 768
    );
});

// ========================================
// Azure AI Search for Production Vector Store
// ========================================
services.AddVectorStore(builder =>
{
    builder.UseAzureAISearch(
        endpoint: Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")!,
        apiKey: Environment.GetEnvironmentVariable("AZURE_SEARCH_KEY")!
    );
});

services.AddHPDAgentMemory();

var serviceProvider = services.BuildServiceProvider();

// Get named services
var chatClient = serviceProvider.GetRequiredKeyedService<IChatClient>("openai-chat");
var embedder = serviceProvider.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>("ollama-embeddings");

Console.WriteLine("✅ Hybrid AI stack configured!");
Console.WriteLine("   - GPT-4 for chat (quality)");
Console.WriteLine("   - Ollama for embeddings (cost)");
Console.WriteLine("   - Azure Search for vectors (scale)");
```

---

## Using AI Services in Handlers

### Example Handler: Text Embedding Generator

```csharp
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Core.Contexts;
using Microsoft.Extensions.AI;

public class GenerateEmbeddingsHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly ILogger<GenerateEmbeddingsHandler> _logger;

    public string StepName => "generate_embeddings";

    public GenerateEmbeddingsHandler(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ILogger<GenerateEmbeddingsHandler> logger)
    {
        _embedder = embedder;
        _logger = logger;
    }

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get all text partitions
            var partitions = context.GetFilesByType(FileArtifactType.TextPartition);

            foreach (var partition in partitions)
            {
                // Skip if already processed
                if (partition.AlreadyProcessedBy(StepName))
                {
                    _logger.LogDebug("Skipping already processed partition: {FileId}", partition.Id);
                    continue;
                }

                // Read partition text
                var text = await ReadPartitionTextAsync(context, partition);

                // Generate embedding using Microsoft.Extensions.AI
                var embeddings = await _embedder.GenerateEmbeddingVectorAsync(
                    text,
                    cancellationToken: cancellationToken);

                // Store embedding
                await StoreEmbeddingAsync(context, partition, embeddings);

                // Mark as processed
                partition.MarkProcessedBy(StepName);

                _logger.LogInformation(
                    "Generated embedding for partition {FileId}: {Dimensions} dimensions",
                    partition.Id,
                    embeddings.Length);
            }

            return PipelineResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate embeddings");
            return PipelineResult.FatalFailure("Embedding generation failed", ex);
        }
    }

    private async Task<string> ReadPartitionTextAsync(
        DocumentIngestionContext context,
        DocumentFile partition)
    {
        var documentStore = context.Services.GetRequiredService<IDocumentStore>();
        return await documentStore.ReadTextFileAsync(
            context.Index,
            context.PipelineId,
            partition.Name);
    }

    private async Task StoreEmbeddingAsync(
        DocumentIngestionContext context,
        DocumentFile partition,
        ReadOnlyMemory<float> embedding)
    {
        var vectorStore = context.Services.GetRequiredService<IVectorStore>();
        var collection = vectorStore.GetCollection<string, VectorRecord>("embeddings");

        var record = new VectorRecord
        {
            Key = $"{context.DocumentId}/{partition.Id}",
            Vector = embedding,
            Metadata = new Dictionary<string, object>
            {
                ["document_id"] = context.DocumentId,
                ["partition_id"] = partition.Id,
                ["partition_number"] = partition.PartitionNumber,
                ["execution_id"] = context.ExecutionId
            }
        };

        await collection.UpsertAsync(record);
    }
}

// Vector record for storage
public class VectorRecord
{
    [VectorStoreRecordKey]
    public required string Key { get; init; }

    [VectorStoreRecordVector(Dimensions: 1536)]
    public required ReadOnlyMemory<float> Vector { get; init; }

    [VectorStoreRecordData]
    public Dictionary<string, object> Metadata { get; init; } = new();
}
```

### Example Handler: Semantic Search with Vector Store

```csharp
using HPDAgent.Memory.Abstractions.Models;
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Core.Contexts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

public class VectorSearchHandler : IPipelineHandler<SemanticSearchContext>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<VectorSearchHandler> _logger;

    public string StepName => "vector_search";

    public VectorSearchHandler(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IVectorStore vectorStore,
        ILogger<VectorSearchHandler> logger)
    {
        _embedder = embedder;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Generate query embedding
            if (context.QueryEmbedding == null)
            {
                var embedding = await _embedder.GenerateEmbeddingVectorAsync(
                    context.Query,
                    cancellationToken: cancellationToken);

                context.QueryEmbedding = embedding.ToArray();

                _logger.LogDebug("Generated query embedding: {Dimensions} dimensions", embedding.Length);
            }

            // Search vector store
            var collection = _vectorStore.GetCollection<string, VectorRecord>("embeddings");

            var searchOptions = new VectorSearchOptions
            {
                Top = context.MaxResults,
                Filter = BuildVectorFilter(context.Filter)
            };

            var results = await collection.VectorizedSearchAsync(
                context.QueryEmbedding,
                searchOptions,
                cancellationToken);

            // Convert to search results
            await foreach (var result in results.ConfigureAwait(false))
            {
                if (result.Score < context.MinRelevance)
                    continue;

                var searchResult = new SearchResult
                {
                    Id = result.Record.Key,
                    DocumentId = result.Record.Metadata["document_id"]?.ToString() ?? "",
                    Score = result.Score,
                    Content = await LoadContentAsync(result.Record.Key, context),
                    Source = StepName
                };

                context.AddResults(new[] { searchResult });
            }

            _logger.LogInformation(
                "Vector search found {Count} results above threshold {Threshold}",
                context.Results.Count,
                context.MinRelevance);

            return PipelineResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vector search failed");
            return PipelineResult.TransientFailure("Vector search failed", ex);
        }
    }

    private VectorSearchFilter? BuildVectorFilter(MemoryFilter? memoryFilter)
    {
        if (memoryFilter == null || memoryFilter.IsEmpty())
            return null;

        // Convert MemoryFilter to VectorSearchFilter
        // Implementation depends on vector store capabilities
        var filter = new VectorSearchFilter();

        foreach (var (key, values) in memoryFilter.GetTags())
        {
            // Add filter clauses
            filter.AddClause($"metadata/{key}", FilterOperator.In, values);
        }

        return filter;
    }

    private async Task<string> LoadContentAsync(string key, SemanticSearchContext context)
    {
        // Load actual content from document store
        var documentStore = context.Services.GetRequiredService<IDocumentStore>();

        var parts = key.Split('/');
        var documentId = parts[0];
        var partitionId = parts[1];

        return await documentStore.ReadTextFileAsync(
            context.Index,
            documentId,
            $"{partitionId}.txt");
    }
}
```

---

## Configuration Best Practices

### 1. Use Environment Variables for Secrets

```csharp
// ❌ DON'T hardcode secrets
builder.UseOpenAI(apiKey: "sk-abc123...");

// ✅ DO use environment variables
builder.UseOpenAI(apiKey: Environment.GetEnvironmentVariable("OPENAI_API_KEY")!);

// ✅ BETTER: Use configuration system
var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>()
    .Build();

builder.UseOpenAI(apiKey: config["OpenAI:ApiKey"]!);
```

### 2. Configure Retry Policies

```csharp
services.AddChatClient(builder =>
{
    builder.UseOpenAI(...)
           .WithRetry(maxRetries: 3, backoff: TimeSpan.FromSeconds(2));
});

services.AddEmbeddingGenerator<string, Embedding<float>>(builder =>
{
    builder.UseOpenAI(...)
           .WithRetry(maxRetries: 5, backoff: TimeSpan.FromSeconds(1));
});
```

### 3. Add Telemetry

```csharp
services.AddChatClient(builder =>
{
    builder.UseOpenAI(...)
           .WithTelemetry(enableTracing: true, enableMetrics: true);
});
```

### 4. Configure Model Parameters

```csharp
services.AddChatClient(builder =>
{
    builder.UseOpenAI(...)
           .WithDefaultOptions(new ChatOptions
           {
               Temperature = 0.7f,
               MaxTokens = 4000,
               TopP = 0.9f,
               FrequencyPenalty = 0.0f,
               PresencePenalty = 0.0f
           });
});
```

---

## Testing with Mock Providers

For unit tests, use mock implementations:

```csharp
using Microsoft.Extensions.AI;
using Moq;

public class EmbeddingHandlerTests
{
    [Fact]
    public async Task Should_Generate_Embeddings_For_All_Partitions()
    {
        // Arrange
        var mockEmbedder = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        mockEmbedder
            .Setup(e => e.GenerateEmbeddingVectorAsync(
                It.IsAny<string>(),
                It.IsAny<EmbeddingGenerationOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadOnlyMemory<float>(new float[1536]));

        var services = new ServiceCollection();
        services.AddSingleton(mockEmbedder.Object);
        services.AddLogging();

        var serviceProvider = services.BuildServiceProvider();

        var handler = new GenerateEmbeddingsHandler(
            mockEmbedder.Object,
            serviceProvider.GetRequiredService<ILogger<GenerateEmbeddingsHandler>>());

        var context = new DocumentIngestionContext { /* ... */ };

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.True(result.IsSuccess);
        mockEmbedder.Verify(e => e.GenerateEmbeddingVectorAsync(
            It.IsAny<string>(),
            It.IsAny<EmbeddingGenerationOptions>(),
            It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
```

---

## Troubleshooting

### Common Issues

**1. "No service for type IChatClient registered"**
```csharp
// ❌ Missing registration
services.AddHPDAgentMemory();

// ✅ Add chat client first
services.AddChatClient(builder => builder.UseOpenAI(...));
services.AddHPDAgentMemory();
```

**2. "Embedding dimensions mismatch"**
```csharp
// Ensure vector store collection dimensions match embedding dimensions
services.AddEmbeddingGenerator<string, Embedding<float>>(builder =>
{
    builder.UseOpenAI(
        modelId: "text-embedding-3-small",
        dimensions: 1536 // ← Must match collection
    );
});

var collection = vectorStore.GetCollection<string, VectorRecord>("embeddings");
// VectorRecord.Vector must have [VectorStoreRecordVector(Dimensions: 1536)]
```

**3. "Rate limit exceeded"**
```csharp
// Add retry with exponential backoff
services.AddChatClient(builder =>
{
    builder.UseOpenAI(...)
           .WithRetry(
               maxRetries: 5,
               backoff: TimeSpan.FromSeconds(2),
               backoffMultiplier: 2.0 // 2s, 4s, 8s, 16s, 32s
           );
});
```

---

## Next Steps

1. ✅ Set up AI providers (this guide)
2. 📝 Implement pipeline handlers (see USAGE_EXAMPLES.md)
3. 🚀 Run your first ingestion pipeline
4. 🔍 Test semantic search
5. 📊 Monitor with telemetry

For handler examples, see [USAGE_EXAMPLES.md](USAGE_EXAMPLES.md).
For architecture details, see [PROJECT_STRUCTURE.md](PROJECT_STRUCTURE.md).
