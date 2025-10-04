# Chat History Reduction Architecture

## Overview

This document explains the chat history reduction mechanism that prevents unbounded conversation growth and manages LLM context windows efficiently.

## The Problem We Solved

### Before (Broken Architecture)
```
Turn 1 (50 messages):
  Conversation._messages: [M1-M50]
  Agent reduces → Sends [Summary] [M31-M50] to LLM ✅
  LLM responds
  Conversation stores response → [M1-M50] [R1] (51 messages) ❌

Turn 2 (51 messages):
  Conversation._messages: [M1-M51]
  Agent RE-SUMMARIZES all 51 messages ❌ (wasteful!)
  Agent → Sends [Summary] [M32-M51] to LLM ✅
  Conversation stores → [M1-M51] [R2] (52 messages) ❌

Result: Re-summarization every turn, full history keeps growing
```

**Critical Flaw:** Agent reduced messages for LLM calls, but Conversation stored full unreduced history, causing redundant summarization work every single turn.

### After (Fixed Architecture)
```
Turn 1 (50 messages):
  Conversation._messages: [M1-M50]
  Agent reduces → [Summary] [M31-M50]
  Agent signals: "I created a summary, removed 30 messages"
  LLM responds
  Conversation applies reduction: Remove M1-M30, Insert Summary
  Conversation._messages: [Summary] [M31-M50] [R1] (22 messages) ✅

Turn 2 (22 messages):
  Conversation._messages: [Summary] [M31-M50] [R1]
  Agent checks: "Summary found, only 21 messages after it"
  21 < 25 (threshold) → Skip reduction (cache hit!) ✅
  Agent → Sends [Summary] [M31-M50] [R1] to LLM
  Conversation stores: [Summary] [M31-M50] [R1] [R2] (23 messages) ✅

Turn 7 (56 messages after summary):
  Agent checks: 56 > 25 → Reduction needed!
  Re-summarize: [Summary] + [M31-M50] + [R1-R6] → [NewSummary]
  Conversation applies: [NewSummary] [Recent 20] ✅
```

**Key Innovation:** Cache-aware reduction that checks for existing summaries and only re-reduces when threshold exceeded.

---

## Architecture Components

### 1. Configuration (`AgentConfig.cs`)

```csharp
public class HistoryReductionConfig
{
    public bool Enabled { get; set; } = false;
    public HistoryReductionStrategy Strategy { get; set; } = HistoryReductionStrategy.MessageCounting;
    public int TargetMessageCount { get; set; } = 20;
    public int? SummarizationThreshold { get; set; } = 5;

    // Summary marker for cache detection
    public const string SummaryMetadataKey = "__summary__";

    // Re-summarize everything vs layered summaries
    public bool UseSingleSummary { get; set; } = true;

    // Cost optimization: cheaper model for summaries
    public ProviderConfig? SummarizerProvider { get; set; }
}

public enum HistoryReductionStrategy
{
    MessageCounting,  // Keep last N messages (fast, simple)
    Summarizing       // LLM-based summarization (preserves context)
}
```

### 2. Agent Components

#### A. Reduction Metadata Record (`Agent.cs`)
```csharp
// NEW: Metadata is now returned via result object, not stored in agent
public record ReductionMetadata
{
    public ChatMessage? SummaryMessage { get; init; }
    public int MessagesRemovedCount { get; init; }
}
```

#### B. StreamingTurnResult with Reduction (`Agent.cs`)
```csharp
public class StreamingTurnResult
{
    public IAsyncEnumerable<BaseEvent> EventStream { get; }
    public Task<IReadOnlyList<ChatMessage>> FinalHistory { get; }

    // NEW: Reduction metadata returned in result, not stored in agent instance
    public ReductionMetadata? Reduction { get; init; }
}
```

**Key Change:** Agent is now **stateless** - reduction metadata is returned via `StreamingTurnResult.Reduction` instead of being stored in agent instance fields. This allows safe agent reuse across multiple conversations.

#### C. MessageProcessor - Cache-Aware Reduction (`Agent.cs:1239-1300`)

**The Brain of the System:**

