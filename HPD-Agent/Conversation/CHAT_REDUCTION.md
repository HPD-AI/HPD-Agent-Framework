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

#### A. Metadata Storage (`Agent.cs:35-37`)
```csharp
// Tracks reduction from last turn
private ChatMessage? _lastSummaryMessage;
private int? _lastMessagesRemovedCount;
```

#### B. Public API (`Agent.cs:899-933`)
```csharp
// Returns metadata for Conversation to apply reduction
public IReadOnlyDictionary<string, object> GetReductionMetadata()
{
    var metadata = new Dictionary<string, object>();
    if (_lastSummaryMessage != null)
        metadata["SummaryMessage"] = _lastSummaryMessage;
    if (_lastMessagesRemovedCount.HasValue)
        metadata["MessagesRemovedCount"] = _lastMessagesRemovedCount.Value;
    return metadata;
}

// Called by Conversation after applying reduction
public void ClearReductionMetadata()
{
    _lastSummaryMessage = null;
    _lastMessagesRemovedCount = null;
}
```

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

#### D. Metadata Capture (`Agent.cs:399-410`)
```csharp
// After PrepareMessagesAsync
if (_messageProcessor.LastReductionMetadata.HasValue)
{
    _lastSummaryMessage = _messageProcessor.LastReductionMetadata.Value.SummaryMessage;
    _lastMessagesRemovedCount = _messageProcessor.LastReductionMetadata.Value.RemovedCount;
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

**SendAsync - Single Agent** (`Conversation.cs:219`)
```csharp
orchestrationResult = new OrchestrationResult
{
    Response = response,
    SelectedAgent = agent,
    Metadata = new OrchestrationMetadata
    {
        StrategyName = "SingleAgent",
        Context = agent.GetReductionMetadata() // ← Include metadata
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

**Streaming Path** (`Conversation.cs:520-531`)
```csharp
// After streaming completes
var reductionMetadata = agent.GetReductionMetadata();
if (reductionMetadata.TryGetValue("SummaryMessage", out var summaryObj) && ...)
{
    int systemMsgCount = _messages.Count(m => m.Role == ChatRole.System);
    _messages.RemoveRange(systemMsgCount, count);
    _messages.Insert(systemMsgCount, summary);
    agent.ClearReductionMetadata();
}
```

---

## Data Flow

### Complete Turn Flow

```
1. User sends message
   ↓
2. Conversation adds to _messages
   ↓
3. Agent.PrepareMessagesAsync()
   ├─ Check for __summary__ marker
   ├─ Count messages after summary
   ├─ If > threshold: Reduce
   ├─ Extract summary message
   └─ Store in LastReductionMetadata
   ↓
4. Agent captures metadata (RunAgenticLoopCore)
   ├─ _lastSummaryMessage = metadata.SummaryMessage
   └─ _lastMessagesRemovedCount = metadata.RemovedCount
   ↓
5. Agent sends reduced messages to LLM
   ↓
6. LLM responds
   ↓
7. Agent.GetReductionMetadata()
   └─ Returns Dictionary with SummaryMessage + MessagesRemovedCount
   ↓
8. Conversation receives OrchestrationResult
   └─ Metadata.Context contains reduction metadata
   ↓
9. Conversation.ApplyReductionIfPresent()
   ├─ Remove old messages: RemoveRange(systemMsgCount, count)
   ├─ Insert summary: Insert(systemMsgCount, summary)
   └─ Clear agent metadata
   ↓
10. Conversation adds response to _messages
```

### Metadata Flow via OrchestrationMetadata.Context

**Why use Context dictionary?**
- ✅ No contract changes to `OrchestrationResult`
- ✅ Orchestrators automatically support it
- ✅ Clean separation of concerns
- ✅ Extensible for future metadata

```csharp
// Agent side
agent.GetReductionMetadata() → Dictionary<string, object>

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

If you're creating a custom orchestrator, **you must pass through reduction metadata**:

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
        // Your agent selection logic
        var selectedAgent = SelectBestAgent(history, agents);

        // Call the agent
        var response = await selectedAgent.GetResponseAsync(
            history, options, cancellationToken);

        return new OrchestrationResult
        {
            Response = response,
            SelectedAgent = selectedAgent,
            Metadata = new OrchestrationMetadata
            {
                StrategyName = "MyStrategy",
                DecisionDuration = TimeSpan.Zero,
                Context = selectedAgent.GetReductionMetadata() // ← CRITICAL!
            }
        };
    }
}
```

**Without this line**, reduction won't be applied to Conversation storage!

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

1. **Agent detects and reduces**, Conversation applies
2. **Cache-aware**: Check for `__summary__` marker to avoid redundant work
3. **Metadata flows** via `OrchestrationMetadata.Context` (no contract changes!)
4. **Use `AdditionalProperties`**, not `Metadata` (Microsoft.Extensions.AI quirk)
5. **Orchestrators must call** `GetReductionMetadata()` to support reduction
6. **Single storage** - old messages are deleted (Semantic Kernel pattern)
7. **Performance**: ~60% reduction in summarization costs with caching

---

## References

- **Semantic Kernel Pattern**: `ChatHistory.ReduceInPlaceAsync()`
- **Microsoft.Extensions.AI**: `IChatReducer` interface
- **AgentConfig.cs**: Configuration options
- **Agent.cs**: Reduction logic and metadata management
- **Conversation.cs**: Storage and application of reduction

**Last Updated:** 2025-01-02
**Architecture Version:** 1.0
**Status:** ✅ Implemented and Tested
