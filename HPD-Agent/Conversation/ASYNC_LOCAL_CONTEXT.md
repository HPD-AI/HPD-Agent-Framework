# Ambient Context Pattern with AsyncLocal

## Overview

HPD-Agent uses the **Ambient Context pattern** via `AsyncLocal<T>` to provide conversation-scoped context to plugins and filters without explicit parameter passing. This is implemented in `ConversationContext` and is critical for features like Plan Mode.

## Why AsyncLocal at the Conversation Level?

### The Architecture Difference

**Microsoft.Extensions.AI**: Stateless single-turn chat client
```csharp
// Each call is independent
await chatClient.GetResponseAsync(messages);
```

**HPD-Agent**: Stateful multi-turn conversations
```csharp
var conversation = new Conversation(agent);
await conversation.SendAsync("Create a plan");      // Turn 1
await conversation.SendAsync("Update step 1");      // Turn 2 - same conversation
await conversation.SendAsync("Add context note");   // Turn 3 - same conversation
```

### Conversation vs Agent Scope

We set AsyncLocal at the **Conversation level** (not Agent level) because:

1. **Conversation owns the ID**: The `Conversation` class naturally has `_conversationId`
2. **Multi-turn scope**: Context needs to persist across multiple agent turns
3. **State management**: Plans, Memory, History are all conversation-scoped
4. **Natural ownership**: Conversation sets context once, agent/tools consume it

```csharp
// Conversation level (what we do)
Conversation.SendAsync()
{
    ConversationContext.Set(_conversationId);  // Set ONCE
    await agent.GetResponseAsync(...);         // Multiple tool calls use same ID
    ConversationContext.Clear();
}

// Agent level (what Microsoft does - not suitable for us)
Agent.InvokeFunction()
{
    AgentContext.Set(...);  // Would need to set PER FUNCTION - doesn't work for conversation scope
}
```

## Current Implementation

### ConversationContext.cs

```csharp
public static class ConversationContext
{
    private static readonly AsyncLocal<string?> _currentConversationId = new();
    private static string? _fallbackConversationId; // Static fallback

    public static string? CurrentConversationId
    {
        get
        {
            var asyncLocalValue = _currentConversationId.Value;
            if (!string.IsNullOrEmpty(asyncLocalValue))
                return asyncLocalValue;
            return _fallbackConversationId;
        }
    }

    internal static void SetConversationId(string? conversationId)
    {
        _currentConversationId.Value = conversationId;
        _fallbackConversationId = conversationId;
    }
}
```

### Why the Fallback?

The static fallback exists because we weren't initially sure if AsyncLocal would flow through the Microsoft.Extensions.AI pipeline. In practice, AsyncLocal **does flow correctly**, but the fallback provides extra safety for edge cases.

## Real-World Example: Plan Mode

**Before AsyncLocal** (what we would have had to do):

```csharp
public class AgentPlanPlugin
{
    [AIFunction]
    public Task<string> CreatePlanAsync(
        string goal,
        string[] steps,
        string conversationId)  // ← Every method needs this parameter!
    {
        var plan = _manager.CreatePlan(conversationId, goal, steps);
        return Task.FromResult($"Created plan {plan.Id}");
    }

    [AIFunction]
    public Task<string> UpdateStepAsync(
        string stepId,
        string status,
        string conversationId)  // ← Repeated everywhere!
    {
        _manager.UpdateStep(conversationId, stepId, status);
        return Task.FromResult("Updated");
    }
}
```

**After AsyncLocal** (what we actually have):

```csharp
public class AgentPlanPlugin
{
    [AIFunction]
    public Task<string> CreatePlanAsync(string goal, string[] steps)
    {
        // Magic! ConversationId flows automatically
        var conversationId = ConversationContext.CurrentConversationId;
        var plan = _manager.CreatePlan(conversationId, goal, steps);
        return Task.FromResult($"Created plan {plan.Id}");
    }

    [AIFunction]
    public Task<string> UpdateStepAsync(string stepId, string status)
    {
        // No conversationId parameter needed!
        var conversationId = ConversationContext.CurrentConversationId;
        _manager.UpdateStep(conversationId, stepId, status);
        return Task.FromResult("Updated");
    }
}
```

## Making It Extensible

### Current State: Single Property

Right now, `ConversationContext` only exposes `CurrentConversationId`. This works for Plan Mode, but future plugins might need more context.

### Future: Rich Context Object

To make it extensible without breaking existing code:

