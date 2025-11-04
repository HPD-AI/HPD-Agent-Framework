# Parallel Safety Enforcement: Never Trust Users

**Philosophy**: Users will lie about thread safety (intentionally or accidentally). We must ENFORCE safety by default, not rely on opt-in.

---

## The Trust Problem

### What Users Will Do Wrong

```csharp
// User thinks this is safe (it's NOT):
public class EmbeddingHandler : IPipelineHandler<DocumentIngestionContext>, IParallelSafeHandler
{
    private List<float[]> _embeddings = new();  // ❌ Shared mutable state!

    public bool IsThreadSafe => true;  // ❌ User lies (or doesn't know better)

    public async Task<PipelineResult> HandleAsync(DocumentIngestionContext context, ...)
    {
        var embedding = await GenerateEmbeddingAsync(...);
        _embeddings.Add(embedding);  // ❌ RACE CONDITION!
        return PipelineResult.Success();
    }
}
```

**Problem**: User marked `IsThreadSafe = true` but has shared mutable state. **We can't trust the opt-in!**

---

## Enforced Safety: The Isolation Pattern

### Core Principle: **Handlers CANNOT share state during parallel execution**

Instead of trusting handlers, **we enforce isolation**:

1. **Clone context for each parallel handler** (copy-on-write)
2. **Detect shared state mutations** (throw exception)
3. **Merge results after all handlers complete** (orchestrator controls)
4. **Default to sequential** (parallel is OPT-IN at orchestrator level)

---

## Architecture: Isolated Parallel Execution

### Step 1: Context Cloning

```csharp
public interface IPipelineContext
{
    // Existing members...

    /// <summary>
    /// Create isolated copy for parallel execution.
    /// Changes to the copy don't affect original until merged.
    /// </summary>
    IPipelineContext CreateIsolatedCopy();

    /// <summary>
    /// Merge changes from isolated copy back to main context.
    /// Orchestrator calls this after handler completes.
    /// </summary>
    void MergeFrom(IPipelineContext isolatedContext);

    /// <summary>
    /// Check if context is isolated (read-only reference to main context).
    /// </summary>
    bool IsIsolated { get; }
}
```

### Step 2: Orchestrator Enforces Isolation

```csharp
public class InProcessOrchestrator<TContext> : IPipelineOrchestrator<TContext>
{
    private readonly ParallelExecutionMode _defaultMode;

    public InProcessOrchestrator(
        ILogger logger,
        ParallelExecutionMode defaultMode = ParallelExecutionMode.Sequential)
    {
        _defaultMode = defaultMode;
    }

    public async Task<TContext> ExecuteAsync(TContext context, CancellationToken ct)
    {
        while (!context.IsComplete)
        {
            var step = context.CurrentStep;

            if (step is ParallelStep parallel)
            {
                // Check if parallel execution is allowed
                var mode = parallel.ExecutionMode ?? _defaultMode;

                if (mode == ParallelExecutionMode.Concurrent)
                {
                    // ENFORCE ISOLATION - users can't opt out
                    await ExecuteParallelWithIsolationAsync(parallel, context, ct);
                }
                else
                {
                    // Safe default: sequential
                    await ExecuteSequentialAsync(parallel, context, ct);
                }
            }
            else if (step is SequentialStep seq)
            {
                await ExecuteSequentialStepAsync(seq, context, ct);
            }
        }

        return context;
    }

    private async Task ExecuteParallelWithIsolationAsync(
        ParallelStep parallel,
        TContext mainContext,
        CancellationToken ct)
    {
        // 1. Create isolated contexts (one per handler)
        var isolatedContexts = parallel.HandlerNames
            .Select(_ => mainContext.CreateIsolatedCopy())
            .ToList();

        // 2. Execute handlers with isolated contexts
        var tasks = parallel.HandlerNames
            .Select((name, index) =>
            {
                var handler = _handlers[name];
                var isolatedContext = (TContext)isolatedContexts[index];
                return ExecuteHandlerIsolatedAsync(handler, isolatedContext, ct);
            })
            .ToList();

        var results = await Task.WhenAll(tasks);

        // 3. Check for failures
        var failures = results
            .Select((r, i) => (Result: r, Handler: parallel.HandlerNames[i]))
            .Where(x => !x.Result.IsSuccess)
            .ToList();

        if (failures.Any())
        {
            throw new PipelineException(
                $"{failures.Count} handlers failed: {string.Join(", ", failures.Select(f => f.Handler))}");
        }

        // 4. Merge results back to main context (IN ORDER)
        foreach (var isolatedContext in isolatedContexts)
        {
            mainContext.MergeFrom(isolatedContext);
        }

        mainContext.MoveToNextStep();
    }

    private async Task<PipelineResult> ExecuteHandlerIsolatedAsync(
        IPipelineHandler<TContext> handler,
        TContext isolatedContext,
        CancellationToken ct)
    {
        try
        {
            // Handler gets isolated context - can't affect others
            return await handler.HandleAsync(isolatedContext, ct);
        }
        catch (Exception ex)
        {
            return PipelineResult.FatalFailure($"Handler threw exception: {ex.Message}", ex);
        }
    }
}
```

