# Proposal: Callback-Based Retrieval for Static Memory

**Date:** October 14, 2025  
**Status:** Proposed  
**Author:** AI Architecture Team  
**Reviewer:** Assistant

---

## Executive Summary

This proposal recommends refactoring the Static Memory (agent knowledge) retrieval system from a rigid interface-based approach (`IDocumentMemoryPipeline`) to a flexible callback-based architecture. This change aligns with HPD-Agent.Memory's "infrastructure-first" philosophy and future-proofs the system against rapidly evolving RAG techniques.

---

## Problem Statement

### Current Architecture Issues

The current `IDocumentMemoryPipeline` interface forces all retrieval strategies into a fixed shape:

```csharp
public interface IDocumentMemoryPipeline
{
    Task<RetrievalResult> RetrieveAsync(
        string query,          // ❌ Text-only queries
        string index,          // ❌ Single index only
        int maxResults = 10,   // ❌ Fixed parameters
        double minRelevanceScore = 0.0
    );
}
```

**Limitations:**

1. **Text-Only Queries**: Cannot support multimodal queries (text + images)
2. **Fixed Parameters**: Cannot add custom metadata, filters, or retrieval options
3. **Single Strategy**: Cannot support GraphRAG (entities + relationships)
4. **Rigid Output**: `RetrievalResult` cannot represent graph structures or complex results
5. **Vendor Lock-in**: Forces users into specific pipeline implementations

### Real-World Scenarios That Don't Work

| RAG Technique | Why Current Interface Fails |
|--------------|----------------------------|
| **GraphRAG** | Needs entity IDs, relationship types, hop count - can't fit in `RetrieveAsync(string query, ...)` |
| **Multimodal RAG** | Needs image queries, audio queries - locked to `string query` |
| **Hybrid Search** | Needs multiple search types (vector + keyword + graph) - single `RetrievalResult` insufficient |
| **Agentic RAG** | Needs query rewriting, tool use, multi-hop reasoning - too complex for simple interface |
| **Access Control** | Needs user context, permissions, org filtering - no place in current signature |

### The RAG Innovation Treadmill

**Historical Context:**
- 2023: Basic RAG (embed → retrieve → generate)
- 2024: GraphRAG, HyDE, Multi-query, Reranking
- 2025: Agentic RAG, Corrective RAG, Self-RAG
- **Every month**: New papers, new techniques, new "best practices"

**Problem:** Any concrete implementation becomes obsolete within months.

**Example:** Microsoft Kernel Memory (2023) → locked into simple chunking, cannot support GraphRAG without major breaking changes.

---

## Proposed Solution

### Callback-Based Architecture

Replace the rigid interface with a flexible callback:

```csharp
// Callback signature: simple input → formatted output
public delegate Task<string> RetrievalCallback(
    string query, 
    CancellationToken cancellationToken
);

// Usage in StaticMemoryOptions
public class StaticMemoryOptions
{
    // User provides their own retrieval logic
    public RetrievalCallback? RetrievalCallback { get; set; }
    
    // Convenience: backwards compatibility
    public IDocumentMemoryPipeline? MemoryPipeline { get; set; }
}
```

### Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│  HPD-Agent Infrastructure Layer                             │
│  (Handles WHEN/WHERE/HOW to inject)                         │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  IndexedRetrievalFilter / StaticMemoryRetrievalPlugin       │
│    │                                                        │
│    ├─► Extracts query from conversation                    │
│    ├─► Calls user's RetrievalCallback(query, ct)           │
│    └─► Injects returned knowledge into prompt              │
│                                                             │
└─────────────────────────────────────────────────────────────┘
                         │
                         │ Passes query string
                         ▼
┌─────────────────────────────────────────────────────────────┐
│  User's Application Layer                                   │
│  (Handles WHAT/HOW to retrieve)                             │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  async (query, ct) => {                                     │
│      // User controls everything inside:                   │
│      var context = CreateMyContext(query);                 │
│      context = await myOrchestrator.ExecuteAsync(context); │
│      return FormatResults(context);                        │
│  }                                                          │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

