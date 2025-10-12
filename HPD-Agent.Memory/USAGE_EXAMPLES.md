# HPD-Agent.Memory Usage Examples

This document demonstrates how to use HPD-Agent.Memory - a next-generation memory system that improves upon Microsoft Kernel Memory with modern patterns.

## 🎯 What Makes HPD-Agent.Memory Better

| Feature | Kernel Memory | HPD-Agent.Memory |
|---------|---------------|------------------|
| **Pipelines** | Ingestion only | ✅ **Ingestion + Retrieval** |
| **AI Interfaces** | Custom interfaces | ✅ **Microsoft.Extensions.AI** |
| **Context Type** | Hardcoded DataPipeline | ✅ **Generic IPipelineContext** |
| **Service Access** | Via orchestrator | ✅ **Standard DI** |
| **File Storage** | In orchestrator | ✅ **Separate IDocumentStore** |
| **Graph DBs** | Not supported | ✅ **IGraphStore** |
| **Return Types** | Tuple + enum | ✅ **Rich PipelineResult** |

---

## 📦 Basic Setup

```csharp
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Core.Contexts;
using HPDAgent.Memory.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Setup DI container
var services = new ServiceCollection();
 
// Add logging
services.AddLogging(builder => builder.AddConsole());

// Add orchestrator
services.AddSingleton<IPipelineOrchestrator<DocumentIngestionContext>,
    InProcessOrchestrator<DocumentIngestionContext>>();

// Add your handlers (we'll show these later)
// services.AddSingleton<IPipelineHandler<DocumentIngestionContext>, ExtractTextHandler>();
// services.AddSingleton<IPipelineHandler<DocumentIngestionContext>, PartitionTextHandler>();

var serviceProvider = services.BuildServiceProvider();
```

---

## 📝 Example 1: Document Ingestion Pipeline

```csharp
using HPDAgent.Memory.Core.Contexts;
using HPDAgent.Memory.Core.Orchestration;

// Create ingestion context using builder with template
var context = PipelineTemplates
    .DocumentIngestion<DocumentIngestionContext>(serviceProvider)
    .WithIndex("my-documents")
    .WithMaxTokensPerChunk(500)
    .WithOverlapTokens(50)
    .WithBatchSize(10)
    .WithTag("source", "user-upload")
    .WithTag("category", "technical-docs")
    .BuildContext();

// Set document ID
context.DocumentId = "doc_12345";

// Add files to process
context.Files.Add(new DocumentFile
{
    Id = Guid.NewGuid().ToString("N"),
    Name = "technical-manual.pdf",
    Size = 1024000,
    MimeType = "application/pdf",
    ArtifactType = FileArtifactType.SourceDocument
});

// Get orchestrator and execute
var orchestrator = serviceProvider
    .GetRequiredService<IPipelineOrchestrator<DocumentIngestionContext>>();

var result = await orchestrator.ExecuteAsync(context);

Console.WriteLine($"Pipeline completed! Executed {result.CompletedSteps.Count} steps.");
```

---

## 🔍 Example 2: Semantic Search Pipeline

**This is what Kernel Memory CANNOT do!**

```csharp
using HPDAgent.Memory.Core.Contexts;

// Create retrieval context using template
var searchContext = PipelineTemplates
    .SemanticSearch<SemanticSearchContext>(serviceProvider)
    .WithIndex("my-documents")
    .WithMaxResults(10)
    .WithMinScore(0.7f)
    .WithTag("filter", "technical-docs")
    .BuildContext();

// Set search query
searchContext.Query = "How do I configure the database connection?";
searchContext.UserId = "user_12345";

// Execute retrieval pipeline
var orchestrator = serviceProvider
    .GetRequiredService<IPipelineOrchestrator<SemanticSearchContext>>();

var result = await orchestrator.ExecuteAsync(searchContext);

// Get results
var topResults = result.GetTopResults(5);

foreach (var item in topResults)
{
    Console.WriteLine($"Score: {item.Score:F3}");
    Console.WriteLine($"Content: {item.Content}");
    Console.WriteLine($"Source: {item.Source}");
    Console.WriteLine();
}
```