```csharp
public static class ConversationContext
{
    private static readonly AsyncLocal<ConversationExecutionContext?> _current = new();

    // Rich context object
    public static ConversationExecutionContext? Current => _current.Value;

    // Backwards compatibility - existing code still works
    public static string? CurrentConversationId => Current?.ConversationId;

    internal static void Set(ConversationExecutionContext? context)
    {
        _current.Value = context;
    }
}

public class ConversationExecutionContext
{
    // Core identity
    public string ConversationId { get; init; }
    public string AgentName { get; init; }

    // Runtime state (useful for advanced plugins)
    public AgentRunContext? RunContext { get; set; }
    public int CurrentIteration => RunContext?.CurrentIteration ?? 0;
    public int MaxIterations => RunContext?.MaxIterations ?? 10;
    public TimeSpan ElapsedTime => RunContext?.ElapsedTime ?? TimeSpan.Zero;

    // Conversation-level metadata
    public Dictionary<string, object> Metadata { get; } = new();

    // Helper methods
    public bool IsNearTimeout(TimeSpan threshold)
    {
        var maxDuration = TimeSpan.FromMinutes(5); // From config
        return ElapsedTime > (maxDuration - threshold);
    }
}
```

## Extensibility Benefits

### 1. Adaptive Tools

```csharp
[Description("Performs deep codebase analysis")]
public async Task<string> DeepAnalysisAsync(string target)
{
    var ctx = ConversationContext.Current;

    // Check if we're running out of time
    if (ctx?.IsNearTimeout(TimeSpan.FromSeconds(30)) == true)
    {
        return await QuickAnalysis(target); // Faster alternative
    }

    // Check if we're near iteration limit
    if (ctx?.CurrentIteration >= ctx?.MaxIterations - 2)
    {
        return "Skipping deep analysis - near turn limit";
    }

    return await FullDeepAnalysis(target);
}
```

### 2. Cross-Plugin Coordination

```csharp
[Description("Saves important information to memory")]
public async Task<string> SaveToMemoryAsync(string key, string value)
{
    var ctx = ConversationContext.Current;

    // Store in conversation metadata for other plugins to access
    ctx?.Metadata[$"memory.{key}"] = value;

    await _memoryStore.SaveAsync(ctx?.ConversationId, key, value);
    return $"Saved {key} to memory";
}

[Description("Retrieves information from memory")]
public async Task<string> GetFromMemoryAsync(string key)
{
    var ctx = ConversationContext.Current;

    // Try in-memory cache first (from metadata)
    if (ctx?.Metadata.TryGetValue($"memory.{key}", out var cachedValue) == true)
    {
        return cachedValue?.ToString() ?? "null";
    }

    // Fallback to storage
    return await _memoryStore.GetAsync(ctx?.ConversationId, key);
}
```

### 3. Telemetry and Logging

```csharp
public class MyPlugin
{
    [AIFunction]
    public async Task<string> DoWorkAsync(string input)
    {
        var ctx = ConversationContext.Current;

        // Automatic context-aware logging
        _logger.LogInformation(
            "Plugin executing - Conversation: {ConversationId}, Agent: {AgentName}, Iteration: {Iteration}",
            ctx?.ConversationId ?? "unknown",
            ctx?.AgentName ?? "unknown",
            ctx?.CurrentIteration ?? -1);

        // Automatic telemetry
        _telemetry.TrackEvent("PluginExecution", new Dictionary<string, string>
        {
            ["ConversationId"] = ctx?.ConversationId ?? "unknown",
            ["Iteration"] = ctx?.CurrentIteration.ToString() ?? "unknown",
            ["ElapsedTime"] = ctx?.ElapsedTime.ToString() ?? "unknown"
        });

        return await ProcessAsync(input);
    }
}
```

## Migration Path

### Phase 1: Current State ✅
- Single property: `CurrentConversationId`
- Works for Plan Mode
- Simple and focused

### Phase 2: Add Rich Context (When Needed)
```csharp
// Backwards compatible - existing code still works
var id = ConversationContext.CurrentConversationId;

// New code can access rich context
var ctx = ConversationContext.Current;
var iteration = ctx?.CurrentIteration;
```

### Phase 3: Extend as Needed
```csharp
public class ConversationExecutionContext
{
    // Add properties as requirements emerge
    public IReadOnlyList<ChatMessage> History { get; init; }
    public CancellationToken CancellationToken { get; init; }
    public ConversationSettings Settings { get; init; }
    // etc.
}
```

## Best Practices

### 1. Always Check for Null