---

## Implementation Plan

### Phase 1: Update Core Types

**Files to modify:**
- `StaticMemoryOptions.cs` - Add `RetrievalCallback` property
- `IndexedRetrievalFilter.cs` - Accept callback instead of `IDocumentMemoryPipeline`
- `StaticMemoryRetrievalPlugin.cs` - Accept callback instead of `IDocumentMemoryPipeline`

**Backward Compatibility:**
```csharp
// Old API (still works)
opts.UseIndexedRetrieval()
    .WithMemoryPipeline(pipeline);  // Adapter wraps it

// New API (full power)
opts.UseIndexedRetrieval(async (query, ct) => 
{
    // Custom logic
});
```

### Phase 2: Extension Methods

Add fluent builder methods:

```csharp
// Callback-based (full control)
public static StaticMemoryOptions UseIndexedRetrieval(
    this StaticMemoryOptions opts,
    Func<string, CancellationToken, Task<string>> retrievalCallback)

// Orchestrator-based (convenience)
public static StaticMemoryOptions UseIndexedRetrieval<TContext>(
    this StaticMemoryOptions opts,
    IPipelineOrchestrator<TContext> orchestrator,
    Func<string, string, TContext> contextFactory,
    Func<TContext, string> resultFormatter)
    where TContext : IRetrievalContext
```

### Phase 3: Adapter for Legacy Interface

Wrap `IDocumentMemoryPipeline` as a callback:

```csharp
internal static Func<string, CancellationToken, Task<string>> 
    WrapPipeline(IDocumentMemoryPipeline pipeline, string index)
{
    return async (query, ct) =>
    {
        var result = await pipeline.RetrieveAsync(query, index, 10, 0.7, ct);
        return FormatRetrievalResult(result);
    };
}
```

### Phase 4: Documentation & Examples

Create comprehensive examples:
- `examples/text-rag-retrieval.md` - Basic semantic search
- `examples/graphrag-retrieval.md` - GraphRAG with entities
- `examples/multimodal-retrieval.md` - Text + image queries
- `examples/hybrid-retrieval.md` - Combined vector + graph + keyword
- `examples/custom-api-retrieval.md` - External API integration

### Phase 5: Migration Guide

Document migration path for existing users:

```markdown
# Migration Guide

## Before (Old API)
```csharp
builder.WithStaticMemory(opts => 
{
    opts.UseIndexedRetrieval()
        .WithMemoryPipeline(myPipeline);
});
```

## After (New API - Option 1: Wrapped)
```csharp
builder.WithStaticMemory(opts => 
{
    // Still works! Internal adapter wraps it
    opts.UseIndexedRetrieval()
        .WithMemoryPipeline(myPipeline);
});
```

## After (New API - Option 2: Direct)
```csharp
builder.WithStaticMemory(opts => 
{
    opts.UseIndexedRetrieval(async (query, ct) => 
    {
        var context = new SemanticSearchContext { Query = query };
        context = await orchestrator.ExecuteAsync(context, ct);
        return FormatResults(context.Results);
    });
});
```
```

---

## Benefits

### 1. Future-Proof Architecture

**Problem Solved:** RAG techniques evolve monthly, concrete implementations become obsolete.

**Solution:** Users provide their own retrieval logic as callbacks. When new techniques emerge, they just update their callback—no framework changes needed.

```csharp
// 2025: Text RAG
opts.UseIndexedRetrieval(async (query, ct) => 
    await TextRagPipeline(query));

// 2026: New technique discovered
opts.UseIndexedRetrieval(async (query, ct) => 
    await NewTechniquePipeline(query));

// Framework code unchanged! ✅
```

### 2. Infrastructure-First Philosophy

**Separation of Concerns:**

