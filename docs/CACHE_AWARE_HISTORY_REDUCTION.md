# Cache-Aware History Reduction: How It Works

## Overview

The history reduction system has a **cache mechanism** that prevents redundant LLM calls when re-reducing already-summarized conversation history. This is implemented using a special marker in message metadata.

## The Summary Marker

### Technical Implementation

```csharp
// Constant from HistoryReductionConfig
public const string SummaryMetadataKey = "__summary__";

// Example summary message
var summaryMessage = new ChatMessage(ChatRole.Assistant, "User previously discussed...")
{
    AdditionalProperties = new Dictionary<string, object>
    {
        ["__summary__"] = true  // ✅ Cache marker
    }
};
```

### Detection Logic

```csharp
// From Agent.cs:3743-3744
var lastSummaryIndex = messagesList.FindLastIndex(m =>
    m.AdditionalProperties?.ContainsKey(HistoryReductionConfig.SummaryMetadataKey) == true);

// Only counts messages AFTER the last summary
bool shouldReduce = ShouldTriggerReduction(messagesList, lastSummaryIndex);
```

## How It's SUPPOSED to Work (Ideal Flow)

### Turn 1: Initial Reduction

```
┌─────────────────────────────────────────────────────────────────┐
│ INPUT: 100 messages in thread                                   │
├─────────────────────────────────────────────────────────────────┤
│ msg1: "Hello"                                                    │
│ msg2: "Hi there"                                                 │
│ ... (98 more)                                                    │
│ msg100: "What's the weather?"                                    │
└─────────────────────────────────────────────────────────────────┘

PrepareMessagesAsync(100 messages):
├─ Check for __summary__ marker: NOT FOUND
├─ shouldReduce = true (100 > targetCount)
├─ Call ChatReducer.ReduceAsync()
└─ LLM generates summary

┌─────────────────────────────────────────────────────────────────┐
│ OUTPUT: effectiveMessages (sent to LLM)                         │
├─────────────────────────────────────────────────────────────────┤
│ [SYSTEM] "You are a helpful assistant"                          │
│ [SUMMARY] "User discussed greetings..." {__summary__: true} ✅  │
│ msg91-msg100 (recent 10 messages)                               │
│                                                                  │
│ Count: 12 messages (token savings: 88 messages!)                │
└─────────────────────────────────────────────────────────────────┘
```

### Turn 2: Cache Hit (Ideal Behavior)

```
┌─────────────────────────────────────────────────────────────────┐
│ INPUT: 110 messages (10 new since last time)                    │
├─────────────────────────────────────────────────────────────────┤
│ msg1: "Hello"                                                    │
│ [SUMMARY] "User discussed..." {__summary__: true} ✅            │
│ msg91-msg100 (kept from last reduction)                         │
│ msg101-msg110 (10 NEW messages)                                 │
└─────────────────────────────────────────────────────────────────┘

PrepareMessagesAsync(110 messages):
├─ Check for __summary__ marker: FOUND at index 1! ✅
├─ lastSummaryIndex = 1
├─ Count messages AFTER summary: 109 messages
├─ shouldReduce = ShouldTriggerReduction(110, lastSummaryIndex=1)
│   └─ Only counts messages from index 2 onwards
│   └─ 109 messages > targetCount → true
├─ Re-reduce from index 1 onwards (skip msg1, keep summary)
└─ ❌ NO LLM CALL for old summary! (cache hit!)

┌─────────────────────────────────────────────────────────────────┐
│ OUTPUT: effectiveMessages                                       │
├─────────────────────────────────────────────────────────────────┤
│ [SYSTEM] "You are a helpful assistant"                          │
│ [SUMMARY] "User discussed..." {__summary__: true} ✅ REUSED!    │
│ msg101-msg110 (recent 10 new messages)                          │
│                                                                  │
│ Count: 12 messages (no redundant LLM call!)                     │
└─────────────────────────────────────────────────────────────────┘
```

## The Problem with Current Fix

### What Actually Happens

```
┌─────────────────────────────────────────────────────────────────┐
│ TURN 1: Fresh run with 100 messages                             │
└─────────────────────────────────────────────────────────────────┘

state.CurrentMessages = [msg1...msg100]  ✅ FULL unreduced history
effectiveMessages = [SYSTEM, SUMMARY{__summary__}, msg91-100]

CHECKPOINT SAVED:
└─ state.CurrentMessages = [msg1...msg100]  ❌ No __summary__ marker!

┌─────────────────────────────────────────────────────────────────┐
│ TURN 2: Resume with 10 new messages                             │
└─────────────────────────────────────────────────────────────────┘

Load from checkpoint:
└─ state.CurrentMessages = [msg1...msg100]  ❌ No __summary__ marker!

PrepareMessagesAsync(110 messages):
├─ Check for __summary__ marker: NOT FOUND! ❌
├─ lastSummaryIndex = -1
├─ shouldReduce = true (110 > targetCount)
├─ Call ChatReducer.ReduceAsync() AGAIN! ❌
└─ LLM call for redundant re-summarization! ❌ CACHE MISS!
```

