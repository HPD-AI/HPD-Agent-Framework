# IPromptMiddleware Guide

## Overview

`IPromptMiddleware` provides a powerful middleware pattern for:
- **Pre-processing**: Modifying messages, options, and context before LLM invocation
- **Post-processing**: Learning from responses, extracting memories, and analyzing results

This is more flexible than Microsoft's `AIContextProvider` because it offers:
- ✅ Full message transformation (not just addition)
- ✅ ChatOptions modification (tools, instructions, temperature, etc.)
- ✅ Context passing via Properties dictionary
- ✅ Short-circuit capability
- ✅ Bi-directional lifecycle (pre + post hooks)

---

## Interface

```csharp
public interface IPromptMiddleware
{
    // Pre-processing: Modify messages/options before LLM
    Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptMiddlewareContext context,
        Func<PromptMiddlewareContext, Task<IEnumerable<ChatMessage>>> next);

    // Post-processing: Learn from responses (optional, default: no-op)
    Task PostInvokeAsync(PostInvokeContext context, CancellationToken cancellationToken);
}
```

---

## Pre-Processing (InvokeAsync)

### Context Available

```csharp
public class PromptMiddlewareContext
{
    public IEnumerable<ChatMessage> Messages { get; set; }  // Mutable!
    public ChatOptions? Options { get; }                     // Mutable properties!
    public string AgentName { get; }
    public CancellationToken CancellationToken { get; }
    public Dictionary<string, object> Properties { get; }    // Your innovation!
}
```

### Common Patterns

#### 1. Inject Context Messages

```csharp
public async Task<IEnumerable<ChatMessage>> InvokeAsync(
    PromptMiddlewareContext context,
    Func<PromptMiddlewareContext, Task<IEnumerable<ChatMessage>>> next)
{
    if (context.Properties.TryGetValue("Project", out var proj) && proj is Project project)
    {
        var documents = await project.DocumentManager.GetDocumentsAsync();
        var documenTMetadata = BuildDocumenTMetadata(documents);

        // Prepend document context as system message
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, documenTMetadata)
        };
        messages.AddRange(context.Messages);
        context.Messages = messages;
    }

    return await next(context);
}
```

#### 2. Inject Tools Dynamically

```csharp
public async Task<IEnumerable<ChatMessage>> InvokeAsync(
    PromptMiddlewareContext context,
    Func<PromptMiddlewareContext, Task<IEnumerable<ChatMessage>>> next)
{
    if (context.Options != null)
    {
        // Add tools based on context
        context.Options.Tools ??= new List<AITool>();
        context.Options.Tools.Add(CreateSearchTool());
        context.Options.Tools.Add(CreateDocumentQueryTool());
    }

    return await next(context);
}
```

#### 3. Modify Instructions

```csharp
public async Task<IEnumerable<ChatMessage>> InvokeAsync(
    PromptMiddlewareContext context,
    Func<PromptMiddlewareContext, Task<IEnumerable<ChatMessage>>> next)
{
    if (context.Options != null)
    {
        var baseInstructions = context.Options.Instructions ?? "";
        context.Options.Instructions = baseInstructions +
            "\n\nYou have access to 5 project documents. Reference them when answering.";
    }

    return await next(context);
}
```

#### 4. Transform Messages

```csharp
public async Task<IEnumerable<ChatMessage>> InvokeAsync(
    PromptMiddlewareContext context,
    Func<PromptMiddlewareContext, Task<IEnumerable<ChatMessage>>> next)
{
    // Remove duplicate messages
    context.Messages = context.Messages
        .GroupBy(m => m.Text)
        .Select(g => g.First());

    // Or redact sensitive information
    context.Messages = context.Messages.Select(m =>
        new ChatMessage(m.Role, RedactSensitiveData(m.Text)));

    return await next(context);
}
```

#### 5. Short-Circuit

```csharp
public async Task<IEnumerable<ChatMessage>> InvokeAsync(
    PromptMiddlewareContext context,
    Func<PromptMiddlewareContext, Task<IEnumerable<ChatMessage>>> next)
{
    // Check cache first
    var cacheKey = ComputeCacheKey(context.Messages);
    if (_cache.TryGetValue(cacheKey, out var cachedResponse))
    {
        // Skip LLM call entirely!
        return cachedResponse;
    }

    return await next(context);
}
```

---

## Post-Processing (PostInvokeAsync)

### Context Available

```csharp
public class PostInvokeContext
{
    public IEnumerable<ChatMessage> RequestMessages { get; }   // What was sent to LLM
    public IEnumerable<ChatMessage>? ResponseMessages { get; } // LLM response (null if failed)
    public Exception? Exception { get; }                       // Error if failed
    public IReadOnlyDictionary<string, object> Properties { get; } // Shared context
    public string AgentName { get; }
    public ChatOptions? Options { get; }

    public bool IsSuccess => Exception == null && ResponseMessages != null;
    public bool IsFailure => !IsSuccess;
}
```

### Common Patterns

#### 1. Extract Memories

```csharp
public async Task PostInvokeAsync(PostInvokeContext context, CancellationToken cancellationToken)
{
    if (!context.IsSuccess) return;

    var assistantMessages = context.ResponseMessages!
        .Where(m => m.Role == ChatRole.Assistant);

    foreach (var message in assistantMessages)
    {
        // Extract facts the assistant wants to remember
        var facts = ExtractRememberTags(message.Text);
        foreach (var fact in facts)
        {
            await _memoryManager.AddMemoryAsync(context.AgentName, "Fact", fact);
        }
    }
}
```

#### 2. Track Document Usage

