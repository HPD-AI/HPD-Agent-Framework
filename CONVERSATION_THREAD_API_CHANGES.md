# ConversationThread API Changes - Hybrid Design

## Summary

Implemented **Hybrid #2 (Flipped)** pattern for `ConversationThread`:
- **Internal pull API**: `GetMessagesAsync()` - for efficient framework operations
- **Public push API**: `AddMessagesAsync()` - for user code to update state
- Best of both worlds: framework efficiency + user simplicity

---

## What Changed

### ✅ Methods Made **INTERNAL** (Framework Use Only)

```csharp
// ❌ Users can NO LONGER call these:
internal Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(...)
internal Task<int> GetMessageCountAsync(...)
internal IReadOnlyList<ChatMessage> GetMessagesSync()  // FFI only
internal int GetMessageCountSync()  // FFI only
```

### ✅ Methods That Are **PUBLIC** (User-Facing)

```csharp
// ✅ Users SHOULD use these:
public async Task AddMessageAsync(ChatMessage message, ...)
public async Task AddMessagesAsync(IEnumerable<ChatMessage> messages, ...)
public async Task ApplyReductionAsync(ChatMessage summaryMessage, int removedCount, ...)
public async Task ClearAsync(...)

// Properties:
public string? DisplayName { get; set; }
public IReadOnlyDictionary<string, object> Metadata { get; }
public bool RequiresAsyncAccess { get; }
```

---

## Why This Design?

### **For Framework/Internal Code (Agent.cs)**
```csharp
// ✅ Internal code can efficiently pull snapshots
var currentMessages = await thread.GetMessagesAsync(ct);

// Work with snapshot
foreach (var msg in messages)
{
    if (!currentMessages.Contains(msg))
        await thread.AddMessagesAsync(new[] { msg }, ct);
}

// Refresh snapshot when needed
currentMessages = await thread.GetMessagesAsync(ct);
```

### **For User Code**
```csharp
// ✅ Users push messages - simple and safe
var thread = new ConversationThread();
var userMessage = new ChatMessage(ChatRole.User, "Hello");

await thread.AddMessageAsync(userMessage);  // Push pattern

// ❌ Users CANNOT do this anymore (compile error):
// var messages = await thread.GetMessagesAsync();  // Internal!

// ✅ Users work with the agent, which manages thread internally
await agent.RunAsync("Hello", thread);
```

---

## Migration Guide

### **If You're Using ConversationThread Directly (External Code)**

#### ❌ Before (No Longer Works):
```csharp
var thread = new ConversationThread();

// Query messages
var messages = await thread.GetMessagesAsync();  // ❌ Compile error!
var count = await thread.GetMessageCountAsync();  // ❌ Compile error!

// Iterate over messages
foreach (var msg in messages)
{
    Console.WriteLine(msg.Text);
}
```

#### ✅ After (New Pattern):
```csharp
var thread = new ConversationThread();

// Add messages (push pattern)
await thread.AddMessageAsync(new ChatMessage(ChatRole.User, "Hello"));

// Let the agent handle the thread
var response = await agent.RunAsync("Hello", thread);

// Access response messages
foreach (var msg in response.Messages)
{
    Console.WriteLine(msg.Text);
}
```

### **If You're Working Inside the Framework (Agent.cs, etc.)**

#### ✅ No Changes Needed:
```csharp
// Internal code still works the same way
var currentMessages = await conversationThread.GetMessagesAsync(ct);

// Refresh pattern still works
currentMessages = await conversationThread.GetMessagesAsync(ct);
```

---

## Benefits

### ✅ **For Users**
1. **Simple API** - Only see `AddMessagesAsync()`, no confusion
2. **No stale data bugs** - Can't accidentally query and forget to refresh
3. **Clear intent** - "I add messages, framework manages state"
4. **Less to learn** - One way to do things

### ✅ **For Framework**
1. **Internal efficiency** - Can pull snapshots as needed
2. **No breaking changes** - Agent.cs code unchanged
3. **Flexibility** - Refresh pattern still available internally
4. **Performance** - Avoid unnecessary round-trips

### ✅ **Architecture**
1. **Clear boundaries** - Public push vs Internal pull
2. **Best practices enforced** - Users can't make mistakes
3. **Microsoft-aligned** - Similar to ChatClientAgentThread pattern
4. **Future-proof** - Easy to add features without API churn

---

## API Surface Comparison

| Method | Old Visibility | New Visibility | Why |
|--------|---------------|----------------|-----|
| `GetMessagesAsync()` | `public` | `internal` | Prevent stale data bugs |
| `GetMessageCountAsync()` | `public` | `internal` | Same reason |
| `AddMessageAsync()` | `public` | `public` | User-facing push API |
| `AddMessagesAsync()` | `public` | `public` | User-facing push API |
| `GetMessagesSync()` | `internal` | `internal` | FFI only, unchanged |
| `GetMessageCountSync()` | `internal` | `internal` | FFI only, unchanged |

---

## Example: Full User Workflow

```csharp
// Create agent and thread
var agent = new Agent(...);
var thread = new ConversationThread();

// User adds a message
var userMsg = new ChatMessage(ChatRole.User, "What's the weather?");
await thread.AddMessageAsync(userMsg);

// Run the agent (it internally manages the thread)
var response = await agent.RunAsync("What's the weather?", thread);

// Access the response
Console.WriteLine(response.Text);

// Continue the conversation
await agent.RunAsync("What about tomorrow?", thread);

// Set metadata
thread.DisplayName = "Weather Chat";
thread.AddMetadata("category", "weather");

// Serialize for persistence
var snapshot = thread.Serialize();
```

---

## Notes

- **No public breaking changes** - Your internal Agent.cs code works unchanged
- **FFI methods unchanged** - `GetMessagesSync()` still available for P/Invoke
- **Microsoft-aligned** - Similar pattern to `ChatClientAgentThread.MessagesReceivedAsync()`
- **Thread-safe** - All operations remain thread-safe as before

---

## Related Files

- `ConversationThread.cs` - Main implementation
- `Agent.cs` - Uses internal pull API
- `NativeExports.cs` - Uses FFI sync methods
- `Project.cs` - Uses public push API

---

**This design gives you the best of both worlds: simplicity for users, flexibility for framework code!** 🎯