| HPD-Agent (Infrastructure) | User (Application) |
|---------------------------|-------------------|
| ✓ WHEN to retrieve | ✓ HOW to retrieve |
| ✓ WHERE to inject | ✓ WHAT to retrieve |
| ✓ HOW to inject | ✓ HOW to format |
| ✓ Lifecycle management | ✓ Pipeline implementation |

### 3. Maximum Flexibility

Users can implement **any** retrieval strategy:

- ✅ Text RAG (semantic search)
- ✅ GraphRAG (entities + relationships)
- ✅ Multimodal RAG (text + images + audio)
- ✅ Hybrid RAG (vector + keyword + graph)
- ✅ Agentic RAG (LLM-powered query rewriting)
- ✅ Custom APIs (external services)
- ✅ Local search (file system, databases)
- ✅ **Techniques that don't exist yet**

### 4. No Breaking Changes

Existing code continues to work through adapter pattern:

```csharp
// Old code (still works)
opts.UseIndexedRetrieval()
    .WithMemoryPipeline(pipeline);

// Internally converts to:
opts.RetrievalCallback = WrapPipeline(pipeline, index);
```

### 5. Consistent Behavior

Automatic retrieval filter AND agent plugin use same callback:

```csharp
opts.UseIndexedRetrieval(async (query, ct) => 
{
    return await MyRetrievalLogic(query);
});

opts.EnableAgentControlledRetrieval = true;

// Both use same callback:
// - IndexedRetrievalFilter (automatic)
// - StaticMemoryRetrievalPlugin (agent-controlled)
// → Single source of truth ✅
```

---

## Developer Experience Examples

### Example 1: Simple Text RAG

```csharp
builder.WithStaticMemory(opts => 
{
    opts.UseIndexedRetrieval(async (query, ct) => 
    {
        // Simple semantic search
        var context = new SemanticSearchContext
        {
            Query = query,
            Index = "my-knowledge",
            MaxResults = 5,
            MinScore = 0.7f
        };
        
        context = await textRagOrchestrator.ExecuteAsync(context, ct);
        
        return string.Join("\n\n", context.Results.Select(r => 
            $"[Source: {r.DocumentId}]\n{r.Content}"));
    });
    
    opts.AddDocument("./docs/python-guide.md");
});
```

### Example 2: GraphRAG with Entity Extraction

```csharp
builder.WithStaticMemory(opts => 
{
    opts.UseIndexedRetrieval(async (query, ct) => 
    {
        // Extract entities from query
        var entities = await ExtractEntities(query);
        
        // Create GraphRAG context
        var context = new GraphRAGContext
        {
            Query = query,
            EntityIds = entities.Select(e => e.Id).ToList(),
            MaxHops = 2,
            IncludeVectorSearch = true
        };
        
        // Run pipeline with parallel graph + vector search
        context = await graphRagOrchestrator.ExecuteAsync(context, ct);
        
        // Format with graph structure
        var knowledge = new StringBuilder();
        knowledge.AppendLine("# Knowledge Graph");
        
        foreach (var entity in context.Entities)
        {
            knowledge.AppendLine($"## {entity.Name}");
            knowledge.AppendLine(entity.Description);
        }
        
        knowledge.AppendLine("\n## Relationships:");
        foreach (var rel in context.Relationships)
        {
            knowledge.AppendLine($"- {rel.From} --[{rel.Type}]--> {rel.To}");
        }
        
        knowledge.AppendLine("\n## Documents:");
        foreach (var doc in context.ConnectedDocuments)
        {
            knowledge.AppendLine($"[{doc.Title}]: {doc.Content}");
        }
        
        return knowledge.ToString();
    });
});
```

### Example 3: Multimodal RAG

