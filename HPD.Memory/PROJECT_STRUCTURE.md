# HPD-Agent.Memory Project Structure

## Overview

HPD-Agent.Memory is a next-generation document memory system built with "second mover's advantage" - learning from Microsoft's Kernel Memory and improving upon it.

## Directory Structure

```
HPD-Agent.Memory/
├── Abstractions/              # Interfaces and contracts
│   ├── Pipeline/             # Pipeline system abstractions
│   │   ├── IPipelineContext.cs
│   │   ├── IIngestionContext.cs
│   │   ├── IRetrievalContext.cs
│   │   ├── IPipelineHandler.cs
│   │   ├── IPipelineOrchestrator.cs
│   │   └── PipelineContextExtensions.cs
│   ├── Models/               # Domain models
│   │   └── DocumentFile.cs
│   └── Storage/              # Storage abstractions
│       ├── IDocumentStore.cs
│       ├── IGraphStore.cs
│       └── GraphModels.cs
├── Core/                      # Core implementations
│   ├── Contexts/             # Concrete context implementations
│   │   ├── DocumentIngestionContext.cs
│   │   └── SemanticSearchContext.cs
│   ├── Orchestration/        # Pipeline orchestration
│   │   ├── InProcessOrchestrator.cs
│   │   └── PipelineBuilder.cs
│   └── Storage/              # Storage implementations
│       ├── LocalFileDocumentStore.cs
│       └── InMemoryGraphStore.cs
├── Extensions/               # Dependency injection
│   └── MemoryServiceCollectionExtensions.cs
├── GETTING_STARTED.md        # Quick start guide
├── USAGE_EXAMPLES.md         # Detailed examples
└── PROJECT_STRUCTURE.md      # This file
```

## Layer Architecture

```
┌─────────────────────────────────────────┐
│         Application Layer                │
│   (Your handlers and business logic)    │
└─────────────────────────────────────────┘
                  ↓
┌─────────────────────────────────────────┐
│      Core Orchestration Layer           │
│  - InProcessOrchestrator                │
│  - Pipeline execution                   │
│  - Error handling                       │
└─────────────────────────────────────────┘
                  ↓
┌─────────────────────────────────────────┐
│      Pipeline Abstractions Layer        │
│  - IPipelineContext                     │
│  - IPipelineHandler<TContext>           │
│  - PipelineResult                       │
└─────────────────────────────────────────┘
                  ↓
┌─────────────────────────────────────────┐
│         Storage Layer                   │
│  - IDocumentStore → LocalFileStore      │
│  - IGraphStore → InMemoryGraphStore     │
└─────────────────────────────────────────┘
```

## Key Design Patterns

### 1. Generic Context Pattern

**Problem**: Kernel Memory hardcoded `DataPipeline` type everywhere.

**Solution**: Generic `IPipelineContext` base with specialized markers:

```csharp
// Base contract
public interface IPipelineContext { ... }

// Marker interfaces for type constraints
public interface IIngestionContext : IPipelineContext { }
public interface IRetrievalContext : IPipelineContext { }

// Generic orchestrator works with any context
public interface IPipelineOrchestrator<TContext>
    where TContext : IPipelineContext
{
    Task<TContext> ExecuteAsync(TContext context, ...);
}
```

**Benefits**:
- ✅ Same orchestrator handles ingestion AND retrieval
- ✅ Type-safe handler registration
- ✅ Easy to add new pipeline types

### 2. File Lineage Tracking

**Inspired by**: Kernel Memory's FileDetails pattern

**Improvement**: Parent-child relationships with deduplication:

```csharp
public class DocumentFile
{
    public Dictionary<string, GeneratedFile> GeneratedFiles { get; set; }
    public List<string> ProcessedBy { get; set; }
}

public class GeneratedFile : DocumentFile
{
    public required string ParentId { get; init; }
    public string? SourcePartitionId { get; set; }
    public string? ContentSHA256 { get; set; } // For deduplication
}
```

**Benefits**:
- ✅ Track entire document transformation pipeline
- ✅ Deduplicate identical content across documents
- ✅ Enable provenance queries ("Which source created this?")

### 3. Idempotency Pattern

**Borrowed from**: Kernel Memory's AlreadyProcessedBy/MarkProcessedBy

**Applied at**: Both context level and file level:

```csharp
// Context level: Has this step run for this pipeline?
if (!context.AlreadyProcessedBy("extract_text"))
{
    // Run handler
    context.MarkProcessedBy("extract_text");
}

// File level: Has this handler processed this file?
foreach (var file in context.Files)
{
    if (file.AlreadyProcessedBy("generate_embeddings", "chunk_0"))
    {
        continue; // Already embedded this chunk
    }

    // Process...

    file.MarkProcessedBy("generate_embeddings", "chunk_0");
}
```

**Benefits**:
- ✅ Safe retries after failures
- ✅ Distributed execution (multiple workers can process same pipeline)
- ✅ Resume from any point in pipeline

### 4. Rich Result Pattern

**Problem**: Kernel Memory uses simple enum return type:

```csharp
// Kernel Memory approach
public enum ReturnType { Success, TransientError, FatalError }
```

**Solution**: Rich PipelineResult record:

