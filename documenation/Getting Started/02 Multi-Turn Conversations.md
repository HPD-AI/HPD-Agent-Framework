# Multi-Turn Conversations

To enable Multi-Turn Conversations with Agents we cover `Session` and `Branch` — the core types that hold conversation state — and the persistence options available.

## What are Session and Branch?

HPD-Agent uses a two-level architecture for conversation state:

**Session** — The container for session-level metadata:
- **Metadata** — Custom key-value data attached to the session
- **Session-scoped Middleware State** — Cross-branch state (e.g., permission choices like "Always Allow Bash")
- **Store Reference** — For asset access

**Branch** — A conversation path within a session:
- **Messages** — The conversation history (user messages, assistant responses, tool calls/results)
- **Branch-scoped Middleware State** — Per-conversation state (e.g., plan progress, history cache)
- **Fork Tracking** — Where this branch was forked from (if applicable)

```csharp
public class Session
{
    public string Id { get; }
    public DateTime CreatedAt { get; }
    public DateTime LastActivity { get; }
    public Dictionary<string, object> Metadata { get; }
    public Dictionary<string, string> MiddlewareState { get; }  // Session-scoped
}

public class Branch
{
    public string Id { get; }
    public string SessionId { get; }
    public List<ChatMessage> Messages { get; }
    public Dictionary<string, string> MiddlewareState { get; }  // Branch-scoped
    public string? ForkedFrom { get; }
    public int? ForkedAtMessageIndex { get; }
}
```

