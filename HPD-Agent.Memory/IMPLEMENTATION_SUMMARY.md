# HPD-Agent.Memory Three-Package Restructuring: Implementation Summary

## What We've Built

I've restructured HPD-Agent.Memory from a single package into **three separate NuGet packages** in a monorepo:

### 1. **HPD.Memory.Abstractions** - Interfaces Only ✅

**Location:** `src/HPD.Memory.Abstractions/`

**What's included:**
- ✅ `IMemoryClient` - Universal RAG interface (NEW!)
- ✅ `IngestionRequest`, `RetrievalRequest`, `GenerationRequest` - Request contracts (NEW!)
- ✅ `IIngestionResult`, `IRetrievalResult`, `IGenerationResult` - Response contracts (NEW!)
- ✅ `IRetrievedItem`, `ICitation`, `IGenerationChunk` - Supporting interfaces (NEW!)
- ✅ `IMemoryCapabilities` - Runtime capability discovery (NEW!)
- ✅ Pipeline abstractions (from existing code - needs cleanup)
- ✅ Storage abstractions (from existing code - needs cleanup)
- ✅ Domain models (from existing code - needs cleanup)

**Critical requirement:** ZERO dependencies except System.*

**Current status:** ⚠️ Copied files have dependencies that need to be removed or moved to Core

### 2. **HPD.Memory.Core** - Pipeline Infrastructure

**Location:** `src/HPD.Memory.Core/`

**What should be here:**
- Your existing `InProcessOrchestrator<TContext>`
- Your existing pipeline contexts (`DocumentIngestionContext`, `SemanticSearchContext`)
- Your existing storage implementations (`LocalFileDocumentStore`, `InMemoryGraphStore`)
- Extension methods that have dependencies (Microsoft.Extensions.AI, etc.)
- DI registration extensions

**Dependencies:** HPD.Memory.Abstractions + Microsoft.Extensions.*

**Current status:** ⏳ Needs file organization

### 3. **HPD.Memory.Client** - IMemoryClient Implementations (NEW!)

**Location:** `src/HPD.Memory.Client/`

**What needs to be built:**
- `BasicMemoryClient` - Uses your pipeline for vector RAG
- `GraphMemoryClient` - Uses your pipeline for GraphRAG
- `HybridMemoryClient` - Combines both

**Dependencies:** HPD.Memory.Abstractions + HPD.Memory.Core

**Current status:** ⏳ Not yet implemented

---

## File Organization Needed

### Files that MUST stay in Abstractions (interfaces only):

```
HPD.Memory.Abstractions/
├── Client/                          ← NEW! All done ✅
│   ├── IMemoryClient.cs
│   ├── IngestionRequest.cs
│   ├── RetrievalRequest.cs
│   ├── GenerationRequest.cs
│   ├── IIngestionResult.cs
│   ├── IRetrievalResult.cs
│   └── IGenerationResult.cs
│
├── Pipeline/                        ← From existing, keep only:
│   ├── IPipelineContext.cs         ← Interface (keep)
│   ├── IIngestionContext.cs        ← Interface (keep)
│   ├── IRetrievalContext.cs        ← Interface (keep)
│   ├── IPipelineHandler.cs         ← Interface (keep)
│   ├── IPipelineOrchestrator.cs    ← Interface (keep)
│   ├── PipelineStep.cs             ← Model (keep)
│   └── PipelineContextExtensions.cs ← ⚠️ MOVE to Core (has MS.Extensions.AI deps!)
│
├── Storage/                         ← From existing, interfaces only:
│   ├── IDocumentStore.cs           ← Interface (keep)
│   ├── IGraphStore.cs              ← Interface (keep)
│   └── GraphModels.cs              ← Models (keep)
│
└── Models/                          ← From existing, pure models only:
    ├── DocumentFile.cs              ← Model (keep)
    ├── MemoryFilter.cs              ← Model (keep)
    ├── TagConstants.cs              ← Constants (keep)
    └── TagCollectionExtensions.cs   ← Extension methods (keep if no deps)
```

### Files that MUST move to Core (implementation):

