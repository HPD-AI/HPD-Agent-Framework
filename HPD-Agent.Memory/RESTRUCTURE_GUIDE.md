# HPD-Agent.Memory Restructuring Guide

## What Changed?

HPD-Agent.Memory has been restructured into **three separate NuGet packages** in a monorepo, following the "infrastructure-first" philosophy similar to Microsoft.Extensions.*.

### Before (Single Package)

```
HPD-Agent.Memory/
├── Abstractions/
├── Core/
├── Extensions/
└── HPD-Agent.Memory.csproj  (everything in one package)
```

**Problem:** Users had to take everything or nothing. No clean separation between interfaces and implementations.

### After (Three Packages)

```
HPD-Agent.Memory/
├── src/
│   ├── HPD.Memory.Abstractions/    ← Interfaces only (NEW!)
│   │   ├── Client/                  ← IMemoryClient + contracts
│   │   ├── Pipeline/                ← Pipeline abstractions
│   │   ├── Storage/                 ← Storage abstractions
│   │   └── Models/                  ← Domain models
│   │
│   ├── HPD.Memory.Core/             ← Pipeline infrastructure
│   │   ├── Orchestration/
│   │   ├── Contexts/
│   │   ├── Storage/
│   │   └── Extensions/
│   │
│   └── HPD.Memory.Client/           ← IMemoryClient implementations (NEW!)
│       ├── BasicMemoryClient.cs
│       ├── GraphMemoryClient.cs
│       └── HybridMemoryClient.cs
└── HPD.Memory.sln
```

**Benefits:**
- ✅ Clean dependency direction (Abstractions ← Core ← Client)
- ✅ Users can reference just abstractions for interfaces
- ✅ Separate NuGet packages allow independent evolution
- ✅ Future extraction to separate repo is trivial

## Package Overview

### 1. HPD.Memory.Abstractions

**What:** Interfaces and contracts only. Zero implementation dependencies.

**When to use:**
- Building a custom RAG implementation
- Writing code that's IMemoryClient-agnostic
- Mocking RAG systems in tests
- Defining custom pipeline handlers

**Install:**
```bash
dotnet add package HPD.Memory.Abstractions
```

**Key Exports:**
- `IMemoryClient` (universal RAG interface)
- `IPipelineContext`, `IPipelineHandler<TContext>`, `IPipelineOrchestrator<TContext>`
- `IDocumentStore`, `IGraphStore`
- `DocumentFile`, `MemoryFilter`, `GraphEntity`, etc.

### 2. HPD.Memory.Core

**What:** Pipeline infrastructure and storage implementations.

**When to use:**
- Building custom RAG pipelines
- Implementing custom pipeline handlers
- Using the orchestration system directly
- Need full control over pipeline execution

**Install:**
```bash
dotnet add package HPD.Memory.Core
```

**Key Exports:**
- `InProcessOrchestrator<TContext>` (pipeline orchestrator)
- `DocumentIngestionContext`, `SemanticSearchContext`
- `LocalFileDocumentStore`, `InMemoryGraphStore`
- `PipelineBuilder`, DI extensions

**Dependencies:**
- HPD.Memory.Abstractions
- Microsoft.Extensions.AI
- Microsoft.Extensions.VectorData.Abstractions
- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.Logging.Abstractions

### 3. HPD.Memory.Client

**What:** IMemoryClient implementations (BasicMemory, GraphMemory, HybridMemory).

**When to use:**
- Want simple, standard RAG API
- Don't want to build pipelines yourself
- Switching between RAG approaches
- Typical application development

**Install:**
```bash
dotnet add package HPD.Memory.Client
```

**Key Exports:**
- `BasicMemoryClient` (vector RAG)
- `GraphMemoryClient` (GraphRAG)
- `HybridMemoryClient` (vector + graph)

**Dependencies:**
- HPD.Memory.Abstractions
- HPD.Memory.Core
- Microsoft.Extensions.AI
- Microsoft.Extensions.Logging.Abstractions

## Dependency Graph

```
┌─────────────────────────────┐
│  Your Application           │
└─────────────────────────────┘
         │
         ▼
┌─────────────────────────────┐
│  HPD.Memory.Client          │  ← Simple RAG API
│  (BasicMemory, GraphMemory) │
└─────────────────────────────┘
         │
         ▼
┌─────────────────────────────┐
│  HPD.Memory.Core            │  ← Pipeline infrastructure
│  (Orchestrators, Contexts)  │
└─────────────────────────────┘
         │
         ▼
┌─────────────────────────────┐
│  HPD.Memory.Abstractions    │  ← Interfaces only
│  (IMemoryClient, IPipeline*)│
└─────────────────────────────┘
```

## Migration Guide

### If you were using the old single package:

**Before:**
```csharp
using HPDAgent.Memory;
using HPDAgent.Memory.Abstractions.Pipeline;

var orchestrator = provider.GetRequiredService<IPipelineOrchestrator<DocumentIngestionContext>>();
// ... use orchestrator directly
```

