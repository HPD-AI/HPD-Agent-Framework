# ConversationContext Usage Examples

This document shows practical examples of using the extensible `ConversationContext` in plugins.

## Basic Usage (Backwards Compatible)

Your existing Plan Mode code continues to work without changes:

```csharp
public class AgentPlanPlugin
{
    [AIFunction]
    public Task<string> CreatePlanAsync(string goal, string[] steps)
    {
        // OLD WAY - still works!
        var conversationId = ConversationContext.CurrentConversationId;

        if (string.IsNullOrEmpty(conversationId))
        {
            return Task.FromResult("Error: No conversation context available.");
        }

        var plan = _manager.CreatePlan(conversationId, goal, steps);
        return Task.FromResult($"Created plan {plan.Id}");
    }
}
```

## Advanced Usage: Accessing Rich Context

New plugins can access the full context for advanced features:

### Example 1: Adaptive Search Plugin

```csharp
public class SearchPlugin
{
    [AIFunction]
    [Description("Searches codebase for patterns")]
    public async Task<string> SearchCodebaseAsync(string pattern)
    {
        var ctx = ConversationContext.Current;

        // Check how many times we've searched already
        var searchCount = ctx?.RunContext?.CompletedFunctions
            .Count(f => f == "SearchCodebaseAsync") ?? 0;

        if (searchCount >= 3)
        {
            return "Searched 3 times already. Please refine your search pattern.";
        }

        // Check if we're running out of time
        if (ctx?.IsNearTimeout(TimeSpan.FromSeconds(30)) == true)
        {
            // Do a fast search instead of deep search
            return await QuickSearchAsync(pattern);
        }

        return await DeepSearchAsync(pattern);
    }
}
```

### Example 2: Budget-Aware Analysis Plugin

```csharp
public class AnalysisPlugin
{
    [AIFunction]
    [Description("Performs code quality analysis")]
    public async Task<string> AnalyzeCodeAsync(string filePath)
    {
        var ctx = ConversationContext.Current;

        // If we're near the iteration limit, give a quick summary
        if (ctx?.IsNearIterationLimit(buffer: 2) == true)
        {
            return await QuickAnalysisAsync(filePath);
        }

        // We have budget for deep analysis
        return await DeepAnalysisAsync(filePath);
    }
}
```

### Example 3: Cross-Plugin Data Sharing

```csharp
public class WebSearchPlugin
{
    [AIFunction]
    [Description("Searches the web for information")]
    public async Task<string> SearchWebAsync(string query)
    {
        var ctx = ConversationContext.Current;

        var results = await _searchService.SearchAsync(query);

        // Store results in metadata for other plugins to access
        ctx?.Metadata["lastWebSearchResults"] = results;
        ctx?.Metadata["lastWebSearchQuery"] = query;

        return FormatResults(results);
    }
}

public class SummarizePlugin
{
    [AIFunction]
    [Description("Summarizes previous web search results")]
    public Task<string> SummarizeLastSearchAsync()
    {
        var ctx = ConversationContext.Current;

        // Retrieve data stored by WebSearchPlugin
        if (ctx?.Metadata.TryGetValue("lastWebSearchResults", out var resultsObj) != true)
        {
            return Task.FromResult("No previous search results available.");
        }

        var results = resultsObj as List<SearchResult>;
        var summary = GenerateSummary(results);

        return Task.FromResult(summary);
    }
}
```

### Example 4: Automatic Telemetry

```csharp
public class TelemetryAwarePlugin
{
    private readonly ITelemetryClient _telemetry;

    [AIFunction]
    [Description("Processes data with automatic telemetry")]
    public async Task<string> ProcessDataAsync(string data)
    {
        var ctx = ConversationContext.Current;

        // Automatic context-aware telemetry
        using var operation = _telemetry.StartOperation("ProcessData");
        operation.Telemetry.Properties["ConversationId"] = ctx?.ConversationId ?? "unknown";
        operation.Telemetry.Properties["AgentName"] = ctx?.AgentName ?? "unknown";
        operation.Telemetry.Properties["Iteration"] = ctx?.CurrentIteration.ToString() ?? "0";
        operation.Telemetry.Properties["ElapsedTime"] = ctx?.ElapsedTime.ToString() ?? "0";

        try
        {
            var result = await ProcessAsync(data);
            operation.Telemetry.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            operation.Telemetry.Success = false;
            _telemetry.TrackException(ex);
            throw;
        }
    }
}
```

