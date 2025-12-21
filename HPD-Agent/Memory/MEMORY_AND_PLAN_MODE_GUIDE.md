# Memory Systems & Plan Mode Guide

This guide explains how to use HPD-Agent's three memory systems: **DynamicMemory**, **StaticMemory**, and **Plan Mode**.

## Table of Contents
- [Overview](#overview)
- [DynamicMemory (Working Memory)](#dynamicmemory-working-memory)
- [StaticMemory (Knowledge Base)](#staticmemory-knowledge-base)
- [Plan Mode (Execution Planning)](#plan-mode-execution-planning)
- [Storage Options](#storage-options)
- [Common Patterns](#common-patterns)

---

## Overview

HPD-Agent provides three complementary memory systems:

| System | Purpose | Agent Can... | Persists |
|--------|---------|-------------|----------|
| **DynamicMemory** | Working memory for facts and context | Create, Read, Update, Delete | ✓ Optional |
| **StaticMemory** | Read-only knowledge base | Read only | ✓ Optional |
| **Plan Mode** | Execution planning and tracking | Create, Update, Track | ✓ Optional |

All three systems support **pluggable storage backends** (in-memory, JSON files, SQL, Redis, etc.).

---

## DynamicMemory (Working Memory)

**DynamicMemory** is the agent's editable working memory. The agent can store facts, preferences, and context that it learns during conversations.

### When to Use
- Storing user preferences ("User prefers dark mode")
- Remembering conversation context across sessions
- Tracking state that changes over time
- Building up knowledge dynamically

### Basic Setup

```csharp
var agent = new AgentBuilder("MyAgent")
    .WithDynamicMemory(opts => opts
        .WithMaxTokens(4000)              // Max tokens to inject into prompt
        .WithStorageKey("user-123")       // Collapse memories by user/session
        .WithStorageDirectory("./memories"))
    .Build();
```

### With Persistence

```csharp
// File-based persistence (default if you specify directory)
.WithDynamicMemory(opts => opts
    .WithStorageDirectory("./user-memories")
    .WithStorageKey("user-123"))
```

### Custom Storage Backend

```csharp
// Use your own storage (SQL, Redis, etc.)
.WithDynamicMemory(opts => opts
    .WithStore(new SqlDynamicMemoryStore(connectionString))
    .WithStorageKey("user-123"))
```

### Agent Functions Available

When enabled, the agent automatically gets these AI functions:

```javascript
// Agent can call these during conversation:
create_memory(title, content)              // Store new memory
get_memories()                             // List all memories
update_memory(memory_id, title, content)   // Update existing memory
delete_memory(memory_id)                   // Remove memory
```

### How It Works

1. **Automatic Injection**: Memories are automatically injected into the system prompt on every request
2. **Token Limit**: Only the most relevant memories are included (up to `MaxTokens`)
3. **Agent Control**: The agent decides when to create/update/delete memories
4. **Collapse**: Memories are Collapsed by `StorageKey` (e.g., per-user or per-conversation)

### Example Flow

```
User: "I prefer emails in Spanish"
Agent: [Calls create_memory("Language Preference", "User prefers Spanish for emails")]
       "Got it! I'll remember to send emails in Spanish."

[Later conversation...]
User: "Send an email to John"
[DynamicMemory automatically injects: "Language Preference: User prefers Spanish for emails"]
Agent: "I'll compose that email in Spanish as you prefer..."
```

---

## StaticMemory (Knowledge Base)

**StaticMemory** is a read-only knowledge base for domain expertise, documentation, or reference material.

### When to Use
- Company policies and procedures
- Technical documentation (API docs, Python guides)
- Product catalogs
- Style guides and best practices
- Any reference material the agent should know but not modify

### Basic Setup

```csharp
var agent = new AgentBuilder("MyAgent")
    .WithStaticMemory(opts => opts
        .WithMaxTokens(8000)
        .WithAgentName("MyAgent")  // Collapse knowledge per agent
        .WithStrategy(MemoryStrategy.FullTextInjection))
    .Build();
```

### Adding Documents at Build Time

```csharp
.WithStaticMemory(opts => opts
    .WithAgentName("PythonExpert")
    .AddDocument("./docs/python-best-practices.md",
        description: "Python coding standards",
        tags: ["python", "standards"])
    .AddDocument("./docs/api-reference.pdf",
        description: "Internal API documentation",
        tags: ["api", "reference"]))
```

### Adding Documents at Runtime

```csharp
// Access the store to add documents dynamically
var staticStore = new JsonStaticMemoryStore(
    "./knowledge-base",
    new TextExtractionUtility());

await staticStore.AddDocumentFromFileAsync(
    "PythonExpert",
    "./docs/new-guide.md",
    description: "New Python guide",
    tags: new List<string> { "python", "tutorial" });
```

### With Persistence

```csharp
// JSON file-based storage (automatically persistent)
.WithStaticMemory(opts => opts
    .WithStorageDirectory("./knowledge-base")
    .WithAgentName("MyAgent"))
```

### Custom Storage Backend

```csharp
// Use your own storage
.WithStaticMemory(opts => opts
    .WithStore(new VectorDbStaticMemoryStore(connectionString))
    .WithAgentName("MyAgent"))
```

### Memory Strategies

Two strategies are available:

1. **FullTextInjection** (Current): Injects all knowledge text into system prompt
   - Simple and reliable
   - Limited by token count
   - Good for smaller knowledge bases

2. **IndexedRetrieval** (Future): Vector search for relevant knowledge
   - Scalable for large knowledge bases
   - Retrieves only relevant chunks
   - Requires vector database

```csharp
.WithStaticMemory(opts => opts
    .WithStrategy(MemoryStrategy.FullTextInjection)  // or IndexedRetrieval
    .WithMaxTokens(8000))
```

### How It Works

1. **Automatic Injection**: Knowledge is automatically injected into the system prompt
2. **Read-Only**: Agent cannot modify static memory (no AI functions provided)
3. **Collapse**: Knowledge is Collapsed by agent name
4. **Text Extraction**: Supports multiple formats (`.txt`, `.md`, `.pdf`, `.docx`, URLs)

### Example Flow

```
[Agent built with StaticMemory containing Python documentation]

User: "How do I handle exceptions in Python?"
[StaticMemory automatically injects Python docs into context]
Agent: "Based on the Python best practices, here's how to handle exceptions..."
```

---

## Plan Mode (Execution Planning)

**Plan Mode** enables agents to create and track structured execution plans for complex multi-step tasks.

### When to Use
- Complex tasks requiring multiple steps
- Tasks where progress tracking is valuable
- Workflows that might span multiple sessions
- When you want visibility into agent's planning process

### Basic Setup

```csharp
var agent = new AgentBuilder("MyAgent")
    .WithPlanMode()  // Uses in-memory storage (non-persistent)
    .Build();
```

### With Persistence

```csharp
// File-based persistence - plans survive restarts
.WithPlanMode(opts => opts
    .WithPersistence()
    .WithStorageDirectory("./plans"))
```

### Custom Storage Backend

```csharp
// Use your own storage (SQL, Redis, etc.)
.WithPlanMode(opts => opts
    .WithStore(new RedisPlanStore(redisConnection)))
```

### Agent Functions Available

When enabled, the agent automatically gets these AI functions:

```javascript
// Agent can call these during conversation:
create_plan(goal, steps[])           // Create execution plan
update_plan_step(stepId, status, notes)  // Update step progress
add_plan_step(description, afterStepId)  // Add new step to plan
add_context_note(note)               // Record important findings
complete_plan()                      // Mark plan as complete
```

### Plan Lifecycle

1. **Creation**: Agent creates plan with goal and initial steps
2. **Execution**: Agent updates step status as it works (`pending` → `in_progress` → `completed`)
3. **Adaptation**: Agent can add new steps if it discovers additional work
4. **Context Notes**: Agent records important discoveries during execution
5. **Completion**: Agent marks plan complete when goal is achieved

### Plan Visibility

The current plan is **automatically injected** into every agent request, so the agent always sees:
- Current goal
- All steps and their status
- Context notes
- Progress overview

### Example Flow

```
User: "Build an online store website"

Agent: [Calls create_plan(...)]
       "I've created a 10-step plan for building your online store:
        1. Define requirements
        2. Choose tech stack
        3. Design UI/UX
        ..."

[Later in the conversation...]
Agent: [Calls update_plan_step("1", "completed", "Requirements defined: React, Node.js, PostgreSQL")]
       [Calls update_plan_step("2", "in_progress")]
       "I've completed defining requirements and I'm now working on selecting the technology stack..."

[If agent discovers more work needed...]
Agent: [Calls add_plan_step("Set up CI/CD pipeline", afterStepId="5")]
       [Calls add_context_note("Need to integrate Stripe for payments")]
```

### Plan Storage Collapse

Plans are **conversation-Collapsed** (more precisely: **thread-Collapsed**), meaning:
- Each conversation thread has its own plan (or no plan)
- Plans are keyed by `ConversationId` (which is actually the `ThreadId`)
- With persistence, plans survive app restarts
- Plans can be shared across multiple agents/instances (with shared store)

### Multi-Agent Plan Collaboration

Plan Mode supports true multi-agent collaboration when agents share both the **thread** and the **store**:

```csharp
// Create shared infrastructure
var sharedStore = new JsonAgentPlanStore("./shared-plans");
var sharedThread = new ConversationThread(); // One thread ID

// Build multiple specialized agents
var analyzer = new AgentBuilder("CodeAnalyzer")
    .WithPlanMode(opts => opts.Store = sharedStore)
    .Build();

var refactorer = new AgentBuilder("CodeRefactorer")
    .WithPlanMode(opts => opts.Store = sharedStore)
    .Build();

// Create conversations with the SAME thread
var conv1 = new Conversation(analyzer, sharedThread);
var conv2 = new Conversation(refactorer, sharedThread);

// Agent 1 creates a plan
await conv1.RunAsync([new ChatMessage(ChatRole.User, "Analyze and plan refactoring")]);
// Creates plan in sharedStore[sharedThread.Id]

// Agent 2 can see and update THE SAME plan
await conv2.RunAsync([new ChatMessage(ChatRole.User, "Execute step 1")]);
// Reads plan from sharedStore[sharedThread.Id]
// Updates step 1 status
```

**Key Insight**: `Conversation.Id` actually returns `Thread.Id`, so when multiple `Conversation` instances share the same thread, they share the same identity and can access the same plan.

---

## Multi-Agent Collaboration & Sharing

Understanding how agents share memory and plans requires understanding the relationship between **Threads**, **Stores**, and **Agents**.

### The Identity Model

```
Thread = Identity (has the ID)
  ↓
Conversation = Thread + Agent (delegates ID to Thread)
  ↓
Store = Storage Backend (indexed by Thread ID)
```

**Critical Architecture Point**: `Conversation.Id` delegates to `Thread.Id`, so:
- Multiple `Conversation` instances can share the same `Thread`
- When they share the same thread, they share the same identity
- When they also share the same `Store` instance, they access the same data

### The Sharing Matrix

You control sharing behavior with **3 variables**:

| Thread Instance | Store Instance | Result | Use Case |
|----------------|---------------|--------|----------|
| **Same** | **Same** |  **Real-time sharing** | Multi-agent collaboration |
| **Same** | Different (in-memory) |    No sharing | Bug/misconfiguration |
| **Same** | Different (file, same dir) |   **Eventual consistency** | Multi-process/distributed |
| **Different** | Same |    No sharing | Independent conversations |
| **Different** | Different |    No sharing | Independent conversations |

### Scenario 1: Real-Time Collaboration (Same Thread + Same Store)

**Use When**: Multiple specialized agents working together on the same task

```csharp
var sharedStore = new JsonAgentPlanStore("./plans");
var sharedThread = new ConversationThread(); // threadId = "abc123"

var agent1 = new AgentBuilder("Analyzer")
    .WithPlanMode(opts => opts.Store = sharedStore)
    .WithDynamicMemory(opts => opts
        .Store = new JsonDynamicMemoryStore("./shared-memory")
        .WithStorageKey(sharedThread.Id))  // Use thread ID as key
    .Build();

var agent2 = new AgentBuilder("Executor")
    .WithPlanMode(opts => opts.Store = sharedStore)
    .WithDynamicMemory(opts => opts
        .Store = new JsonDynamicMemoryStore("./shared-memory")
        .WithStorageKey(sharedThread.Id))  // Same key = shared memories
    .Build();

var conv1 = new Conversation(agent1, sharedThread);
var conv2 = new Conversation(agent2, sharedThread);

// Both agents see the same plan and memories in real-time
// Changes by agent1 are immediately visible to agent2
```

**Why it works**:
- `conv1.Id == conv2.Id == sharedThread.Id`
- Both query `sharedStore[sharedThread.Id]` → same plan object
- Both agents have in-memory cache for real-time updates
- File persistence ensures durability

### Scenario 2: Eventual Consistency (Same Thread + Different Stores, File-Based)

**Use When**: Multi-process or distributed agents (different machines/containers)

```csharp
// Process 1
var store1 = new JsonAgentPlanStore("./shared-plans");
var thread = ConversationThread.Deserialize(threadSnapshot);
var agent1 = new AgentBuilder("Agent1")
    .WithPlanMode(opts => opts.Store = store1)
    .Build();
var conv1 = new Conversation(agent1, thread);

// Process 2 (different machine/container)
var store2 = new JsonAgentPlanStore("./shared-plans");  // Same directory via shared filesystem
var thread = ConversationThread.Deserialize(threadSnapshot);  // Same thread
var agent2 = new AgentBuilder("Agent2")
    .WithPlanMode(opts => opts.Store = store2)
    .Build();
var conv2 = new Conversation(agent2, thread);

// Both use thread.Id = "abc123"
// store1 writes: ./shared-plans/abc123.json
// store2 reads: ./shared-plans/abc123.json
//   Not real-time (file I/O latency), but eventually consistent
```

**Why it works**:
- Same thread ID → same filename: `{threadId}.json`
- File system acts as shared storage medium
- Each process has its own cache, syncs via disk
- Network filesystem (NFS, SMB) enables cross-machine sharing

**Limitations**:
- Not real-time (file I/O delay)
- Potential race conditions (use file locking if needed)
- Better for async collaboration than interactive

### Scenario 3: Independent Conversations (Different Threads)

**Use When**: Each user/session has their own conversation

```csharp
var sharedStore = new JsonAgentPlanStore("./plans");

// User A's conversation
var threadA = new ConversationThread(); // threadId = "abc123"
var convA = new Conversation(agent, threadA);

// User B's conversation
var threadB = new ConversationThread(); // threadId = "xyz789"
var convB = new Conversation(agent, threadB);

// Both use the same store, but different thread IDs
// sharedStore["abc123"] = User A's plan
// sharedStore["xyz789"] = User B's plan
// No sharing between users
```

### Scenario 4: Multi-Agent Workflow (WorkflowBuilder Integration)

**Use When**: Orchestrating multiple agents in a workflow

```csharp
var sharedPlanStore = new JsonAgentPlanStore("./workflow-plans");
var sharedMemoryStore = new JsonDynamicMemoryStore("./workflow-memory");

var analyzer = new AgentBuilder("Analyzer")
    .WithPlanMode(opts => opts.Store = sharedPlanStore)
    .WithDynamicMemory(opts => opts.Store = sharedMemoryStore)
    .Build();

var implementer = new AgentBuilder("Implementer")
    .WithPlanMode(opts => opts.Store = sharedPlanStore)
    .WithDynamicMemory(opts => opts.Store = sharedMemoryStore)
    .Build();

var reviewer = new AgentBuilder("Reviewer")
    .WithPlanMode(opts => opts.Store = sharedPlanStore)
    .WithDynamicMemory(opts => opts.Store = sharedMemoryStore)
    .Build();

// Create conversations that share the same thread
var sharedThread = new ConversationThread();
var analyzerConv = new Conversation(analyzer, sharedThread);
var implementerConv = new Conversation(implementer, sharedThread);
var reviewerConv = new Conversation(reviewer, sharedThread);

// WorkflowBuilder orchestrates these agents
// They all see the same plan and memories because they share:
// 1. The same thread (same ID)
// 2. The same store instances
// 3. Storage key derived from thread ID
```

### Understanding the Flow

When a `Conversation` runs:

```csharp
// Conversation.cs - RunAsync
var conversationContextDict = BuildConversationContext();
// This sets: ["ConversationId"] = this.Id
// But this.Id returns _thread.Id!

var executionContext = new ConversationExecutionContext(Id);
// Again, Id = _thread.Id
ConversationContext.Set(executionContext);

// When plugin executes:
// AgentPlanPlugin.cs
var conversationId = ConversationContext.CurrentConversationId;
// This is the thread ID

var plan = await _store.GetPlanAsync(conversationId);
// Looks up: _store[threadId]
```

So the complete data flow is:
1. **Thread** provides the identity (ID)
2. **Conversation** delegates its ID to the thread
3. **Store** uses that ID as a key to look up data
4. Multiple conversations sharing the same thread = same ID = same store key = shared data

### Advanced: Storage Key Strategies

For **DynamicMemory**, you can explicitly control sharing:

```csharp
// Strategy 1: Per-thread (share across agents in same conversation)
.WithDynamicMemory(opts => opts
    .WithStorageKey(thread.Id)  // All agents on this thread share memories
    .WithStore(sharedMemoryStore))

// Strategy 2: Per-user (share across all conversations for a user)
.WithDynamicMemory(opts => opts
    .WithStorageKey($"user-{userId}")  // All conversations for this user share memories
    .WithStore(sharedMemoryStore))

// Strategy 3: Per-agent (isolated per agent type)
.WithDynamicMemory(opts => opts
    .WithStorageKey($"{agentName}-{thread.Id}")  // Each agent has separate memories
    .WithStore(sharedMemoryStore))

// Strategy 4: Per-task (share across workflow for a task)
.WithDynamicMemory(opts => opts
    .WithStorageKey($"task-{taskId}")  // All agents working on this task share memories
    .WithStore(sharedMemoryStore))
```

### Production Patterns

**Pattern: Horizontally Scaled Multi-Agent System**

```csharp
// Use Redis for true shared state across instances
var redisPlanStore = new RedisPlanStore(redisConnection);
var redisDynamicMemoryStore = new RedisDynamicMemoryStore(redisConnection);

// All instances use the same stores
// Thread serialization/deserialization for cross-instance communication
var thread = ConversationThread.Deserialize(threadSnapshot);

var agent = new AgentBuilder("Agent")
    .WithPlanMode(opts => opts.Store = redisPlanStore)
    .WithDynamicMemory(opts => opts
        .Store = redisDynamicMemoryStore
        .WithStorageKey(thread.Id))
    .Build();

// Multiple instances, multiple processes, all sharing state via Redis
```

**Pattern: Agent Handoff**

```csharp
// Agent 1 creates plan and starts work
var thread = new ConversationThread();
var conv1 = new Conversation(agent1, thread);
await conv1.RunAsync([new ChatMessage(ChatRole.User, "Start task")]);

// Serialize thread for handoff
var threadSnapshot = thread.SerializeToSnapshot();
SaveToDatabase(threadSnapshot);

// Later, Agent 2 continues the work
var restoredThread = ConversationThread.Deserialize(LoadFromDatabase());
var conv2 = new Conversation(agent2, restoredThread);
await conv2.RunAsync([new ChatMessage(ChatRole.User, "Continue from step 3")]);

// Works because:
// 1. restoredThread.Id == original thread.Id
// 2. Both agents use the same store instances
// 3. Plan is looked up by thread ID
```

---

## Storage Options

All three memory systems support the same pluggable storage architecture:

### In-Memory (Default)

**Use for**: Development, testing, ephemeral sessions

```csharp
// Implicitly in-memory if no storage specified
.WithDynamicMemory(opts => opts.WithMaxTokens(4000))
.WithStaticMemory(opts => opts.WithMaxTokens(8000))
.WithPlanMode()
```

**Pros**: Fast, simple, no file I/O
**Cons**: Lost on restart

### JSON File Storage

**Use for**: Simple persistence, single-instance deployments

```csharp
.WithDynamicMemory(opts => opts
    .WithStorageDirectory("./agent-dynamic-memory"))

.WithStaticMemory(opts => opts
    .WithStorageDirectory("./agent-static-memory"))

.WithPlanMode(opts => opts
    .WithPersistence()
    .WithStorageDirectory("./agent-plans"))
```

**Pros**: Simple, human-readable, no database needed
**Cons**: Not suitable for multi-instance deployments

### Custom Storage

**Use for**: Production, multi-instance, scalability

```csharp
// Implement the abstract store classes:
// - DynamicMemoryStore
// - StaticMemoryStore
// - AgentPlanStore

.WithDynamicMemory(opts => opts
    .WithStore(new SqlDynamicMemoryStore(connectionString)))

.WithStaticMemory(opts => opts
    .WithStore(new VectorDbStaticMemoryStore(connectionString)))

.WithPlanMode(opts => opts
    .WithStore(new RedisPlanStore(redisConnection)))
```

**Pros**: Scalable, shared across instances, production-ready
**Cons**: Requires infrastructure

### Serialization Support

All stores support snapshotting for backup/restore:

```csharp
// Snapshot
var snapshot = dynamicStore.SerializeToSnapshot();
SaveToBackup(snapshot);

// Restore
var restoredStore = DynamicMemoryStore.Deserialize(snapshot);
```

---

## Common Patterns

### Pattern 1: Personal Assistant Agent

```csharp
var agent = new AgentBuilder("PersonalAssistant")
    // Remember user preferences
    .WithDynamicMemory(opts => opts
        .WithStorageKey(userId)
        .WithStorageDirectory("./user-memories")
        .WithMaxTokens(4000))

    // Knowledge about company policies
    .WithStaticMemory(opts => opts
        .WithAgentName("PersonalAssistant")
        .AddDocument("./docs/company-handbook.pdf")
        .AddDocument("./docs/expense-policy.md")
        .WithMaxTokens(8000))

    .Build();
```

### Pattern 2: Task Execution Agent with Planning

```csharp
var agent = new AgentBuilder("TaskExecutor")
    // Track progress with plans
    .WithPlanMode(opts => opts
        .WithPersistence()
        .WithStorageDirectory("./task-plans"))

    // Remember task-specific context
    .WithDynamicMemory(opts => opts
        .WithStorageKey($"task-{taskId}")
        .WithMaxTokens(2000))

    .Build();
```

### Pattern 3: Domain Expert Agent

```csharp
var agent = new AgentBuilder("PythonExpert")
    // Extensive Python documentation
    .WithStaticMemory(opts => opts
        .WithAgentName("PythonExpert")
        .AddDocument("./docs/python-official-docs.md")
        .AddDocument("./docs/common-patterns.md")
        .AddDocument("./docs/best-practices.md")
        .WithMaxTokens(10000)
        .WithStrategy(MemoryStrategy.FullTextInjection))

    .Build();
```

### Pattern 4: Multi-Instance Production Setup

```csharp
// Shared storage across multiple instances
var redisConnection = ConnectionMultiplexer.Connect("redis:6379");
var sqlConnection = "Server=...";

var agent = new AgentBuilder("ProductionAgent")
    .WithDynamicMemory(opts => opts
        .WithStore(new RedisDynamicMemoryStore(redisConnection))
        .WithStorageKey(userId))

    .WithStaticMemory(opts => opts
        .WithStore(new SqlStaticMemoryStore(sqlConnection))
        .WithAgentName("ProductionAgent"))

    .WithPlanMode(opts => opts
        .WithStore(new RedisPlanStore(redisConnection)))

    .Build();
```

### Pattern 5: Testing with In-Memory Stores

```csharp
[Fact]
public async Task TestAgentMemory()
{
    // Fast in-memory stores for unit tests
    var agent = new AgentBuilder("TestAgent")
        .WithDynamicMemory(opts => opts
            .WithStore(new InMemoryDynamicMemoryStore())
            .WithStorageKey("test-session"))

        .WithPlanMode(opts => opts
            .WithStore(new InMemoryAgentPlanStore()))

        .Build();

    // Test agent behavior...
}
```

---

## Architecture Notes

### Memory Injection Flow

```
User Message
    ↓
Conversation.RunStreamingAsync()
    ↓
ConversationContext.Set(conversationId)  ← Sets AsyncLocal context
    ↓
DynamicMemoryFilter.InvokeAsync()        ← Injects memories into prompt
StaticMemoryFilter.InvokeAsync()         ← Injects knowledge into prompt
AgentPlanFilter.InvokeAsync()            ← Injects current plan into prompt
    ↓
Agent.ExecuteStreamingTurnAsync()
    ↓
    Agent sees all context in system prompt
    Agent can call memory/plan functions
    ↓
ConversationContext.Clear()               ← Cleanup
```

### Key Design Principles

1. **Automatic Injection**: Memory/plans are automatically available to the agent - no manual prompt engineering needed
2. **Pluggable Storage**: All systems support custom backends via abstract store classes
3. **Collapsed Context**: Each system has appropriate Collapsing (user, agent, conversation)
4. **Serializable**: All stores can snapshot/restore state for backup/migration
5. **AsyncLocal Context**: ConversationId flows through async calls for plugins to access

---

## Migration Guide

### From Old DynamicMemoryManager

```csharp
// Old (deprecated)
var manager = new DynamicMemoryManager("./memories");
builder.WithDynamicMemory(manager);

// New
builder.WithDynamicMemory(opts => opts
    .WithStorageDirectory("./memories")
    .WithStorageKey("session-123"));
```

### From Old StaticMemoryManager

```csharp
// Old (deprecated)
var manager = new StaticMemoryManager("./knowledge");
builder.WithStaticMemory(manager);

// New
builder.WithStaticMemory(opts => opts
    .WithStorageDirectory("./knowledge")
    .WithAgentName("MyAgent"));
```

### From Old AgentPlanManager

```csharp
// Old (deprecated)
var manager = new AgentPlanManager();
builder.WithPlanMode(manager);

// New
builder.WithPlanMode(opts => opts
    .WithPersistence()  // if you want persistence
    .WithStorageDirectory("./plans"));
```

---

## FAQ

**Q: Can I use all three systems together?**
A: Yes! They complement each other and can be used simultaneously.

**Q: What happens if I exceed MaxTokens?**
A: The filter will truncate injected content to fit within the limit. Oldest/least relevant content is dropped first.

**Q: Can multiple agents share the same DynamicMemory?**
A: Yes, if they use the same `StorageKey`. Useful for agent teams collaborating on a task.

**Q: Can I access stores programmatically outside the agent?**
A: Yes! All stores can be instantiated and used independently of the agent.

**Q: How do I implement a custom storage backend?**
A: Extend `DynamicMemoryStore`, `StaticMemoryStore`, or `AgentPlanStore` abstract classes. See [ARCHITECTURE.md](./ARCHITECTURE.md) for details.

**Q: Why is my plan not persisting?**
A: By default, PlanMode uses in-memory storage. Use `.WithPersistence()` to enable JSON file persistence.

**Q: Can I disable Plan Mode's automatic injection?**
A: No, automatic injection is core to the design. The agent always sees the current plan. If you don't want a plan, simply don't call `create_plan()`.

---

## Best Practices

1. **Token Budgeting**: Reserve tokens carefully - a typical prompt is 8K-16K tokens
   - System prompt: ~1K
   - DynamicMemory: 2-4K
   - StaticMemory: 4-8K
   - Conversation history: 4-8K
   - User message: 1-2K

2. **Storage Keys**: Use meaningful keys that reflect Collapse
   - User-Collapsed: `user-{userId}`
   - Session-Collapsed: `session-{sessionId}`
   - Task-Collapsed: `task-{taskId}`

3. **Static vs Dynamic**:
   - Use **Static** for content that rarely changes
   - Use **Dynamic** for content that the agent learns/modifies

4. **Plan Granularity**: Create plans with 5-15 steps for best results
   - Too few: Loses tracking value
   - Too many: Overwhelming for agent

5. **Testing**: Use in-memory stores for unit tests, persistent stores for integration tests

6. **Production**: Use Redis/SQL stores for multi-instance deployments

---

For implementation details and architecture, see [ARCHITECTURE.md](./ARCHITECTURE.md).