```csharp
public async Task<(IEnumerable<ChatMessage>, ChatOptions?)> PrepareMessagesAsync(...)
{
    if (_chatReducer != null)
    {
        var messagesList = effectiveMessages.ToList();

        // 🔍 CACHE CHECK: Look for existing summary marker
        var lastSummaryIndex = messagesList.FindLastIndex(m =>
            m.AdditionalProperties?.ContainsKey(HistoryReductionConfig.SummaryMetadataKey) == true);

        bool shouldReduce = false;

        if (lastSummaryIndex >= 0)
        {
            // Summary found - only count messages AFTER it
            var messagesAfterSummary = messagesList.Count - lastSummaryIndex - 1;
            shouldReduce = messagesAfterSummary > (targetCount + threshold);
        }
        else
        {
            // No summary - check total count
            shouldReduce = messagesList.Count > (targetCount + threshold);
        }

        if (shouldReduce)
        {
            // Perform reduction
            var reduced = await _chatReducer.ReduceAsync(effectiveMessages, cancellationToken);

            // Extract summary and count removed messages
            var summaryMsg = reducedList.FirstOrDefault(m =>
                m.AdditionalProperties?.ContainsKey(HistoryReductionConfig.SummaryMetadataKey) == true);

            if (summaryMsg != null)
            {
                int removedCount = messagesList.Count - reducedList.Count + 1;
                LastReductionMetadata = (summaryMsg, removedCount);
            }

            effectiveMessages = reducedList;
        }
        else
        {
            // Clear metadata - no reduction this turn
            LastReductionMetadata = null;
        }
    }

    return (effectiveMessages, effectiveOptions);
}
```

**Cache Optimization:**
- ✅ Checks for `__summary__` marker in message `AdditionalProperties`
- ✅ Only counts messages after last summary
- ✅ Skips reduction if under threshold (saves LLM calls!)

#### D. Metadata Capture in ExecuteStreamingTurnAsync (`Agent.cs`)
```csharp
public async Task<StreamingTurnResult> ExecuteStreamingTurnAsync(...)
{
    // ... streaming logic ...

    // Capture reduction metadata from MessageProcessor
    ReductionMetadata? reductionMetadata = null;
    if (_messageProcessor.LastReductionMetadata.HasValue)
    {
        var metadata = _messageProcessor.LastReductionMetadata.Value;
        reductionMetadata = new ReductionMetadata
        {
            SummaryMessage = metadata.SummaryMessage,
            MessagesRemovedCount = metadata.RemovedCount
        };
    }

    // Return metadata in result object (not stored in agent instance)
    return new StreamingTurnResult(responseStream, wrappedHistoryTask)
    {
        Reduction = reductionMetadata
    };
}
```

### 3. Conversation Components

#### A. Helper Method (`Conversation.cs:567-591`)
```csharp
private void ApplyReductionIfPresent(OrchestrationResult result)
{
    var context = result.Metadata.Context;

    if (context.TryGetValue("SummaryMessage", out var summaryObj) &&
        summaryObj is ChatMessage summary &&
        context.TryGetValue("MessagesRemovedCount", out var countObj) &&
        countObj is int count)
    {
        // Preserve system messages
        int systemMsgCount = _messages.Count(m => m.Role == ChatRole.System);

        // Remove old summarized messages
        _messages.RemoveRange(systemMsgCount, count);

        // Insert summary right after system messages
        _messages.Insert(systemMsgCount, summary);

        // Clear agent's metadata
        result.SelectedAgent?.ClearReductionMetadata();
    }
}
```

#### B. Integration Points

**SendAsync - Single Agent** (`Conversation.cs`)
```csharp
// NEW: Use ExecuteStreamingTurnAsync to get reduction metadata
var streamingResult = await agent.ExecuteStreamingTurnAsync(_messages, options, cancellationToken: cancellationToken);

// Consume stream
await foreach (var _ in streamingResult.EventStream.WithCancellation(cancellationToken)) { }

// Get final history
var finalHistory = await streamingResult.FinalHistory;

// Package reduction metadata into Context dictionary
var reductionContext = new Dictionary<string, object>();
if (streamingResult.Reduction != null)
{
    if (streamingResult.Reduction.SummaryMessage != null)
    {
        reductionContext["SummaryMessage"] = streamingResult.Reduction.SummaryMessage;
    }
    reductionContext["MessagesRemovedCount"] = streamingResult.Reduction.MessagesRemovedCount;
}

orchestrationResult = new OrchestrationResult
{
    Response = new ChatResponse(finalHistory.ToList()),
    SelectedAgent = agent,
    Metadata = new OrchestrationMetadata
    {
        StrategyName = "SingleAgent",
        DecisionDuration = TimeSpan.Zero,
        Context = reductionContext // ← Include metadata from result, not agent
    }
};
```