### Root Cause

**The summary marker lives ONLY in `effectiveMessages`**, which is:
1. Used for iteration 0 LLM call
2. **Discarded** after iteration 0
3. Never stored in `state.CurrentMessages` (which has full unreduced history)

**Result**: Every fresh run triggers full re-reduction, defeating the cache!

## Solution: Preserve Summary Marker in Full History

### Enhanced Fix (Option A)

**Goal**: Store the summary marker in `state.CurrentMessages` WITHOUT storing the reduced history.

#### Approach 1: Mark the Last Message Before Summary Range

```csharp
// After PrepareMessagesAsync returns reduced messages
var summaryMsg = effectiveMessages.FirstOrDefault(m =>
    m.AdditionalProperties?.ContainsKey(HistoryReductionConfig.SummaryMetadataKey) == true);

if (summaryMsg != null && reductionMetadata != null)
{
    // Find the message in state.CurrentMessages that corresponds to the
    // last message BEFORE the summary range (reduction boundary marker)
    int summaryEndIndex = reductionMetadata.OriginalCount - reductionMetadata.KeptRecentCount - 1;

    if (summaryEndIndex >= 0 && summaryEndIndex < state.CurrentMessages.Count)
    {
        var boundaryMsg = state.CurrentMessages[summaryEndIndex];
        boundaryMsg.AdditionalProperties ??= new Dictionary<string, object>();
        boundaryMsg.AdditionalProperties["__summary_boundary__"] = summaryEndIndex;

        // Store the actual summary content for reuse
        boundaryMsg.AdditionalProperties["__summary_content__"] = summaryMsg.Content;
    }
}
```

**Storage Structure**:
```
state.CurrentMessages (FULL history):
├─ msg1-msg90: Normal messages (these were summarized)
├─ msg90: {__summary_boundary__: 90, __summary_content__: "User discussed..."} ✅
├─ msg91-msg100: Kept messages (recent)
└─ Total: 100 messages (full count preserved)
```

**Next Run Detection**:
```csharp
var lastBoundaryIndex = messagesList.FindLastIndex(m =>
    m.AdditionalProperties?.ContainsKey("__summary_boundary__") == true);

if (lastBoundaryIndex >= 0)
{
    // Cache hit! Extract stored summary
    var summaryContent = messagesList[lastBoundaryIndex]
        .AdditionalProperties["__summary_content__"] as string;

    // Build effectiveMessages with cached summary
    effectiveMessages = new[] {
        systemMessage,
        new ChatMessage(ChatRole.Assistant, summaryContent) {
            AdditionalProperties = new() { ["__summary__"] = true }
        }
    }.Concat(messagesList.Skip(lastBoundaryIndex + 1));

    // ✅ NO LLM CALL! Cache hit!
}
```

#### Approach 2: Inject Summary Message into Full History

```csharp
// After reduction, inject summary as a special message in state.CurrentMessages
if (summaryMsg != null && reductionMetadata != null)
{
    var summaryIndex = reductionMetadata.OriginalCount - reductionMetadata.KeptRecentCount;

    // Create a copy of the summary message to inject
    var injectedSummary = new ChatMessage(ChatRole.Assistant, summaryMsg.Content)
    {
        AdditionalProperties = new Dictionary<string, object>
        {
            ["__summary__"] = true,
            ["__injected__"] = true  // Mark as not part of actual conversation
        }
    };

    // Insert into state.CurrentMessages
    var updatedMessages = state.CurrentMessages.ToList();
    updatedMessages.Insert(summaryIndex, injectedSummary);
    state = state.WithMessages(updatedMessages);
}
```

**Storage Structure**:
```
state.CurrentMessages (FULL + summary marker):
├─ msg1-msg90: Normal messages
├─ [SUMMARY] "User discussed..." {__summary__: true, __injected__: true} ✅
├─ msg91-msg100: Kept messages
└─ Total: 101 messages (100 real + 1 injected marker)
```

**Pros**:
- ✅ Natural structure: Summary sits between old/new messages
- ✅ PrepareMessagesAsync detects it automatically (existing logic works!)

**Cons**:
- ❌ Message count mismatch: state has 101, thread has 100
- ❌ ValidateConsistency would fail! ❌

### Recommended Solution: Hybrid Approach

**Store summary metadata on the boundary message, reconstruct on load**:

