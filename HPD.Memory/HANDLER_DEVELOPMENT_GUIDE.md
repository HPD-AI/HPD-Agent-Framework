# Handler Development Guide

**How to build custom pipeline handlers for HPD-Agent.Memory**

---

## Philosophy: You Own Your Handlers

HPD-Agent.Memory is **infrastructure-only** by design. We provide:

✅ Generic pipeline system
✅ Idempotency tracking
✅ File lineage
✅ Tag-based filtering
✅ Storage abstractions
✅ Error handling

We **deliberately do NOT** provide handler implementations because:

1. **RAG techniques evolve too fast** - New approaches every month
2. **Every domain is different** - Legal ≠ Medical ≠ Code ≠ Research
3. **You know your needs best** - We can't predict your use case
4. **Avoid vendor lock-in** - Switch techniques without fighting the framework

**Think of us as React, not WordPress.**

---

## Handler Anatomy

### The Interface

Every handler implements `IPipelineHandler<TContext>`:

```csharp
namespace HPDAgent.Memory.Abstractions.Pipeline;

public interface IPipelineHandler<in TContext> where TContext : IPipelineContext
{
    /// <summary>
    /// Unique name for this step in the pipeline.
    /// Used for idempotency tracking and logging.
    /// </summary>
    string StepName { get; }

    /// <summary>
    /// Process the context.
    /// </summary>
    Task<PipelineResult> HandleAsync(
        TContext context,
        CancellationToken cancellationToken = default);
}
```

### The PipelineResult

Return one of three result types:

```csharp
// ✅ Success - Continue to next step
return PipelineResult.Success();

// ⚠️ Transient failure - Can retry
return PipelineResult.TransientFailure(
    "API rate limit exceeded",
    exception,
    metadata: new Dictionary<string, object>
    {
        ["retry_after_seconds"] = 60
    });

// ❌ Fatal failure - Stop pipeline
return PipelineResult.FatalFailure(
    "Invalid document format",
    exception);
```

---

## Handler Development Pattern

### 1. Basic Handler Template

```csharp
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Core.Contexts;
using Microsoft.Extensions.Logging;

public class MyCustomHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly ILogger<MyCustomHandler> _logger;

    public string StepName => "my_custom_step";

    public MyCustomHandler(ILogger<MyCustomHandler> logger)
    {
        _logger = logger;
    }

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "Processing document {DocumentId} in index {Index}",
                context.DocumentId,
                context.Index);

            // YOUR LOGIC HERE

            return PipelineResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handler failed");
            return PipelineResult.FatalFailure("Processing failed", ex);
        }
    }
}
```

### 2. Add Idempotency

**Always check if work is already done:**

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    // Check context-level idempotency
    if (context.AlreadyProcessedBy(StepName))
    {
        _logger.LogDebug("Step already completed, skipping");
        return PipelineResult.Success();
    }

    foreach (var file in context.Files)
    {
        // Check file-level idempotency
        if (file.AlreadyProcessedBy(StepName))
        {
            _logger.LogDebug("File {FileId} already processed, skipping", file.Id);
            continue;
        }

        // Process file
        await ProcessFileAsync(file, context, cancellationToken);

        // Mark as processed
        file.MarkProcessedBy(StepName);
    }

    // Mark context as processed
    context.MarkProcessedBy(StepName);

    return PipelineResult.Success();
}
```

### 3. Use Sub-Steps for Fine-Grained Tracking

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    foreach (var file in context.Files)
    {
        // Process in batches
        var chunks = ChunkFile(file, batchSize: 100);

        for (int i = 0; i < chunks.Count; i++)
        {
            var subStep = $"batch_{i}";

            if (file.AlreadyProcessedBy(StepName, subStep))
            {
                continue; // Already processed this batch
            }

            await ProcessBatchAsync(chunks[i], cancellationToken);

            file.MarkProcessedBy(StepName, subStep);
        }
    }

    return PipelineResult.Success();
}
```

### 4. Access Services via DI

