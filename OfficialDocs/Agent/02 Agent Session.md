# Agent Session

This guide covers `AgentSession` - the core type that holds conversation state - and the persistence options available.

## What is AgentSession?

`AgentSession` is the container for all conversation state:

- **Messages** - The conversation history (user messages, assistant responses, tool calls/results)
- **Metadata** - Custom key-value data attached to the session
- **Middleware Persistent State** - Cross-turn state for middleware (e.g., history reduction state)
- **ExecutionState** - Runtime state during agent execution (only present while running)

```csharp
public class AgentSession
{
    public string Id { get; }
    public IReadOnlyList<ChatMessage> Messages { get; }
    public DateTime CreatedAt { get; }
    public DateTime LastActivity { get; }

    // Only set during execution (null after turn completes)
    public AgentLoopState? ExecutionState { get; }
}
```

## Stateless Usage (No Persistence)

The simplest way to use HPD-Agent is without any persistence. The session lives only in memory:

```csharp
var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4o", apiKey)
    .WithPlugin<MyTools>()
    .Build();

// Create a new in-memory session
var session = new AgentSession();

// Run multiple turns in the same session
// Signature: RunAsync(message, session?, options?, cancellationToken)
await foreach (var evt in agent.RunAsync("Hello!", session)) { }
await foreach (var evt in agent.RunAsync("What did I just say?", session)) { }

// Session is lost when the process ends
```

**When to use stateless:**
- Simple scripts or one-off tasks
- Testing and prototyping
- Serverless functions where each request is independent
- When you manage state yourself (e.g., storing messages in your own database)

**Limitations:**
- Session is lost if the process crashes or restarts
- Cannot resume conversations across process restarts
- No crash recovery during long-running agent executions

---

## Persistence Options

For applications that need to persist conversations, HPD-Agent provides two independent features:

| Feature | Purpose | When Saved | Size |
|---------|---------|------------|------|
| **Session Persistence** | Save conversation history | After turn completes | ~20KB |
| **Durable Execution** | Crash recovery during execution | During agent loop | ~100KB |

These features are **independent** - you can use either, both, or neither.

### ISessionStore

Both features use an `ISessionStore` to handle the actual storage:

```csharp
// Built-in implementations
var inMemory = new InMemorySessionStore();  // For testing (data lost on restart)
var fileBased = new JsonSessionStore("./sessions");  // File-based storage
```