```csharp
// === STEP 1: After reduction (Agent.cs:590-596) ===

var summaryMsg = effectiveMessages.FirstOrDefault(m =>
    m.AdditionalProperties?.ContainsKey(HistoryReductionConfig.SummaryMetadataKey) == true);

if (summaryMsg != null && reductionMetadata != null)
{
    // Mark the last message in the summarized range
    int boundaryIndex = reductionMetadata.OriginalCount - reductionMetadata.KeptRecentCount - 1;

    if (boundaryIndex >= 0 && boundaryIndex < state.CurrentMessages.Count)
    {
        var messagesList = state.CurrentMessages.ToList();
        var boundaryMsg = messagesList[boundaryIndex];

        boundaryMsg.AdditionalProperties ??= new Dictionary<string, object>();
        boundaryMsg.AdditionalProperties["__summary_after_this__"] = new Dictionary<string, object>
        {
            ["content"] = summaryMsg.Content,
            ["timestamp"] = DateTime.UtcNow,
            ["message_count"] = reductionMetadata.OriginalCount
        };

        // Update state with marked message
        state = state.WithMessages(messagesList);
    }
}

// === STEP 2: In PrepareMessagesAsync (MessageProcessor.cs) ===

// Check for cached summary boundary
var lastBoundaryIndex = messagesList.FindLastIndex(m =>
    m.AdditionalProperties?.ContainsKey("__summary_after_this__") == true);

if (lastBoundaryIndex >= 0)
{
    var boundaryMetadata = messagesList[lastBoundaryIndex]
        .AdditionalProperties["__summary_after_this__"] as Dictionary<string, object>;

    var cachedSummaryContent = boundaryMetadata?["content"] as string;

    if (cachedSummaryContent != null)
    {
        // Only reduce NEW messages added AFTER the boundary
        int newMessageCount = messagesList.Count - lastBoundaryIndex - 1;

        if (newMessageCount <= config.TargetMessageCount)
        {
            // ✅ CACHE HIT: Just use cached summary + new messages
            var summaryMessage = new ChatMessage(ChatRole.Assistant, cachedSummaryContent)
            {
                AdditionalProperties = new() { [SummaryMetadataKey] = true }
            };

            return new[] { summaryMessage }
                .Concat(messagesList.Skip(lastBoundaryIndex + 1));

            // ✅ NO LLM CALL!
        }
        else
        {
            // Too many new messages - re-reduce from boundary onwards
            // (Still saves tokens by skipping old summarized messages)
        }
    }
}
```

## Benefits of Cache-Aware Reduction

### Token Savings Example

**Without Cache** (current behavior):
```
Turn 1: Reduce 100 messages → Summary (1 LLM call)
Turn 2: Reduce 110 messages → Summary (1 LLM call) ❌ Redundant!
Turn 3: Reduce 120 messages → Summary (1 LLM call) ❌ Redundant!

Total LLM calls: 3
```

**With Cache** (proposed enhancement):
```
Turn 1: Reduce 100 messages → Summary (1 LLM call)
Turn 2: Use cached summary ✅ (0 LLM calls)
Turn 3: Use cached summary ✅ (0 LLM calls)

Total LLM calls: 1  (66% reduction!)
```

### Cost Impact

**Scenario**: 10-turn conversation, each turn adds 10 messages

| Metric | Without Cache | With Cache | Savings |
|--------|--------------|------------|---------|
| Reduction LLM calls | 10 | 1 | 90% |
| Tokens for reduction | ~500k | ~50k | 90% |
| Cost (GPT-4 pricing) | ~$5 | ~$0.50 | 90% |

## Implementation Checklist

- [ ] Add summary boundary metadata to state.CurrentMessages
- [ ] Modify PrepareMessagesAsync to detect cached summaries
- [ ] Add cache hit/miss metrics to HistoryReductionMetadata
- [ ] Test: Verify cache hit on second run
- [ ] Test: Verify re-reduction when new messages exceed target
- [ ] Test: Verify ValidateConsistency still passes (count unchanged)
- [ ] Test: Verify checkpoint resume with cached summary
- [ ] Document cache mechanism in user-facing docs

## Migration Strategy

**Phase 1: Add cache detection (non-breaking)**
- Modify PrepareMessagesAsync to look for boundary markers
- Fall back to full reduction if not found
- Deploy and observe

**Phase 2: Add cache writing (opt-in)**
- Add flag to enable cache writing: `HistoryReductionConfig.EnableCache`
- Write boundary metadata only if enabled
- Monitor effectiveness

**Phase 3: Enable by default**
- Make cache-aware reduction the default
- Remove opt-in flag
- Update all documentation

## Related Files

- [Agent.cs:557-597](../HPD-Agent/Agent/Agent.cs#L557-L597) - State initialization
- [Agent.cs:3728-3777](../HPD-Agent/Agent/Agent.cs#L3728-L3777) - PrepareMessagesAsync
- [HistoryReductionConfig.cs](../HPD-Agent/Conversation/HistoryReductionConfig.cs) - Summary marker constant

---

**Status**: Proposed enhancement
**Priority**: Medium (optimization, not critical bug)
**Estimated effort**: 2-3 hours implementation + testing