```csharp
public class EmbeddingHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IVectorStore _vectorStore;
    private readonly IDocumentStore _documentStore;
    private readonly ILogger<EmbeddingHandler> _logger;

    public string StepName => "generate_embeddings";

    // Inject services via constructor
    public EmbeddingHandler(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IVectorStore vectorStore,
        IDocumentStore documentStore,
        ILogger<EmbeddingHandler> logger)
    {
        _embedder = embedder;
        _vectorStore = vectorStore;
        _documentStore = documentStore;
        _logger = logger;
    }

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        // Use injected services
        var text = await _documentStore.ReadTextFileAsync(...);
        var embedding = await _embedder.GenerateEmbeddingVectorAsync(text);
        await _vectorStore.UpsertAsync(embedding);

        return PipelineResult.Success();
    }
}
```

### 5. Use Context for Runtime Services

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    // Get services from context.Services (IServiceProvider)
    var httpClient = context.Services.GetRequiredService<IHttpClient>();
    var cache = context.Services.GetService<ICache>(); // Optional service

    // Your logic here

    return PipelineResult.Success();
}
```

### 6. Work with Tags

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    // Add tags to context (inherited by all files)
    context.Tags.AddTag(TagConstants.DepartmentTag, "engineering");
    context.Tags.AddTag("processed_by", StepName);

    foreach (var file in context.Files)
    {
        // Add file-specific tags
        file.Tags.AddTag(TagConstants.FileType, file.MimeType);
        file.Tags.AddTag(TagConstants.ArtifactType, file.ArtifactType.ToString());

        // Copy context tags to file
        context.Tags.CopyTagsTo(file.Tags);
    }

    return PipelineResult.Success();
}
```

### 7. Track File Lineage

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    foreach (var sourceFile in context.GetSourceDocuments())
    {
        // Generate partitions from source
        var partitions = await PartitionFileAsync(sourceFile);

        foreach (var (partitionText, partitionNumber) in partitions)
        {
            // Create generated file with lineage
            var partition = new GeneratedFile
            {
                Id = $"{sourceFile.Id}_partition_{partitionNumber}",
                Name = sourceFile.GetPartitionFileName(partitionNumber),
                ParentId = sourceFile.Id, // ← Track lineage
                Size = partitionText.Length,
                ArtifactType = FileArtifactType.TextPartition,
                PartitionNumber = partitionNumber
            };

            // Add to source file's generated files
            sourceFile.GeneratedFiles[partition.Id] = partition;

            // Add to context
            context.AddFile(partition);
        }
    }

    return PipelineResult.Success();
}
```

### 8. Add Logging

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    // Structured logging
    _logger.LogInformation(
        "Starting {StepName} for document {DocumentId}",
        StepName,
        context.DocumentId);

    // Add to pipeline log (user-visible)
    context.Log(
        StepName,
        $"Processing {context.Files.Count} files",
        LogLevel.Information);

    // File-specific logging
    foreach (var file in context.Files)
    {
        file.Log(
            StepName,
            $"Processed {file.Name}",
            LogLevel.Information);

        _logger.LogDebug(
            "Processed file {FileId}: {FileName}",
            file.Id,
            file.Name);
    }

    return PipelineResult.Success();
}
```

### 9. Handle Errors Gracefully

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    try
    {
        await ProcessAsync(context, cancellationToken);
        return PipelineResult.Success();
    }
    catch (RateLimitException ex)
    {
        // Transient - can retry
        _logger.LogWarning(ex, "Rate limit hit, can retry");

        return PipelineResult.TransientFailure(
            "Rate limit exceeded",
            ex,
            metadata: new Dictionary<string, object>
            {
                ["retry_after"] = ex.RetryAfter,
                ["attempts"] = context.GetData<int>("retry_count") ?? 0
            });
    }
    catch (InvalidFormatException ex)
    {
        // Fatal - cannot retry
        _logger.LogError(ex, "Invalid document format");

        context.Log(
            StepName,
            $"Document format invalid: {ex.Message}",
            LogLevel.Error);

        return PipelineResult.FatalFailure(
            "Invalid document format",
            ex);
    }
    catch (Exception ex)
    {
        // Unknown error - treat as fatal
        _logger.LogError(ex, "Unexpected error");

        return PipelineResult.FatalFailure(
            "Unexpected error occurred",
            ex);
    }
}
```

---

## Common Handler Patterns

### Pattern 1: File Processor

Process each file independently:

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    foreach (var file in context.Files.Where(ShouldProcess))
    {
        if (file.AlreadyProcessedBy(StepName))
            continue;

        try
        {
            await ProcessFileAsync(file, context, cancellationToken);
            file.MarkProcessedBy(StepName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process file {FileId}", file.Id);
            // Continue with other files or fail?
        }
    }

    return PipelineResult.Success();
}

private bool ShouldProcess(DocumentFile file)
{
    // Only process source documents
    return file.ArtifactType == FileArtifactType.SourceDocument;
}
```