```csharp
var conversationId = ConversationContext.CurrentConversationId;
if (string.IsNullOrEmpty(conversationId))
{
    return "Error: No conversation context available.";
}
```

### 2. Use Metadata for Plugin Coordination

```csharp
// Plugin A stores data
ctx?.Metadata["lastSearchResult"] = searchResult;

// Plugin B retrieves it later in the same conversation
var lastResult = ctx?.Metadata.TryGetValue("lastSearchResult", out var result)
    ? result : null;
```

### 3. Don't Mutate Core Properties

```csharp
// ❌ BAD - Don't modify core state
ctx.ConversationId = "something-else";

// ✅ GOOD - Use Metadata for plugin-specific state
ctx.Metadata["myPlugin.state"] = "something";
```

## When to Use AsyncLocal Context

### ✅ Use It For:
- **Conversation-scoped state** (Plan Mode, Memory, History)
- **Cross-plugin coordination** (shared data within a conversation)
- **Telemetry and logging** (automatic context enrichment)
- **Adaptive behavior** (tools that need runtime awareness)

### ❌ Don't Use It For:
- **Business logic parameters** (use function parameters instead)
- **External system state** (use dependency injection)
- **User input** (pass explicitly)
- **Global configuration** (use config files/DI)

## Architecture Decision Records

### ADR 1: Conversation-Level vs Agent-Level AsyncLocal

**Decision**: Set AsyncLocal at the Conversation level, not Agent level.

**Rationale**:
- Conversation naturally owns the ConversationId
- Multi-turn state requires conversation scope
- Cleaner ownership model
- Aligns with stateful conversation abstraction

**Alternatives Considered**:
- Agent-level (rejected: would need conversationId passed to Agent)
- Per-function (rejected: too granular, doesn't support multi-turn state)

### ADR 2: Single Property vs Rich Context Object

**Decision**: Start with single property (`CurrentConversationId`), evolve to rich context as needed.

**Rationale**:
- YAGNI (You Aren't Gonna Need It) - start simple
- Easy to extend without breaking changes
- Current use case (Plan Mode) only needs ConversationId
- Future plugins can access `Current` property when they need more

**Migration Path**: Backwards compatible evolution.

## Comparison to Other Patterns

| Pattern | Example | When to Use |
|---------|---------|-------------|
| **Explicit Parameters** | `CreatePlan(conversationId, goal)` | External inputs, business logic |
| **Dependency Injection** | Constructor injection of services | Stateless services, external dependencies |
| **AsyncLocal (Ambient Context)** | `ConversationContext.CurrentConversationId` | Cross-cutting concerns that flow through call stack |
| **Options Pattern** | `ChatOptions.AdditionalProperties` | Per-request configuration |

## Technical Details

### How AsyncLocal Works

AsyncLocal uses the `ExecutionContext` that flows automatically across async calls:

```csharp
async Task OuterAsync()
{
    ConversationContext.Set("conv-123");  // Set in outer scope
    await InnerAsync();                   // Flows to inner async method
}

async Task InnerAsync()
{
    var id = ConversationContext.CurrentConversationId; // ✅ "conv-123"
}
```

### Thread Safety

AsyncLocal is thread-safe for async flows but **NOT** for concurrent requests on the same thread. The Conversation class ensures each conversation has its own execution context.

### Cleanup

Always clear context after use to prevent leaks:

```csharp
try
{
    ConversationContext.Set(context);
    await agent.GetResponseAsync(...);
}
finally
{
    ConversationContext.Clear();
}
```

## Future Enhancements

Potential additions as requirements emerge:

1. **Structured Logging Integration**
   ```csharp
   using (_logger.BeginScope(ConversationContext.Current))
   {
       // All logs auto-tagged with conversation context
   }
   ```

2. **Distributed Tracing**
   ```csharp
   Activity.Current?.SetTag("conversation.id", ConversationContext.CurrentConversationId);
   ```

3. **Plugin Lifecycle Hooks**
   ```csharp
   public interface IConversationAwarePlugin
   {
       Task OnConversationStartAsync(ConversationExecutionContext context);
       Task OnConversationEndAsync(ConversationExecutionContext context);
   }
   ```

## Conclusion

The AsyncLocal ambient context pattern is a key architectural decision that enables clean plugin APIs while maintaining conversation-scoped state. By setting context at the Conversation level (not Agent level), we align with our stateful multi-turn architecture and provide a natural extensibility point for future features.

**Key Takeaway**: This isn't a "workaround" - it's the industry-standard solution for providing ambient context to components without polluting APIs with cross-cutting parameters.