---

## 🔗 Example 3: Hybrid Search with Graph (GraphRAG)

**Advanced retrieval combining vector search + knowledge graphs**

```csharp
// Create hybrid search pipeline
var hybridContext = PipelineTemplates
    .HybridSearch<SemanticSearchContext>(serviceProvider)
    .WithIndex("knowledge-base")
    .WithMaxResults(20)
    .WithMinScore(0.6f)
    .WithConfiguration("graph_max_hops", 2)
    .WithConfiguration("graph_relationship_types", new[] { "cites", "relates_to" })
    .BuildContext();

hybridContext.Query = "What are the latest developments in RAG?";

// Pipeline will:
// 1. Rewrite query (expand with synonyms)
// 2. Generate query embedding
// 3. Vector search in documents
// 4. Graph search through relationships
// 5. Merge results from both sources
// 6. Rerank combined results
// 7. Apply access control filters

var result = await orchestrator.ExecuteAsync(hybridContext);

// Results contain both vector similarity matches AND graph-connected documents
Console.WriteLine($"Found {result.Results.Count} results from hybrid search");
```

---

## 🛠️ Example 4: Creating a Custom Handler

```csharp
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Core.Contexts;

/// <summary>
/// Example handler that extracts text from documents.
/// Shows idempotency tracking pattern from Kernel Memory.
/// </summary>
public class ExtractTextHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly IDocumentStore _documentStore;
    private readonly ILogger<ExtractTextHandler> _logger;

    public string StepName => "extract_text";

    public ExtractTextHandler(
        IDocumentStore documentStore,
        ILogger<ExtractTextHandler> logger)
    {
        _documentStore = documentStore;
        _logger = logger;
    }

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var file in context.Files)
            {
                // ✅ Idempotency check (Kernel Memory pattern)
                if (file.AlreadyProcessedBy(StepName))
                {
                    _logger.LogDebug("File {FileName} already processed, skipping", file.Name);
                    continue;
                }

                _logger.LogInformation("Extracting text from {FileName}", file.Name);

                // Read file from storage
                var fileContent = await _documentStore.ReadFileAsync(
                    context.Index,
                    context.PipelineId,
                    file.Name,
                    cancellationToken);

                // Extract text (simplified - you'd use a real PDF library)
                var extractedText = ExtractTextFromPdf(fileContent);

                // Write extracted text back to storage
                var outputFileName = $"{file.Name}.extract.txt";
                await _documentStore.WriteTextFileAsync(
                    context.Index,
                    context.PipelineId,
                    outputFileName,
                    extractedText,
                    cancellationToken);

                // Track generated file (Kernel Memory pattern)
                var generatedFile = new GeneratedFile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ParentId = file.Id,
                    Name = outputFileName,
                    Size = extractedText.Length,
                    MimeType = "text/plain",
                    ArtifactType = FileArtifactType.ExtractedText
                };
                generatedFile.MarkProcessedBy(StepName);
                file.GeneratedFiles.Add(outputFileName, generatedFile);

                // ✅ Mark file as processed (Kernel Memory pattern)
                file.MarkProcessedBy(StepName);

                // Log to context
                context.Log(StepName, $"Extracted {extractedText.Length} characters from {file.Name}");
            }

            return PipelineResult.Success(new Dictionary<string, object>
            {
                ["files_processed"] = context.Files.Count,
                ["total_characters"] = context.Files.Sum(f => f.Size)
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            // Transient error - can retry
            _logger.LogWarning(ex, "Transient error during text extraction");
            return PipelineResult.TransientFailure(
                "Text extraction failed due to network issue",
                exception: ex);
        }
        catch (Exception ex)
        {
            // Fatal error - cannot retry
            _logger.LogError(ex, "Fatal error during text extraction");
            return PipelineResult.FatalFailure(
                "Text extraction failed permanently",
                exception: ex);
        }
    }

    private string ExtractTextFromPdf(BinaryData content)
    {
        // Simplified - use a real PDF library like PdfPig, iText, etc.
        return "Extracted text content...";
    }
}
```