### Pattern 2: Batch Processor

Process files in batches:

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    var files = context.Files
        .Where(f => !f.AlreadyProcessedBy(StepName))
        .ToList();

    // Get batch size from configuration
    var batchSize = context.GetBatchSizeOrDefault(defaultValue: 10);

    // Process in batches
    for (int i = 0; i < files.Count; i += batchSize)
    {
        var batch = files.Skip(i).Take(batchSize).ToList();

        await ProcessBatchAsync(batch, context, cancellationToken);

        // Mark batch as processed
        foreach (var file in batch)
        {
            file.MarkProcessedBy(StepName);
        }
    }

    return PipelineResult.Success();
}
```

### Pattern 3: Generator

Create new files from existing ones:

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    foreach (var sourceFile in context.GetSourceDocuments())
    {
        if (sourceFile.AlreadyProcessedBy(StepName))
            continue;

        // Generate outputs
        var outputs = await GenerateOutputsAsync(sourceFile, cancellationToken);

        foreach (var (outputData, index) in outputs.WithIndex())
        {
            var generatedFile = new GeneratedFile
            {
                Id = $"{sourceFile.Id}_output_{index}",
                Name = sourceFile.GetHandlerOutputFileName(StepName, index),
                ParentId = sourceFile.Id,
                ArtifactType = FileArtifactType.SyntheticData,
                // ... other properties
            };

            // Store output data
            await StoreAsync(generatedFile, outputData, context);

            // Track lineage
            sourceFile.GeneratedFiles[generatedFile.Id] = generatedFile;
            context.AddFile(generatedFile);
        }

        sourceFile.MarkProcessedBy(StepName);
    }

    return PipelineResult.Success();
}
```

### Pattern 4: Aggregator

Combine data from multiple files:

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    if (context.AlreadyProcessedBy(StepName))
        return PipelineResult.Success();

    // Collect data from all files
    var allData = new List<DataItem>();

    foreach (var file in context.Files)
    {
        var data = await LoadDataAsync(file, context, cancellationToken);
        allData.AddRange(data);
    }

    // Aggregate
    var aggregated = AggregateData(allData);

    // Store aggregated result
    await StoreAggregatedAsync(aggregated, context, cancellationToken);

    context.MarkProcessedBy(StepName);

    return PipelineResult.Success();
}
```

### Pattern 5: Filter/Validator

Validate and filter files:

```csharp
public async Task<PipelineResult> HandleAsync(
    DocumentIngestionContext context,
    CancellationToken cancellationToken = default)
{
    var filesToRemove = new List<DocumentFile>();

    foreach (var file in context.Files)
    {
        if (file.AlreadyProcessedBy(StepName))
            continue;

        // Validate
        var validation = await ValidateAsync(file, cancellationToken);

        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "File {FileId} failed validation: {Reason}",
                file.Id,
                validation.Reason);

            file.Log(StepName, $"Validation failed: {validation.Reason}", LogLevel.Warning);

            filesToRemove.Add(file);
        }
        else
        {
            file.MarkProcessedBy(StepName);
        }
    }

    // Remove invalid files
    foreach (var file in filesToRemove)
    {
        context.Files.Remove(file);
    }

    if (context.Files.Count == 0)
    {
        return PipelineResult.FatalFailure("All files failed validation");
    }

    return PipelineResult.Success();
}
```

---

## Testing Handlers

### Unit Test Template

```csharp
using Xunit;
using Moq;
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Core.Contexts;

