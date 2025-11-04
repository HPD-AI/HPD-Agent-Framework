# Parallel Execution Guide

This guide explains how to use parallel execution in HPD-Agent.Memory pipelines for improved performance.

## 🎯 Overview

HPD-Agent.Memory supports **parallel execution of pipeline handlers** with **automatic safety enforcement**. This means you can run multiple handlers concurrently without worrying about race conditions or shared state corruption.

### Key Benefits

- **2-3x Performance Improvement**: Hybrid search, multi-model embedding, multi-storage writes
- **Zero Trust Safety**: Context isolation enforced by orchestrator, not user promises
- **Simple API**: One line to declare parallel execution
- **Production Ready**: Based on patterns from LiteRAG and other production systems

---

## 📐 Architecture

### Sequential vs Parallel Steps

```
Sequential Pipeline (Old):
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   Extract   │───▶│  Partition  │───▶│  Generate   │
│    Text     │    │    Text     │    │ Embeddings  │
└─────────────┘    └─────────────┘    └─────────────┘
   5 seconds          3 seconds          8 seconds

Total Time: 16 seconds

Parallel Pipeline (New):
┌─────────────┐    ┌─────────────────────────────────┐
│   Extract   │───▶│  Generate Embeddings (Parallel) │
│    Text     │    │  ┌─────┐  ┌─────┐  ┌─────┐     │
└─────────────┘    │  │OpenAI│ │Azure│ │Local│     │
   5 seconds       │  └─────┘  └─────┘  └─────┘     │
                   └─────────────────────────────────┘
                              8 seconds (max)

Total Time: 13 seconds (23% faster)
```

### Context Isolation Pattern

```
Main Context                    Isolated Contexts
┌──────────────┐               ┌──────────────┐
│              │──copy──────▶  │  Handler 1   │
│              │               │  (isolated)  │
│   Shared     │               └──────┬───────┘
│   State      │                      │
│              │──copy──────▶         │merge
│              │               ┌──────▼───────┐
└──────────────┘               │  Handler 2   │
                               │  (isolated)  │
                               └──────────────┘

No race conditions! Each handler works on its own copy.
Results merged back after all handlers complete.
```

---

## 🚀 Quick Start

### 1. Basic Parallel Step

```csharp
using HPDAgent.Memory.Abstractions.Pipeline;

// Define parallel step
var steps = new List<PipelineStep>
{
    new SequentialStep { HandlerName = "extract_text" },
    new ParallelStep
    {
        HandlerNames = new[] { "vector_search", "graph_search" }
    },
    new SequentialStep { HandlerName = "merge_results" }
};

// Create context
var context = new SemanticSearchContext
{
    Index = "documents",
    Query = "search query",
    Services = serviceProvider,
    Steps = steps,
    RemainingSteps = new List<PipelineStep>(steps)
};

// Execute - parallel step runs automatically!
var result = await orchestrator.ExecuteAsync(context);
```

### 2. Using Built-in Templates

```csharp
// HybridSearchSteps already includes parallel execution!
var context = new SemanticSearchContext
{
    Index = "knowledge-base",
    Query = "What is RAG?",
    Services = serviceProvider,
    Steps = PipelineTemplates.HybridSearchSteps.ToList(),
    RemainingSteps = PipelineTemplates.HybridSearchSteps.ToList()
};

// vector_search and graph_search run in parallel automatically
var result = await orchestrator.ExecuteAsync(context);
```

### 3. Using PipelineBuilder

```csharp
var builder = new PipelineBuilder<DocumentIngestionContext>()
    .WithServices(serviceProvider)
    .AddStep("extract_text")
    .AddParallelStep("vector_embed", "sparse_embed", "multimodal_embed")
    .AddStep("save_records");

context.Steps = builder._steps;
context.RemainingSteps = new List<PipelineStep>(builder._steps);
```

---

## 🎨 Common Patterns

### Pattern 1: Multi-Model Embedding

**Use Case**: Generate embeddings with multiple models for hybrid search