```csharp
builder.WithStaticMemory(opts => 
{
    opts.UseIndexedRetrieval(async (query, ct) => 
    {
        // Search both text and images
        var context = new MultimodalRetrievalContext
        {
            TextQuery = query,
            Modalities = new[] { Modality.Text, Modality.Image },
            MaxResults = 5,
            CrossModalFusion = true
        };
        
        context = await multimodalOrchestrator.ExecuteAsync(context, ct);
        
        // Format with image references
        var knowledge = new StringBuilder();
        foreach (var result in context.Results)
        {
            knowledge.AppendLine($"## {result.Title}");
            knowledge.AppendLine(result.TextContent);
            
            if (result.HasImage)
            {
                knowledge.AppendLine($"![{result.ImageCaption}]({result.ImageUrl})");
            }
        }
        
        return knowledge.ToString();
    });
});
```

### Example 4: Hybrid Search

```csharp
builder.WithStaticMemory(opts => 
{
    opts.UseIndexedRetrieval(async (query, ct) => 
    {
        var context = new HybridSearchContext { Query = query };
        
        // Pipeline runs vector + keyword + graph in parallel
        // then fuses with reciprocal rank fusion
        context = await hybridOrchestrator.ExecuteAsync(context, ct);
        
        return string.Join("\n\n", context.FusedResults.Select((r, i) => 
        {
            var sources = string.Join(", ", r.SourceTypes);
            return $"[Result {i+1}] (score: {r.FusedScore:F2}, sources: {sources})\n{r.Content}";
        }));
    });
});
```

### Example 5: Custom API Integration

```csharp
builder.WithStaticMemory(opts => 
{
    opts.UseIndexedRetrieval(async (query, ct) => 
    {
        // Use external RAG service
        var response = await httpClient.GetAsync(
            $"https://rag-api.company.com/search?q={Uri.EscapeDataString(query)}", 
            ct);
        
        var results = await response.Content.ReadFromJsonAsync<ApiResults>(ct);
        
        return FormatApiResults(results);
    });
});
```

---

## Risks & Mitigation

### Risk 1: Breaking Changes for Current Users

**Mitigation:** Adapter pattern maintains backward compatibility.

```csharp
// Old code still works
opts.WithMemoryPipeline(pipeline);  // Internally wrapped
```

### Risk 2: Complexity for Simple Use Cases

**Mitigation:** Provide convenience methods and templates.

```csharp
// Simple template
opts.UseTextRAG(orchestrator);  // Wraps complexity

// Full power when needed
opts.UseIndexedRetrieval(customCallback);
```

### Risk 3: Documentation Overhead

**Mitigation:** Comprehensive examples repository with common patterns.

```
examples/
├── text-rag/              # Basic semantic search
├── graphrag/              # Entity-based retrieval
├── multimodal/            # Text + images
├── hybrid/                # Combined strategies
├── custom-api/            # External services
└── migration-guide.md     # Step-by-step migration
```

### Risk 4: Performance Concerns

**Mitigation:** Callbacks are as fast as direct interface calls. No overhead.

```csharp
// Interface call
await pipeline.RetrieveAsync(query, ...);  // ← Direct invocation

// Callback
await callback(query, ct);  // ← Direct invocation (same cost)
```

---

## Success Metrics

1. **Adoption Rate**
   - Target: 50% of new StaticMemory implementations use callbacks within 3 months

2. **Flexibility Proof**
   - Target: 5+ different RAG strategies implemented in examples (text, graph, multimodal, hybrid, custom)

3. **Backward Compatibility**
   - Target: 100% of existing code continues working without changes

4. **Documentation Quality**
   - Target: Each RAG strategy has working example with <5 minute setup time

5. **Community Feedback**
   - Target: Positive feedback from early adopters on flexibility vs. complexity tradeoff

---

## Timeline

| Phase | Duration | Deliverables |
|-------|----------|-------------|
| **Phase 1: Core Implementation** | 3 days | Updated filter/plugin/options classes |
| **Phase 2: Extension Methods** | 2 days | Fluent builder API |
| **Phase 3: Adapter & Compat** | 2 days | `IDocumentMemoryPipeline` adapter |
| **Phase 4: Documentation** | 3 days | Examples for 5 RAG strategies |
| **Phase 5: Testing** | 2 days | Unit tests, integration tests |
| **Total** | **12 days** | Production-ready implementation |