public class MyHandlerTests
{
    [Fact]
    public async Task Should_Process_File_Successfully()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<MyHandler>>();
        var handler = new MyHandler(mockLogger.Object);

        var context = new DocumentIngestionContext
        {
            Index = "test-index",
            DocumentId = "doc-123",
            Services = CreateMockServiceProvider(),
            Steps = new[] { handler.StepName }.ToList()
        };

        context.Files.Add(new DocumentFile
        {
            Id = "file-1",
            Name = "test.pdf",
            ArtifactType = FileArtifactType.SourceDocument
        });

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(context.Files[0].AlreadyProcessedBy(handler.StepName));
    }

    [Fact]
    public async Task Should_Skip_Already_Processed_Files()
    {
        // Arrange
        var handler = new MyHandler(Mock.Of<ILogger<MyHandler>>());
        var context = CreateContext();

        var file = context.Files[0];
        file.MarkProcessedBy(handler.StepName); // Already processed

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.True(result.IsSuccess);
        // Verify processing was skipped
    }

    [Fact]
    public async Task Should_Return_Transient_Failure_On_Rate_Limit()
    {
        // Arrange
        var mockService = new Mock<IExternalService>();
        mockService
            .Setup(s => s.ProcessAsync(It.IsAny<string>()))
            .ThrowsAsync(new RateLimitException());

        var handler = new MyHandler(mockService.Object, Mock.Of<ILogger<MyHandler>>());
        var context = CreateContext();

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.True(result.IsTransient);
    }

    private IServiceProvider CreateMockServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Add other required services
        return services.BuildServiceProvider();
    }
}
```

---

## Best Practices

### ✅ DO

1. **Always check idempotency** - Use `AlreadyProcessedBy()` / `MarkProcessedBy()`
2. **Use structured logging** - Include context (document ID, file ID, etc.)
3. **Handle cancellation** - Respect `CancellationToken`
4. **Track file lineage** - Use `GeneratedFile.ParentId`
5. **Add tags** - Help with filtering and organization
6. **Return appropriate results** - Success vs Transient vs Fatal
7. **Log user-visible events** - Use `context.Log()` for important events
8. **Test thoroughly** - Unit tests for all code paths

### ❌ DON'T

1. **Don't process without checking idempotency** - Wastes resources
2. **Don't swallow exceptions** - Return proper PipelineResult
3. **Don't hardcode configuration** - Use context extensions
4. **Don't assume file types** - Check `ArtifactType`
5. **Don't modify context structure** - Only add files, don't remove steps
6. **Don't ignore cancellation tokens** - Can cause issues in distributed scenarios
7. **Don't log sensitive data** - PII, secrets, etc.
8. **Don't mutate shared state** - Handlers should be stateless

---

## Next Steps

1. Read [REFERENCE_HANDLER_EXAMPLES.md](REFERENCE_HANDLER_EXAMPLES.md) for complete handler implementations
2. Read [RAG_TECHNIQUES_COOKBOOK.md](RAG_TECHNIQUES_COOKBOOK.md) for specific RAG patterns
3. Check [AI_PROVIDER_SETUP_GUIDE.md](AI_PROVIDER_SETUP_GUIDE.md) for integrating AI services
4. See [USAGE_EXAMPLES.md](USAGE_EXAMPLES.md) for end-to-end pipeline examples

---

## Philosophy Reminder

> "We give you the plumbing, you build the house"

HPD-Agent.Memory handles:
- ✅ Pipeline orchestration
- ✅ State management
- ✅ Idempotency
- ✅ Error handling
- ✅ Storage

You handle:
- 🎯 Your domain logic
- 🎯 Your RAG technique
- 🎯 Your data format
- 🎯 Your business rules
- 🎯 Your optimization strategy

**This separation ensures you're never locked into our choices as RAG techniques evolve.**