---

## Context Implementation: Copy-on-Write

### DocumentIngestionContext with Isolation

```csharp
public class DocumentIngestionContext : IIngestionContext
{
    private readonly DocumentIngestionContext? _mainContext;  // Reference to main if isolated
    private bool _isIsolated;

    // Existing properties...
    public List<DocumentFile> Files { get; init; } = new();
    public Dictionary<string, object> Data { get; } = new();

    public bool IsIsolated => _isIsolated;

    /// <summary>
    /// Create isolated copy for parallel execution.
    /// </summary>
    public IPipelineContext CreateIsolatedCopy()
    {
        if (_isIsolated)
            throw new InvalidOperationException("Cannot create isolated copy from already isolated context");

        return new DocumentIngestionContext
        {
            // Copy structure (immutable)
            PipelineId = this.PipelineId,
            ExecutionId = this.ExecutionId,
            DocumentId = this.DocumentId,
            Index = this.Index,
            Services = this.Services,  // Shared (services are thread-safe)

            // Copy steps (immutable once set)
            Steps = new List<PipelineStep>(this.Steps),
            CompletedSteps = new List<PipelineStep>(this.CompletedSteps),
            RemainingSteps = new List<PipelineStep>(this.RemainingSteps),

            // DEEP COPY mutable state
            Files = this.Files.Select(f => CloneFile(f)).ToList(),
            Data = new Dictionary<string, object>(this.Data),
            Tags = new Dictionary<string, List<string>>(
                this.Tags.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new List<string>(kvp.Value)
                )
            ),

            // Mark as isolated
            _isIsolated = true,
            _mainContext = this  // Keep reference to main context
        };
    }

    /// <summary>
    /// Merge changes from isolated context back to main.
    /// </summary>
    public void MergeFrom(IPipelineContext isolatedContext)
    {
        if (!isolatedContext.IsIsolated)
            throw new InvalidOperationException("Can only merge from isolated context");

        if (_isIsolated)
            throw new InvalidOperationException("Cannot merge into isolated context");

        var isolated = (DocumentIngestionContext)isolatedContext;

        // Merge file changes
        foreach (var file in isolated.Files)
        {
            var existingFile = Files.FirstOrDefault(f => f.Id == file.Id);
            if (existingFile != null)
            {
                // Update existing file
                MergeFileChanges(existingFile, file);
            }
            else
            {
                // Add new file
                Files.Add(CloneFile(file));
            }
        }

        // Merge data dictionary
        foreach (var kvp in isolated.Data)
        {
            Data[kvp.Key] = kvp.Value;
        }

        // Merge tags
        foreach (var kvp in isolated.Tags)
        {
            if (!Tags.ContainsKey(kvp.Key))
            {
                Tags[kvp.Key] = new List<string>();
            }
            // Union tags (avoid duplicates)
            Tags[kvp.Key] = Tags[kvp.Key].Union(kvp.Value).ToList();
        }

        // Merge idempotency tracking
        foreach (var handler in isolated.GetProcessedHandlers())
        {
            if (!AlreadyProcessedBy(handler))
            {
                MarkProcessedBy(handler);
            }
        }
    }

    private DocumentFile CloneFile(DocumentFile file)
    {
        if (file is GeneratedFile generated)
        {
            return new GeneratedFile
            {
                Id = generated.Id,
                Name = generated.Name,
                ParentId = generated.ParentId,
                Size = generated.Size,
                MimeType = generated.MimeType,
                ArtifactType = generated.ArtifactType,
                Tags = new Dictionary<string, List<string>>(
                    generated.Tags.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new List<string>(kvp.Value)
                    )
                ),
                ProcessedBy = new List<string>(generated.ProcessedBy),
                GeneratedFiles = new Dictionary<string, GeneratedFile>(generated.GeneratedFiles)
            };
        }

        return new DocumentFile
        {
            Id = file.Id,
            Name = file.Name,
            Size = file.Size,
            MimeType = file.MimeType,
            ArtifactType = file.ArtifactType,
            Tags = new Dictionary<string, List<string>>(
                file.Tags.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new List<string>(kvp.Value)
                )
            ),
            ProcessedBy = new List<string>(file.ProcessedBy),
            GeneratedFiles = new Dictionary<string, GeneratedFile>(file.GeneratedFiles)
        };
    }

    private void MergeFileChanges(DocumentFile target, DocumentFile source)
    {
        // Merge tags
        foreach (var kvp in source.Tags)
        {
            if (!target.Tags.ContainsKey(kvp.Key))
            {
                target.Tags[kvp.Key] = new List<string>();
            }
            target.Tags[kvp.Key] = target.Tags[kvp.Key].Union(kvp.Value).ToList();
        }

        // Merge ProcessedBy
        target.ProcessedBy = target.ProcessedBy.Union(source.ProcessedBy).ToList();

        // Merge GeneratedFiles
        foreach (var kvp in source.GeneratedFiles)
        {
            target.GeneratedFiles[kvp.Key] = kvp.Value;
        }

        // Update mutable properties
        target.Size = source.Size;
        target.ArtifactType = source.ArtifactType;
    }
}
```