You can implement `ISessionStore` for your own backend (SQL, Redis, etc.). See [Storage Layout](#storage-layout-jsonsessionstore) for the file structure used by `JsonSessionStore`.

---

## Session Persistence (Snapshots)

Session persistence saves your conversation state after each turn completes successfully. This is the "normal" persistence most applications need.

### What Gets Saved

A `SessionSnapshot` contains:
- Messages (conversation history)
- Session metadata
- Middleware persistent state (e.g., history reduction state)
- Timestamps (CreatedAt, LastActivity)

### Configuration

```csharp
// Option A: Manual save (you control when to save)
var agent = new AgentBuilder()
    .WithSessionStore(store)
    .Build();

var session = await agent.LoadSessionAsync("session-123");
await foreach (var evt in agent.RunAsync("Hello", session)) { }
await agent.SaveSessionAsync(session);  // Your responsibility

// Option B: Auto-save after each turn (using sessionId overload)
var agent = new AgentBuilder()
    .WithSessionStore(store, persistAfterTurn: true)
    .Build();

// This overload auto-loads the session, runs, and auto-saves
await foreach (var evt in agent.RunAsync("Hello", sessionId: "session-123")) { }
// Automatically saved after turn completes
```

### Loading Sessions

```csharp
// Load existing session or create new one
var session = await agent.LoadSessionAsync("session-123");
```

---

## Durable Execution (Checkpoints)

Durable execution saves `ExecutionCheckpoint`s **during** agent execution for crash recovery. Use this for long-running agents where you can't afford to lose progress if the process crashes mid-execution.

### What Gets Saved

An `ExecutionCheckpoint` contains:
- `ExecutionState` (iteration count, middleware runtime state)
- Messages (inside `ExecutionState.CurrentMessages`)
- Checkpoint metadata (step, source, parent checkpoint)

### Configuration

```csharp
var agent = new AgentBuilder()
    .WithSessionStore(store)
    .WithDurableExecution(CheckpointFrequency.PerIteration, RetentionPolicy.LastN(5))
    .Build();
```

### Checkpoint Frequency Options

| Frequency | Description | Use Case |
|-----------|-------------|----------|
| `PerTurn` | Checkpoint after each message turn | Recommended for most use cases |
| `PerIteration` | Checkpoint after each LLM call | Long-running agents (>10 iterations) |
| `Manual` | Only when explicitly requested | Full control over checkpoint timing |

### Manual Checkpointing

When using `CheckpointFrequency.Manual`, you control exactly when checkpoints are created:

```csharp
var agent = new AgentBuilder()
    .WithSessionStore(store)
    .WithDurableExecution(CheckpointFrequency.Manual, RetentionPolicy.LastN(5))
    .Build();

var session = await agent.LoadSessionAsync("session-123");

await foreach (var evt in agent.RunAsync("Complex task", session))
{
    // Save checkpoint at strategic points (e.g., after expensive operations)
    if (evt is FunctionResultEvent funcResult && funcResult.FunctionName == "expensive_operation")
    {
        var checkpointId = await agent.SaveCheckpointAsync(session);
        Console.WriteLine($"Checkpoint saved: {checkpointId}");
    }
}
```

### Retention Policies

| Policy | Description |
|--------|-------------|
| `RetentionPolicy.LatestOnly` | Keep only the most recent checkpoint |
| `RetentionPolicy.LastN(n)` | Keep the last N checkpoints |
| `RetentionPolicy.FullHistory` | Keep all checkpoints (time-travel debugging) |
| `RetentionPolicy.TimeBased(duration)` | Keep checkpoints from the last duration |

### Pending Writes (Partial Failure Recovery)

When an agent executes multiple tool calls in parallel, some may succeed before a crash occurs. **Pending writes** save successful tool results incrementally, so you don't re-execute them on recovery.

```csharp
var agent = new AgentBuilder()
    .WithSessionStore(store)
    .WithDurableExecution(config =>
    {
        config.Frequency = CheckpointFrequency.PerIteration;
        config.EnablePendingWrites = true;  // Enable partial recovery
    })
    .Build();
```

**How it works:**
1. Agent calls 3 tools in parallel
2. Tool A completes → result saved as pending write
3. Tool B completes → result saved as pending write
4. Tool C fails / process crashes
5. On recovery: Tools A and B results are restored, only Tool C re-executes

**When to use:**
- Expensive tool calls (API calls, database operations)
- Parallel tool execution where partial progress matters
- Long-running iterations with multiple steps

---

## Crash Recovery

**Important:** Checkpoint recovery is always **explicit**. There is no automatic checkpoint loading because checkpoints are tied to specific message states and may be stale if turns ran without DurableExecution enabled.

### Recovery Flow

```csharp
// Step 1: List available checkpoints
var checkpoints = await agent.GetCheckpointManifestAsync("session-123");

foreach (var entry in checkpoints)
{
    Console.WriteLine($"Checkpoint: {entry.ExecutionCheckpointId}");
    Console.WriteLine($"  Step: {entry.Step}");
    Console.WriteLine($"  Messages: {entry.MessageIndex}");
    Console.WriteLine($"  Created: {entry.CreatedAt}");
}

// Step 2: User decides which checkpoint to use (if any)
if (checkpoints.Count > 0 && UserWantsToRecover())
{
    var selectedId = checkpoints[0].ExecutionCheckpointId;

    // Step 3: Load the specific checkpoint
    var session = await agent.LoadSessionAtCheckpointAsync("session-123", selectedId);

    // session.ExecutionState is populated - resume with empty messages
    await foreach (var evt in agent.RunAsync(Array.Empty<ChatMessage>(), session)) { }
}
```

### Why No Automatic Recovery?

Automatic recovery is dangerous because:

1. **Checkpoints are tied to message counts** - A checkpoint at "5 messages" is invalid if the session now has 7 messages
2. **Stale checkpoints** - If turns ran without DurableExecution, the checkpoint doesn't reflect current state
3. **Silent wrong behavior** - Auto-loading could resume from the wrong point without warning
4. **User should decide** - Only the user knows if recovery is appropriate for their situation

---

## Using Both Features Together

For maximum durability, use both features:

```csharp
var agent = new AgentBuilder()
    .WithSessionStore(store, persistAfterTurn: true)      // Save snapshot after turn
    .WithDurableExecution(CheckpointFrequency.PerIteration) // Checkpoint during execution
    .Build();
```

**What happens:**
- During execution: `ExecutionCheckpoint` saved after each iteration (~100KB)
- After turn completes: `SessionSnapshot` saved (~20KB)
- Checkpoints cleaned up after successful completion

---

## Cleanup Methods

`ISessionStore` provides methods for managing storage in production:

```csharp
// Keep only the 5 most recent checkpoints for a session
await store.PruneCheckpointsAsync("session-123", keepLatest: 5);

// Delete all checkpoints/snapshots older than 30 days
await store.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30));

// Delete sessions inactive for more than 90 days
// Use dryRun: true to preview what would be deleted
var count = await store.DeleteInactiveSessionsAsync(
    TimeSpan.FromDays(90),
    dryRun: false);
Console.WriteLine($"Deleted {count} inactive sessions");

// Delete specific checkpoints by ID
await store.DeleteCheckpointsAsync("session-123", new[] { "chk-1", "chk-2" });
```

**Best practices:**
- Run cleanup on a schedule (e.g., daily cron job)
- Use `dryRun: true` first to verify what will be deleted
- Set retention policies appropriate for your compliance requirements

---

## Storage Layout (JsonSessionStore)

The file-based `JsonSessionStore` uses this directory structure:

```
{basePath}/
├── sessions/
│   └── {sessionId}/
│       ├── manifest.json                    # Index of snapshots and checkpoints
│       ├── snapshots/
│       │   └── {snapshotId}.json            # SessionSnapshot (~20KB)
│       └── checkpoints/
│           └── {checkpointId}.json          # ExecutionCheckpoint (~100KB)
└── pending/
    └── {sessionId}_{checkpointId}.json      # PendingWrites (temporary)
```

**`manifest.json`** tracks all snapshots and checkpoints for a session:
- List of snapshots (newest first) with IDs, timestamps, message counts
- List of checkpoints (newest first) with IDs, timestamps, step numbers

Other `ISessionStore` implementations (e.g., SQL, Redis, in-memory) will have different storage structures.

---

## API Reference

### RunAsync Signatures

The agent has a consolidated API with optional parameters:

```csharp
// Core signature (all parameters after messages are optional)
IAsyncEnumerable<AgentEvent> RunAsync(
    IEnumerable<ChatMessage> messages,
    AgentSession? session = null,
    AgentRunOptions? options = null,
    CancellationToken cancellationToken = default)

// String convenience (wraps message as ChatMessage)
IAsyncEnumerable<AgentEvent> RunAsync(
    string userMessage,
    AgentSession? session = null,
    AgentRunOptions? options = null,
    CancellationToken cancellationToken = default)

// SessionId convenience (auto-loads/saves session)
IAsyncEnumerable<AgentEvent> RunAsync(
    string userMessage,
    string sessionId,
    AgentRunOptions? options = null,
    CancellationToken cancellationToken = default)
```

**Common usage patterns:**

```csharp
// Stateless (no session)
await foreach (var evt in agent.RunAsync("Hello")) { }

// With session
await foreach (var evt in agent.RunAsync("Hello", session)) { }

// With options
var options = new AgentRunOptions { Chat = new ChatRunOptions { Temperature = 0.7f } };
await foreach (var evt in agent.RunAsync("Hello", session, options)) { }

// Auto-load session by ID
await foreach (var evt in agent.RunAsync("Hello", sessionId: "session-123")) { }
```

### Agent Methods

| Method | Description |
|--------|-------------|
| `LoadSessionAsync(sessionId)` | Load session from snapshot (or create new) |
| `SaveSessionAsync(session)` | Save session snapshot manually |
| `GetCheckpointManifestAsync(sessionId)` | List available checkpoints |
| `LoadSessionAtCheckpointAsync(sessionId, checkpointId)` | Load specific checkpoint |
| `SaveCheckpointAsync(session)` | Manually save an execution checkpoint (for Manual frequency) |

### AgentBuilder Extensions

| Method | Description |
|--------|-------------|
| `WithSessionStore(store)` | Configure store with manual save |
| `WithSessionStore(store, persistAfterTurn)` | Configure store with auto-save option |
| `WithDurableExecution(frequency, retention)` | Enable crash recovery checkpointing |

### Types

| Type | Description |
|------|-------------|
| `AgentSession` | Container for conversation state (messages, metadata, execution state) |
| `SessionSnapshot` | Lightweight save (~20KB) - messages + metadata |
| `ExecutionCheckpoint` | Full checkpoint (~100KB) - ExecutionState only |
| `CheckpointManifestEntry` | Metadata about a checkpoint (ID, step, timestamp) |

### ISessionStore Methods

| Method | Description |
|--------|-------------|
| `LoadSessionAsync(sessionId)` | Load latest session snapshot |
| `SaveSessionAsync(session)` | Save session snapshot |
| `LoadCheckpointAsync(sessionId)` | Load latest execution checkpoint |
| `SaveCheckpointAsync(checkpoint, metadata)` | Save execution checkpoint |
| `LoadCheckpointAtAsync(sessionId, checkpointId)` | Load specific checkpoint by ID |
| `GetCheckpointManifestAsync(sessionId)` | List all checkpoints for a session |
| `PruneCheckpointsAsync(sessionId, keepLatest)` | Keep N most recent checkpoints |
| `DeleteOlderThanAsync(cutoff)` | Delete checkpoints/snapshots older than date |
| `DeleteInactiveSessionsAsync(threshold, dryRun)` | Delete sessions inactive for threshold duration |
| `DeleteCheckpointsAsync(sessionId, checkpointIds)` | Delete specific checkpoints |
| `SavePendingWritesAsync(sessionId, checkpointId, writes)` | Save pending tool results |
| `LoadPendingWritesAsync(sessionId, checkpointId)` | Load pending tool results |

---

## Best Practices

1. **Start stateless** - Use in-memory sessions for prototyping and simple use cases

2. **Use `persistAfterTurn: true`** for most applications - it's the simplest way to persist conversations

3. **Add DurableExecution** only if:
   - Your agents run for many iterations (>10)
   - You can't afford to lose progress on crash
   - You need time-travel debugging

4. **Always use explicit checkpoint recovery** - never assume a checkpoint is valid

5. **Choose appropriate retention** - `LastN(3)` is usually sufficient; use `FullHistory` only for debugging

6. **Handle recovery in your UI** - show users available checkpoints and let them choose

---
