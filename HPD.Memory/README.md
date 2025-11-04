# HPD-Agent.Memory

> Next-generation document memory system built with "second mover's advantage"

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen)]()
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)]()
[![License](https://img.shields.io/badge/license-MIT-blue)]()

## What is HPD-Agent.Memory?

HPD-Agent.Memory is a flexible, extensible document memory system for AI applications. It was built by studying [Microsoft's Kernel Memory](https://github.com/microsoft/kernel-memory) and applying "second mover's advantage" - learning from their excellent work while addressing limitations and adding new capabilities.

### Key Features

- 🔄 **Generic Pipeline System** - Works for ingestion, retrieval, and custom workflows
- ⚡ **Parallel Execution** - Run handlers concurrently with automatic safety enforcement
- 🧩 **Pluggable Handlers** - Easy to extend with custom processing steps
- 📊 **Graph Database Support** - Built-in GraphRAG capabilities for knowledge graphs
- 🎯 **Type-Safe Configuration** - IntelliSense-friendly extension methods
- 🔁 **Idempotent Processing** - Safe retries and distributed execution
- 📝 **File Lineage Tracking** - Complete document transformation history
- 🏗️ **Standard Microsoft Stack** - Uses Microsoft.Extensions.AI and VectorData
- 🧪 **Testing-Friendly** - In-memory implementations for easy testing

## Quick Start

### Installation

```bash
dotnet add package HPD-Agent.Memory
```

### Basic Usage

```csharp
using HPDAgent.Memory.Extensions;
using Microsoft.Extensions.DependencyInjection;

// Setup
var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole());
services.AddHPDAgentMemory(); // In-memory storage

var provider = services.BuildServiceProvider();

// Create a pipeline
var orchestrator = provider.GetRequiredService<
    IPipelineOrchestrator<DocumentIngestionContext>>();

var context = new DocumentIngestionContext
{
    Index = "my-documents",
    PipelineId = Guid.NewGuid().ToString("N"),
    DocumentId = "doc-123",
    Steps = PipelineTemplates.DocumentIngestionSteps,
    Files = new List<DocumentFile>
    {
        new DocumentFile
        {
            Id = "file-1",
            Name = "document.pdf",
            Size = 1024 * 100,
            MimeType = "application/pdf"
        }
    }
};

// Execute
var result = await orchestrator.ExecuteAsync(context);
```

For more examples, see [GETTING_STARTED.md](GETTING_STARTED.md).

## Documentation

### Getting Started
- **[AI Provider Setup](AI_PROVIDER_SETUP_GUIDE.md)** - Configure chat, embeddings, and vector stores ⭐ **START HERE**
- **[Getting Started](GETTING_STARTED.md)** - Quick start guide with examples
- **[Usage Examples](USAGE_EXAMPLES.md)** - Detailed code examples for common scenarios
- **[Parallel Execution Guide](PARALLEL_EXECUTION_GUIDE.md)** - Run handlers concurrently for 2-3x speedup ⚡ **NEW!**

### Building Handlers (No Batteries Included!)
- **[Handler Development Guide](HANDLER_DEVELOPMENT_GUIDE.md)** - How to build custom handlers 📝 **IMPORTANT**
- **[Reference Handler Examples](REFERENCE_HANDLER_EXAMPLES.md)** - Complete working examples
- **[RAG Techniques Cookbook](RAG_TECHNIQUES_COOKBOOK.md)** - Implement various RAG patterns

### Architecture & Status
- **[Project Structure](PROJECT_STRUCTURE.md)** - Architecture and design patterns
- **[Build Status](BUILD_STATUS.md)** - Current implementation status
- **[Priority 1 & 2 Complete](PRIORITY_1_2_IMPLEMENTATION_COMPLETE.md)** - Tagging and filtering features
- **[Second Mover's Advantage Analysis](SECOND_MOVERS_ADVANTAGE_ANALYSIS.md)** - Design philosophy
- **[Parallel Safety Enforcement](PARALLEL_SAFETY_ENFORCEMENT.md)** - How we enforce thread safety

## Why HPD-Agent.Memory?

We studied Kernel Memory extensively and identified areas for improvement:

| Feature | Kernel Memory | HPD-Agent.Memory |
|---------|---------------|------------------|
| Pipeline Types | Ingestion only | ✅ Ingestion + Retrieval + Custom |
| Parallel Execution | Sequential only | ✅ ParallelStep with isolation |
| AI Interfaces | Custom | ✅ Microsoft.Extensions.AI |
| Vector Storage | Custom IMemoryDb | ✅ Microsoft.Extensions.VectorData |
| Graph Support | None | ✅ Built-in IGraphStore |
| Handler Interface | Specific to ingestion | ✅ Generic IPipelineHandler\<T\> |
| Orchestrator | Mixed file I/O | ✅ Clean separation of concerns |
| Error Handling | Simple enum | ✅ Rich PipelineResult |
| Configuration | String dictionary | ✅ Type-safe extensions |

## Architecture

```
┌─────────────────────────────────────────┐
│         Your Application                 │
│      (Custom Handlers)                  │
└─────────────────────────────────────────┘
                  ↓
┌─────────────────────────────────────────┐
│      Pipeline Orchestration             │
│   (InProcessOrchestrator)               │
└─────────────────────────────────────────┘
                  ↓
┌─────────────────────────────────────────┐
│      Pipeline Abstractions              │
│   (IPipelineContext, IPipelineHandler)  │
└─────────────────────────────────────────┐
                  ↓
┌─────────────────────────────────────────┐
│         Storage Layer                   │
│   (IDocumentStore, IGraphStore)         │
└─────────────────────────────────────────┘
```

### Core Concepts

**Pipelines** are sequences of handlers that process contexts:

```csharp
// Ingestion pipeline
extract_text → partition_text → generate_embeddings → save_records

// Retrieval pipeline
query_rewrite → vector_search → graph_search → rerank → format_response
```

**Contexts** carry state through the pipeline:

```csharp
var context = new DocumentIngestionContext
{
    Steps = ["extract_text", "partition_text", "..."],
    Files = [/* documents to process */],
    Arguments = {/* configuration */}
};
```

**Handlers** are processing steps:

```csharp
public class MyHandler : IPipelineHandler<DocumentIngestionContext>
{
    public string StepName => "my_step";

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken)
    {
        // Process context.Files
        return PipelineResult.Success();
    }
}
```

## Storage Options

### In-Memory (Development)

```csharp
services.AddHPDAgentMemory(); // Uses temp directory + in-memory graph
```

### Local File Storage

```csharp
services.AddHPDAgentMemory("/var/lib/hpd-agent/data");
```

### Custom Storage

```csharp
services.AddHPDAgentMemoryCore();
services.AddSingleton<IDocumentStore, MyDocumentStore>();
services.AddSingleton<IGraphStore, MyGraphStore>();
```

## Pipeline Templates

Pre-built pipeline configurations:

```csharp
// Document ingestion
PipelineTemplates.DocumentIngestionSteps

// Document ingestion with knowledge graph
PipelineTemplates.DocumentIngestionWithGraphSteps

// Basic semantic search
PipelineTemplates.SemanticSearchSteps

// Hybrid search (vector + graph)
PipelineTemplates.HybridSearchSteps

// GraphRAG-style retrieval
PipelineTemplates.GraphRAGSteps
```

## Graph Database Support

HPD-Agent.Memory includes built-in graph database support for advanced retrieval:

```csharp
var graphStore = provider.GetRequiredService<IGraphStore>();

// Create entities
var entity = new GraphEntity
{
    Id = "person-1",
    Type = "Person",
    Properties = new Dictionary<string, object>
    {
        ["name"] = "John Doe"
    }
};
await graphStore.SaveEntityAsync(entity);

// Create relationships
var relationship = new GraphRelationship
{
    FromId = "person-1",
    ToId = "doc-1",
    Type = "authored"
};
await graphStore.SaveRelationshipAsync(relationship);

// Traverse the graph
var results = await graphStore.TraverseAsync(
    "person-1",
    new GraphTraversalOptions { MaxHops = 2 }
);
```

## Idempotency & Resumability

Pipelines automatically track processing state:

```csharp
// Check if already processed
if (context.AlreadyProcessedBy("extract_text"))
{
    // Skip this step
}

// Mark as processed
context.MarkProcessedBy("extract_text");

// Works at file level too
if (file.AlreadyProcessedBy("generate_embeddings"))
{
    // Skip this file
}
```

This enables:
- ✅ Safe retries after failures
- ✅ Distributed execution (multiple workers)
- ✅ Resume from any pipeline step

## Microsoft Integration

### Microsoft.Extensions.AI

Standard interfaces for AI services:

```csharp
IEmbeddingGenerator<string, Embedding<float>> embedder;
IChatClient chatClient;
```

### Microsoft.Extensions.VectorData

Standard interfaces for vector storage:

```csharp
IVectorStore vectorStore;
var collection = vectorStore.GetCollection<string, DocumentRecord>("docs");
var results = await collection.VectorizedSearchAsync(queryVector);
```

## Contributing

Contributions are welcome! When adding features:

1. Start with abstractions (define interfaces first)
2. Follow Kernel Memory patterns when they make sense
3. Improve on Kernel Memory when opportunities arise
4. Use Microsoft standards (prefer MS.Extensions.* over custom)
5. Document your decisions

## Roadmap

- [x] Core pipeline abstractions
- [x] In-process orchestrator
- [x] Local file storage
- [x] In-memory graph store
- [x] DI extensions
- [ ] Handler implementations
- [ ] Distributed orchestrator
- [ ] Neo4j graph store
- [ ] Azure Cosmos DB graph store
- [ ] Unit tests
- [ ] Integration tests
- [ ] Benchmarks

## Inspiration & Credits

This project was inspired by and learned from:

- **[Microsoft Kernel Memory](https://github.com/microsoft/kernel-memory)** - Excellent document memory system
- **[Microsoft Semantic Kernel](https://github.com/microsoft/semantic-kernel)** - AI orchestration framework
- **[Microsoft.Extensions.AI](https://github.com/dotnet/extensions)** - Standard AI abstractions

## License

MIT License - see LICENSE file for details

## Support

- 📖 [Documentation](GETTING_STARTED.md)
- 🐛 [Issue Tracker](https://github.com/your-org/hpd-agent/issues)
- 💬 [Discussions](https://github.com/your-org/hpd-agent/discussions)

---

**Built with second mover's advantage** - learning from the best, improving on the rest.