**Why the split?** One session can have multiple branches (like ChatGPT's "edit message" feature). All branches share session-level state (permissions, assets) while maintaining independent message histories.

## In-Memory Sessions (No Persistence)

The simplest way to use HPD-Agent is without any persistence. The session and branch live only in memory:

```csharp
var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4o", apiKey)
    .WithTool<MyTools>()
    .Build();

// Create a session + branch
var session = new Session();
var branch = session.CreateBranch();

var userMessages = new[]
{
    "Add 10 and 20",                    // First tool call
    "Now multiply the result by 5"      // References previous result
};

// Run multiple turns in the same session
foreach (var message in userMessages)
{
    await foreach (var evt in agent.RunAsync(message, branch))
    {
        if (evt is TextDeltaEvent textDelta)
            Console.Write(textDelta.Text);
    }
    Console.WriteLine();
}
```

**Limitations:**
- Session is lost if the process crashes or restarts
- Cannot resume conversations across process restarts

---

## Persistence Options

For applications that need to persist conversations, HPD-Agent provides:

| Feature | Purpose | When Saved |
|---------|---------|------------|
| **Session Persistence** | Save conversation history | After turn completes |
| **Crash Recovery** | Resume if process crashes mid-execution | During agent loop (automatic) |

### ISessionStore

Both features use an `ISessionStore` to handle the actual storage:

```csharp
// Built-in implementations
var inMemory = new InMemorySessionStore();  // For testing (data lost on restart)
var fileBased = new JsonSessionStore("./sessions");  // File-based storage
```

You can implement `ISessionStore` for your own backend (SQL, Redis, etc.). See [Storage Layout](#storage-layout-jsonsessionstore) for the file structure used by `JsonSessionStore`.

---

## Session Persistence

Session persistence saves your conversation state after each turn completes successfully.

### What Gets Saved

- **Session**: Metadata, session-scoped middleware state, timestamps
- **Branch**: Messages, branch-scoped middleware state, fork info

### Configuration

```csharp
// Option A: Manual save (you control when to save)
var agent = new AgentBuilder()
    .WithSessionStore(store)
    .Build();

var (session, branch) = await store.LoadOrCreateSessionAndBranchAsync("session-123");
await foreach (var evt in agent.RunAsync("Hello", branch)) { }
await store.SaveSessionAsync(session);
await store.SaveBranchAsync(session.Id, branch);

// Option B: Auto-save after each turn (using sessionId overload)
var agent = new AgentBuilder()
    .WithSessionStore(store, persistAfterTurn: true)
    .Build();

// This overload auto-loads, runs, and auto-saves
await foreach (var evt in agent.RunAsync("Hello", sessionId: "session-123")) { }
```

### Loading Sessions

```csharp
// Load existing session + branch, or create new ones (defaults to "main" branch)
var (session, branch) = await store.LoadOrCreateSessionAndBranchAsync("session-123");

// Or specify a branch explicitly
var (session, branch) = await store.LoadOrCreateSessionAndBranchAsync("session-123", "formal");
```

---

## Crash Recovery (Uncommitted Turns)

When an agent has a session store configured, crash recovery is **automatic**. During execution, an `UncommittedTurn` is saved after each tool batch completes. This captures only the turn delta (messages added during the current turn).

### How It Works

1. Agent starts processing a user message
2. After each tool batch completes → `UncommittedTurn` saved (fire-and-forget)
3. Turn completes successfully → `UncommittedTurn` deleted
4. If process crashes mid-execution → uncommitted turn persists in storage

### Recovery Flow

Recovery is automatic when you call `RunAsync` with a session that has an uncommitted turn:

```csharp
var (session, branch) = await store.LoadOrCreateSessionAndBranchAsync("session-123");

// If an uncommitted turn exists, RunAsync automatically detects it and resumes
await foreach (var evt in agent.RunAsync(Array.Empty<ChatMessage>(), branch)) { }
```

The framework reconstructs the full conversation from `branch.Messages + uncommittedTurn.TurnMessages` and resumes where it left off.

### Configuration

Crash recovery requires no special configuration — just having a session store is enough:

```csharp
var agent = new AgentBuilder()
    .WithSessionStore(store, persistAfterTurn: true)
    .Build();
// Crash recovery is automatically enabled
```

---

## Branching

Branches enable exploring alternative conversation paths from any point:

```csharp
// Fork a branch at message index 3
var sourceBranch = await store.LoadBranchAsync("session-123", "main");
var newBranch = await agent.ForkBranchAsync(
    sourceBranch,
    newBranchId: "experiment",
    fromMessageIndex: 3);

// Run the new branch with a different approach
await foreach (var evt in agent.RunAsync("Try a different approach", newBranch)) { }

// List all branches
var branchIds = await agent.ListBranchesAsync(session.Id);
```

**Fork behavior:**
- Messages up to `fromMessageIndex` are **copied** to the new branch
- Branch-scoped middleware state is **copied** (then diverges independently)
- Session-scoped middleware state is **shared** (permissions apply everywhere)

---

## Storage Layout (JsonSessionStore)

The file-based `JsonSessionStore` uses this directory structure:

```
{basePath}/
└── sessions/
    └── {sessionId}/
        ├── session.json              # Session metadata + session-scoped state
        ├── branches/
        │   └── {branchId}/
        │       └── branch.json       # Messages + branch-scoped state
        ├── uncommitted.json          # UncommittedTurn (crash recovery, temporary)
        └── assets/                   # Binary content (uploaded files)
```

Other `ISessionStore` implementations (e.g., SQL, Redis, in-memory) will have different storage structures.

---

## API Reference

### RunAsync Signatures

The agent has a consolidated API with optional parameters:

```csharp
// Core signature (branch is optional)
IAsyncEnumerable<AgentEvent> RunAsync(
    IEnumerable<ChatMessage> messages,
    Branch? branch = null,
    AgentRunConfig? options = null,
    CancellationToken cancellationToken = default)

// String convenience (wraps message as ChatMessage)
IAsyncEnumerable<AgentEvent> RunAsync(
    string userMessage,
    Branch? branch = null,
    AgentRunConfig? options = null,
    CancellationToken cancellationToken = default)

// SessionId convenience (auto-loads/saves session + branch)
IAsyncEnumerable<AgentEvent> RunAsync(
    string userMessage,
    string sessionId,
    string branchId = "main",
    AgentRunConfig? options = null,
    CancellationToken cancellationToken = default)
```

**Common usage patterns:**

```csharp
// Stateless (no session)
await foreach (var evt in agent.RunAsync("Hello")) { }

// With branch (session is implicit via branch.Session)
await foreach (var evt in agent.RunAsync("Hello", branch)) { }

// With options
var options = new AgentRunConfig { Chat = new ChatRunConfig { Temperature = 0.7f } };
await foreach (var evt in agent.RunAsync("Hello", branch, options)) { }

// Auto-load session by ID
await foreach (var evt in agent.RunAsync("Hello", sessionId: "session-123")) { }
```

### Agent Methods

| Method | Description |
|--------|-------------|
| `LoadSessionAndBranchAsync(sessionId, branchId)` | Load session + branch from store |
| `SaveSessionAndBranchAsync(session, branch)` | Save session + branch to store |
| `ForkBranchAsync(sourceBranch, newBranchId, fromMessageIndex)` | Fork a branch at a message index |
| `ListBranchesAsync(sessionId)` | List branch IDs for a session |
| `DeleteBranchAsync(branch)` | Delete a branch |

### AgentBuilder Extensions

| Method | Description |
|--------|-------------|
| `WithSessionStore(store)` | Configure store with manual save |
| `WithSessionStore(store, persistAfterTurn)` | Configure store with auto-save option |
| `WithSessionStore(path, persistAfterTurn)` | File-based store with auto-save option |

### Types

| Type | Description |
|------|-------------|
| `Session` | Session metadata (ID, timestamps, session-scoped middleware state) |
| `Branch` | Conversation path (messages, branch-scoped middleware state, fork info) |
| `UncommittedTurn` | Crash recovery buffer (turn delta, saved during execution) |

### ISessionStore Methods

| Method | Description |
|--------|-------------|
| `LoadSessionAsync(sessionId)` | Load session metadata |
| `SaveSessionAsync(session)` | Save session metadata |
| `ListSessionIdsAsync()` | List all session IDs |
| `DeleteSessionAsync(sessionId)` | Delete session + all branches + assets |
| `LoadBranchAsync(sessionId, branchId)` | Load a branch |
| `SaveBranchAsync(sessionId, branch)` | Save a branch |
| `ListBranchIdsAsync(sessionId)` | List branch IDs for a session |
| `DeleteBranchAsync(sessionId, branchId)` | Delete a branch |
| `LoadUncommittedTurnAsync(sessionId)` | Load crash recovery data |
| `SaveUncommittedTurnAsync(turn)` | Save crash recovery data |
| `DeleteUncommittedTurnAsync(sessionId)` | Clear crash recovery data |
| `GetAssetStore(sessionId)` | Get asset store for binary content |
| `DeleteInactiveSessionsAsync(threshold, dryRun)` | Cleanup inactive sessions |

---

## Best Practices

1. **Start stateless** - Use in-memory sessions for prototyping and simple use cases

2. **Use `persistAfterTurn: true`** for most applications - it's the simplest way to persist conversations

3. **Crash recovery is automatic** - Just having a session store configured enables it; no extra configuration needed

4. **Use branching for exploration** - When users want to try a different approach, fork instead of losing the original conversation

5. **Cleanup inactive sessions** - Use `DeleteInactiveSessionsAsync` on a schedule to manage storage

---