```csharp
new ParallelStep
{
    HandlerNames = new[]
    {
        "generate_openai_embeddings",    // Dense embeddings
        "generate_azure_embeddings",     // Dense embeddings (fallback)
        "generate_bm25_embeddings"       // Sparse embeddings
    }
}

// Result: All embeddings generated concurrently
// Handler merge logic: Context.Data["embeddings"] = all three results
```

### Pattern 2: Multi-Source Retrieval

**Use Case**: Search across multiple data sources simultaneously

```csharp
new ParallelStep
{
    HandlerNames = new[]
    {
        "vector_search",      // Search vector database
        "graph_search",       // Traverse knowledge graph
        "keyword_search"      // BM25 full-text search
    }
}

// Result: All search results available for hybrid merging
// 3x faster than sequential search
```

### Pattern 3: Multi-Storage Write

**Use Case**: Write to multiple storage backends for redundancy

```csharp
new ParallelStep
{
    HandlerNames = new[]
    {
        "save_to_postgres",
        "save_to_mongodb",
        "save_to_s3"
    }
}

// Result: Data persisted to all backends concurrently
// If any write fails, all fail (all-or-nothing)
```

### Pattern 4: Multi-Stage Parallel Pipeline

**Use Case**: Complex pipeline with multiple parallel stages

```csharp
var steps = new List<PipelineStep>
{
    // Stage 1: Extraction
    new SequentialStep { HandlerName = "extract_text" },

    // Stage 2: Parallel content processing
    new ParallelStep
    {
        HandlerNames = new[]
        {
            "extract_tables",
            "extract_images",
            "extract_code_blocks"
        }
    },

    new SequentialStep { HandlerName = "merge_extractions" },

    // Stage 3: Parallel embedding generation
    new ParallelStep
    {
        HandlerNames = new[]
        {
            "generate_dense_embeddings",
            "generate_sparse_embeddings"
        }
    },

    // Stage 4: Parallel storage
    new ParallelStep
    {
        HandlerNames = new[]
        {
            "save_to_vector_db",
            "save_to_document_store"
        }
    }
};
```

---

## 🔒 Safety Guarantees

### Automatic Context Isolation

The orchestrator **automatically** creates isolated context copies for each parallel handler:

```csharp
// You don't write this - the orchestrator does it automatically!
var isolatedContexts = handlers.Select(_ => context.CreateIsolatedCopy()).ToList();

// Each handler gets its own copy
await Task.WhenAll(
    handler1.HandleAsync(isolatedContext1, ct),
    handler2.HandleAsync(isolatedContext2, ct),
    handler3.HandleAsync(isolatedContext3, ct)
);

// Results merged back safely
foreach (var isolated in isolatedContexts)
{
    mainContext.MergeFrom(isolated);
}
```

### What Gets Isolated?

Each isolated context gets a **deep copy** of:
- ✅ Data dictionary
- ✅ Tags
- ✅ Log entries
- ✅ Files (for ingestion contexts)
- ✅ Results (for retrieval contexts)

**Shared (read-only)**:
- ✅ Services (IServiceProvider)
- ✅ Pipeline metadata (PipelineId, ExecutionId, Index)

### All-or-Nothing Error Policy

```csharp
// If ANY handler fails, the ENTIRE step fails
new ParallelStep
{
    HandlerNames = new[] { "handler1", "handler2", "handler3" }
}

// Scenario 1: All succeed ✅
// - All results merged
// - Pipeline continues to next step

// Scenario 2: One fails ❌
// - Entire step fails immediately
// - Pipeline stops with exception
// - No partial results merged
```

---

## ⚙️ Advanced Configuration

### Rate Limiting with MaxConcurrency

```csharp
// Limit concurrent handlers (useful for API rate limits)
new ParallelStep
{
    HandlerNames = new[]
    {
        "call_openai_api",
        "call_azure_api",
        "call_anthropic_api",
        "call_cohere_api"
    },
    MaxConcurrency = 2  // Only 2 API calls at once
}

// Execution order:
// T0: call_openai_api + call_azure_api start
// T1: call_openai_api finishes, call_anthropic_api starts
// T2: call_azure_api finishes, call_cohere_api starts
// T3: call_anthropic_api + call_cohere_api finish
```