### Example 5: Context-Aware Logging

```csharp
public class LoggingAwarePlugin
{
    private readonly ILogger<LoggingAwarePlugin> _logger;

    [AIFunction]
    [Description("Executes task with enriched logging")]
    public async Task<string> ExecuteTaskAsync(string task)
    {
        var ctx = ConversationContext.Current;

        // Create enriched log scope
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["ConversationId"] = ctx?.ConversationId ?? "unknown",
            ["AgentName"] = ctx?.AgentName ?? "unknown",
            ["Iteration"] = ctx?.CurrentIteration ?? 0,
            ["ElapsedTime"] = ctx?.ElapsedTime.ToString() ?? "0"
        }))
        {
            _logger.LogInformation("Starting task execution");

            var result = await ExecuteAsync(task);

            _logger.LogInformation("Task completed successfully");
            return result;
        }
    }
}
```

### Example 6: Memory Plugin with Cache

```csharp
public class MemoryPlugin
{
    private readonly IMemoryStore _memoryStore;

    [AIFunction]
    [Description("Saves information to long-term memory")]
    public async Task<string> SaveToMemoryAsync(string key, string value)
    {
        var ctx = ConversationContext.Current;

        // Cache in conversation metadata for fast access
        ctx?.Metadata[$"memory.{key}"] = value;

        // Persist to storage
        await _memoryStore.SaveAsync(ctx?.ConversationId, key, value);

        return $"Saved '{key}' to memory";
    }

    [AIFunction]
    [Description("Retrieves information from memory")]
    public async Task<string> GetFromMemoryAsync(string key)
    {
        var ctx = ConversationContext.Current;

        // Try conversation cache first (fast)
        if (ctx?.Metadata.TryGetValue($"memory.{key}", out var cachedValue) == true)
        {
            return cachedValue?.ToString() ?? "null";
        }

        // Fallback to storage (slower)
        var value = await _memoryStore.GetAsync(ctx?.ConversationId, key);

        // Populate cache for future calls
        if (ctx != null && value != null)
        {
            ctx.Metadata[$"memory.{key}"] = value;
        }

        return value ?? "null";
    }
}
```

### Example 7: Self-Terminating Tool

```csharp
public class IntensiveAnalysisPlugin
{
    [AIFunction]
    [Description("Performs intensive multi-step analysis")]
    public async Task<string> DeepAnalysisAsync(string target)
    {
        var ctx = ConversationContext.Current;

        // Step 1: Quick check
        var quickResult = await QuickCheckAsync(target);

        // Check if we should continue based on runtime state
        if (ctx?.IsNearTimeout(TimeSpan.FromSeconds(45)) == true)
        {
            // Signal termination to prevent timeout
            if (ctx.RunContext != null)
            {
                ctx.RunContext.IsTerminated = true;
                ctx.RunContext.TerminationReason = "Stopping early to avoid timeout";
            }

            return $"Quick analysis only: {quickResult}";
        }

        // Step 2: Deep analysis (we have time)
        var deepResult = await DeepCheckAsync(target);

        return $"Complete analysis:\nQuick: {quickResult}\nDeep: {deepResult}";
    }
}
```

### Example 8: Plugin Coordination