```csharp
public record PipelineResult
{
    public required bool IsSuccess { get; init; }
    public bool IsTransient { get; init; }
    public string? ErrorMessage { get; init; }
    public Exception? Exception { get; init; }
    public Dictionary<string, object> Metadata { get; init; }

    public static PipelineResult Success(...);
    public static PipelineResult TransientFailure(...);
    public static PipelineResult FatalFailure(...);
}
```

**Benefits**:
- ✅ Include exception for debugging
- ✅ Add custom metadata
- ✅ Better error messages
- ✅ Still supports pattern matching

### 5. Type-Safe Configuration

**Inspired by**: Kernel Memory's context arguments pattern

**Improvement**: Strongly-typed extension methods:

```csharp
// Instead of:
context.Arguments["max_tokens"] = 1000;
var maxTokens = (int)context.Arguments["max_tokens"];

// We have:
context.SetMaxTokensPerChunk(1000);
var maxTokens = context.GetMaxTokensPerChunkOrDefault(defaultValue: 1000);
```

**Benefits**:
- ✅ Compile-time type safety
- ✅ IntelliSense support
- ✅ Default value handling
- ✅ No casting errors

## Key Improvements Over Kernel Memory

| Feature | Kernel Memory | HPD-Agent.Memory |
|---------|---------------|------------------|
| Pipeline Types | Ingestion only | Ingestion + Retrieval + Custom |
| AI Interfaces | Custom interfaces | Microsoft.Extensions.AI |
| Vector Storage | Custom IMemoryDb | Microsoft.Extensions.VectorData |
| Graph Support | None | Built-in IGraphStore |
| Orchestrator | Mixes file I/O & orchestration | Clean separation |
| Context Type | Hardcoded DataPipeline | Generic IPipelineContext |
| Handler Interface | Specific to ingestion | Generic IPipelineHandler<T> |
| Error Handling | Simple enum | Rich PipelineResult |
| Configuration | String dictionary | Type-safe extensions |
| DI Integration | Special service provider | Standard ASP.NET Core |

## Integration Points

### Microsoft.Extensions.AI

Used for AI service abstractions:

```csharp
// Embedding generation
IEmbeddingGenerator<string, Embedding<float>> embedder;
var embeddings = await embedder.GenerateAsync(["text1", "text2"]);

// Chat completion
IChatClient chatClient;
var response = await chatClient.CompleteAsync([...messages]);
```

### Microsoft.Extensions.VectorData.Abstractions

Used for vector storage:

```csharp
// Vector search
IVectorStore vectorStore;
var collection = vectorStore.GetCollection<string, DocumentRecord>("documents");
var results = await collection.VectorizedSearchAsync(
    queryVector,
    new VectorSearchOptions { Top = 10 }
);
```

### Custom IGraphStore

No Microsoft equivalent, so we built it:

```csharp
// Graph operations
IGraphStore graphStore;

// Save entities and relationships
await graphStore.SaveEntityAsync(entity);
await graphStore.SaveRelationshipAsync(relationship);

// Traverse graph
var results = await graphStore.TraverseAsync(
    startId,
    new GraphTraversalOptions { MaxHops = 2 }
);

// Find shortest path
var path = await graphStore.FindShortestPathAsync(fromId, toId);
```

## Future Roadmap

### Storage Implementations

- [ ] Neo4j graph store
- [ ] Azure Cosmos DB (Gremlin) graph store
- [ ] Azure Blob Storage document store
- [ ] S3-compatible document store

### Orchestration

- [ ] Distributed orchestrator (using queues)
- [ ] Batch processing orchestrator
- [ ] Streaming orchestrator (for real-time updates)

### Handlers

- [ ] Text extraction (PDF, DOCX, etc.)
- [ ] Partitioning (semantic chunking)
- [ ] Entity extraction (NER)
- [ ] Embedding generation
- [ ] Graph building
- [ ] Vector search
- [ ] Graph traversal
- [ ] Reranking
- [ ] Access control filtering

### Advanced Features

- [ ] Pipeline versioning
- [ ] A/B testing support
- [ ] Metrics and observability
- [ ] Pipeline caching
- [ ] Conditional steps (branching pipelines)

## Testing Strategy

### Unit Tests

- Test individual handlers in isolation
- Mock IPipelineContext
- Verify idempotency logic

### Integration Tests

- Test full pipelines with InMemoryGraphStore
- Test storage implementations
- Test orchestrator behavior

### End-to-End Tests

- Test complete ingestion → retrieval flows
- Test error recovery and retries
- Test with real AI services (or mocks)

## Contributing Guidelines

When adding new features:

1. **Start with abstractions**: Define interfaces first
2. **Follow Kernel Memory patterns**: When they make sense
3. **Improve on Kernel Memory**: When you see opportunities
4. **Use Microsoft standards**: Prefer MS.Extensions.* over custom
5. **Document decisions**: Explain why in comments and docs
6. **Test thoroughly**: Unit + integration + e2e

## References

- [Kernel Memory Source](https://github.com/microsoft/kernel-memory)
- [Microsoft.Extensions.AI](https://github.com/dotnet/extensions)
- [Microsoft.Extensions.VectorData](https://github.com/microsoft/semantic-kernel)
- [Second Mover's Advantage](https://en.wikipedia.org/wiki/First-mover_advantage)