### Custom Merge Logic in Handlers

Handlers control how their results are merged:

```csharp
public class VectorSearchHandler : IPipelineHandler<SemanticSearchContext>
{
    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken)
    {
        // Perform vector search
        var results = await PerformVectorSearch(context.Query);

        // Add results to context - merge logic is automatic!
        context.Results.AddRange(results.Select(r => new SearchResult
        {
            Id = r.Id,
            DocumentId = r.DocumentId,
            Score = r.Score,
            Content = r.Content,
            Source = "vector_search"  // Tag source
        }));

        return PipelineResult.Success();
    }
}

// When merged back to main context:
// - Results are union-merged by ID
// - Duplicate IDs are deduplicated
// - All unique results preserved
```

---

## 📊 Performance Guidelines

### When to Use Parallel Execution

✅ **Good candidates:**
- Multi-source retrieval (vector + graph + keyword)
- Multi-model inference (multiple LLMs, multiple embedding models)
- Multi-storage writes (redundant persistence)
- Independent data transformations

❌ **Poor candidates:**
- Handlers with dependencies (use sequential instead)
- Very fast handlers (<100ms) - overhead not worth it
- Handlers that modify shared external state

### Expected Performance Gains

| Scenario | Sequential Time | Parallel Time | Speedup |
|----------|----------------|---------------|---------|
| Hybrid search (2 sources) | 10s | 5.5s | 1.8x |
| Multi-model embedding (3 models) | 15s | 6s | 2.5x |
| Multi-storage write (3 backends) | 9s | 4s | 2.25x |
| Complex pipeline (3 parallel stages) | 30s | 14s | 2.1x |

**Note**: Actual speedup depends on:
- Handler execution time variance
- System resources (CPU cores)
- External API latency
- Network conditions

### Overhead Analysis

Context isolation overhead: **~1.5ms per handler**

```
Example: 3-handler parallel step
- Isolation overhead: 3 × 1.5ms = 4.5ms
- Handler execution: 5000ms (slowest handler)
- Total overhead: 4.5ms / 5000ms = 0.09%

Verdict: Negligible for real-world handlers!
```

---

## 🔍 Debugging and Monitoring

### Logging

The orchestrator logs detailed information:

```
[Information] Executing parallel step with 3 handlers: vector_search, graph_search, keyword_search
[Debug] Created 3 isolated contexts for parallel execution
[Information] Handler 'vector_search' completed successfully in parallel step
[Information] Handler 'graph_search' completed successfully in parallel step
[Information] Handler 'keyword_search' completed successfully in parallel step
[Debug] Merging 3 isolated contexts back into main context
[Information] Parallel step completed successfully: 3 handlers executed
```

### Error Handling

Failed parallel steps provide detailed diagnostics:

```
PipelineException: Parallel step failed: 1 handler(s) failed. First failure: Network timeout
  Step: Parallel(vector_search, graph_search)
  Failed Handlers: graph_search
  IsTransient: true

Stack trace shows:
- Which handler failed
- Why it failed
- Whether it's retryable
```

### Progress Tracking

Monitor parallel execution progress:

```csharp
// In handler, use context progress properties
_logger.LogInformation(
    "Handler progress: {Current}/{Total} ({Percentage:F1}%)",
    context.CurrentStepIndex,
    context.TotalSteps,
    context.Progress * 100);

// For parallel steps, track handler completion
if (context.IsCurrentStepParallel)
{
    var completed = context.GetCompletedHandlersInCurrentStep();
    _logger.LogInformation(
        "Parallel step progress: {Completed}/{Total} handlers",
        completed.Count,
        context.CurrentHandlerNames.Count);
}
```

---

## 🎓 Best Practices

### 1. Design for Idempotency

Handlers should be safe to retry:

```csharp
public async Task<PipelineResult> HandleAsync(SemanticSearchContext context, ...)
{
    // ✅ Check if already processed
    if (context.AlreadyProcessedBy(StepName))
    {
        return PipelineResult.Success();
    }

    // Do work...

    // ✅ Mark as processed
    context.MarkProcessedBy(StepName);
    return PipelineResult.Success();
}
```

### 2. Use Descriptive Handler Names

```csharp
// ❌ Bad
new ParallelStep { HandlerNames = new[] { "h1", "h2", "h3" } }

// ✅ Good
new ParallelStep
{
    HandlerNames = new[]
    {
        "vector_search_openai",
        "vector_search_azure",
        "bm25_search"
    }
}
```

### 3. Handle Transient Errors

```csharp
try
{
    // Call external API
}
catch (HttpRequestException ex)
{
    // Transient - can retry
    return PipelineResult.TransientFailure(
        "API call failed due to network issue",
        exception: ex);
}
catch (InvalidDataException ex)
{
    // Fatal - cannot retry
    return PipelineResult.FatalFailure(
        "Data validation failed",
        exception: ex);
}
```

### 4. Consider Rate Limits

```csharp
// If calling rate-limited APIs, use MaxConcurrency
new ParallelStep
{
    HandlerNames = new[]
    {
        "openai_embedding_batch1",
        "openai_embedding_batch2",
        "openai_embedding_batch3",
        "openai_embedding_batch4"
    },
    MaxConcurrency = 2  // OpenAI rate limit: 2 concurrent requests
}
```

### 5. Profile Before Parallelizing

```csharp
// Measure handler execution time first
var sw = Stopwatch.StartNew();
await handler.HandleAsync(context, ct);
sw.Stop();

_logger.LogInformation("Handler {Name} took {Ms}ms", handler.StepName, sw.ElapsedMilliseconds);

// Only parallelize if handlers take >100ms
// Overhead not worth it for fast handlers
```

---

## 🆚 Comparison with Other Systems

### HPD-Agent.Memory vs Kernel Memory

| Feature | Kernel Memory | HPD-Agent.Memory |
|---------|---------------|------------------|
| Parallel execution | ❌ Sequential only | ✅ Built-in parallel steps |
| Safety enforcement | N/A | ✅ Automatic context isolation |
| Error handling | Basic | ✅ All-or-nothing with diagnostics |
| Configuration | N/A | ✅ MaxConcurrency for rate limiting |
| DX | N/A | ✅ One-line parallel declaration |

### HPD-Agent.Memory vs LiteRAG

| Feature | LiteRAG | HPD-Agent.Memory |
|---------|---------|------------------|
| Parallel execution | ✅ priority_limit_async_func_call | ✅ ParallelStep |
| Concurrency limiting | ✅ Semaphore | ✅ MaxConcurrency |
| Timeout hierarchy | ✅ 4 layers | ⚠️ Basic (can be extended) |
| Health checks | ✅ Worker health monitoring | ❌ Not implemented |
| Scope | Complete RAG app | Infrastructure library |

**Key Difference**: LiteRAG is a complete application with full control over handlers. HPD-Agent.Memory is infrastructure - we provide primitives, users control implementation.

---

## 📚 Additional Resources

- [USAGE_EXAMPLES.md](./USAGE_EXAMPLES.md) - More code examples
- [LITERAG_PRODUCTION_PATTERNS_ANALYSIS.md](./LITERAG_PRODUCTION_PATTERNS_ANALYSIS.md) - Production patterns we studied
- [PARALLEL_SAFETY_ENFORCEMENT.md](./PARALLEL_SAFETY_ENFORCEMENT.md) - Deep dive on safety design
- [INFRASTRUCTURE_VS_APPLICATION_SCOPE.md](./INFRASTRUCTURE_VS_APPLICATION_SCOPE.md) - Scope decisions

---

## 🤝 Contributing

Found a bug or have a feature request? Please open an issue!

Want to contribute? See [HANDLER_DEVELOPMENT_GUIDE.md](./HANDLER_DEVELOPMENT_GUIDE.md) for guidelines.
