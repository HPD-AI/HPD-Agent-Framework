# HPD.Memory.Abstractions

> Core abstractions for HPD-Agent.Memory - A next-generation document memory and RAG framework for .NET

[![NuGet](https://img.shields.io/nuget/v/HPD.Memory.Abstractions.svg)](https://www.nuget.org/packages/HPD.Memory.Abstractions/)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)]()
[![License](https://img.shields.io/badge/license-MIT-blue)]()

## What is this package?

This package contains **ONLY interfaces and contracts** for building RAG (Retrieval-Augmented Generation) systems in .NET. It has **ZERO implementation dependencies** - just pure abstractions.

Think of it like:
- `Microsoft.Extensions.Logging.Abstractions` (just `ILogger`, no implementations)
- `Microsoft.Extensions.AI` (just `IChatClient`, no implementations)
- `Microsoft.Extensions.Caching.Abstractions` (just `IDistributedCache`, no implementations)

## What's included?

### 1. **IMemoryClient** - Universal RAG Interface

```csharp
public interface IMemoryClient
{
    // Ingest documents
    Task<IIngestionResult> IngestAsync(IngestionRequest request, CT ct = default);

    // Retrieve relevant knowledge
    Task<IRetrievalResult> RetrieveAsync(RetrievalRequest request, CT ct = default);

    // Generate answers (RAG)
    Task<IGenerationResult> GenerateAsync(GenerationRequest request, CT ct = default);
}
```

**Goal:** Be the standard interface for RAG in .NET, like `ILogger` is for logging.

### 2. **Pipeline Abstractions**

- `IPipelineContext` - Base context for all pipeline executions
- `IPipelineHandler<TContext>` - Generic handler interface
- `IPipelineOrchestrator<TContext>` - Generic orchestrator interface
- `PipelineStep`, `PipelineResult` - Pipeline execution building blocks

### 3. **Storage Abstractions**

- `IDocumentStore` - File/document storage operations
- `IGraphStore` - Graph database operations for GraphRAG
- `GraphEntity`, `GraphRelationship`, `GraphTraversalOptions` - Graph models

### 4. **Domain Models**

- `DocumentFile` - File metadata with processing state
- `GeneratedFile` - Files generated during processing
- `MemoryFilter` - Filtering for retrieval operations

## Installation

```bash
dotnet add package HPD.Memory.Abstractions
```

## When to use this package

### ✅ Use this package if you want to:

- **Build a custom RAG implementation** that others can consume via standard interface
- **Write code that works with ANY IMemoryClient** (implementation-agnostic)
- **Mock RAG systems in tests** without pulling in heavy dependencies
- **Define your own pipeline handlers** using the pipeline abstractions

### ❌ Don't use this package if you want:

- **Ready-to-use RAG functionality** - Use `HPD.Memory.Client` instead
- **Pipeline infrastructure** (orchestration, storage) - Use `HPD.Memory.Core` instead
- **A complete solution** - Install all three packages

## Package Dependencies

```
Your App
    ↓
HPD.Memory.Client (implementations)
    ↓
HPD.Memory.Core (infrastructure)
    ↓
HPD.Memory.Abstractions (interfaces only) ← YOU ARE HERE
```

## Quick Start

### Using IMemoryClient (as a consumer)

```csharp
using HPDAgent.Memory.Abstractions.Client;

public class MyService
{
    private readonly IMemoryClient _memory;

    public MyService(IMemoryClient memory)
    {
        _memory = memory; // Don't care which implementation!
    }

    public async Task ProcessDocumentAsync(string filePath)
    {
        // Ingest
        var ingestion = await _memory.IngestAsync(
            IngestionRequest.FromFile(filePath));

        // Retrieve
        var retrieval = await _memory.RetrieveAsync(
            new RetrievalRequest { Query = "What is RAG?" });

        // Generate answer
        var answer = await _memory.GenerateAsync(
            new GenerationRequest { Question = "What is RAG?" });

        Console.WriteLine(answer.Answer);
    }
}
```

### Implementing IMemoryClient

```csharp
using HPDAgent.Memory.Abstractions.Client;

public class MyCustomRAG : IMemoryClient
{
    public async Task<IIngestionResult> IngestAsync(
        IngestionRequest request,
        CancellationToken ct = default)
    {
        // Your ingestion logic here
        // Process the document and generate artifacts
        var artifactCounts = new Dictionary<string, int>
        {
            [StandardArtifacts.Chunks] = 10,
            [StandardArtifacts.Embeddings] = 10
        };

        return IngestionResult.CreateSuccess(
            documentId: request.DocumentId ?? Guid.NewGuid().ToString(),
            index: this.Index,  // Client is scoped to an index
            artifactCounts: artifactCounts);
    }

    public async Task<IRetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken ct = default)
    {
        // Your retrieval logic here
        var items = new List<IRetrievedItem>
        {
            RetrievedItem.CreateTextChunk(
                content: "RAG combines retrieval with generation...",
                score: 0.95,
                documentId: "doc-1",
                documentName: "rag-intro.pdf")
        };

        return new RetrievalResult
        {
            Query = request.Query,
            Items = items
        };
    }

    public async Task<IGenerationResult> GenerateAsync(
        GenerationRequest request,
        CancellationToken ct = default)
    {
        // 1. Retrieve relevant knowledge
        var retrieval = await RetrieveAsync(
            new RetrievalRequest { Query = request.Question },
            ct);

        // 2. Generate answer using your LLM
        var answer = await YourLLM.GenerateAsync(retrieval.Items, request.Question);

        // 3. Return with citations
        return GenerationResult.Create(
            question: request.Question,
            answer: answer,
            citations: retrieval.Items.Select(Citation.FromRetrievedItem).ToList());
    }

    // Implement other methods...
}
```

## Design Philosophy

### Infrastructure-First, Not Implementation-First

This package defines **WHAT** RAG systems should do, not **HOW** they do it.

Benefits:
- ✅ Applications code to interface, not implementation
- ✅ Easy to swap implementations (one line change)
- ✅ Easy to test (mock the interface)
- ✅ Easy to compose (decorators, routers, fallbacks)
- ✅ Future-proof (new RAG techniques don't break existing code)

### Convention Over Configuration

Common parameters are first-class properties:

```csharp
var request = new RetrievalRequest
{
    Query = "...",           // ← Required, first-class
    MaxResults = 10,         // ← Common, first-class
    MinRelevanceScore = 0.7, // ← Common, first-class

    // Advanced/implementation-specific via Options
    Options = new()
    {
        ["max_hops"] = 2,              // GraphRAG-specific
        ["rerank"] = true,             // Advanced feature
        ["embedding_model"] = "ada-3"  // Implementation detail
    }
};
```

### Consistent Output, Diverse Strategies

All implementations return the same result types, but items can represent different things:

```csharp
// Basic RAG returns text chunks
Items = [
    { Content = "...", ContentType = "text_chunk", Score = 0.95 }
]

// GraphRAG returns entities + relationships + chunks
Items = [
    { Content = "...", ContentType = "text_chunk", Score = 0.95 },
    { Content = "Entity: RAG", ContentType = "entity", Score = 0.98 },
    { Content = "RAG → uses → Vectors", ContentType = "relationship", Score = 0.92 }
]
```

Consumer code works with all implementations, but can handle different `ContentType` values when needed.

## Relationship to HPD-Agent.Memory

```
HPD.Memory.Abstractions (this package)
    ↓ implements
HPD.Memory.Core (pipeline infrastructure)
    ↓ uses
HPD.Memory.Client (IMemoryClient implementations)
    → BasicMemoryClient (vector RAG)
    → GraphMemoryClient (GraphRAG)
    → HybridMemoryClient (vector + graph)
```

This package is the foundation. Other packages build on it.

## Contributing

We welcome implementations of `IMemoryClient` from the community!

If you build a RAG system, consider implementing `IMemoryClient` so your users can:
- Easily switch between your implementation and others
- Test their code without your full implementation
- Compose your implementation with decorators (caching, logging, etc.)

See [IMPLEMENTATION_GUIDE.md](../../docs/IMPLEMENTATION_GUIDE.md) for details.

## License

MIT License - see [LICENSE](../../LICENSE) for details

## Links

- **Documentation**: https://github.com/your-org/hpd-agent/tree/main/HPD-Agent.Memory
- **Source Code**: https://github.com/your-org/hpd-agent
- **NuGet**: https://www.nuget.org/packages/HPD.Memory.Abstractions/
- **Issues**: https://github.com/your-org/hpd-agent/issues

---

**Built with "infrastructure-first" philosophy** - defining interfaces that enable ecosystem growth.
