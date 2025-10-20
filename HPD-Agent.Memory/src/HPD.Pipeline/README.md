# HPD.Pipeline

> Generic workflow orchestration infrastructure for .NET

[![NuGet](https://img.shields.io/nuget/v/HPD.Pipeline.svg)](https://www.nuget.org/packages/HPD.Pipeline/)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)]()
[![License](https://img.shields.io/badge/license-MIT-blue)]()

## What is this?

**HPD.Pipeline** is a domain-agnostic workflow orchestration framework for .NET. It provides the building blocks for creating multi-step pipelines for **any** domain - RAG systems, video processing, trading platforms, ETL, data pipelines, or custom workflows.

Think of it as:
- **ASP.NET Core middleware** - but for any workflow, not just HTTP
- **MediatR** - but with ordered multi-step pipelines instead of single handlers
- **Airflow/Temporal** - but embedded in-process and type-safe

## Core Concepts

### 1. **IPipelineContext** - The State Container

Every pipeline execution has a context that flows through all steps:

```csharp
public interface IPipelineContext
{
    string PipelineId { get; }        // Unique ID for this execution
    string Index { get; }              // Namespace/collection
    IReadOnlyList<PipelineStep> Steps { get; }
    IDictionary<string, object> Data { get; }  // Shared data between handlers
    IServiceProvider Services { get; }  // DI container

    void Log(string source, string message, LogLevel level);
    bool AlreadyProcessedBy(string handlerName);
    void MarkProcessedBy(string handlerName);
}
```

### 2. **IPipelineHandler<TContext>** - The Worker

Handlers perform one specific task in the pipeline:

```csharp
public interface IPipelineHandler<in TContext> where TContext : IPipelineContext
{
    string StepName { get; }
    Task<PipelineResult> HandleAsync(TContext context, CancellationToken ct);
}
```

### 3. **IPipelineOrchestrator<TContext>** - The Coordinator

The orchestrator executes handlers in sequence:

```csharp
public interface IPipelineOrchestrator<TContext> where TContext : IPipelineContext
{
    Task AddHandlerAsync(IPipelineHandler<TContext> handler);
    Task<TContext> ExecuteAsync(TContext context, CancellationToken ct);
}
```

### 4. **PipelineStep** - Sequential or Parallel

Steps can run one handler or multiple handlers in parallel:

```csharp
// Sequential: one handler at a time
new SequentialStep { HandlerName = "extract_text" }

// Parallel: multiple handlers concurrently
new ParallelStep("generate_embeddings", "extract_entities", "detect_language")
```

## Example: RAG Document Ingestion

```csharp
// 1. Define your context (domain-specific)
public class DocumentIngestionContext : IPipelineContext
{
    // ... IPipelineContext implementation

    // Domain-specific properties
    public List<DocumentFile> Files { get; set; } = new();
    public string DocumentId { get; set; } = "";
}

// 2. Create handlers (domain-specific)
public class ExtractTextHandler : IPipelineHandler<DocumentIngestionContext>
{
    public string StepName => "extract_text";

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken ct)
    {
        foreach (var file in context.Files)
        {
            file.ExtractedText = await ExtractTextAsync(file.Content);
        }
        return PipelineResult.Success();
    }
}

public class GenerateEmbeddingsHandler : IPipelineHandler<DocumentIngestionContext>
{
    public string StepName => "generate_embeddings";

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken ct)
    {
        var embeddingGenerator = context.Services.GetRequiredService<IEmbeddingGenerator>();

        foreach (var file in context.Files)
        {
            file.Embeddings = await embeddingGenerator.GenerateAsync(file.ExtractedText);
        }
        return PipelineResult.Success();
    }
}

// 3. Build and execute the pipeline
var orchestrator = new InProcessOrchestrator<DocumentIngestionContext>();
await orchestrator.AddHandlerAsync(new ExtractTextHandler());
await orchestrator.AddHandlerAsync(new GenerateEmbeddingsHandler());

var context = new DocumentIngestionContext
{
    PipelineId = Guid.NewGuid().ToString(),
    Index = "documents",
    Steps = new List<PipelineStep>
    {
        new SequentialStep { HandlerName = "extract_text" },
        new SequentialStep { HandlerName = "generate_embeddings" }
    },
    Files = new List<DocumentFile> { myDocument }
};

var result = await orchestrator.ExecuteAsync(context);
```

## Example: Video Processing

```csharp
// 1. Define your context
public class VideoProcessingContext : IPipelineContext
{
    // ... IPipelineContext implementation

    public string VideoPath { get; set; } = "";
    public List<string> OutputFormats { get; set; } = new();
}

// 2. Create handlers
public class TranscodeHandler : IPipelineHandler<VideoProcessingContext>
{
    public string StepName => "transcode";

    public async Task<PipelineResult> HandleAsync(
        VideoProcessingContext context,
        CancellationToken ct)
    {
        var transcoder = context.Services.GetRequiredService<IVideoTranscoder>();
        await transcoder.TranscodeAsync(context.VideoPath, context.OutputFormats);
        return PipelineResult.Success();
    }
}

// 3. Execute
var context = new VideoProcessingContext
{
    Steps = new List<PipelineStep>
    {
        new SequentialStep { HandlerName = "extract_audio" },
        new ParallelStep("transcode_1080p", "transcode_720p", "transcode_480p"),
        new SequentialStep { HandlerName = "generate_thumbnails" }
    }
};
```

## Key Features

### ✅ **Domain-Agnostic**
Works for RAG, video, trading, ETL, or any multi-step workflow. Zero dependencies on any specific domain.

### ✅ **Type-Safe**
Fully generic with compile-time type checking. Your context is strongly typed.

### ✅ **Parallel Execution**
Run multiple handlers concurrently with context isolation and automatic merging.

### ✅ **Idempotency Built-In**
`AlreadyProcessedBy()` / `MarkProcessedBy()` patterns prevent duplicate work on retries.

### ✅ **Service Integration**
Full DI support - handlers can resolve dependencies from `context.Services`.

### ✅ **Flexible Error Handling**
Distinguish between transient (retryable) and fatal (non-retryable) failures.

### ✅ **Progress Tracking**
Built-in logging, step tracking, and progress reporting.

## Installation

```bash
dotnet add package HPD.Pipeline
```

## When to Use This

### ✅ Use HPD.Pipeline if you need:
- Multi-step workflows with ordered execution
- Parallel processing with context isolation
- Idempotent retry-safe operations
- Type-safe pipeline definitions
- DI integration in pipeline steps

### ❌ Don't use HPD.Pipeline if you need:
- Single-step request/response (use MediatR)
- Simple sequential functions (use plain async methods)
- Complex workflow orchestration with branching/conditionals (use Temporal/Elsa)

## Philosophy

**Infrastructure-First, Not Domain-First**

This package provides the **infrastructure** for building pipelines, not the domain logic. You bring your own:
- Context type (extends `IPipelineContext`)
- Handlers (implement `IPipelineHandler<YourContext>`)
- Domain models and business logic

## Ecosystem

HPD.Pipeline is the foundation for domain-specific packages:

```
HPD.Pipeline                          ← You are here (generic infrastructure)
├── HPD.Memory.Abstractions          ← RAG/Memory domain
├── YourCompany.Trading.Pipeline     ← Trading domain
├── YourCompany.Media.Pipeline       ← Video/audio domain
└── YourCompany.ETL.Pipeline         ← Data pipelines
```

## Documentation

- **Getting Started**: [examples/](examples/)
- **Advanced Features**: [docs/](docs/)
- **API Reference**: [API Docs](https://your-org.github.io/hpd-pipeline)

## Contributing

We welcome contributions! If you're using HPD.Pipeline for a specific domain, consider publishing your handlers and contexts as a separate package.

## License

MIT License - see [LICENSE](../../LICENSE) for details

## Links

- **Documentation**: https://github.com/your-org/hpd-agent/tree/main/HPD-Agent.Memory
- **Source Code**: https://github.com/your-org/hpd-agent
- **NuGet**: https://www.nuget.org/packages/HPD.Pipeline/
- **Issues**: https://github.com/your-org/hpd-agent/issues

---

**Built with "infrastructure-first" philosophy** - pure abstractions that enable ecosystem growth.