---

## 📊 Example 5: Using Context Extensions (Type-Safe Configuration)

**Inspired by Kernel Memory's context argument pattern but better**

```csharp
// Setting configuration
var context = new DocumentIngestionContext
{
    Index = "documents",
    DocumentId = "doc_123",
    Services = serviceProvider
};

// ✅ Type-safe extension methods
context.SetMaxTokensPerChunk(1000);
context.SetOverlapTokens(100);
context.SetBatchSize(20);
context.SetEmbeddingModel("text-embedding-3-small");

// In handler, retrieve with defaults
public async Task<PipelineResult> HandleAsync(DocumentIngestionContext context, ...)
{
    // ✅ Get configuration with fallback
    var maxTokens = context.GetMaxTokensPerChunkOrDefault(500);
    var overlap = context.GetOverlapTokensOrDefault(50);
    var batchSize = context.GetBatchSizeOrDefault(10);

    // Use configuration...
}
```

---

## 🔄 Example 6: Handler with Sub-Steps

**For handlers that need to track multiple passes (e.g., multiple embedding models)**

```csharp
public class GenerateEmbeddingsHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;

    public string StepName => "generate_embeddings";

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken)
    {
        var models = new[] { "openai", "azure", "local" };

        foreach (var model in models)
        {
            // ✅ Sub-step tracking (Kernel Memory pattern)
            if (context.AlreadyProcessedBy(StepName, model))
            {
                continue;
            }

            // Generate embeddings with this model...
            await GenerateEmbeddingsWithModel(context, model, cancellationToken);

            // ✅ Mark sub-step complete
            context.MarkProcessedBy(StepName, model);
        }

        return PipelineResult.Success();
    }
}
```

---

## 🎨 Example 7: Custom Pipeline Templates

```csharp
public static class MyCustomTemplates
{
    /// <summary>
    /// Financial document processing with compliance checks.
    /// </summary>
    public static PipelineBuilder<DocumentIngestionContext> FinancialDocument(
        IServiceProvider services)
    {
        return new PipelineBuilder<DocumentIngestionContext>()
            .WithServices(services)
            .AddSteps(
                "extract_text",
                "detect_pii",           // Custom: PII detection
                "redact_sensitive",     // Custom: Redaction
                "partition_text",
                "extract_entities",
                "compliance_check",     // Custom: Compliance validation
                "generate_embeddings",
                "save_records")
            .WithTag("category", "financial")
            .WithTag("compliance", "required")
            .WithConfiguration("pii_detection_enabled", true)
            .WithConfiguration("redaction_level", "strict");
    }
}

// Usage
var context = MyCustomTemplates
    .FinancialDocument(serviceProvider)
    .WithIndex("financial-docs")
    .BuildContext();
```

---

## 🚀 Key Takeaways

### What We Learned from Kernel Memory:
- ✅ **Idempotency tracking** (`AlreadyProcessedBy`, `MarkProcessedBy`)
- ✅ **File lineage** (parent/child relationships)
- ✅ **Sub-step support** (handlers with multiple passes)
- ✅ **Type-safe configuration** (extension methods)
- ✅ **Generated file tracking**

### What We Improved:
- ✅ **Retrieval pipelines** (Kernel Memory can't do this!)
- ✅ **Generic contexts** (works for any pipeline type)
- ✅ **Separate storage** (IDocumentStore, not in orchestrator)
- ✅ **Standard DI** (services injected normally)
- ✅ **Rich error handling** (PipelineResult with metadata)
- ✅ **Microsoft.Extensions.AI** (standard interfaces)
- ✅ **Graph database support** (IGraphStore)

### Result:
**Second mover's advantage achieved!** 🎯

We have all the good patterns from Kernel Memory, none of the limitations, and support for modern RAG patterns (retrieval pipelines, graph databases, hybrid search).
