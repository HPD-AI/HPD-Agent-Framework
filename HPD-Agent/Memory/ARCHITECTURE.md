# Memory Systems Architecture

This document explains the internal architecture of HPD-Agent's memory systems for developers and maintainers.

## Table of Contents
- [High-Level Overview](#high-level-overview)
- [Design Principles](#design-principles)
- [Core Components](#core-components)
- [DynamicMemory Architecture](#dynamicmemory-architecture)
- [StaticMemory Architecture](#staticmemory-architecture)
- [Plan Mode Architecture](#plan-mode-architecture)
- [ConversationContext & AsyncLocal](#conversationcontext--asynclocal)
- [Filter Pipeline](#filter-pipeline)
- [Storage Abstraction](#storage-abstraction)
- [Implementation Guide](#implementation-guide)

---

## High-Level Overview

```
┌─────────────────────────────────────────────────────────────┐
│                       User Request                          │
└─────────────────────┬───────────────────────────────────────┘
                      │
                      ▼
         ┌────────────────────────┐
         │  Conversation.RunAsync │
         │ RunStreamingAsync()    │
         └────────┬───────────────┘
                  │
                  │ 1. Set ConversationContext (AsyncLocal)
                  │
                  ▼
         ┌────────────────────────┐
         │   Filter Pipeline      │
         ├────────────────────────┤
         │ DynamicMemoryFilter    │──┐
         │ StaticMemoryFilter     │  │ Inject into
         │ AgentPlanFilter        │  │ System Prompt
         └────────┬───────────────┘  │
                  │◄─────────────────┘
                  │
                  ▼
         ┌────────────────────────┐
         │  Agent Execution       │
         │  (with full context)   │
         └────────┬───────────────┘
                  │
                  │ Agent may call AIFunctions
                  │
                  ▼
         ┌────────────────────────┐
         │  Memory Toolkits        │
         ├────────────────────────┤
         │ DynamicMemoryToolkit    │──┐
         │ AgentPlanToolkit        │  │ Access stores via
         └────────┬───────────────┘  │ ConversationContext
                  │◄─────────────────┘
                  │
                  ▼
         ┌────────────────────────┐
         │  Storage Stores        │
         ├────────────────────────┤
         │ DynamicMemoryStore     │
         │ StaticMemoryStore      │
         │ AgentPlanStore         │
         └────────┬───────────────┘
                  │
                  ▼
         ┌────────────────────────┐
         │  Storage Backends      │
         ├────────────────────────┤
         │ InMemory / JSON / SQL  │
         │ Redis / Custom         │
         └────────────────────────┘
```

---

## Design Principles

### 1. Pluggable Storage

- **Abstract Base Class**: Defines the contract
- **Multiple Implementations**: In-memory, JSON, SQL, Redis, etc.
- **Serialization Support**: Snapshot/restore via records
- **Factory Pattern**: `Deserialize(snapshot)` returns appropriate implementation

### 2. Automatic Injection

Memory/plans are automatically injected into prompts via the filter pipeline:
- No manual prompt engineering required
- Agent always has access to relevant context
- Token limits are enforced at filter level

### 3. Collapsed Context

Each system has appropriate Collapsing:
- **DynamicMemory**: User/session Collapsed (`StorageKey`)
- **StaticMemory**: Agent Collapsed (`AgentName`)
- **Plan Mode**: Conversation Collapsed (`ConversationId`)

### 4. AsyncLocal Context Flow

`ConversationContext` uses `AsyncLocal<T>` to flow conversation metadata through async calls:
- Set once at conversation level
- Flows through all async operations
- Accessible to Toolkits without parameter passing
- Cleaned up after each turn

### 5. Separation of Concerns

- **Stores**: Handle persistence logic
- **Filters**: Handle prompt injection logic
- **Toolkits**: Handle agent interaction logic (AIFunctions)
- **Options**: Handle configuration

---

## Core Components

### Component Diagram

```
┌──────────────────────────────────────────────────────────────┐
│                        Agent Builder                         │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  .WithDynamicMemory()                                  │  │
│  │  .WithStaticMemory()                                   │  │
│  │  .WithPlanMode()                                       │  │
│  └────────────────────────────────────────────────────────┘  │
└──────────────────────┬───────────────────────────────────────┘
                       │
                       │ Creates and registers
                       │
          ┌────────────┼────────────┬──────────────────┐
          │            │            │                  │
          ▼            ▼            ▼                  ▼
    ┌─────────┐  ┌─────────┐  ┌─────────┐      ┌─────────┐
    │  Store  │  │ Filter  │  │ Toolkit  │      │ Options │
    └─────────┘  └─────────┘  └─────────┘      └─────────┘
          │            │            │                  │
          │            │            │                  │
    Persistence   Injection    AIFunctions     Configuration
```

### Key Files

```
HPD-Agent/Memory/Agent/
├── DynamicMemory/
│   ├── DynamicMemoryStore.cs              # Abstract base
│   ├── InMemoryDynamicMemoryStore.cs      # In-memory impl
│   ├── JsonDynamicMemoryStore.cs          # JSON file impl
│   ├── DynamicMemoryToolkit.cs             # AIFunctions
│   ├── DynamicMemoryFilter.cs             # Prompt injection
│   └── DynamicMemoryOptions.cs            # Configuration
│
├── StaticMemory/
│   ├── StaticMemoryStore.cs               # Abstract base
│   ├── InMemoryStaticMemoryStore.cs       # In-memory impl
│   ├── JsonStaticMemoryStore.cs           # JSON file impl
│   ├── StaticMemoryFilter.cs              # Prompt injection
│   └── StaticMemoryOptions.cs             # Configuration
│
└── PlanMode/
    ├── AgentPlanStore.cs                  # Abstract base
    ├── InMemoryAgentPlanStore.cs          # In-memory impl
    ├── JsonAgentPlanStore.cs              # JSON file impl
    ├── AgentPlan.cs                       # Data model
    ├── AgentPlanToolkit.cs                 # AIFunctions
    ├── AgentPlanFilter.cs                 # Prompt injection
    └── PlanModeOptions.cs                 # Configuration
```

---

## DynamicMemory Architecture

### Class Diagram

```
                 ┌──────────────────────┐
                 │ DynamicMemoryStore   │
                 │  (abstract)          │
                 ├──────────────────────┤
                 │ + GetMemoriesAsync() │
                 │ + CreateMemoryAsync()│
                 │ + UpdateMemoryAsync()│
                 │ + DeleteMemoryAsync()│
                 │ + SerializeToSnapshot│
                 │ + Deserialize()      │
                 └──────────┬───────────┘
                            │
              ┌─────────────┴─────────────┐
              │                           │
              ▼                           ▼
┌──────────────────────────┐  ┌──────────────────────────┐
│InMemoryDynamicMemoryStore│  │ JsonDynamicMemoryStore   │
├──────────────────────────┤  ├──────────────────────────┤
│ - _memories: Dictionary  │  │ - _storageDir: string    │
│                          │  │ - _cache: Dictionary     │
│ + FromSnapshot()         │  │ + FromSnapshot()         │
└──────────────────────────┘  └──────────────────────────┘
```

### Data Flow

```
1. Agent Build
   ────────────
   AgentBuilder.WithDynamicMemory()
      │
      ├─> Creates DynamicMemoryStore (JSON or InMemory)
      ├─> Creates DynamicMemoryToolkit(store)
      ├─> Creates DynamicMemoryFilter(store, options)
      ├─> Registers Toolkit to ToolkitManager
      └─> Registers filter to PromptMiddlewares


2. Prompt Injection (Every Request)
   ────────────────────────────────
   Conversation.RunStreamingAsync()
      │
      ├─> ConversationContext.Set(conversationId)
      │
      └─> DynamicMemoryFilter.InvokeAsync()
            │
            ├─> storageKey = options.MemoryId ?? context.AgentName
            ├─> memories = await _store.GetMemoriesAsync(storageKey)
            ├─> Build prompt text from memories (up to MaxTokens)
            └─> Inject as system message


3. Agent Creates Memory
   ─────────────────────
   Agent calls create_memory(title, content)
      │
      └─> DynamicMemoryToolkit.CreateMemoryAsync()
            │
            ├─> storageKey = options.MemoryId ?? context.AgentName
            └─> await _store.CreateMemoryAsync(storageKey, title, content)
                  │
                  └─> [InMemory] Add to dictionary
                      [JSON]     Write to file
                      [SQL]      INSERT INTO memories
```

### Storage Schema

**InMemory**: `Dictionary<string, List<DynamicMemory>>`

**JSON File Structure**:
```
./agent-dynamic-memory/
├── user-123.json
│   [
│     {
│       "id": "mem-001",
│       "title": "User Preference",
│       "content": "Prefers dark mode",
│       "createdAt": "2025-01-15T10:00:00Z",
│       "updatedAt": "2025-01-15T10:00:00Z"
│     }
│   ]
└── user-456.json
```

**Snapshot Structure**:
```csharp
public record DynamicMemoryStoreSnapshot
{
    public DynamicMemoryStoreType StoreType { get; init; }  // InMemory, JsonFile, Custom
    public Dictionary<string, List<DynamicMemory>> Memories { get; init; }
    public Dictionary<string, object>? Configuration { get; init; }  // Store-specific config
}
```

---

## StaticMemory Architecture

### Class Diagram

```
                 ┌──────────────────────┐
                 │  StaticMemoryStore   │
                 │   (abstract)         │
                 ├──────────────────────┤
                 │ + GetDocumentsAsync()│
                 │ + AddDocumentAsync() │
                 │ + GetCombinedKnowledge│
                 │ + SerializeToSnapshot│
                 │ + Deserialize()      │
                 └──────────┬───────────┘
                            │
              ┌─────────────┴─────────────┐
              │                           │
              ▼                           ▼
┌──────────────────────────┐  ┌──────────────────────────┐
│InMemoryStaticMemoryStore │  │ JsonStaticMemoryStore    │
├──────────────────────────┤  ├──────────────────────────┤
│ - _documents: Dictionary │  │ - _storageDir: string    │
│                          │  │ - _textExtractor: ...    │
│ + FromSnapshot()         │  │ + AddDocumentFromFile()  │
└──────────────────────────┘  └──────────────────────────┘
```

### Data Flow

```
1. Agent Build
   ────────────
   AgentBuilder.WithStaticMemory()
      │
      ├─> Creates StaticMemoryStore (JSON or InMemory)
      ├─> If documents specified, add them to store
      ├─> Creates StaticMemoryFilter(store, agentName, maxTokens)
      └─> Registers filter to PromptMiddlewares (NO Toolkit)


2. Prompt Injection (Every Request)
   ────────────────────────────────
   Conversation.RunStreamingAsync()
      │
      └─> StaticMemoryFilter.InvokeAsync()
            │
            ├─> agentName = options.AgentName ?? context.AgentName
            ├─> knowledge = await _store.GetCombinedKnowledgeTextAsync(agentName, maxTokens)
            └─> Inject as system message


3. Add Document (Build Time or Runtime)
   ────────────────────────────────────
   builder.WithStaticMemory(opts => opts.AddDocument(...))
   OR
   await store.AddDocumentFromFileAsync(...)
      │
      ├─> Extract text from file (TextExtractionUtility)
      │     Supports: .txt, .md, .pdf, .docx, URLs
      │
      └─> Store document with metadata
            │
            └─> [InMemory] Add to dictionary
                [JSON]     Write to file with extracted text
                [SQL]      INSERT INTO documents
```

### Storage Schema

**InMemory**: `Dictionary<string, List<StaticMemoryDocument>>`

**JSON File Structure**:
```
./agent-static-memory/
├── PythonExpert/
│   ├── documents.json
│   │   [
│   │     {
│   │       "id": "doc-001",
│   │       "title": "Python Best Practices",
│   │       "extractedText": "...",
│   │       "description": "Official Python guide",
│   │       "tags": ["python", "standards"],
│   │       "sourceFile": "./docs/python-guide.pdf",
│   │       "addedAt": "2025-01-15T10:00:00Z"
│   │     }
│   │   ]
│   └── metadata.json
└── JavaExpert/
    └── documents.json
```

### Memory Strategies

**Current: FullTextInjection**
```
StaticMemoryFilter.InvokeAsync()
   │
   ├─> Get all documents for agent
   ├─> Concatenate all extractedText
   ├─> Truncate to MaxTokens if needed
   └─> Inject into system prompt
```

**Future: IndexedRetrieval** (Vector Search)
```
StaticMemoryFilter.InvokeAsync()
   │
   ├─> Get user query from last message
   ├─> Vector search for relevant chunks
   ├─> Retrieve top K chunks (within MaxTokens)
   └─> Inject relevant chunks into system prompt
```

---

## Plan Mode Architecture

### Class Diagram

```
                 ┌──────────────────────┐
                 │   AgentPlanStore     │
                 │    (abstract)        │
                 ├──────────────────────┤
                 │ + CreatePlanAsync()  │
                 │ + GetPlanAsync()     │
                 │ + UpdateStepAsync()  │
                 │ + AddStepAsync()     │
                 │ + CompletePlanAsync()│
                 │ + SerializeToSnapshot│
                 │ + Deserialize()      │
                 └──────────┬───────────┘
                            │
              ┌─────────────┴─────────────┐
              │                           │
              ▼                           ▼
┌──────────────────────────┐  ┌──────────────────────────┐
│ InMemoryAgentPlanStore   │  │  JsonAgentPlanStore      │
├──────────────────────────┤  ├──────────────────────────┤
│ - _plans: ConcurrentDict │  │ - _storageDir: string    │
│                          │  │ - _cache: ConcurrentDict │
│ + FromSnapshot()         │  │ + FromSnapshot()         │
└──────────────────────────┘  └──────────────────────────┘


        ┌──────────────────────┐
        │     AgentPlan        │
        ├──────────────────────┤
        │ + Id: string         │
        │ + Goal: string       │
        │ + Steps: List<...>   │
        │ + ContextNotes: List │
        │ + IsComplete: bool   │
        │ + CreatedAt: DateTime│
        │ + CompletedAt: ...   │
        └──────────────────────┘
                 │
                 │ has many
                 ▼
        ┌──────────────────────┐
        │     PlanStep         │
        ├──────────────────────┤
        │ + Id: string         │
        │ + Description: string│
        │ + Status: enum       │
        │ + Notes: string      │
        │ + LastUpdated: ...   │
        └──────────────────────┘
```

### Data Flow

```
1. Agent Build
   ────────────
   AgentBuilder.WithPlanMode()
      │
      ├─> Creates AgentPlanStore (InMemory, JSON, or custom)
      ├─> Creates AgentPlanToolkit(store)
      ├─> Creates AgentPlanFilter(store)
      ├─> Registers Toolkit to ToolkitManager
      └─> Registers filter to PromptMiddlewares


2. Prompt Injection (Every Request)
   ────────────────────────────────
   Conversation.RunStreamingAsync()
      │
      ├─> ConversationContext.Set(conversationId)
      │
      └─> AgentPlanFilter.InvokeAsync()
            │
            ├─> conversationId = context.ConversationId
            ├─> if (!await _store.HasPlanAsync(conversationId)) return
            ├─> plan = await _store.BuildPlanPromptAsync(conversationId)
            └─> Inject plan as system message


3. Agent Creates Plan
   ───────────────────
   Agent calls create_plan(goal, steps[])
      │
      └─> AgentPlanToolkit.CreatePlanAsync()
            │
            ├─> conversationId = ConversationContext.CurrentConversationId
            └─> await _store.CreatePlanAsync(conversationId, goal, steps)
                  │
                  ├─> Create AgentPlan object
                  ├─> Create PlanStep objects
                  └─> [InMemory] Add to dictionary
                      [JSON]     Write to file
                      [Redis]    SET plan:{conversationId}


4. Agent Updates Step
   ──────────────────
   Agent calls update_plan_step(stepId, status, notes)
      │
      └─> AgentPlanToolkit.UpdatePlanStepAsync()
            │
            ├─> conversationId = ConversationContext.CurrentConversationId
            └─> await _store.UpdateStepAsync(conversationId, stepId, status, notes)
                  │
                  ├─> Find plan by conversationId
                  ├─> Find step by stepId
                  ├─> Update step.Status and step.Notes
                  └─> Persist changes
```

### Storage Schema

**InMemory**: `ConcurrentDictionary<string, AgentPlan>`
- Key: `conversationId`
- Value: `AgentPlan` object

**JSON File Structure**:
```
./agent-plans/
├── conv-abc123.json
│   {
│     "id": "plan-001",
│     "goal": "Build online store",
│     "steps": [
│       {
│         "id": "1",
│         "description": "Define requirements",
│         "status": "Completed",
│         "notes": "React, Node.js, PostgreSQL",
│         "lastUpdated": "2025-01-15T10:30:00Z"
│       },
│       {
│         "id": "2",
│         "description": "Choose tech stack",
│         "status": "InProgress",
│         "notes": null,
│         "lastUpdated": "2025-01-15T10:35:00Z"
│       }
│     ],
│     "contextNotes": [
│       "[10:32:15] Need to integrate Stripe for payments"
│     ],
│     "isComplete": false,
│     "createdAt": "2025-01-15T10:00:00Z",
│     "completedAt": null
│   }
└── conv-xyz789.json
```

### Plan Format (Injected into Prompt)

```
[CURRENT_PLAN]
Goal: Build an online store website
Plan ID: plan-001
Created: 2025-01-15 10:00:00
Status: In Progress

Steps:
  ● [1] Define requirements (Completed)
      Notes: React, Node.js, PostgreSQL
  ◐ [2] Choose tech stack (InProgress)
  ○ [3] Design UI/UX (Pending)
  ○ [4] Set up development environment (Pending)
  ...

Context Notes:
  • [10:32:15] Need to integrate Stripe for payments
  • [10:45:22] User wants mobile-responsive design

[END_CURRENT_PLAN]
```

---

## ConversationContext & AsyncLocal

### Why AsyncLocal?

Plan Mode functions need access to `ConversationId` to know which plan to operate on, but:
- AIFunctions can't have conversation parameters (they're generic)
- Passing conversationId through the entire call stack is impractical
- The conversation is already known at the `Conversation` class level

**Solution**: Use `AsyncLocal<T>` to flow context through async calls.

### Implementation

```csharp
// ConversationContext.cs
public static class ConversationContext
{
    private static readonly AsyncLocal<ConversationExecutionContext?> _current = new();
    private static ConversationExecutionContext? _fallbackContext;

    // Public API
    public static ConversationExecutionContext? Current =>
        _current.Value ?? _fallbackContext;

    public static string? CurrentConversationId =>
        Current?.ConversationId;

    // Internal API (called by Conversation class)
    internal static void Set(ConversationExecutionContext? context)
    {
        _current.Value = context;
        _fallbackContext = context;
    }

    internal static void Clear()
    {
        _current.Value = null;
        _fallbackContext = null;
    }
}

// ConversationExecutionContext.cs
public class ConversationExecutionContext
{
    public string ConversationId { get; }
    public string? AgentName { get; set; }
    public AgentRunContext? RunContext { get; set; }

    // Extensible for future needs
    public int CurrentIteration => RunContext?.CurrentIteration ?? 0;
    public int MaxIterations => RunContext?.MaxIterations ?? 0;
}
```

### Usage in Conversation

```csharp
// Conversation.cs
public override async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(...)
{
    // ... setup code ...

    // Create context
    var executionContext = new ConversationExecutionContext(Id)
    {
        AgentName = _agent.Name
    };

    // Set AsyncLocal context
    ConversationContext.Set(executionContext);

    try
    {
        // Execute agent (context flows through all async calls)
        var streamResult = await _agent.ExecuteStreamingTurnAsync(...);

        // ... process results ...
    }
    finally
    {
        // Always clean up
        ConversationContext.Clear();
    }
}
```

### Usage in Toolkits

```csharp
// AgentPlanToolkit.cs
public async Task<string> CreatePlanAsync(string goal, string[] steps)
{
    // Access conversation ID from AsyncLocal context
    var conversationId = ConversationContext.CurrentConversationId;

    if (string.IsNullOrEmpty(conversationId))
    {
        return "Error: No conversation context available.";
    }

    // Use conversationId to create plan
    var plan = await _store.CreatePlanAsync(conversationId, goal, steps);
    return $"Created plan {plan.Id}...";
}
```

### AsyncLocal Flow Diagram

```
┌────────────────────────────────────────┐
│ Conversation.RunStreamingAsync()       │
│                                        │
│ ConversationContext.Set(context) ──┐   │
└────────────┬───────────────────────┼───┘
             │                       │
             │ AsyncLocal flows ────>│
             │                       │
             ▼                       │
┌────────────────────────────────────┼───┐
│ Agent.ExecuteStreamingTurnAsync()  │   │
│                                    │   │
│ [Multiple async operations]    <───┘   │
└────────────┬───────────────────────────┘
             │
             │ AsyncLocal still available
             │
             ▼
┌────────────────────────────────────────┐
│ AgentPlanToolkit.CreatePlanAsync()      │
│                                        │
│ conversationId = ConversationContext  │
│   .CurrentConversationId  <─────────  │ ✓ Works!
└────────────────────────────────────────┘
```

### Fallback Context

The system maintains a **static fallback** in addition to AsyncLocal:
- Handles edge cases where ExecutionContext might be lost
- Provides resilience for unusual execution patterns
- Not ideal but prevents hard failures

### Cleanup

**Critical**: Always clean up in `finally` blocks:
```csharp
try
{
    ConversationContext.Set(context);
    // ... execute agent ...
}
finally
{
    ConversationContext.Clear();  // Prevent context leaks!
}
```

---

## Filter Pipeline

### Filter Execution Order

Filters execute in registration order:

```csharp
AgentBuilder.Build()
   │
   ├─> Register DynamicMemoryFilter
   ├─> Register StaticMemoryFilter
   ├─> Register AgentPlanFilter
   ├─> Register UserCustomFilter
   └─> Register PermissionMiddleware
```

**Execution Flow**:
```
User Message
    ↓
DynamicMemoryFilter  (injects memories)
    ↓
StaticMemoryFilter   (injects knowledge)
    ↓
AgentPlanFilter      (injects current plan)
    ↓
UserCustomFilter     (custom logic)
    ↓
PermissionMiddleware     (permissions check)
    ↓
Agent Execution
```

### IPromptMiddleware Interface

```csharp
public interface IPromptMiddleware
{
    Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptMiddlewareContext context,
        Func<PromptMiddlewareContext, Task<IEnumerable<ChatMessage>>> next);
}
```

### Filter Implementation Pattern

```csharp
public class DynamicMemoryFilter : IPromptMiddleware
{
    private readonly DynamicMemoryStore _store;
    private readonly DynamicMemoryOptions _options;

    public async Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptMiddlewareContext context,
        Func<PromptMiddlewareContext, Task<IEnumerable<ChatMessage>>> next)
    {
        // 1. Get storage key (from options or context)
        var storageKey = _options.MemoryId ?? context.AgentName;

        // 2. Load memories from store
        var memories = await _store.GetMemoriesAsync(storageKey);

        // 3. Build prompt text (within token limit)
        var memoryText = BuildMemoryPrompt(memories, _options.MaxTokens);

        // 4. Inject as system message
        var messagesWithMemory = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, memoryText)
        };
        messagesWithMemory.AddRange(context.Messages);
        context.Messages = messagesWithMemory;

        // 5. Continue pipeline
        return await next(context);
    }
}
```

### Token Management

Each filter respects its `MaxTokens` configuration:

```csharp
private string BuildMemoryPrompt(List<DynamicMemory> memories, int maxTokens)
{
    var sb = new StringBuilder();
    var currentTokens = 0;

    sb.AppendLine("[DYNAMIC_MEMORY]");
    foreach (var memory in memories)
    {
        var memoryText = $"- {memory.Title}: {memory.Content}\n";
        var memoryTokens = EstimateTokens(memoryText);

        if (currentTokens + memoryTokens > maxTokens)
            break;  // Stop adding memories

        sb.Append(memoryText);
        currentTokens += memoryTokens;
    }
    sb.AppendLine("[END_DYNAMIC_MEMORY]");

    return sb.ToString();
}
```

---

## Storage Abstraction

### Abstract Store Pattern

All stores follow the same pattern:

```csharp
// 1. Abstract base class defines contract
public abstract class DynamicMemoryStore
{
    public abstract Task<List<DynamicMemory>> GetMemoriesAsync(...);
    public abstract Task<DynamicMemory> CreateMemoryAsync(...);
    // ... other methods ...

    // Serialization
    public abstract DynamicMemoryStoreSnapshot SerializeToSnapshot();
    public static DynamicMemoryStore Deserialize(DynamicMemoryStoreSnapshot snapshot);
}

// 2. Snapshot record for serialization
public record DynamicMemoryStoreSnapshot
{
    public DynamicMemoryStoreType StoreType { get; init; }
    public Dictionary<string, List<DynamicMemory>> Memories { get; init; }
    public Dictionary<string, object>? Configuration { get; init; }
}

// 3. Enum for store types
public enum DynamicMemoryStoreType
{
    InMemory,
    JsonFile,
    Custom
}
```

### Implementation Requirements

To implement a custom store:

1. **Extend the abstract class**:
```csharp
public class SqlDynamicMemoryStore : DynamicMemoryStore
{
    private readonly string _connectionString;

    public SqlDynamicMemoryStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public override async Task<List<DynamicMemory>> GetMemoriesAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqlConnection(_connectionString);
        return await connection.QueryAsync<DynamicMemory>(
            "SELECT * FROM Memories WHERE StorageKey = @StorageKey",
            new { StorageKey = storageKey });
    }

    // Implement other abstract methods...
}
```

2. **Implement serialization**:
```csharp
public override DynamicMemoryStoreSnapshot SerializeToSnapshot()
{
    // Query all memories from database
    var allMemories = GetAllMemoriesFromDatabase();

    return new DynamicMemoryStoreSnapshot
    {
        StoreType = DynamicMemoryStoreType.Custom,
        Memories = allMemories,
        Configuration = new Dictionary<string, object>
        {
            ["ConnectionString"] = _connectionString
        }
    };
}

public static SqlDynamicMemoryStore FromSnapshot(DynamicMemoryStoreSnapshot snapshot)
{
    var connectionString = snapshot.Configuration?["ConnectionString"] as string;
    var store = new SqlDynamicMemoryStore(connectionString);

    // Restore memories to database
    store.RestoreMemories(snapshot.Memories);

    return store;
}
```

3. **Update Deserialize factory method**:
```csharp
// In DynamicMemoryStore.cs
public static DynamicMemoryStore Deserialize(DynamicMemoryStoreSnapshot snapshot)
{
    return snapshot.StoreType switch
    {
        DynamicMemoryStoreType.InMemory => InMemoryDynamicMemoryStore.FromSnapshot(snapshot),
        DynamicMemoryStoreType.JsonFile => JsonDynamicMemoryStore.FromSnapshot(snapshot),
        DynamicMemoryStoreType.Custom => SqlDynamicMemoryStore.FromSnapshot(snapshot),  // Add this
        _ => throw new ArgumentException($"Unknown store type: {snapshot.StoreType}")
    };
}
```

### Store Comparison

| Feature | InMemory | JSON | SQL/Redis |
|---------|----------|------|-----------|
| **Setup** | Zero | Simple | Requires infrastructure |
| **Persistence** | No | Yes | Yes |
| **Performance** | Fastest | Fast | Network latency |
| **Multi-instance** | No | No | Yes |
| **Scalability** | Limited | Limited | High |
| **Backup** | Manual snapshot | File copy | Database backup |
| **Use Case** | Testing | Single instance | Production |

---

## Implementation Guide

### Adding a New Memory System

If you wanted to add a fourth memory system (e.g., "EpisodicMemory"), follow this pattern:

1. **Create data model**:
```csharp
public class EpisodicMemoryEntry
{
    public string Id { get; set; }
    public string EventDescription { get; set; }
    public DateTime Timestamp { get; set; }
}
```

2. **Create abstract store**:
```csharp
public abstract class EpisodicMemoryStore
{
    public abstract Task<List<EpisodicMemoryEntry>> GetEntriesAsync(string agentName);
    public abstract Task AddEntryAsync(string agentName, string description);
    public abstract EpisodicMemoryStoreSnapshot SerializeToSnapshot();
    public static EpisodicMemoryStore Deserialize(EpisodicMemoryStoreSnapshot snapshot);
}
```

3. **Create implementations**:
```csharp
public class InMemoryEpisodicMemoryStore : EpisodicMemoryStore { }
public class JsonEpisodicMemoryStore : EpisodicMemoryStore { }
```

4. **Create filter** (for injection):
```csharp
public class EpisodicMemoryFilter : IPromptMiddleware
{
    private readonly EpisodicMemoryStore _store;

    public async Task<IEnumerable<ChatMessage>> InvokeAsync(...)
    {
        var entries = await _store.GetEntriesAsync(context.AgentName);
        var promptText = BuildEpisodicPrompt(entries);
        // Inject into messages...
    }
}
```

5. **Create Toolkit** (if agent needs to modify):
```csharp
public class EpisodicMemoryToolkit
{
    private readonly EpisodicMemoryStore _store;

    [AIFunction]
    public async Task<string> RecordEventAsync(string description)
    {
        var agentName = ConversationContext.Current?.AgentName;
        await _store.AddEntryAsync(agentName, description);
        return "Event recorded.";
    }
}
```

6. **Create options**:
```csharp
public class EpisodicMemoryOptions
{
    public EpisodicMemoryStore? Store { get; set; }
    public int MaxEntries { get; set; } = 50;

    public EpisodicMemoryOptions WithStore(EpisodicMemoryStore store)
    {
        Store = store;
        return this;
    }
}
```

7. **Create builder extension**:
```csharp
public static AgentBuilder WithEpisodicMemory(
    this AgentBuilder builder,
    Action<EpisodicMemoryOptions> configure)
{
    var options = new EpisodicMemoryOptions();
    configure(options);

    var store = options.Store ?? new InMemoryEpisodicMemoryStore();
    var Toolkit = new EpisodicMemoryToolkit(store);
    var filter = new EpisodicMemoryFilter(store, options);

    builder.ToolkitManager.RegisterToolkit(Toolkit);
    builder.PromptMiddlewares.Add(filter);

    return builder;
}
```

8. **Usage**:
```csharp
var agent = new AgentBuilder("MyAgent")
    .WithEpisodicMemory(opts => opts
        .WithMaxEntries(100)
        .WithStore(new JsonEpisodicMemoryStore("./episodes")))
    .Build();
```

### Testing Custom Stores

```csharp
[Fact]
public async Task TestCustomStore()
{
    // Arrange
    var store = new InMemorySqlDynamicMemoryStore();
    var memory = new DynamicMemory
    {
        Title = "Test",
        Content = "Test content"
    };

    // Act
    await store.CreateMemoryAsync("test-key", memory.Title, memory.Content);
    var memories = await store.GetMemoriesAsync("test-key");

    // Assert
    Assert.Single(memories);
    Assert.Equal("Test", memories[0].Title);
}

[Fact]
public async Task TestStoreSerialization()
{
    // Arrange
    var store = new JsonDynamicMemoryStore("./test-storage");
    await store.CreateMemoryAsync("key1", "Title", "Content");

    // Act - Serialize
    var snapshot = store.SerializeToSnapshot();

    // Act - Deserialize
    var restoredStore = DynamicMemoryStore.Deserialize(snapshot);
    var memories = await restoredStore.GetMemoriesAsync("key1");

    // Assert
    Assert.Single(memories);
    Assert.Equal("Title", memories[0].Title);
}
```

---

## Historical Context

### Evolution

1. **v1 (Original)**:
   - `DynamicMemoryManager` - Hard-coded JSON storage
   - `StaticMemoryManager` - Hard-coded JSON storage
   - `AgentPlanManager` - In-memory only

2. **v2 (Pluggable Storage)**:
   - Abstract store pattern
   - InMemory / JSON / Custom implementations
   - Serialization support
   - Consistent architecture across all systems

### Naming History

- **"InjectedMemory"** → **"DynamicMemory"**: More intuitive name for editable memory
- **"Knowledge"** → **"StaticMemory"**: Clearer distinction from dynamic memory
- **"AgentStaticMemoryStrategy"** → **"MemoryStrategy"**: Shared enum for consistency

### Removed/Deprecated

- `Project` class: Originally for multi-agent knowledge sharing, now deprecated since Microsoft's agent framework handles orchestration differently
- Direct manager classes: Replaced with store abstraction

---

## Performance Considerations

### Token Budget

Typical 16K context window breakdown:
```
System Prompt:        ~1,000 tokens
DynamicMemory:         4,000 tokens (configurable)
StaticMemory:          6,000 tokens (configurable)
Plan:                    500 tokens (auto-sized)
Conversation History:  3,000 tokens
User Message:          1,500 tokens
──────────────────────────────────
Total:                16,000 tokens
```

### Caching

**Current**: Each filter loads fresh on every request
**Future**: Consider caching strategies:
- Cache store results for N seconds
- Invalidate cache on writes
- Use distributed cache (Redis) for multi-instance

### Concurrency

- **InMemory stores**: Use `ConcurrentDictionary` for thread safety
- **JSON stores**: Use `SemaphoreSlim` for file locking
- **Database stores**: Rely on database locking

---

## Troubleshooting

### "No conversation context available"

**Cause**: `ConversationContext.CurrentConversationId` is null

**Solutions**:
1. Ensure `Conversation.RunAsync()` or `RunStreamingAsync()` is being used (not direct agent calls)
2. Check that `ConversationContext.Set()` is called before agent execution
3. Verify AsyncLocal context hasn't been lost (unusual execution patterns)

### Plan not persisting

**Cause**: Using in-memory store by default

**Solution**: Enable persistence explicitly:
```csharp
.WithPlanMode(opts => opts
    .WithPersistence()
    .WithStorageDirectory("./plans"))
```

### Token limit exceeded

**Cause**: Memory/knowledge injection exceeds available tokens

**Solutions**:
1. Reduce `MaxTokens` for each system
2. Trim conversation history more aggressively
3. Use `IndexedRetrieval` strategy for StaticMemory (future)

### Memory not injecting

**Cause**: Filter not registered or wrong storage key

**Debug**:
```csharp
// Add logging to filter
_logger?.LogInformation("Injecting {Count} memories for key {Key}",
    memories.Count, storageKey);
```

---

## Future Enhancements

### Planned

1. **Vector Search for StaticMemory**:
   - Implement `IndexedRetrieval` strategy
   - Integrate with vector databases (Pinecone, Weaviate, pgvector)
   - Semantic search for relevant knowledge chunks

2. **Cache Invalidation**:
   - Callback-based cache updates
   - Distributed cache for multi-instance

3. **Async Streaming for Large Memories**:
   - Stream memory injection for large datasets
   - Progressive loading

4. **Memory Priorities**:
   - Weight memories by relevance/recency
   - Intelligent memory selection within token budget

5. **Plan Templates**:
   - Pre-defined plan templates for common tasks
   - Plan inheritance and composition

### Considered but Deferred

- **Hierarchical Plans**: Sub-plans within steps (added complexity)
- **Shared Plans**: Multiple agents collaborating on one plan (orchestration handles this)
- **Memory Decay**: Time-based memory fading (use case unclear)

---

## Contributing

When modifying the memory architecture:

1. **Maintain Consistency**: Follow the same patterns across all three systems
2. **Preserve Serialization**: Ensure snapshots remain compatible
3. **Update Documentation**: Keep this document and the user guide in sync
4. **Add Tests**: Cover both in-memory and persistent stores
5. **Consider Backward Compatibility**: Provide migration paths for breaking changes

---

## Conclusion

The memory architecture follows a consistent, pluggable design inspired by Microsoft's agent abstractions. The AsyncLocal context pattern provides clean access to conversation metadata without parameter threading. The filter pipeline enables automatic injection of context into prompts, making the agent experience seamless.

When extending the system, follow the established patterns and maintain the separation of concerns between stores, filters, Toolkits, and options.

For usage examples and user-facing documentation, see [MEMORY_AND_PLAN_MODE_GUIDE.md](./MEMORY_AND_PLAN_MODE_GUIDE.md).