```
HPD.Memory.Core/
├── Orchestration/                   ← From existing
│   ├── InProcessOrchestrator.cs
│   └── PipelineBuilder.cs
│
├── Contexts/                        ← From existing
│   ├── DocumentIngestionContext.cs
│   └── SemanticSearchContext.cs
│
├── Storage/                         ← From existing
│   ├── LocalFileDocumentStore.cs
│   └── InMemoryGraphStore.cs
│
└── Extensions/                      ← From existing
    ├── MemoryServiceCollectionExtensions.cs
    └── PipelineContextExtensions.cs  ← MOVE from Abstractions (has deps!)
```

### Files to CREATE in Client:

```
HPD.Memory.Client/
├── BasicMemoryClient.cs       ← NEW! Implement IMemoryClient
├── GraphMemoryClient.cs       ← NEW! Implement IMemoryClient
└── HybridMemoryClient.cs      ← NEW! Implement IMemoryClient
```

---

## Next Steps (Priority Order)

### Step 1: Clean up Abstractions (High Priority)

**Goal:** Make HPD.Memory.Abstractions build with ZERO dependencies

**Actions:**
1. Remove `Abstractions/Pipeline/PipelineContextExtensions.cs` (move to Core)
2. Remove any files that reference Microsoft.Extensions.AI types
3. Keep only pure interfaces and models
4. Add back required dependencies ONLY to Core

**How to verify:**
```bash
cd src/HPD.Memory.Abstractions
dotnet build
# Should succeed with no errors, zero package dependencies
```

### Step 2: Organize Core (High Priority)

**Goal:** Move existing implementation files to HPD.Memory.Core

**Actions:**
1. Copy implementation files from old structure to `src/HPD.Memory.Core/`
2. Update namespaces from `HPDAgent.Memory.*` to `HPDAgent.Memory.Core.*`
3. Add project reference to HPD.Memory.Abstractions
4. Build and fix any missing dependencies

### Step 3: Implement BasicMemoryClient (Medium Priority)

**Goal:** Prove IMemoryClient works with your pipeline system

**Actions:**
1. Create `src/HPD.Memory.Client/BasicMemoryClient.cs`
2. Implement the three main methods:
   - `IngestAsync()` - Use your `DocumentIngestionContext` + orchestrator
   - `RetrieveAsync()` - Use your `SemanticSearchContext` + orchestrator
   - `GenerateAsync()` - Combine retrieval + IChatClient
3. Build and test

**Template provided:** See example implementation below

### Step 4: Documentation (Low Priority)

**Goal:** Help users understand the new structure

**Actions:**
1. Update main README.md
2. Create migration guide
3. Add examples for each package
4. Document IMemoryClient usage patterns

---

## Example: BasicMemoryClient Implementation

Here's the complete implementation pattern you should follow:

```csharp
// File: src/HPD.Memory.Client/BasicMemoryClient.cs
using HPDAgent.Memory.Abstractions.Client;
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Abstractions.Storage;
using HPDAgent.Memory.Core.Contexts;
using HPDAgent.Memory.Core.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HPDAgent.Memory.Client;

/// <summary>
/// Basic IMemoryClient implementation using vector RAG.
/// Uses HPD-Agent.Memory pipeline infrastructure internally.
/// </summary>
public class BasicMemoryClient : IMemoryClient
{
    private readonly IPipelineOrchestrator<DocumentIngestionContext> _ingestionOrchestrator;
    private readonly IPipelineOrchestrator<SemanticSearchContext> _searchOrchestrator;
    private readonly IDocumentStore _documentStore;
    private readonly IChatClient _chatClient;
    private readonly ILogger<BasicMemoryClient> _logger;
    private readonly string _defaultIndex;

    public BasicMemoryClient(
        IPipelineOrchestrator<DocumentIngestionContext> ingestionOrchestrator,
        IPipelineOrchestrator<SemanticSearchContext> searchOrchestrator,
        IDocumentStore documentStore,
        IChatClient chatClient,
        ILogger<BasicMemoryClient> logger,
        string defaultIndex = "default")
    {
        _ingestionOrchestrator = ingestionOrchestrator;
        _searchOrchestrator = searchOrchestrator;
        _documentStore = documentStore;
        _chatClient = chatClient;
        _logger = logger;
        _defaultIndex = defaultIndex;
    }

    public async Task<IIngestionResult> IngestAsync(
        IngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        // Convert IMemoryClient request → Your pipeline context
        var context = new DocumentIngestionContext
        {
            Index = request.Index ?? _defaultIndex,
            PipelineId = Guid.NewGuid().ToString("N"),
            DocumentId = request.DocumentId ?? Guid.NewGuid().ToString("N"),
            Steps = PipelineTemplates.DocumentIngestionSteps,
            Files = new List<DocumentFile>
            {
                new DocumentFile
                {
                    Id = "file-0",
                    Name = request.FileName,
                    Size = request.Content.Length,
                    MimeType = request.ContentType ?? "application/octet-stream"
                }
            }
        };

        // Copy tags and options
        foreach (var tag in request.Tags)
            context.Tags[tag.Key] = tag.Value;
        foreach (var opt in request.Options)
            context.Data[opt.Key] = opt.Value;

        // Save file using your storage
        await _documentStore.WriteFileAsync(
            context.Index,
            context.PipelineId,
            request.FileName,
            request.Content,
            cancellationToken);

        // Execute YOUR pipeline!
        var completedContext = await _ingestionOrchestrator.ExecuteAsync(context, cancellationToken);

        // Convert your result → IMemoryClient result
        // Build artifact counts dictionary
        var artifactCounts = new Dictionary<string, int>();
        foreach (var file in completedContext.Files)
        {
            foreach (var (key, generatedFile) in file.GeneratedFiles)
            {
                var artifactType = generatedFile.ArtifactType.ToString().ToLowerInvariant();
                artifactCounts[artifactType] = artifactCounts.GetValueOrDefault(artifactType, 0) + 1;
            }
        }

        return IngestionResult.CreateSuccess(
            documentId: completedContext.DocumentId,
            index: completedContext.Index,
            artifactCounts: artifactCounts,
            metadata: new Dictionary<string, object>
            {
                ["pipeline_id"] = completedContext.PipelineId,
                ["completed_steps"] = completedContext.CompletedSteps.Count
            });
    }

    public async Task<IRetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        // Convert request → Your search context
        var context = new SemanticSearchContext
        {
            Index = request.Index ?? _defaultIndex,
            PipelineId = Guid.NewGuid().ToString("N"),
            Query = request.Query,
            Steps = PipelineTemplates.SemanticSearchSteps,
            MaxResults = request.MaxResults
        };

        // Copy filter and options
        if (request.Filter != null)
            context.Data["filter"] = request.Filter;
        foreach (var opt in request.Options)
            context.Data[opt.Key] = opt.Value;

        // Execute YOUR search pipeline!
        var completedContext = await _searchOrchestrator.ExecuteAsync(context, cancellationToken);

        // Extract results (populated by your handlers)
        var searchResults = completedContext.GetData<List<SearchResult>>("search_results")
            ?? new List<SearchResult>();

        // Convert → IRetrievedItem list
        return new RetrievalResult
        {
            Query = request.Query,
            Items = searchResults.Select(r => RetrievedItem.CreateTextChunk(
                content: r.Text,
                score: r.Score,
                documentId: r.DocumentId,
                documentName: r.FileName,
                chunkId: r.PartitionId
            )).ToList(),
            Metadata = new Dictionary<string, object>
            {
                ["total_results"] = searchResults.Count
            }
        };
    }

    public async Task<IGenerationResult> GenerateAsync(
        GenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        // 1. Retrieve relevant context
        var retrieval = await RetrieveAsync(new RetrievalRequest
        {
            Query = request.Question,
            Index = request.Index,
            MaxResults = request.MaxResults ?? 5,
            Filter = request.Filter
        }, cancellationToken);

        // 2. Build RAG prompt
        var contextText = string.Join("\n\n", retrieval.Items.Select(item =>
            $"[Source: {item.Metadata.GetValueOrDefault("document_name", "unknown")}]\n{item.Content}"));

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System,
                request.SystemPrompt ?? "You are a helpful assistant. Answer questions based on the provided context."),
            new ChatMessage(ChatRole.User,
                $"Context:\n{contextText}\n\nQuestion: {request.Question}")
        };

        // 3. Generate answer using LLM
        var response = await _chatClient.CompleteAsync(messages, cancellationToken: cancellationToken);

        // 4. Return with citations
        return GenerationResult.Create(
            question: request.Question,
            answer: response.Message.Text ?? "",
            citations: retrieval.Items.Select(Citation.FromRetrievedItem).ToList(),
            metadata: new Dictionary<string, object>
            {
                ["retrieval_count"] = retrieval.Items.Count,
                ["model"] = response.ModelId ?? "unknown"
            });
    }

    // Implement other IMemoryClient methods...
    public async Task DeleteDocumentAsync(string documentId, string? index = null, CancellationToken cancellationToken = default)
    {
        // Use your document store
        await _documentStore.DeleteAllFilesAsync(index ?? _defaultIndex, documentId, cancellationToken);
    }

    public Task<bool> DocumentExistsAsync(string documentId, string? index = null, CancellationToken cancellationToken = default)
    {
        // Implement using your storage
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IGenerationChunk> GenerateStreamAsync(GenerationRequest request, CancellationToken cancellationToken = default)
    {
        // Implement streaming (optional for now)
        throw new NotSupportedException("Streaming not yet implemented");
    }

    public IMemoryCapabilities Capabilities => new BasicMemoryCapabilities();
}

internal class BasicMemoryCapabilities : IMemoryCapabilities
{
    public bool SupportsGraphTraversal => false;
    public bool SupportsStreaming => false;
    public bool SupportsMultiModal => false;
    public bool SupportsAgenticRetrieval => false;
    public bool SupportsBatchIngestion => false;
    public bool SupportsMetadataFiltering => true;
    public int? MaxRetrievalItems => 100;
    public long? MaxDocumentSize => 10 * 1024 * 1024; // 10MB
}
```

