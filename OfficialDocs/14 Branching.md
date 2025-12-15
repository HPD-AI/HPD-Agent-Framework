# Conversation Branching

**Edit any message, explore different paths, and never lose context.**

Conversation branching lets users go back, edit a previous message, and get a different response—without losing the original conversation. Think of it like "undo" that keeps the history.

## Table of Contents
- [What is Branching?](#what-is-branching)
- [When to Use Branching](#when-to-use-branching)
- [Quick Setup](#quick-setup)
- [Basic Usage](#basic-usage)
- [Client Integration](#client-integration)
- [Client Library Utilities](#client-library-utilities)
- [API Reference](#api-reference)
- [Configuration Options](#configuration-options)

---

## What is Branching?

Branching creates alternative conversation paths from any point in the history:

```
Original conversation:
User: "Write a poem about cats"
Agent: "Soft paws padding through the night..."
User: "Make it rhyme"
Agent: "Cats so fine, they always shine..."

After editing the first message:
                                    ┌─→ User: "Write a poem about dogs"
User: "Write a poem about cats" ───┤   Agent: "Loyal friend with wagging tail..."
Agent: "Soft paws..."              │
User: "Make it rhyme"              └─→ (original path preserved)
Agent: "Cats so fine..."
```

**Key concepts:**
- **Fork**: Create a new branch from any checkpoint
- **Branch**: An alternative path through the conversation
- **Checkpoint**: A saved state at a specific point (created automatically)
- **Switch**: Move between branches to see different paths

---

## When to Use Branching

| Scenario | Without Branching | With Branching |
|----------|------------------|----------------|
| User misspelled something | Start over | Edit and continue |
| User wants to try different approach | Copy-paste, new chat | Fork and explore |
| User wants to compare responses | Manual screenshots | Switch between branches |
| User regrets a follow-up question | Can't undo | Fork from before that message |

**Enable branching when:**
- Users interact with long conversations
- Exploring different approaches is valuable
- Preserving conversation history matters
- Building ChatGPT-like experiences

---

## Quick Setup

### Backend (C#)

Branching requires **checkpoints** to exist. You can either:
1. Use **Durable Execution** (auto-creates checkpoints) - recommended
2. Create checkpoints **manually** when needed

#### Option 1: With Durable Execution (Recommended)

The simplest setup - checkpoints are created automatically:

```csharp
using HPD.Agent.Checkpointing.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCheckpointing(opts =>
{
    opts.Store = new JsonConversationThreadStore("./checkpoints");
    
    // Durable execution creates checkpoints automatically
    opts.DurableExecution.Enabled = true;
    opts.DurableExecution.Frequency = CheckpointFrequency.PerTurn;
    opts.DurableExecution.Retention = RetentionPolicy.FullHistory;
    
    // Branching uses those checkpoints for fork/switch/delete
    opts.Branching.Enabled = true;
});

var app = builder.Build();
```

#### Option 2: Branching Only (Manual Checkpoints)

If you don't want auto-checkpointing, enable branching alone:

```csharp
builder.Services.AddCheckpointing(opts =>
{
    opts.Store = new JsonConversationThreadStore("./checkpoints");
    
    // No auto-checkpointing
    opts.DurableExecution.Enabled = false;
    
    // Branching still works - but you must save checkpoints manually
    opts.Branching.Enabled = true;
});
```

With this setup, call `store.SaveThreadAsync(thread)` whenever you want to create a checkpoint that users can fork from.

### Understanding the Two Services

| Service | Purpose | Creates Checkpoints? |
|---------|---------|---------------------|
| **Durable Execution** | Auto-save state for crash recovery | ✅ Yes, automatically |
| **Branching** | Fork, switch, delete conversation branches | ❌ No, uses existing checkpoints |

**Key insight:** Branching *consumes* checkpoints that Durable Execution *produces*. They work together but are independent features.

### Configuration Summary

| Option | What It Does |
|--------|-------------|
| `opts.Branching.Enabled` | Enable fork/switch/delete operations |
| `opts.DurableExecution.Enabled` | Auto-create checkpoints |
| `opts.DurableExecution.Retention` | How many checkpoints to keep |

For full branching (edit any message), use `RetentionPolicy.FullHistory` with Durable Execution.

---

## Basic Usage

### Creating a Fork

When a user edits a message, fork from the checkpoint before that message:

```csharp
// Inject the services
var branchingService = app.Services.GetRequiredService<Branching>();

// Fork from a specific checkpoint
var (thread, evt) = await branchingService.ForkFromCheckpointAsync(
    threadId: conversationId,
    checkpointId: checkpointBeforeEdit,
    branchName: "edit-1"  // Optional, auto-generated if not provided
);

// evt contains: BranchName, CheckpointId, ParentCheckpointId, ForkMessageIndex
Console.WriteLine($"Created branch '{evt.BranchName}' at message {evt.ForkMessageIndex}");
```

### Switching Branches

Let users explore different conversation paths:

```csharp
var result = await branchingService.SwitchBranchAsync(
    threadId: conversationId,
    branchName: "edit-1"
);

if (result.HasValue)
{
    var (thread, evt) = result.Value;
    // thread now contains messages from the selected branch
    Console.WriteLine($"Switched to '{evt.NewBranch}' with {thread.MessageCount} messages");
}
```

### Getting Branch Tree

Visualize all branches in a conversation:

```csharp
var tree = await branchingService.GetBranchTreeAsync(conversationId);

// tree.NamedBranches: Dictionary of branch name → metadata
// tree.Nodes: Dictionary of checkpoint ID → node info
// tree.ActiveBranch: Currently active branch name
```

### Deleting a Branch

Clean up unwanted branches:

```csharp
var evt = await branchingService.DeleteBranchAsync(
    threadId: conversationId,
    branchName: "edit-1",
    pruneOrphanedCheckpoints: true  // Clean up unused checkpoints
);

Console.WriteLine($"Deleted '{evt.BranchName}', pruned {evt.CheckpointsPruned} checkpoints");
```

---

## Client Integration

### TypeScript Client

Install the client library:

```bash
npm install @hpd/hpd-agent-client
```

### Selecting a Checkpoint for Editing

When a user edits message #3, you need to find the right checkpoint to fork from:

```typescript
import { selectCheckpointForEdit, type CheckpointData } from '@hpd/hpd-agent-client';

// Fetch checkpoints from your API (you define the endpoint)
const checkpoints: CheckpointData[] = await yourApi.getCheckpoints(conversationId);

// Find the right checkpoint to fork from
const checkpoint = selectCheckpointForEdit(checkpoints, messageIndex);

if (checkpoint) {
    // Fork from this checkpoint (call your backend)
    await yourApi.createFork(conversationId, checkpoint.checkpointId, `edit-${Date.now()}`);
}
```

### Full Edit Flow Example

```typescript
async function handleMessageEdit(index: number, newContent: string) {
    // 1. Get checkpoint history from your backend
    const checkpoints = await yourApi.getCheckpoints(conversationId);
    
    // 2. Find checkpoint to fork from (uses client library utility)
    const checkpoint = selectCheckpointForEdit(checkpoints, index);
    if (!checkpoint) {
        console.error('No suitable checkpoint found');
        return;
    }
    
    // 3. Create a fork (call your backend which uses branchingService.ForkFromCheckpointAsync)
    await yourApi.createFork(conversationId, checkpoint.checkpointId);
    
    // 4. Reload messages (now at fork point)
    const messages = await yourApi.getMessages(conversationId);
    
    // 5. Send the edited message
    await yourApi.sendMessage(conversationId, newContent);
}
```

---

## Client Library Utilities

The `@hpd/hpd-agent-client` package provides utilities for common branching operations:

### `selectCheckpointForEdit(checkpoints, messageIndex)`

Find the right checkpoint when editing a message:

```typescript
import { selectCheckpointForEdit } from '@hpd/hpd-agent-client';

// Returns checkpoint with highest messageIndex <= target
const checkpoint = selectCheckpointForEdit(checkpoints, 3);
```

### `getCheckpointVariantsAtMessage(checkpoints, messageIndex)`

Get all variants at a specific message (for "1 of 3" navigation):

```typescript
import { getCheckpointVariantsAtMessage } from '@hpd/hpd-agent-client';

const variants = getCheckpointVariantsAtMessage(checkpoints, 2);
// Returns: [{ checkpointId: "...", messageIndex: 2, branchName: "main" }, ...]
```

### `getLatestCheckpoint(checkpoints)`

Find the most recent checkpoint:

```typescript
import { getLatestCheckpoint } from '@hpd/hpd-agent-client';

const latest = getLatestCheckpoint(checkpoints);
```

### `getRootCheckpoint(checkpoints)`

Get the initial conversation state (messageIndex = -1):

```typescript
import { getRootCheckpoint } from '@hpd/hpd-agent-client';

const root = getRootCheckpoint(checkpoints);
```

### `buildCheckpointTree(checkpoints)`

Build a tree structure for visualization:

```typescript
import { buildCheckpointTree } from '@hpd/hpd-agent-client';

const tree = buildCheckpointTree(checkpoints);
// tree.checkpoint: Root checkpoint data
// tree.children: Child nodes
// tree.parent: null (root has no parent)
```

---

## API Reference

### Branching Service

The `Branching` class provides all branching operations:

```csharp
public class Branching
{
    // Fork from a checkpoint to create a new branch
    Task<(ConversationThread, BranchCreatedEvent)> ForkFromCheckpointAsync(
        string threadId,
        string checkpointId,
        string? branchName = null,
        CancellationToken ct = default);
    
    // Switch to an existing branch
    Task<(ConversationThread, BranchSwitchedEvent)?> SwitchBranchAsync(
        string threadId,
        string branchName,
        CancellationToken ct = default);
    
    // Delete a branch and optionally prune orphaned checkpoints
    Task<BranchDeletedEvent> DeleteBranchAsync(
        string threadId,
        string branchName,
        bool pruneOrphanedCheckpoints = true,
        CancellationToken ct = default);
    
    // Rename a branch
    Task<BranchRenamedEvent> RenameBranchAsync(
        string threadId,
        string oldName,
        string newName,
        CancellationToken ct = default);
    
    // Get the full branch tree structure
    Task<BranchTree> GetBranchTreeAsync(
        string threadId,
        CancellationToken ct = default);
    
    // Get all checkpoints for a thread
    Task<IReadOnlyList<CheckpointData>> GetCheckpointsAsync(
        string threadId,
        CancellationToken ct = default);
    
    // Get variants at a specific message index
    Task<IReadOnlyList<CheckpointData>> GetVariantsAtMessageAsync(
        string threadId,
        int messageIndex,
        CancellationToken ct = default);
}
```

### Event Types

Operations return event objects with details about what happened:

```csharp
// Returned by ForkFromCheckpointAsync
public record BranchCreatedEvent(
    string BranchName,
    string CheckpointId,
    string ParentCheckpointId,
    int ForkMessageIndex);

// Returned by SwitchBranchAsync
public record BranchSwitchedEvent(
    string OldBranch,
    string NewBranch,
    string CheckpointId);

// Returned by DeleteBranchAsync
public record BranchDeletedEvent(
    string BranchName,
    int CheckpointsPruned);

// Returned by RenameBranchAsync
public record BranchRenamedEvent(
    string OldName,
    string NewName);
```

### BranchTree Structure

```csharp
public class BranchTree
{
    public string ThreadId { get; }
    public string RootCheckpointId { get; }
    public string? ActiveBranch { get; }
    
    // All checkpoints as tree nodes
    public IReadOnlyDictionary<string, BranchTreeNode> Nodes { get; }
    
    // Named branches with their metadata
    public IReadOnlyDictionary<string, BranchInfo> NamedBranches { get; }
}

public class BranchTreeNode
{
    public string CheckpointId { get; }
    public string? ParentCheckpointId { get; }
    public int MessageCount { get; }
    public IReadOnlyList<string> ChildCheckpointIds { get; }
    public string? BranchName { get; }
}

public class BranchInfo
{
    public string Name { get; }
    public string HeadCheckpointId { get; }
    public DateTime CreatedAt { get; }
}
```

---

## Configuration Options

### Branching Configuration

Branching itself has minimal configuration:

```csharp
opts.Branching.Enabled = true;  // That's it!
```

Everything else is about **how checkpoints are created**, which is controlled by Durable Execution or manual saves.

### Checkpoints and Branching

Branching operates on whatever checkpoints exist. How you get those checkpoints is up to you:

| Approach | Pros | Cons |
|----------|------|------|
| **Durable Execution** | Automatic, no extra code | Uses storage for every turn |
| **Manual Saves** | Full control, save only when needed | More code to write |

### With Durable Execution

If using Durable Execution to auto-create checkpoints, these settings affect what branching can do:

```csharp
// Keep all checkpoints → can fork from ANY message
opts.DurableExecution.Retention = RetentionPolicy.FullHistory;

// Keep only latest → can only fork from CURRENT state
opts.DurableExecution.Retention = RetentionPolicy.LatestOnly;

// Keep last 10 → can fork from last 10 turns
opts.DurableExecution.Retention = RetentionPolicy.LastN(10);
```

> **Note:** For details on Durable Execution (crash recovery, checkpoint frequency, pending writes), see the Durable Execution documentation.

### Storage Backends

Both Branching and Durable Execution share the same store:

```csharp
// JSON files (good for development)
opts.Store = new JsonConversationThreadStore("./checkpoints");

// In-memory (good for testing)
opts.Store = new InMemoryConversationThreadStore();

// Custom storage (implement ICheckpointStore)
opts.Store = new MyCustomStore();
```

---

## Best Practices

### 1. Enable Both for Full Functionality

For the best user experience, enable both services:

```csharp
opts.DurableExecution.Enabled = true;
opts.DurableExecution.Retention = RetentionPolicy.FullHistory;
opts.Branching.Enabled = true;
```

### 2. Auto-Generate Branch Names

Let the system generate unique names if users don't provide them:

```csharp
// branchName is optional - will be "branch-abc12345" if omitted
await branchingService.ForkFromCheckpointAsync(threadId, checkpointId);
```

### 3. Use Client Library Utilities

Don't duplicate checkpoint selection logic—use the utilities:

```typescript
// ✅ Good
import { selectCheckpointForEdit } from '@hpd/hpd-agent-client';
const cp = selectCheckpointForEdit(checkpoints, index);

// ❌ Bad - duplicating logic
const sorted = checkpoints.sort((a, b) => b.messageIndex - a.messageIndex);
const cp = sorted.find(c => c.messageIndex <= index);
```

### 4. Handle Edge Cases

Always check for null results:

```typescript
const checkpoint = selectCheckpointForEdit(checkpoints, index);
if (!checkpoint) {
    // No suitable checkpoint - conversation may be empty
    // Consider creating a new conversation
}
```

---

## Troubleshooting

### "No checkpoints found"

**Cause:** Conversation was created but no checkpoints saved yet.

**Fix:** Ensure `SaveThreadAsync` is called when creating conversations:

```csharp
var thread = new ConversationThread();
await store.SaveThreadAsync(thread);  // Creates root checkpoint
```

### "Checkpoint not found"

**Cause:** Checkpoint was pruned by retention policy.

**Fix:** Use `RetentionPolicy.FullHistory` or ensure checkpoint exists before forking.

### Messages disappear after edit

**Cause:** Wrong checkpoint selected for fork.

**Fix:** Use `selectCheckpointForEdit()` utility which handles edge cases correctly.

---

## Summary

### Branching Operations

| What You Want | How to Do It |
|---------------|--------------|
| Enable branching | `opts.Branching.Enabled = true` |
| Fork from a point | `ForkFromCheckpointAsync(threadId, checkpointId)` |
| Switch branches | `SwitchBranchAsync(threadId, branchName)` |
| Delete a branch | `DeleteBranchAsync(threadId, branchName)` |
| Visualize tree | `GetBranchTreeAsync(threadId)` |

### Client Utilities

| What You Want | How to Do It |
|---------------|--------------|
| Find fork point | `selectCheckpointForEdit(checkpoints, index)` |
| Show "1 of 3" UI | `getCheckpointVariantsAtMessage(checkpoints, index)` |
| Get latest state | `getLatestCheckpoint(checkpoints)` |
| Build tree view | `buildCheckpointTree(checkpoints)` |

### Quick Reference

```
Branching = Fork, switch, delete conversation branches
           (uses existing checkpoints)

Durable Execution = Auto-create checkpoints for crash recovery  
                   (separate feature, see Durable Execution docs)

Together = Users can edit any message and explore alternatives
```

Branching gives users the power to explore, revise, and compare—without losing anything.