---

## Alternatives Considered

### Alternative 1: Keep `IDocumentMemoryPipeline`, Add More Methods

```csharp
public interface IDocumentMemoryPipeline
{
    Task<RetrievalResult> RetrieveAsync(...);           // Text RAG
    Task<GraphRetrievalResult> RetrieveGraphAsync(...); // GraphRAG
    Task<MultimodalResult> RetrieveMultimodalAsync(...);// Multimodal
    // ... more methods as techniques evolve
}
```

**Rejected because:**
- ❌ Interface bloat (methods for every RAG type)
- ❌ Breaking changes every time new technique emerges
- ❌ Forces all implementations to support all methods
- ❌ Still can't support future unknown techniques

### Alternative 2: Generic Interface with Type Parameter

```csharp
public interface IDocumentMemoryPipeline<TContext, TResult>
{
    Task<TResult> RetrieveAsync(TContext context, CancellationToken ct);
}
```

**Rejected because:**
- ❌ Complex generics make DI registration difficult
- ❌ Each RAG type needs separate interface registration
- ❌ Harder to use in filters (need to know TContext at compile time)
- ❌ Overkill for simple use cases

### Alternative 3: Strategy Pattern with Polymorphism

```csharp
public abstract class RetrievalStrategy
{
    public abstract Task<string> RetrieveAsync(string query, CancellationToken ct);
}

public class TextRagStrategy : RetrievalStrategy { }
public class GraphRagStrategy : RetrievalStrategy { }
// etc.
```

**Rejected because:**
- ❌ Requires class inheritance (heavier than callbacks)
- ❌ More boilerplate for users
- ❌ No real benefit over callbacks
- ✅ Callbacks achieve same goal with less code

---

## Recommendation

**Proceed with callback-based implementation** for the following reasons:

1. ✅ **Future-proof**: Works with any RAG technique (current or future)
2. ✅ **Infrastructure-first**: Aligns with HPD-Agent.Memory philosophy
3. ✅ **No breaking changes**: Adapter maintains backward compatibility
4. ✅ **Maximum flexibility**: Users control entire retrieval process
5. ✅ **Simple API**: `Func<string, CT, Task<string>>` is clean and understandable
6. ✅ **Consistent**: Same callback for auto-retrieval and agent-controlled search

---

## Next Steps

### Immediate Actions

1. **Review & Approve**: Get stakeholder sign-off on proposal
2. **Create Feature Branch**: `feature/callback-based-retrieval`
3. **Update Issues**: Create tracking issue with subtasks
4. **Start Phase 1**: Begin core implementation

### Questions for Review

1. Should we deprecate `IDocumentMemoryPipeline` immediately or wait for v2.0?
2. What convenience methods would be most valuable for common use cases?
3. Should we provide reference pipeline implementations or just examples?
4. Any other RAG strategies we should include in initial examples?

---

## Appendix: Code Changes Summary

### Files to Create
- `docs/examples/text-rag-retrieval.md`
- `docs/examples/graphrag-retrieval.md`
- `docs/examples/multimodal-retrieval.md`
- `docs/examples/hybrid-retrieval.md`
- `docs/examples/custom-api-retrieval.md`
- `docs/migration-guide-callback-retrieval.md`

### Files to Modify
- `HPD-Agent/Memory/Agent/StaticMemory/StaticMemoryOptions.cs`
- `HPD-Agent/Memory/Agent/StaticMemory/IndexedRetrieval/IndexedRetrievalFilter.cs`
- `HPD-Agent/Memory/Agent/StaticMemory/IndexedRetrieval/StaticMemoryRetrievalPlugin.cs`
- `HPD-Agent/Agent/AgentBuilder.cs` (extension methods)

### Files to Consider Deprecating
- `HPD-Agent.Memory/IDocumentMemoryPipeline.cs` (keep for backward compat, mark obsolete)

---

**End of Proposal**