**Apply Reduction** (`Conversation.cs:237-238`)
```csharp
// BEFORE adding response to history
ApplyReductionIfPresent(orchestrationResult);

// Then add response
_messages.AddMessages(orchestrationResult.Response);
```

**Streaming Path** (`Conversation.cs`)
```csharp
// NEW: Get reduction metadata from StreamingTurnResult
var result = await agent.ExecuteStreamingTurnAsync(_messages, options, documentPaths, cancellationToken);

// Stream events
await foreach (var evt in result.EventStream.WithCancellation(cancellationToken))
{
    yield return evt;
}

// Wait for final history
var finalHistory = await result.FinalHistory;

// Apply reduction from result metadata
if (result.Reduction != null)
{
    int systemMsgCount = _messages.Count(m => m.Role == ChatRole.System);
    _messages.RemoveRange(systemMsgCount, result.Reduction.MessagesRemovedCount);

    if (result.Reduction.SummaryMessage != null)
    {
        _messages.Insert(systemMsgCount, result.Reduction.SummaryMessage);
    }
}

_messages.AddRange(finalHistory);
```

---

## Data Flow

### Complete Turn Flow

```
1. User sends message
   ↓
2. Conversation adds to _messages
   ↓
3. Agent.PrepareMessagesAsync() (in RunAgenticLoopCore)
   ├─ Check for __summary__ marker
   ├─ Count messages after summary
   ├─ If > threshold: Reduce
   ├─ Extract summary message
   └─ Store in MessageProcessor.LastReductionMetadata
   ↓
4. Agent.ExecuteStreamingTurnAsync captures metadata
   ├─ Read from MessageProcessor.LastReductionMetadata
   ├─ Create ReductionMetadata object
   └─ Return in StreamingTurnResult.Reduction property
   ↓
5. Agent sends reduced messages to LLM
   ↓
6. LLM responds
   ↓
7. Conversation receives StreamingTurnResult
   └─ result.Reduction contains ReductionMetadata (if reduction occurred)
   ↓
8. For SendAsync: Package into OrchestrationMetadata.Context
   └─ Context["SummaryMessage"] = result.Reduction.SummaryMessage
   └─ Context["MessagesRemovedCount"] = result.Reduction.MessagesRemovedCount
   ↓
9. Conversation.ApplyReductionIfPresent() or direct application
   ├─ Remove old messages: RemoveRange(systemMsgCount, count)
   ├─ Insert summary: Insert(systemMsgCount, summary)
   └─ No need to clear metadata (not stored in agent)
   ↓
10. Conversation adds response to _messages
```

### Metadata Flow via StreamingTurnResult and OrchestrationMetadata.Context

**NEW Pattern (Stateless Agent):**
- ✅ Agent returns metadata via `StreamingTurnResult.Reduction` property
- ✅ Metadata is **not stored** in agent instance (agent is stateless)
- ✅ Agent can be safely reused across multiple conversations
- ✅ For orchestrators: Convert `Reduction` → `OrchestrationMetadata.Context`

```csharp
// Agent side (NEW)
StreamingTurnResult.Reduction → ReductionMetadata object (or null)

// For orchestrators: Convert to Context dictionary
if (streamingResult.Reduction != null)
{
    context["SummaryMessage"] = streamingResult.Reduction.SummaryMessage;
    context["MessagesRemovedCount"] = streamingResult.Reduction.MessagesRemovedCount;
}

// Flows through
OrchestrationResult.Metadata.Context → Dictionary<string, object>

// Conversation extracts
context.TryGetValue("SummaryMessage", ...) → ChatMessage
context.TryGetValue("MessagesRemovedCount", ...) → int
```

---

## Summary Message Format

