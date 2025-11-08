# ConversationThread API Reference

**Official Documentation - HPD-Agent Framework**  
**Version:** 1.0  
**Last Updated:** November 7, 2025

---

## Table of Contents

1. [Overview](#overview)
2. [Quick Start](#quick-start)
3. [Architecture](#architecture)
4. [API Reference](#api-reference)
5. [Usage Patterns](#usage-patterns)
6. [Native AOT Support](#native-aot-support)
7. [Advanced Topics](#advanced-topics)
8. [Migration Guide](#migration-guide)

---

## Overview

`ConversationThread` is the core class for managing conversation state in the HPD-Agent framework. It handles message history, metadata, timestamps, and integrates seamlessly with the Microsoft Agent Framework.

### Key Features

- ✅ **Thread-safe message storage** - Concurrent reads/writes without corruption
- ✅ **Pluggable storage backends** - In-memory, database, or custom implementations
- ✅ **Push-based API** - Prevents stale data bugs through explicit state updates
- ✅ **Native AOT compatible** - Factory-based deserialization for optimal performance
- ✅ **Cache-aware history reduction** - Intelligent conversation summarization
- ✅ **Project integration** - Automatic document context sharing
- ✅ **Service thread sync** - Hybrid scenarios with OpenAI Assistants, Azure AI

### When to Use

Use `ConversationThread` when you need:
- Multi-turn conversations with message history
- Persistent conversation state across sessions
- Project-scoped document context
- Conversation metadata (display names, tags, etc.)
- One agent serving multiple concurrent conversations

---

## Quick Start

### Basic Usage

```csharp
using HPD_Agent;
using Microsoft.Extensions.AI;

// 1. Create a thread
var thread = new ConversationThread();
thread.DisplayName = "Weather Chat";

// 2. Add messages (push pattern)
await thread.AddMessageAsync(
    new ChatMessage(ChatRole.User, "What's the weather in Seattle?")
);

// 3. Use with agent
var agent = new Agent(...);
var response = await agent.RunAsync("What's the weather?", thread);

// 4. Access response
Console.WriteLine(response.Text);

// 5. Check message count
var count = await thread.GetMessageCountAsync();
Console.WriteLine($"Total messages: {count}");
```

### With Project Context

```csharp
// Create project with documents
var project = new Project("Financial Analysis");
await project.DocumentManager.AddDocumentAsync("balance_sheet.pdf");

// Associate thread with project
var thread = new ConversationThread();
thread.SetProject(project);

// Thread automatically has access to project documents
await agent.RunAsync("Analyze the balance sheet", thread);
```

### Serialization/Persistence

```csharp
// At application startup (required for deserialization)
ConversationThread.RegisterStoreFactory(
    new InMemoryConversationMessageStoreFactory()
);

// Serialize to disk
var snapshot = thread.Serialize();
await File.WriteAllTextAsync("thread.json", snapshot.GetRawText());

// Deserialize from disk
var json = await File.ReadAllTextAsync("thread.json");
var restored = ConversationThread.Deserialize(
    JsonSerializer.Deserialize<ConversationThreadSnapshot>(json)!
);
```

---

## Architecture

### Class Hierarchy

```
AgentThread (Microsoft.Agents.AI)
    ↓
ConversationThread (HPD-Agent)
    ↓ uses
ConversationMessageStore (abstract)
    ↓ implements
    ├─ InMemoryConversationMessageStore
    └─ DatabaseConversationMessageStore (future)
```

### Component Responsibilities

| Component | Responsibility |
|-----------|---------------|
| **ConversationThread** | Thread state, metadata, timestamps, service integration |
| **ConversationMessageStore** | Message storage, cache-aware reduction, token counting |
| **InMemoryConversationMessageStore** | Fast in-memory storage (default) |
| **IConversationMessageStoreFactory** | AOT-friendly deserialization |

### Design Patterns

#### Push Pattern (Public API)
Users **push** updates to the thread. Framework manages state internally.

```csharp
// ✅ Users add messages
await thread.AddMessageAsync(message);

// ❌ Users DON'T pull messages (prevents stale data)
// var messages = await thread.GetMessagesAsync(); // Compile error!
```

#### Pull Pattern (Internal API)
Framework **pulls** snapshots for efficient operations.

```csharp
// ✅ Framework can pull efficiently
var messages = await thread.GetMessagesAsync(); // Internal only
```

---

## API Reference

### Constructors

#### `ConversationThread()`
Creates a new thread with default in-memory storage.

```csharp
var thread = new ConversationThread();
```

#### `ConversationThread(ConversationMessageStore messageStore)`
Creates a new thread with custom message store.

```csharp
var customStore = new DatabaseConversationMessageStore("connection_string");
var thread = new ConversationThread(customStore);
```

---

### Properties

#### `string Id` (read-only)
Unique identifier for this thread (GUID).

```csharp
Console.WriteLine($"Thread ID: {thread.Id}");
```

#### `DateTime CreatedAt` (read-only)
UTC timestamp when thread was created.

```csharp
Console.WriteLine($"Created: {thread.CreatedAt:yyyy-MM-dd HH:mm:ss}");
```

#### `DateTime LastActivity` (read-only)
UTC timestamp of last activity (message add, metadata update, etc.).

```csharp
var idle = DateTime.UtcNow - thread.LastActivity;
if (idle.TotalMinutes > 30)
    Console.WriteLine("Thread has been idle for 30+ minutes");
```

#### `string? DisplayName` (read-write)
Display name for UI (e.g., conversation list). Falls back to first user message if not set.

```csharp
// Set explicitly
thread.DisplayName = "Financial Analysis Chat";

// Get (async fallback to first message)
var name = await thread.GetDisplayNameAsync();
```

#### `IReadOnlyDictionary<string, object> Metadata` (read-only)
Read-only view of thread metadata.

```csharp
if (thread.Metadata.TryGetValue("category", out var category))
    Console.WriteLine($"Category: {category}");
```

#### `bool RequiresAsyncAccess` (read-only)
Indicates if message operations require async I/O.

```csharp
if (thread.RequiresAsyncAccess)
    Console.WriteLine("Thread uses database storage");
else
    Console.WriteLine("Thread uses in-memory storage");
```

#### `ConversationMessageStore MessageStore` (read-only)
Direct access to underlying message store (advanced scenarios).

```csharp
// Access token counting
var tokens = await thread.MessageStore.GetTotalTokensAsync();
Console.WriteLine($"Total tokens: {tokens}");
```

#### `string? ServiceThreadId` (read-write)
Optional external service thread ID (e.g., OpenAI Assistant thread).

```csharp
// Hybrid scenario: sync with OpenAI
thread.ServiceThreadId = "thread_abc123";
```

---

### Methods

#### Message Operations

##### `Task AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)`
Adds a single message to the thread. **Primary method for updating thread state.**

```csharp
var userMsg = new ChatMessage(ChatRole.User, "Hello");
await thread.AddMessageAsync(userMsg);
```

##### `Task AddMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)`
Adds multiple messages in one operation.

```csharp
var messages = new[]
{
    new ChatMessage(ChatRole.User, "Hello"),
    new ChatMessage(ChatRole.Assistant, "Hi! How can I help?")
};
await thread.AddMessagesAsync(messages);
```

##### `Task<int> GetMessageCountAsync(CancellationToken cancellationToken = default)`
Gets the total number of messages in the thread. **Useful for UI/pagination.**

```csharp
var count = await thread.GetMessageCountAsync();
Console.WriteLine($"Thread has {count} messages");

// Use for pagination
if (count > 50)
    Console.WriteLine("Consider paginating results");
```

##### `Task ApplyReductionAsync(ChatMessage summaryMessage, int removedCount, CancellationToken cancellationToken = default)`
Applies cache-aware history reduction. Removes old messages and inserts summary.

```csharp
// Create summary message (marked with __summary__)
var summary = new ChatMessage(
    ChatRole.System,
    "Summary of previous conversation: User asked about weather, assistant provided forecast."
);

// Remove 10 old messages, replace with summary
await thread.ApplyReductionAsync(summary, removedCount: 10);
```

##### `Task ClearAsync(CancellationToken cancellationToken = default)`
Clears all messages and metadata from the thread.

```csharp
await thread.ClearAsync();
Console.WriteLine("Thread cleared");
```

#### Metadata Operations

##### `void AddMetadata(string key, object value)`
Adds or updates metadata key/value pair.

```csharp
thread.AddMetadata("category", "support");
thread.AddMetadata("priority", 1);
thread.AddMetadata("tags", new[] { "billing", "urgent" });
```

#### Display Name

##### `Task<string> GetDisplayNameAsync(int maxLength = 30, CancellationToken cancellationToken = default)`
Gets display name with automatic fallback to first user message.

```csharp
// Returns explicit DisplayName if set
thread.DisplayName = "My Chat";
var name = await thread.GetDisplayNameAsync(); // "My Chat"

// Otherwise returns first user message (truncated)
thread.DisplayName = null;
await thread.AddMessageAsync(new ChatMessage(ChatRole.User, "This is a very long message..."));
var name2 = await thread.GetDisplayNameAsync(maxLength: 20); // "This is a very lo..."
```

#### Project Association

##### `void SetProject(Project project)`
Associates thread with a project for document context sharing.

```csharp
var project = new Project("Q4 Analysis");
await project.DocumentManager.AddDocumentAsync("report.pdf");

thread.SetProject(project);
// Thread now has access to report.pdf via ProjectInjectedMemoryFilter

await agent.RunAsync("Summarize the report", thread);
```

##### `Project? GetProject()`
Gets associated project, or null if none.

```csharp
var project = thread.GetProject();
if (project != null)
    Console.WriteLine($"Thread belongs to project: {project.Name}");
```

#### Serialization

##### `JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)`
Serializes thread to JSON for persistence.

```csharp
var snapshot = thread.Serialize();

// Save to file
await File.WriteAllTextAsync("thread.json", snapshot.GetRawText());

// Save to database
var json = snapshot.GetRawText();
await db.Conversations.AddAsync(new { Id = thread.Id, Data = json });
```

##### `static ConversationThread Deserialize(ConversationThreadSnapshot snapshot, JsonSerializerOptions? options = null)`
Deserializes thread from snapshot. **Requires factory registration for Native AOT.**

```csharp
// Load from file
var json = await File.ReadAllTextAsync("thread.json");
var snapshot = JsonSerializer.Deserialize<ConversationThreadSnapshot>(json);
var thread = ConversationThread.Deserialize(snapshot);

// Verify restoration
Console.WriteLine($"Restored {await thread.GetMessageCountAsync()} messages");
```

#### Factory Registration (Native AOT)

##### `static void RegisterStoreFactory(IConversationMessageStoreFactory factory)`
Registers factory for AOT-friendly deserialization. **Call once at app startup.**

```csharp
// In Main() or Startup.cs
ConversationThread.RegisterStoreFactory(
    new InMemoryConversationMessageStoreFactory()
);

// For custom stores:
ConversationThread.RegisterStoreFactory(
    new DatabaseConversationMessageStoreFactory()
);
```

---

## Usage Patterns

### Pattern 1: Simple In-Memory Conversation

```csharp
var agent = new Agent(...);
var thread = new ConversationThread();

// Multi-turn conversation
await thread.AddMessageAsync(new ChatMessage(ChatRole.User, "Hello"));
var response1 = await agent.RunAsync("Hello", thread);

await thread.AddMessageAsync(new ChatMessage(ChatRole.User, "Tell me a joke"));
var response2 = await agent.RunAsync("Tell me a joke", thread);

Console.WriteLine($"Conversation has {await thread.GetMessageCountAsync()} messages");
```

### Pattern 2: Persistent Conversation (Save/Load)

```csharp
// Startup: Register factories
ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());

// Session 1: Create and save
var thread = new ConversationThread();
thread.DisplayName = "Customer Support #1234";
await thread.AddMessageAsync(new ChatMessage(ChatRole.User, "I need help"));

var snapshot = thread.Serialize();
await SaveToDatabase(thread.Id, snapshot.GetRawText());

// Session 2: Load and continue
var json = await LoadFromDatabase(threadId);
var restored = ConversationThread.Deserialize(
    JsonSerializer.Deserialize<ConversationThreadSnapshot>(json)!
);

await restored.AddMessageAsync(new ChatMessage(ChatRole.User, "Still need help"));
var response = await agent.RunAsync("Still need help", restored);
```

### Pattern 3: Project-Scoped Conversations

```csharp
// Create project with documents
var project = new Project("Legal Review");
await project.DocumentManager.AddDocumentAsync("contract.pdf");
await project.DocumentManager.AddDocumentAsync("amendments.pdf");

// Create multiple threads in the project
var thread1 = new ConversationThread { DisplayName = "Contract Analysis" };
thread1.SetProject(project);

var thread2 = new ConversationThread { DisplayName = "Risk Assessment" };
thread2.SetProject(project);

// Both threads have access to project documents
await agent.RunAsync("Analyze the contract", thread1);
await agent.RunAsync("What are the risks?", thread2);

// List all project conversations
foreach (var t in project.Threads)
{
    var name = await t.GetDisplayNameAsync();
    var count = await t.GetMessageCountAsync();
    Console.WriteLine($"{name}: {count} messages");
}
```

### Pattern 4: Conversation Metadata & Tagging

```csharp
var thread = new ConversationThread();
thread.DisplayName = "Support Case #1234";

// Add metadata for categorization
thread.AddMetadata("category", "billing");
thread.AddMetadata("priority", "high");
thread.AddMetadata("assignee", "john.doe@company.com");
thread.AddMetadata("tags", new[] { "refund", "urgent" });
thread.AddMetadata("created_by", userId);

// Later: Filter/search by metadata
var category = thread.Metadata["category"];
var priority = thread.Metadata["priority"];

if (priority.ToString() == "high")
    Console.WriteLine("This is a high priority conversation");
```

### Pattern 5: History Reduction (Long Conversations)

```csharp
var thread = new ConversationThread();

// Simulate long conversation
for (int i = 0; i < 100; i++)
{
    await thread.AddMessageAsync(new ChatMessage(ChatRole.User, $"Message {i}"));
    await agent.RunAsync($"Message {i}", thread);
}

var count = await thread.GetMessageCountAsync();
Console.WriteLine($"Before reduction: {count} messages");

// Check token usage
var tokens = await thread.MessageStore.GetTotalTokensAsync();
if (tokens > 8000)
{
    // Create summary of first 50 messages
    var summary = await agent.SummarizeAsync(thread, messageCount: 50);
    
    // Replace first 50 messages with summary
    await thread.ApplyReductionAsync(summary, removedCount: 50);
    
    Console.WriteLine($"After reduction: {await thread.GetMessageCountAsync()} messages");
}
```

### Pattern 6: Hybrid Service Thread Sync

```csharp
// Create local thread
var thread = new ConversationThread();

// Sync with OpenAI Assistant thread
var assistantThread = await openAIClient.CreateThreadAsync();
thread.ServiceThreadId = assistantThread.Id;

// Now you have:
// 1. Local message history (in ConversationThread)
// 2. Remote thread sync (in OpenAI Assistants)

await thread.AddMessageAsync(new ChatMessage(ChatRole.User, "Hello"));

// Use either:
var localResponse = await agent.RunAsync("Hello", thread);
// OR
var remoteResponse = await openAIClient.CreateMessageAsync(thread.ServiceThreadId, "Hello");
```

---

## Native AOT Support

### Why AOT?

Native AOT compilation provides:
- ✅ **Faster startup** - No JIT compilation
- ✅ **Smaller binaries** - Tree-trimmed dependencies
- ✅ **Lower memory** - No runtime overhead
- ✅ **Better performance** - Ahead-of-time optimizations

### Factory Registration Pattern

Traditional deserialization uses reflection (not AOT-compatible):

```csharp
// ❌ This fails in Native AOT
var type = Type.GetType(typeName);
var instance = Activator.CreateInstance(type, args);
```

Factory pattern replaces reflection with explicit registration:

```csharp
// ✅ This works in Native AOT
var factory = _storeFactories[typeName];
var instance = factory.CreateFromSnapshot(state, options);
```

### Setup Instructions

#### Step 1: Enable AOT in .csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <PublishAot>true</PublishAot>
  </PropertyGroup>
</Project>
```

#### Step 2: Register Factories at Startup

```csharp
// Main.cs or Program.cs
public static void Main(string[] args)
{
    // Register factories FIRST (before any deserialization)
    ConversationThread.RegisterStoreFactory(
        new InMemoryConversationMessageStoreFactory()
    );
    
    // Add more factories for custom stores
    // ConversationThread.RegisterStoreFactory(new DatabaseMessageStoreFactory());
    
    // Rest of application
    var app = CreateHostBuilder(args).Build();
    app.Run();
}
```

#### Step 3: Publish as Native AOT

```bash
# macOS ARM64
dotnet publish -c Release -r osx-arm64

# macOS x64
dotnet publish -c Release -r osx-x64

# Linux x64
dotnet publish -c Release -r linux-x64

# Windows x64
dotnet publish -c Release -r win-x64
```

### Creating Custom Factories

If you implement a custom `ConversationMessageStore`, create a factory:

```csharp
public class DatabaseConversationMessageStore : ConversationMessageStore
{
    public DatabaseConversationMessageStore(JsonElement state, JsonSerializerOptions? options)
    {
        // Deserialize from state
    }
    
    // ... implementation
}

// Factory for AOT support
public class DatabaseConversationMessageStoreFactory : IConversationMessageStoreFactory
{
    public string StoreTypeName => typeof(DatabaseConversationMessageStore).AssemblyQualifiedName!;
    
    public ConversationMessageStore CreateFromSnapshot(JsonElement state, JsonSerializerOptions? options)
    {
        return new DatabaseConversationMessageStore(state, options);
    }
}

// Register at startup
ConversationThread.RegisterStoreFactory(new DatabaseConversationMessageStoreFactory());
```

### Fallback Behavior

| Scenario | Factory Registered? | Native AOT? | Result |
|----------|---------------------|-------------|--------|
| Development | No | No | ✅ Works (uses reflection) |
| Development | Yes | No | ✅ Works (uses factory) |
| Production AOT | No | Yes | ❌ Runtime error with helpful message |
| Production AOT | Yes | Yes | ✅ Works (uses factory) |

---

## Advanced Topics

### Thread Safety

`ConversationThread` is thread-safe for concurrent operations:

```csharp
var thread = new ConversationThread();

// Safe: Concurrent reads
var task1 = thread.GetMessageCountAsync();
var task2 = thread.GetMessageCountAsync();
var task3 = thread.GetDisplayNameAsync();
await Task.WhenAll(task1, task2, task3);

// Safe: Concurrent writes (serialized internally)
var addTask1 = thread.AddMessageAsync(new ChatMessage(ChatRole.User, "Hello"));
var addTask2 = thread.AddMessageAsync(new ChatMessage(ChatRole.User, "Hi"));
await Task.WhenAll(addTask1, addTask2);
```

### Custom Message Stores

Implement `ConversationMessageStore` for custom storage:

```csharp
public class RedisConversationMessageStore : ConversationMessageStore
{
    private readonly IDatabase _redis;
    private readonly string _key;
    
    public RedisConversationMessageStore(string connectionString, string conversationId)
    {
        var connection = ConnectionMultiplexer.Connect(connectionString);
        _redis = connection.GetDatabase();
        _key = $"conversation:{conversationId}";
    }
    
    protected override async Task<List<ChatMessage>> LoadMessagesAsync(CancellationToken ct)
    {
        var json = await _redis.StringGetAsync(_key);
        return JsonSerializer.Deserialize<List<ChatMessage>>(json!);
    }
    
    protected override async Task SaveMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(messages);
        await _redis.StringSetAsync(_key, json);
    }
    
    // ... implement other abstract methods
}

// Use it
var store = new RedisConversationMessageStore("localhost:6379", "thread-123");
var thread = new ConversationThread(store);
```

### Accessing Messages (Internal/Framework Code)

If you're building framework components (not application code), you can access `GetMessagesAsync()`:

```csharp
// ⚠️ Internal use only - requires [assembly: InternalsVisibleTo("YourFrameworkAssembly")]
internal class MyFrameworkComponent
{
    internal async Task AnalyzeThread(ConversationThread thread)
    {
        // Framework code can access internal methods
        var messages = await thread.GetMessagesAsync();
        
        foreach (var msg in messages)
        {
            // Analyze message content
        }
    }
}
```

Application code should **never** access `GetMessagesAsync()` directly.

### Event Handling

`ConversationThread` inherits from `AgentThread` and participates in the agent event system:

```csharp
public class MyAgent : Agent
{
    protected override async Task MessagesReceivedAsync(
        IEnumerable<ChatMessage> newMessages,
        CancellationToken cancellationToken)
    {
        // Called when messages are added to thread
        var count = newMessages.Count();
        Console.WriteLine($"Received {count} new messages");
        
        await base.MessagesReceivedAsync(newMessages, cancellationToken);
    }
}
```

---

## Migration Guide

### From Old `Messages` Property (Breaking Change)

**Before:**
```csharp
// ❌ This no longer works
var messages = thread.Messages; // Property removed
foreach (var msg in messages)
    Console.WriteLine(msg.Text);
```

**After:**
```csharp
// ✅ Use agent response instead
var response = await agent.RunAsync("Hello", thread);
foreach (var msg in response.Messages)
    Console.WriteLine(msg.Text);

// ✅ Or use message count for UI
var count = await thread.GetMessageCountAsync();
Console.WriteLine($"Thread has {count} messages");
```

### From Old `GetDisplayName()` (Breaking Change)

**Before:**
```csharp
var name = thread.GetDisplayName(); // Sync method
```

**After:**
```csharp
var name = await thread.GetDisplayNameAsync(); // Now async
```

### From Old `GetMessageCount()` (Breaking Change)

**Before:**
```csharp
var count = thread.MessageCount; // Property (internal)
```

**After:**
```csharp
var count = await thread.GetMessageCountAsync(); // Now public!
```

### Adding Deserialization Support

**Before (no deserialization):**
```csharp
// Nothing to change - just use threads
var thread = new ConversationThread();
```

**After (with deserialization):**
```csharp
// Add one line at startup
ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());

// Rest is identical
var thread = new ConversationThread();
```

---

## Best Practices

### ✅ DO

- **DO** use `AddMessageAsync()` to update thread state
- **DO** use `GetMessageCountAsync()` for UI pagination
- **DO** register factories at application startup for Native AOT
- **DO** use project association for document context
- **DO** add metadata for categorization/filtering
- **DO** use `DisplayName` for UI conversation lists
- **DO** apply history reduction for long conversations

### ❌ DON'T

- **DON'T** try to access `GetMessagesAsync()` from application code (it's internal)
- **DON'T** store `Messages` references and expect them to update (use snapshots)
- **DON'T** forget to register factories if you need deserialization + Native AOT
- **DON'T** block on async methods with `.Result` or `.GetAwaiter().GetResult()`

---

## API Summary

### Public Methods

| Method | Returns | Description |
|--------|---------|-------------|
| `AddMessageAsync()` | `Task` | Add single message (primary update method) |
| `AddMessagesAsync()` | `Task` | Add multiple messages |
| `GetMessageCountAsync()` | `Task<int>` | Get total message count (UI/pagination) |
| `ApplyReductionAsync()` | `Task` | Apply cache-aware history reduction |
| `ClearAsync()` | `Task` | Clear all messages and metadata |
| `AddMetadata()` | `void` | Add metadata key/value pair |
| `GetDisplayNameAsync()` | `Task<string>` | Get display name with fallback |
| `SetProject()` | `void` | Associate with project |
| `GetProject()` | `Project?` | Get associated project |
| `Serialize()` | `JsonElement` | Serialize to JSON |
| `Deserialize()` (static) | `ConversationThread` | Deserialize from snapshot |
| `RegisterStoreFactory()` (static) | `void` | Register AOT factory |

### Internal Methods (Framework Only)

| Method | Returns | Description |
|--------|---------|-------------|
| `GetMessagesAsync()` | `Task<IReadOnlyList<ChatMessage>>` | Get message snapshot (internal use) |
| `GetMessagesSync()` | `IReadOnlyList<ChatMessage>` | Sync access (FFI/P-Invoke only) |
| `GetMessageCountSync()` | `int` | Sync count (FFI/P-Invoke only) |

---

## Support & Resources

- **Source Code:** [GitHub Repository](https://github.com/Ewoofcoding/HPD-Agent)
- **Issues:** [GitHub Issues](https://github.com/Ewoofcoding/HPD-Agent/issues)
- **Examples:** See `samples/` directory
- **Tests:** See `test/HPD-Agent.Tests/` directory

---

## Version History

### v1.0 (November 2025)
- ✅ Public `GetMessageCountAsync()` API
- ✅ Native AOT support via factory pattern
- ✅ Internal `GetMessagesAsync()` to prevent stale data bugs
- ✅ Enhanced documentation with usage patterns
- ✅ `DisplayName` property for UI
- ✅ Project association for document context

### Breaking Changes from v0.x
- `Messages` property removed (use agent response instead)
- `GetDisplayName()` → `GetDisplayNameAsync()` (now async)
- `MessageCount` property removed (use `GetMessageCountAsync()`)
- `GetMessagesAsync()` changed from public to internal

---

**© 2025 HPD-Agent Framework. All rights reserved.**