```csharp
public class ResearchPlugin
{
    [AIFunction]
    [Description("Researches a topic using multiple sources")]
    public async Task<string> ResearchTopicAsync(string topic)
    {
        var ctx = ConversationContext.Current;

        // Mark that research is in progress
        ctx?.Metadata.Add("research.inProgress", true);
        ctx?.Metadata.Add("research.topic", topic);

        try
        {
            var webResults = await SearchWebAsync(topic);
            var codeResults = await SearchCodebaseAsync(topic);
            var memoryResults = await SearchMemoryAsync(topic);

            var combined = CombineResults(webResults, codeResults, memoryResults);

            // Store for other plugins
            ctx?.Metadata.Add("research.results", combined);

            return combined;
        }
        finally
        {
            ctx?.Metadata.Remove("research.inProgress");
        }
    }
}

public class QuestionAnswerPlugin
{
    [AIFunction]
    [Description("Answers questions using available research")]
    public Task<string> AnswerQuestionAsync(string question)
    {
        var ctx = ConversationContext.Current;

        // Check if research is available
        if (ctx?.Metadata.TryGetValue("research.results", out var results) == true)
        {
            return Task.FromResult($"Based on research: {AnswerFromResults(question, results)}");
        }

        return Task.FromResult("No research data available. Use ResearchTopic first.");
    }
}
```

## Migration Guide

### Phase 1: Keep Using CurrentConversationId (Now)

```csharp
// Existing code - no changes needed
var id = ConversationContext.CurrentConversationId;
```

### Phase 2: Access Full Context (When You Need It)

```csharp
// New code can access rich context
var ctx = ConversationContext.Current;
var iteration = ctx?.CurrentIteration;
var isNearEnd = ctx?.IsNearIterationLimit();
```

### Phase 3: Add New Properties (Future)

```csharp
// As requirements emerge, extend ConversationExecutionContext
public class ConversationExecutionContext
{
    // Future additions:
    public IReadOnlyList<ChatMessage> History { get; init; }
    public ConversationSettings Settings { get; init; }
    public UserProfile? User { get; init; }
}
```

## Best Practices

### ✅ DO:
- Always check for null before accessing context
- Use namespaced metadata keys: `"myPlugin.key"`
- Clean up metadata when no longer needed
- Use context for cross-cutting concerns (logging, telemetry, coordination)
- Use helper methods: `IsNearTimeout()`, `IsNearIterationLimit()`

### ❌ DON'T:
- Don't modify `ConversationId` or other core properties
- Don't use context for business logic parameters (use function parameters)
- Don't store large objects in metadata (use references/IDs)
- Don't assume context is always available (null check!)
- Don't use non-namespaced metadata keys (risk of collisions)

## Error Handling

```csharp
[AIFunction]
public Task<string> MyFunctionAsync(string input)
{
    var ctx = ConversationContext.Current;

    // ALWAYS check for null
    if (ctx == null)
    {
        // Decide: fail gracefully or return error?
        _logger.LogWarning("No conversation context available");
        return Task.FromResult("Warning: No conversation context");
    }

    // Safe to use context
    var conversationId = ctx.ConversationId;
    // ...
}
```

## Testing

```csharp
[Test]
public async Task TestPluginWithContext()
{
    // Arrange
    var context = new ConversationExecutionContext("test-conversation-123")
    {
        AgentName = "TestAgent"
    };

    ConversationContext.Set(context);

    try
    {
        // Act
        var plugin = new MyPlugin();
        var result = await plugin.MyFunctionAsync("test");

        // Assert
        Assert.That(result, Is.Not.Null);
    }
    finally
    {
        // Cleanup
        ConversationContext.Clear();
    }
}
```

## Summary

The extensible `ConversationContext` enables:

1. **Backwards Compatibility**: Existing code using `CurrentConversationId` continues to work
2. **Rich Context Access**: New code can access iteration, timing, and runtime state
3. **Plugin Coordination**: Metadata enables sharing data between plugins
4. **Adaptive Behavior**: Tools can adjust based on available time/iterations
5. **Automatic Telemetry**: Context enriches logs and metrics without boilerplate

Start simple with `CurrentConversationId`, then gradually adopt `Current` as your needs grow.