---

## Developer Experience Analysis

### DX 1: User Creates Parallel Step (No Change)

```csharp
var steps = new PipelineStepBuilder()
    .AddSequential("extract_text")
    .AddSequential("partition")
    .AddParallel("generate_embeddings", "extract_entities", "generate_summary")
    .AddSequential("save_records")
    .Build();
```

**DX Impact**: ✅ **No change** - user declares intent as before

---

### DX 2: User Implements Handler (No Change)

```csharp
public class EmbeddingHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly IEmbeddingGenerator _embedder;

    public string StepName => "generate_embeddings";

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken ct)
    {
        // Handler writes to context (isolated copy)
        foreach (var file in context.GetFilesByType(FileArtifactType.TextPartition))
        {
            if (file.AlreadyProcessedBy(StepName))
                continue;

            var embedding = await _embedder.GenerateEmbeddingVectorAsync(...);

            // Store embedding in file
            file.GeneratedFiles["embedding"] = new GeneratedFile
            {
                Id = $"{file.Id}.embedding",
                ParentId = file.Id,
                Name = $"{file.Name}.embedding",
                // ... embedding data
            };

            file.MarkProcessedBy(StepName);
        }

        return PipelineResult.Success();
    }
}
```

**DX Impact**: ✅ **No change** - handler writes to context as normal. Isolation is transparent!

---

### DX 3: Orchestrator Configuration

```csharp
// Default: parallel disabled (safe)
services.AddHPDAgentMemoryCore();

// Opt-in: enable parallel execution
services.AddHPDAgentMemoryCore(options =>
{
    options.DefaultParallelExecution = ParallelExecutionMode.Concurrent;
    options.MaxParallelHandlers = 4;  // Optional throttling
});

// Per-step override
var steps = new PipelineStepBuilder()
    .AddParallel(
        new ParallelStep(
            HandlerNames: new[] { "embed", "entities" },
            ExecutionMode: ParallelExecutionMode.Concurrent  // Override default
        )
    )
    .Build();
```

**DX Impact**: 🟡 **Slightly more complex** - but explicit control over safety vs performance

---

## Safety Guarantees

### What We Guarantee

| Scenario | Guarantee |
|----------|-----------|
| **Parallel handlers modify same file** | ✅ Safe - each has isolated copy, merged after |
| **Parallel handlers add different files** | ✅ Safe - merged into main context |
| **Parallel handlers write to Data** | ✅ Safe - each has isolated dictionary |
| **Parallel handlers throw exceptions** | ✅ Safe - other handlers complete, then we report all failures |
| **User cancels during parallel** | ✅ Safe - cancellation propagates to all tasks |
| **Handler has shared mutable state** | ⚠️ Still unsafe, but **isolated contexts prevent race conditions on context** |

### What We DON'T Guarantee