**Important:** Microsoft.Extensions.AI's `ChatMessage` uses `AdditionalProperties`, **not** `Metadata`:

```csharp
var summaryMessage = new ChatMessage(ChatRole.Assistant, summaryText);

// ✅ Correct
summaryMessage.AdditionalProperties[HistoryReductionConfig.SummaryMetadataKey] = true;
summaryMessage.AdditionalProperties["__created_at__"] = DateTime.UtcNow;
summaryMessage.AdditionalProperties["__summarized_range__"] = "messages_0_to_30";

// ❌ Wrong - will not work
summaryMessage.Metadata["__summary__"] = true; // Property doesn't exist!
```

---

## Usage Examples

### Basic Setup

```csharp
var agent = Agent.CreateBuilder()
    .WithChatProvider(ChatProvider.OpenAI, "gpt-4o", apiKey)
    .WithSystemInstructions("You are a helpful assistant")
    .WithHistoryReduction(new HistoryReductionConfig
    {
        Enabled = true,
        Strategy = HistoryReductionStrategy.Summarizing,
        TargetMessageCount = 20,
        SummarizationThreshold = 5,
        UseSingleSummary = true
    })
    .Build();

var conversation = Conversation.Create(new[] { agent });
```

### Cost Optimization - Separate Summarizer

```csharp
.WithHistoryReduction(new HistoryReductionConfig
{
    Enabled = true,
    Strategy = HistoryReductionStrategy.Summarizing,
    TargetMessageCount = 20,
    SummarizationThreshold = 5,

    // Use cheaper model for summaries
    SummarizerProvider = new ProviderConfig
    {
        Provider = ChatProvider.OpenAI,
        ModelName = "gpt-4o-mini", // Cheap model for summaries
        ApiKey = apiKey
    }
    // Main agent still uses gpt-4o for conversations
})
```

### Single vs Layered Summaries

```csharp
// Option 1: Single Summary (default, better quality)
UseSingleSummary = true
// Result: [ComprehensiveSummary] [Recent20]
// Old summary is RE-SUMMARIZED into new summary
// Pro: No "telephone game" effect, consistent quality
// Con: More expensive over time

// Option 2: Layered Summaries (cheaper)
UseSingleSummary = false
// Result: [Summary1] [Summary2] [Summary3] [Recent20]
// Each summary is kept separate
// Pro: Cheaper (incremental summarization)
// Con: Token count can grow, potential quality degradation
```

---

## For Orchestrator Implementers

If you're creating a custom orchestrator, **you must pass through reduction metadata** from `StreamingTurnResult.Reduction`:

```csharp
public class MyCustomOrchestrator : IOrchestrator
{
    public async Task<OrchestrationResult> OrchestrateAsync(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<Agent> agents,
        string? conversationId = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Your agent selection logic
        var selectedAgent = SelectBestAgent(history, agents);

        // 2. Call the agent using ExecuteStreamingTurnAsync
        var streamingResult = await selectedAgent.ExecuteStreamingTurnAsync(
            history, options, cancellationToken: cancellationToken);

        // 3. Consume the stream
        await foreach (var evt in streamingResult.EventStream.WithCancellation(cancellationToken))
        {
            // Process events as needed
        }

        // 4. Get final history
        var finalHistory = await streamingResult.FinalHistory;

        // 5. Package reduction metadata into Context dictionary
        var reductionContext = new Dictionary<string, object>();
        if (streamingResult.Reduction != null)
        {
            if (streamingResult.Reduction.SummaryMessage != null)
            {
                reductionContext["SummaryMessage"] = streamingResult.Reduction.SummaryMessage;
            }
            reductionContext["MessagesRemovedCount"] = streamingResult.Reduction.MessagesRemovedCount;
        }

        // 6. Return orchestration result
        return new OrchestrationResult
        {
            Response = new ChatResponse(finalHistory.ToList()),
            SelectedAgent = selectedAgent,
            Metadata = new OrchestrationMetadata
            {
                StrategyName = "MyStrategy",
                DecisionDuration = TimeSpan.Zero,
                Context = reductionContext // ← CRITICAL: Include reduction metadata!
            }
        };
    }
}
```

**CRITICAL:** Without packaging `streamingResult.Reduction` into `Context`, reduction won't be applied to Conversation storage!