**After (Option 1: Use IMemoryClient - Recommended for most users):**
```csharp
using HPDAgent.Memory.Abstractions.Client;

// DI setup
services.AddSingleton<IMemoryClient>(sp =>
    new BasicMemoryClient(sp, "default-index"));

// Usage
var memory = provider.GetRequiredService<IMemoryClient>();
await memory.IngestAsync(IngestionRequest.FromFile("doc.pdf"));
var answer = await memory.GenerateAsync(new GenerationRequest { Question = "..." });
```

**After (Option 2: Continue using pipelines directly):**
```csharp
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Core.Orchestration;

// Same as before! Just reference HPD.Memory.Core instead
var orchestrator = provider.GetRequiredService<IPipelineOrchestrator<DocumentIngestionContext>>();
```

## Which Package Should I Use?

### Scenario 1: "I want simple RAG functionality"

```bash
dotnet add package HPD.Memory.Client
```

Use `IMemoryClient`:
```csharp
var memory = serviceProvider.GetRequiredService<IMemoryClient>();
await memory.IngestAsync(...);
var answer = await memory.GenerateAsync(...);
```

### Scenario 2: "I want to build custom RAG pipelines"

```bash
dotnet add package HPD.Memory.Core
```

Use orchestrators and handlers:
```csharp
var orchestrator = new InProcessOrchestrator<MyCustomContext>(logger);
await orchestrator.AddHandlerAsync(new MyCustomHandler());
await orchestrator.ExecuteAsync(context);
```

### Scenario 3: "I want to implement IMemoryClient for my own RAG system"

```bash
dotnet add package HPD.Memory.Abstractions
```

Implement the interface:
```csharp
public class MyRAG : IMemoryClient
{
    public Task<IIngestionResult> IngestAsync(...) { /* your impl */ }
    public Task<IRetrievalResult> RetrieveAsync(...) { /* your impl */ }
    public Task<IGenerationResult> GenerateAsync(...) { /* your impl */ }
}
```

### Scenario 4: "I want everything"

```bash
dotnet add package HPD.Memory.Client
```

This transitively includes Core and Abstractions, giving you access to all three layers.

## Building the Solution

```bash
# Clone repo
git clone https://github.com/your-org/hpd-agent.git
cd hpd-agent/HPD-Agent.Memory

# Restore dependencies
dotnet restore

# Build all packages
dotnet build

# Run tests (when available)
dotnet test

# Pack NuGet packages
dotnet pack -c Release -o ./artifacts
```

This creates three NuGet packages:
- `HPD.Memory.Abstractions.{version}.nupkg`
- `HPD.Memory.Core.{version}.nupkg`
- `HPD.Memory.Client.{version}.nupkg`

## Development Workflow

### Adding a new IMemoryClient implementation:

1. Add class to `HPD.Memory.Client` project
2. Implement `IMemoryClient` interface
3. Use existing `Core` infrastructure (orchestrators, contexts, storage)
4. Register in DI extensions

### Adding a new pipeline handler:

1. Add class to `HPD.Memory.Core` project (or your own project)
2. Implement `IPipelineHandler<TContext>`
3. Register with orchestrator

### Adding a new abstraction:

1. Add interface to `HPD.Memory.Abstractions` project
2. **Important:** Keep zero dependencies (except System.*)
3. Add implementation to `HPD.Memory.Core` or `HPD.Memory.Client`

## Versioning Strategy

All three packages are versioned together during beta (0.9.0-beta).

After 1.0.0:
- **HPD.Memory.Abstractions**: Major version changes ONLY for breaking interface changes (rare)
- **HPD.Memory.Core**: Minor version changes for new features, major for breaking changes
- **HPD.Memory.Client**: Minor version changes for new implementations, major for breaking changes

Example:
```
HPD.Memory.Abstractions v1.0.0  (stable interface)
HPD.Memory.Core v1.2.0          (added new features)
HPD.Memory.Client v1.3.0        (added HybridMemoryClient)
```

Core and Client can evolve faster than Abstractions, which should remain stable.

## Future: Separate Repository?

**Not now, but maybe later.**

Current structure (monorepo with separate packages) gives us:
- ✅ Clean separation (different NuGet packages)
- ✅ Coordinated evolution (same repo)
- ✅ Easy to refactor
- ✅ Simpler workflow

If/when `IMemoryClient` gains significant adoption (3+ community implementations, 1000+ users), we'll consider:
- Separate `dotnet-memory-abstractions` repo for just the interface
- Community governance
- Independent versioning
- .NET Foundation submission

But not until the interface is proven and stable.

## Questions?

- **"Do I have to change my code?"** - No, if you're using pipelines directly. Yes, if you want the simpler IMemoryClient API.
- **"Can I still use the old package?"** - We'll keep it for backward compatibility, but new features go in the new packages.
- **"Will this break my builds?"** - No, we're versioning this as 0.9.0-beta, indicating it's a significant change.
- **"Why three packages?"** - Clean separation of concerns, allows users to take only what they need.
- **"Why not separate repos?"** - Too early. Let's prove the abstractions work first.

## Learn More

- [IMemoryClient Proposal](./docs/IMEMORYCLIENT_PROPOSAL.md)
- [Architecture Overview](./PROJECT_STRUCTURE.md)
- [Implementation Guide](./docs/IMPLEMENTATION_GUIDE.md)
- [API Reference](./docs/API_REFERENCE.md)