```csharp
public async Task PostInvokeAsync(PostInvokeContext context, CancellationToken cancellationToken)
{
    if (!context.IsSuccess) return;

    var assistantText = string.Join(" ", context.ResponseMessages!
        .Where(m => m.Role == ChatRole.Assistant)
        .Select(m => m.Text));

    // Analyze which documents were referenced
    foreach (var doc in _injectedDocuments)
    {
        if (assistantText.Contains(doc.FileName))
        {
            await UpdateDocumentRelevanceScore(doc, +1);
        }
    }
}
```

#### 3. Analytics and Logging

```csharp
public async Task PostInvokeAsync(PostInvokeContext context, CancellationToken cancellationToken)
{
    // Log conversation details
    await _analytics.LogConversationAsync(new
    {
        AgentName = context.AgentName,
        Success = context.IsSuccess,
        MessageCount = context.RequestMessages.Count(),
        ResponseLength = context.ResponseMessages?.Sum(m => m.Text?.Length ?? 0),
        Error = context.Exception?.Message
    });
}
```

#### 4. Update Knowledge Base

```csharp
public async Task PostInvokeAsync(PostInvokeContext context, CancellationToken cancellationToken)
{
    if (!context.IsSuccess) return;

    // Extract question-answer pairs for future retrieval
    var userQuestion = context.RequestMessages
        .LastOrDefault(m => m.Role == ChatRole.User)?.Text;

    var assistantAnswer = context.ResponseMessages!
        .FirstOrDefault(m => m.Role == ChatRole.Assistant)?.Text;

    if (userQuestion != null && assistantAnswer != null)
    {
        await _knowledgeBase.AddQAPairAsync(userQuestion, assistantAnswer);
    }
}
```

---

## Execution Order

```
1. Pre-Processing Pipeline (sequential, reversed):
   Filter3.InvokeAsync() → calls next →
   Filter2.InvokeAsync() → calls next →
   Filter1.InvokeAsync() → calls next →
   [Messages returned to Agent]

2. Agent prepares and sends to LLM

3. LLM responds (or errors)

4. Post-Processing (sequential, forward order):
   Filter1.PostInvokeAsync()
   Filter2.PostInvokeAsync()
   Filter3.PostInvokeAsync()

5. Response returned to user
```

**Note:** Post-processing runs even if LLM call fails (check `context.Exception`).

---

## Comparison with Microsoft's AIContextProvider

| Feature | IPromptMiddleware | AIContextProvider |
|---------|---------------|-------------------|
| **Add messages** | ✅ `context.Messages = ...` | ✅ `AIContext.Messages` |
| **Transform messages** | ✅ Full control | ❌ Read-only input |
| **Add tools** | ✅ `context.Options.Tools.Add(...)` | ✅ `AIContext.Tools` |
| **Modify instructions** | ✅ `context.Options.Instructions` | ✅ `AIContext.Instructions` |
| **Context passing** | ✅ `context.Properties["Project"]` | ❌ Must inject at construction |
| **Short-circuit** | ✅ Don't call `next()` | ❌ All providers always run |
| **Post-processing** | ✅ `PostInvokeAsync` | ✅ `InvokedAsync` |
| **Execution** | Sequential pipeline | Parallel merge |
| **Error handling** | ✅ Post-invoke sees exception | ✅ InvokedContext.Exception |

**Your IPromptMiddleware is strictly more powerful!**

---

## Best Practices

### 1. Use Properties for Context Passing

```csharp
// In Conversation.cs
private Dictionary<string, object> BuildConversationContext()
{
    return new Dictionary<string, object>
    {
        ["ConversationId"] = Id,
        ["Project"] = _project  // ✅ Clean context passing
    };
}

// In Filter
if (context.Properties.TryGetValue("Project", out var proj))
{
    // Use project
}
```

### 2. Make Post-Processing Optional

Default implementation in interface is no-op - only override if needed:

```csharp
public class MyFilter : IPromptMiddleware
{
    public async Task<IEnumerable<ChatMessage>> InvokeAsync(...) { /* required */ }

    // Optional - only override if you need post-processing
    public async Task PostInvokeAsync(PostInvokeContext context, CancellationToken ct)
    {
        // Extract memories, etc.
    }
}
```

### 3. Handle Errors Gracefully

Post-processing is best-effort - don't throw exceptions:

```csharp
public async Task PostInvokeAsync(PostInvokeContext context, CancellationToken ct)
{
    try
    {
        if (context.IsSuccess)
        {
            await ExtractMemories(context.ResponseMessages!);
        }
    }
    catch (Exception ex)
    {
        // Log but don't throw - don't break the response
        _logger?.LogWarning(ex, "Memory extraction failed");
    }
}
```

### 4. Cache Wisely

```csharp
private string? _cachedContext;
private DateTime _lastCacheTime;

public async Task<IEnumerable<ChatMessage>> InvokeAsync(...)
{
    if ((DateTime.UtcNow - _lastCacheTime) < TimeSpan.FromMinutes(2))
    {
        // Use cached context
    }
    else
    {
        // Rebuild and cache
    }
}
```

---

## Examples

See:
- `ExampleMemoryExtractionFilter.cs` - Memory extraction from responses
- `ExampleDocumentUsageFilter.cs` - Document relevance tracking
- `AgentInjectedMemoryFilter.cs` - Production memory injection
- `ProjectInjectedMemoryFilter.cs` - Production document injection

---

## Summary

**IPromptMiddleware gives you everything AIContextProvider has, plus:**
- Message transformation (not just addition)
- Short-circuit capability
- Clean context passing via Properties
- ChatOptions modification

**Keep using IPromptMiddleware for everything. It's superior.**