---

## Performance Characteristics

### Before (Broken)
```
Turn 1 (50 msgs): Summarize 50 → LLM call
Turn 2 (51 msgs): Summarize 51 → LLM call ❌ redundant
Turn 3 (52 msgs): Summarize 52 → LLM call ❌ redundant
Turn 4 (53 msgs): Summarize 53 → LLM call ❌ redundant
Turn 5 (54 msgs): Summarize 54 → LLM call ❌ redundant

Cost: 5 summarization LLM calls
```

### After (Fixed with Cache)
```
Turn 1 (50 msgs): Summarize 50 → LLM call ✅
Turn 2 (21 msgs after summary): Cache hit, skip ✅
Turn 3 (22 msgs after summary): Cache hit, skip ✅
Turn 4 (23 msgs after summary): Cache hit, skip ✅
Turn 5 (24 msgs after summary): Cache hit, skip ✅
Turn 6 (25 msgs after summary): Cache hit, skip ✅
Turn 7 (26 msgs after summary): Threshold! Re-summarize → LLM call ✅

Cost: 2 summarization LLM calls
Savings: 60% reduction in summarization costs!
```

---

## Design Patterns Used

### 1. Semantic Kernel Pattern - Single Storage
- Conversation maintains one `_messages` list
- In-place mutation when reduction occurs
- Old messages are permanently removed (trade-off: memory efficiency > audit trail)

### 2. Cache-Aware Processing
- Agent checks for existing summary markers
- Only reduces when necessary
- Avoids redundant work

### 3. Metadata Signaling
- Agent doesn't mutate Conversation's storage
- Agent signals what it did via metadata
- Conversation decides how to apply changes

### 4. Separation of Concerns
- **Agent**: Detects need, performs reduction, signals
- **Conversation**: Manages storage, applies reduction
- **Orchestrator**: Transparent pass-through (no knowledge of reduction)

---

## Edge Cases Handled

### 1. System Message Preservation
```csharp
int systemMsgCount = _messages.Count(m => m.Role == ChatRole.System);
_messages.RemoveRange(systemMsgCount, count); // Remove AFTER system
_messages.Insert(systemMsgCount, summary);     // Insert AFTER system
```

### 2. Function Call Orphaning Prevention
Reducers must never separate function calls from their results:
```csharp
// If reduction would orphan function content, skip to keep pairs together
while (truncationIndex < chatHistory.Count)
{
    if (chatHistory[truncationIndex].Items.Any(i =>
        i is FunctionCallContent || i is FunctionResultContent))
    {
        truncationIndex++; // Keep function call/result pairs together
    }
    else break;
}
```

### 3. Re-threshold Detection
When messages after summary exceed threshold again:
```csharp
if (lastSummaryIndex >= 0)
{
    var messagesAfterSummary = messagesList.Count - lastSummaryIndex - 1;
    if (messagesAfterSummary > (targetCount + threshold))
    {
        // Time to re-summarize!
    }
}
```

---

## Troubleshooting

### Problem: Reduction not happening

**Check:**
1. Is `HistoryReductionConfig.Enabled = true`?
2. Do you have enough messages? Need `> TargetMessageCount + SummarizationThreshold`
3. Is the reducer configured correctly via `WithHistoryReduction()`?

### Problem: Re-summarizing every turn (no cache benefit)

**Check:**
1. Are summary messages being marked with `__summary__`?
2. Is the marker in `AdditionalProperties` (not `Metadata`)?
3. Is Conversation actually calling `ApplyReductionIfPresent()`?

### Problem: Orchestrator doesn't pass through reduction

**Fix:**
```csharp
// In your orchestrator's OrchestrateAsync:
Context = selectedAgent.GetReductionMetadata() // Add this!
```

### Problem: Messages growing unbounded

**Symptoms:** Storage keeps growing despite reduction enabled

**Causes:**
1. Reduction metadata not flowing through orchestrator
2. `ApplyReductionIfPresent()` not being called
3. Summary marker not being detected (wrong property name)