---

## Build Order

Once files are organized:

```bash
# 1. Build abstractions (should have zero package dependencies)
cd src/HPD.Memory.Abstractions
dotnet build

# 2. Build core (depends on abstractions)
cd ../HPD.Memory.Core
dotnet build

# 3. Build client (depends on both)
cd ../HPD.Memory.Client
dotnet build

# 4. Build entire solution
cd ../..
dotnet build HPD.Memory.sln
```

---

## What You Have Now

✅ **Project structure** - Three .csproj files with proper metadata
✅ **Solution file** - HPD.Memory.sln ties them together
✅ **IMemoryClient interface** - Complete, production-ready
✅ **Request/Response contracts** - Ingestion, Retrieval, Generation
✅ **Documentation** - README for Abstractions, RESTRUCTURE_GUIDE
✅ **Blueprint** - This summary shows exactly what to do next

⚠️ **What needs work:**
- Clean up Abstractions (remove files with dependencies)
- Organize Core (move implementation files)
- Implement BasicMemoryClient (using template above)
- Fix build errors
- Test the integration

---

## Estimated Effort

- **Step 1 (Clean Abstractions):** 1-2 hours
- **Step 2 (Organize Core):** 2-3 hours
- **Step 3 (BasicMemoryClient):** 3-4 hours
- **Step 4 (Documentation):** 1-2 hours

**Total:** ~1 workday to get all three packages building and BasicMemoryClient working.

---

## Benefits of This Structure

✅ **Clean dependencies:** Abstractions → Core → Client
✅ **Swappable implementations:** Change one line to switch RAG systems
✅ **Testable:** Mock IMemoryClient in tests
✅ **Future-proof:** Can extract to separate repo later if needed
✅ **Standard interface:** IMemoryClient can become THE .NET RAG standard
✅ **Composable:** Decorators for caching, logging, routing work automatically

This is the foundation for IMemoryClient to become the standard RAG interface in .NET.