| Scenario | Risk |
|----------|------|
| **Handler has static mutable state** | ❌ Not protected (can't control handler internals) |
| **Handler calls external service** | ⚠️ User must handle rate limiting |
| **Handler writes to shared DB** | ⚠️ User must handle concurrency |

**Key Point**: We guarantee **context isolation**. We can't control what handlers do internally, but we prevent them from corrupting the pipeline state.

---

## Performance Cost of Isolation

### Clone Cost Analysis

```csharp
// Typical ingestion context:
// - 1 document
// - 10 files
// - 50 partitions per file
// - Small Data dictionary

// Clone operation:
// - Copy 500 file references: ~1ms
// - Copy dictionaries: ~0.5ms
// - Total: ~1.5ms per handler

// For 3 parallel handlers: ~4.5ms overhead
// If handlers take 5 seconds each: 0.09% overhead

// Verdict: Negligible compared to LLM calls
```

**Conclusion**: Cloning is **cheap** compared to LLM API calls (which take seconds).

---

## Alternative: Immutable Context (More Extreme)

### Instead of Clone + Merge, Use Immutable Records

```csharp
// Context becomes immutable record
public record DocumentIngestionContext : IIngestionContext
{
    // All properties are init-only
    public required ImmutableList<DocumentFile> Files { get; init; }
    public required ImmutableDictionary<string, object> Data { get; init; }

    // Handler returns NEW context instead of mutating
    public DocumentIngestionContext WithFile(DocumentFile file)
    {
        return this with { Files = Files.Add(file) };
    }
}

// Handler interface changes
public interface IPipelineHandler<TContext>
{
    // Returns NEW context, not mutates existing
    Task<(PipelineResult Result, TContext NewContext)> HandleAsync(
        TContext context,
        CancellationToken ct);
}
```

**Pros**:
- ✅ Thread safety by design
- ✅ No cloning needed
- ✅ Functional purity

**Cons**:
- ❌ **MASSIVE breaking change** to existing API
- ❌ Unfamiliar pattern in C#/.NET
- ❌ Performance cost of immutable collections
- ❌ Handler DX becomes awkward

**Verdict**: Too extreme. Cloning is better trade-off.

---

## Recommendation: Enforced Isolation Pattern

### Ship This:

**1. Context Isolation (Required)**
```csharp
public interface IPipelineContext
{
    IPipelineContext CreateIsolatedCopy();
    void MergeFrom(IPipelineContext isolated);
    bool IsIsolated { get; }
}
```

**2. Default Sequential, Opt-In Parallel**
```csharp
services.AddHPDAgentMemoryCore(options =>
{
    options.DefaultParallelExecution = ParallelExecutionMode.Sequential;  // Safe default
});
```

**3. Per-Step Override**
```csharp
var steps = builder
    .AddParallel(
        handlerNames: new[] { "embed", "entities" },
        mode: ParallelExecutionMode.Concurrent  // Explicit opt-in
    )
    .Build();
```

**4. Documentation Warnings**
```
⚠️ WARNING: Parallel execution requires handlers to be stateless or use
thread-safe state management. Context isolation protects pipeline state
but cannot protect handler internal state or external service calls.

✅ Safe: Handlers that only read/write context
❌ Unsafe: Handlers with static mutable state
⚠️ User Responsibility: Rate limiting external APIs
```

---

## Final DX Comparison

### Before (Trust Model)
```csharp
// User declares handler is safe (we trust them)
public class MyHandler : IPipelineHandler<T>, IParallelSafeHandler
{
    public bool IsThreadSafe => true;  // ❌ User lies
}
```

### After (Isolation Model)
```csharp
// User doesn't need to declare anything
public class MyHandler : IPipelineHandler<T>
{
    // Just write normal code
    // We enforce safety via isolation
}

// Parallel execution is orchestrator-level decision
services.AddHPDAgentMemoryCore(options =>
{
    options.DefaultParallelExecution = ParallelExecutionMode.Concurrent;
});
```

**DX Winner**: Isolation model
- ✅ Simpler for users (no interface to implement)
- ✅ Safer by default
- ✅ Explicit opt-in at configuration level
- ✅ Transparent to handlers

---

## Summary

**Trust is not a strategy. Isolation is.**

| Approach | Safety | DX | Performance |
|----------|--------|-----|-------------|
| **Opt-in (IParallelSafeHandler)** | ❌ Relies on user honesty | 🟡 Extra interface | ✅ No overhead |
| **Enforced Isolation** | ✅ Guaranteed for context | ✅ Transparent | 🟡 ~1-2ms per handler |
| **Immutable Context** | ✅ Guaranteed everywhere | ❌ Awkward DX | 🟡 Higher cost |

**Recommendation**: **Enforced Isolation** - best balance of safety, DX, and performance.