**Debug:**
```csharp
// Add logging in ApplyReductionIfPresent
Console.WriteLine($"Reduction metadata: {context.ContainsKey("SummaryMessage")}");
Console.WriteLine($"Messages before: {_messages.Count}");
// ... apply reduction ...
Console.WriteLine($"Messages after: {_messages.Count}");
```

---

## Future Enhancements (Not Implemented)

### 1. Token-Based Reduction
Instead of message count, reduce based on token count:
```csharp
Strategy = HistoryReductionStrategy.TokenBased,
TargetTokenCount = 8000,
ThresholdTokenCount = 1000
```

### 2. Dual Storage (Full Archive)
Maintain both reduced working memory and full archive:
```csharp
public IReadOnlyList<ChatMessage> Messages => _workingMemory;
public IReadOnlyList<ChatMessage>? FullArchive => _fullArchive;
```

### 3. Persistence Layer Integration
Store full history in database, reduced history in memory:
```csharp
await _persistence.AppendMessageAsync(Id, message);
```

### 4. Reduction Analytics
Track compression ratios and cost savings:
```csharp
public ReductionStats GetReductionStats()
{
    return new ReductionStats
    {
        OriginalMessageCount = ...,
        ReducedMessageCount = ...,
        CompressionRatio = ...,
        EstimatedCostSavings = ...
    };
}
```

---

## Key Takeaways

1. **Agent is now STATELESS** - Reduction metadata returned via `StreamingTurnResult.Reduction`, not stored in agent instance
2. **Agents can be safely reused** across multiple conversations without interference
3. **Agent detects and reduces**, Conversation applies
4. **Cache-aware**: Check for `__summary__` marker to avoid redundant work
5. **Metadata flows** via `StreamingTurnResult.Reduction` → `OrchestrationMetadata.Context`
6. **Use `AdditionalProperties`**, not `Metadata` (Microsoft.Extensions.AI quirk)
7. **Orchestrators must package** `streamingResult.Reduction` into `Context` dictionary
8. **Single storage** - old messages are deleted (Semantic Kernel pattern)
9. **Performance**: ~60% reduction in summarization costs with caching

---

## Breaking Changes (Architecture v2.0)

### What Changed

**REMOVED:**
- ❌ `Agent._lastSummaryMessage` (instance field)
- ❌ `Agent._lastMessagesRemovedCount` (instance field)
- ❌ `Agent.GetReductionMetadata()` method
- ❌ `Agent.ClearReductionMetadata()` method

**ADDED:**
- ✅ `ReductionMetadata` record
- ✅ `StreamingTurnResult.Reduction` property

### Migration Guide

**Old Pattern:**
```csharp
// Agent stores state
var response = await agent.GetResponseAsync(messages, options);
var metadata = agent.GetReductionMetadata(); // ❌ Removed
agent.ClearReductionMetadata(); // ❌ Removed
```

**New Pattern:**
```csharp
// Agent returns metadata in result
var result = await agent.ExecuteStreamingTurnAsync(messages, options);

// Consume stream
await foreach (var evt in result.EventStream) { }

// Get metadata from result, not agent instance
if (result.Reduction != null) // ✅ New
{
    var summary = result.Reduction.SummaryMessage;
    var count = result.Reduction.MessagesRemovedCount;
}
```

### Why This Change?

**Problem:** Agent stored conversation-specific state, preventing safe reuse across multiple conversations.

**Solution:** Return metadata via result objects. Agent is now stateless and can serve multiple conversations concurrently.

**Benefit:** Same agent instance can be shared across thousands of conversations without state interference.

---

## References

- **Semantic Kernel Pattern**: `ChatHistory.ReduceInPlaceAsync()`
- **Microsoft.Extensions.AI**: `IChatReducer` interface
- **AgentConfig.cs**: Configuration options
- **Agent.cs**: Reduction logic and metadata management
- **Conversation.cs**: Storage and application of reduction

**Last Updated:** 2025-01-04
**Architecture Version:** 2.0 (Stateless Agent)
**Status:** ✅ Implemented and Tested

**v2.0 Changes:**
- Agent is now stateless (reduction metadata in result objects)
- Enables safe agent reuse across multiple conversations
- Breaking changes: Removed `GetReductionMetadata()` and `ClearReductionMetadata()`
- Added `ReductionMetadata` record and `StreamingTurnResult.Reduction` property
