# Token-Aware History Reduction - Problem Space

**Status:** Design Phase - Requires architectural decisions before implementation
**Created:** 2025-10-03
**Context:** Extending history reduction to support token budgets in addition to message counts

---

## Table of Contents
1. [Background: How Reduction Currently Works](#background-how-reduction-currently-works)
2. [The Problem](#the-problem)
3. [What We've Implemented (Phase 1)](#what-weve-implemented-phase-1)
4. [Core Design Questions](#core-design-questions)
5. [The Two-Parameter Problem](#the-two-parameter-problem)
6. [Critical Edge Cases](#critical-edge-cases)
7. [Design Options](#design-options)
8. [BAML's Approach (Inspiration)](#bamls-approach-inspiration)
9. [Recommendations](#recommendations)

---

## Background: How Reduction Currently Works

### Current Architecture

**Location:** `Agent.cs` - `MessageProcessor` class

**Flow:**
```
1. MessageProcessor.PrepareMessagesAsync()
   ↓
2. Check if _chatReducer exists
   ↓
3. Decide if reduction is needed (WHEN to reduce)
   - Currently uses HARDCODED values (line 1356, 1364)
   - Ignores config.TargetMessageCount
   - Bug: Should use config values
   ↓
4. Call _chatReducer.ReduceAsync() (HOW to reduce)
   - MessageCountingChatReducer: Keeps last N messages
   - SummarizingChatReducer: Summarizes old messages, keeps recent
   ↓
5. Store metadata for Conversation to apply
```

### Current Reducers (Microsoft.Extensions.AI)

#### MessageCountingChatReducer
```csharp
// Keeps last N messages, drops oldest
new MessageCountingChatReducer(targetCount: 20)
```

**Behavior:**
- Preserves first system message
- Excludes function calls/results
- FIFO queue of last N messages
- **Uses config.TargetMessageCount** ✅

#### SummarizingChatReducer
```csharp
// Summarizes when count > targetCount + threshold
new SummarizingChatReducer(
    chatClient: baseClient,
    targetCount: 20,
    threshold: 5
)
```

**Behavior:**
- Triggers when messages > (targetCount + threshold)
- Calls LLM to summarize old messages
- Marks summary with `__summary__` metadata key
- Keeps recent messages unsummarized
- **Uses config.TargetMessageCount + config.SummarizationThreshold** ✅

### The Existing Bug

**Line 1356-1357, 1364-1365** in `PrepareMessagesAsync`:
```csharp
// HARDCODED - ignores config!
int targetCount = 20;
int threshold = 5;

shouldReduce = messagesAfterSummary > (targetCount + threshold);
```

**Should be:**
```csharp
int targetCount = _reductionConfig?.TargetMessageCount ?? 20;
int threshold = _reductionConfig?.SummarizationThreshold ?? 5;
```

---

## The Problem

### Why Message Counting Isn't Enough

**Message count** ≠ **Token count**

```
Scenario A: Long messages
- 10 messages
- 5000 tokens
- Average: 500 tokens/message

Scenario B: Short messages
- 100 messages
- 5000 tokens
- Average: 50 tokens/message
```

**With message-based reduction (targetCount=20):**
- Scenario A: Keeps 10 messages (all of them) = 5000 tokens ⚠️ Might exceed context window
- Scenario B: Keeps 20 messages = ~1000 tokens ✅ Wastes available context

**Context windows are token-based, not message-based:**
- GPT-4: 128K tokens
- Claude 3.5 Sonnet: 200K tokens
- Gemini 1.5 Pro: 1M tokens

Users need to manage **token budgets**, not message counts.

---

## What We've Implemented (Phase 1)

### ChatMessageExtensions.cs (NEW)

**Location:** `HPD-Agent/Conversation/ChatMessageExtensions.cs`

**Purpose:** Store and retrieve token counts from provider API responses

**Key Methods:**
```csharp
// Store token count (from API response)
message.SetTokenCount(150);

// Retrieve stored count
int? count = message.GetTokenCount();

// Get count with estimation fallback
int tokens = message.GetTokenCountOrEstimate();

// Calculate total for message collection
int total = messages.CalculateTotalTokens();
```

**How It Works (BAML-Inspired Pattern):**

1. **Provider returns usage data:**
```json
{
  "usage": {
    "prompt_tokens": 150,
    "completion_tokens": 75,
    "total_tokens": 225
  }
}
```

2. **M.E.AI parses into `ChatResponse.Usage`:**
```csharp
response.Usage.InputTokenCount = 150
response.Usage.OutputTokenCount = 75
```

3. **Conversation.StoreTokenCounts() captures from response:**
```csharp
userMessage.SetTokenCount(150);
assistantMessage.SetTokenCount(75);
```

4. **Messages stored with metadata:**
```csharp
message.AdditionalProperties["TokenCount"] = 150
```

5. **Future reduction can use actual counts:**
```csharp
int totalTokens = conversation.Messages.CalculateTotalTokens();
// Uses provider-accurate counts when available
// Falls back to estimation (text.Length / 3.5) when not available
```

**Why This Works:**
- ✅ No custom tokenizers needed (OpenAI, Anthropic, Google all return counts)
- ✅ Provider-accurate (uses their internal tokenizers)
- ✅ Graceful degradation (estimates if counts unavailable)
- ✅ Cross-provider compatible via M.E.AI `UsageDetails`

### HistoryReductionConfig Updates (NEW)

**Location:** `AgentConfig.cs` lines 244-265

```csharp
public class HistoryReductionConfig
{
    // EXISTING: Message-based
    public int TargetMessageCount { get; set; } = 20;
    public int? SummarizationThreshold { get; set; } = 5;

    // NEW: Token-based (Phase 2 - not yet fully implemented)
    public int? MaxTokenBudget { get; set; } = null;
    public int TargetTokenBudget { get; set; } = 4000;
    public int TokenBudgetThreshold { get; set; } = 1000;
}
```

---

## Core Design Questions

### Question 1: Trigger Decision

**Who decides WHEN to reduce?**

**Current:** `MessageProcessor.PrepareMessagesAsync()` (line 1347-1362)
- Hardcoded logic (bug)
- Should use config values

**Options:**
1. Keep logic in `PrepareMessagesAsync`, add token-based check
2. Pass decision function from `CreateChatReducer()`
3. Create new token-aware reducer interface

### Question 2: Reducer Selection

**Which reducer handles HOW to reduce?**

**Current:** `CreateChatReducer()` (line 773-792)
```csharp
return historyConfig.Strategy switch
{
    MessageCounting => new MessageCountingChatReducer(targetCount),
    Summarizing => CreateSummarizingReducer(...),
    _ => throw
};
```

**For tokens:**
- Create `TokenBudgetChatReducer`?
- Extend existing reducers?
- Hybrid approach?

### Question 3: Config Coupling

**Should MessageProcessor know about config structure?**

**Current:** No - it receives `IChatReducer` instance
- ✅ Clean separation of concerns
- ✅ Config stays in Builder/Agent
- ❌ Can't access token budget values for trigger decision

**Options:**
1. Pass `HistoryReductionConfig` to `MessageProcessor`
   - ❌ Couples runtime to config
2. Pass decision delegate `Func<IEnumerable<ChatMessage>, bool>`
   - ✅ Config stays in Agent
   - ✅ Flexible, testable
3. Bake decision into reducer itself
   - ⚠️ M.E.AI reducers don't support this

---

## The Two-Parameter Problem

### The Challenge

**Reduction has TWO parameters:**

1. **Threshold (WHEN to reduce):**
   - `MaxTokenBudget = 5000` - "Start reducing at 5000 tokens"
   - Or: `TargetMessageCount + Threshold = 25` - "Start reducing at 25 messages"

2. **Target (HOW MUCH to reduce):**
   - `TargetTokenBudget = 4000` - "Reduce down to 4000 tokens"
   - Or: `TargetMessageCount = 20` - "Keep 20 messages"

### The Nuance Discovered

**Key insight:** Token threshold can be reached at vastly different message counts

**Example 1: Long messages**
```
Message 1: 500 tokens  "Explain quantum mechanics..."
Message 2: 450 tokens  "Now explain relativity..."
...
Message 10: 400 tokens

Total: 10 messages = 4500 tokens
```
✅ Hits token threshold at 10 messages

**Example 2: Short messages**
```
Message 1: 50 tokens   "Hi"
Message 2: 60 tokens   "Hello!"
...
Message 100: 50 tokens

Total: 100 messages = 5500 tokens
```
✅ Hits message threshold FIRST (at 25 messages ≈ 1375 tokens)

### The Design Question

**If BOTH `MaxTokenBudget` AND `TargetMessageCount` are set, which wins?**

This is NOT obvious and requires careful design thought.

---

## Critical Edge Cases

### Edge Case 1: Token Budget Reached, Message Count Low

**Scenario:**
```
Config:
- MaxTokenBudget = 5000 (threshold)
- TargetTokenBudget = 4000 (target)
- TargetMessageCount = 20 (message target)

Current State:
- 10 messages
- 5100 tokens (just exceeded threshold)

After token-based reduction to 4000 tokens:
- Keep 8 messages (long messages)

After message-based reduction to 20 messages:
- Keep all 10 messages (under message limit)
```

**Conflict:** Token says "reduce", messages say "don't reduce"

### Edge Case 2: Message Count Reached, Token Budget Low

**Scenario:**
```
Config:
- MaxTokenBudget = 5000
- TargetTokenBudget = 4000
- TargetMessageCount = 20

Current State:
- 50 short messages
- 2500 tokens (under token budget)

After message-based reduction to 20 messages:
- Keep 20 messages ≈ 1000 tokens

After token-based reduction to 4000 tokens:
- Keep all 50 messages (under token budget)
```

**Conflict:** Messages say "reduce", tokens say "don't reduce"

### Edge Case 3: Target Conflict After Reduction

**Scenario:**
```
Config:
- TargetTokenBudget = 4000
- TargetMessageCount = 20

Reducing 40 short messages (5000 tokens total):

Token-based target: Keep enough for 4000 tokens
- Result: ~35 messages (short messages)

Message-based target: Keep 20 messages
- Result: 20 messages ≈ 2500 tokens
```

**Which is correct?**
- User wanted 4000 tokens but got 2500 (under-utilizing context)
- User set targetCount=20 but got 35 (violated message constraint)

---

## Design Options

### Option A: Token Budget Takes Full Precedence

**Approach:**
```csharp
if (config.MaxTokenBudget.HasValue)
{
    // Use ONLY token-based reduction
    // Ignore TargetMessageCount entirely
    return new TokenBudgetChatReducer(config.TargetTokenBudget);
}
else
{
    // Use message-based reduction (backward compatible)
    return config.Strategy switch
    {
        MessageCounting => new MessageCountingChatReducer(config.TargetMessageCount),
        Summarizing => CreateSummarizingReducer(...),
    };
}
```

**Pros:**
- ✅ Simple: one mode at a time
- ✅ Clear: no ambiguity about which constraint applies
- ✅ Easy to reason about

**Cons:**
- ❌ Can't enforce both constraints simultaneously
- ❌ User loses message-count safety net

**Use case:** User wants pure token management, doesn't care about message count

---

### Option B: Dual Constraints (Whichever is Stricter)

**Approach:**
```csharp
public class HybridChatReducer : IChatReducer
{
    private readonly int _targetTokens;
    private readonly int _targetMessages;

    public Task<IEnumerable<ChatMessage>> ReduceAsync(...)
    {
        int currentTokens = 0;
        int currentMessages = 0;
        var kept = new List<ChatMessage>();

        // Work backward from most recent
        foreach (var msg in messages.Reverse())
        {
            int msgTokens = msg.GetTokenCountOrEstimate();

            // Stop if EITHER constraint would be violated
            if (currentTokens + msgTokens > _targetTokens ||
                currentMessages + 1 > _targetMessages)
            {
                break;
            }

            kept.Insert(0, msg);
            currentTokens += msgTokens;
            currentMessages++;
        }

        return Task.FromResult<IEnumerable<ChatMessage>>(kept);
    }
}
```

**Trigger logic:**
```csharp
bool shouldReduce =
    (config.MaxTokenBudget.HasValue && currentTokens > config.MaxTokenBudget) ||
    currentMessages > (config.TargetMessageCount + config.SummarizationThreshold);
```

**Pros:**
- ✅ Enforces both constraints
- ✅ Safety: Can't have 1000 tiny messages or 10 giant messages
- ✅ Predictable: both budgets respected

**Cons:**
- ⚠️ More complex
- ⚠️ User confusion: "Why only 15 messages when I set 20?"
- ⚠️ Might be overly conservative (wastes context)

**Use case:** Production systems needing hard guarantees on both dimensions

---

### Option C: Token Threshold, Message Fallback

**Approach:**
```csharp
// Trigger: Use token budget if set, otherwise message count
if (config.MaxTokenBudget.HasValue)
{
    shouldReduce = currentTokens > config.MaxTokenBudget;
}
else
{
    shouldReduce = currentMessages > (config.TargetMessageCount + threshold);
}

// Target: Use token budget, but ensure minimum messages
if (config.MaxTokenBudget.HasValue)
{
    int tokenBasedMessages = CountMessagesForTokenBudget(config.TargetTokenBudget);
    int minMessages = Math.Max(tokenBasedMessages, config.TargetMessageCount);

    // Keep at least TargetMessageCount, but respect token budget
}
```

**Pros:**
- ✅ Token budget controls when to reduce
- ✅ Message count provides safety floor
- ✅ Flexible compromise

**Cons:**
- ⚠️ Confusing: "I set 4000 tokens but got 6000?"
- ⚠️ Unpredictable: behavior depends on message lengths
- ⚠️ Complex to explain

**Use case:** Conservative approach for uncertain message sizes

---

### Option D: Strategy-Based Selection

**Approach:**
```csharp
public enum HistoryReductionStrategy
{
    MessageCounting,   // EXISTING: Message-based only
    Summarizing,       // EXISTING: Message-based with summarization
    TokenBudget,       // NEW: Token-based only
    HybridTokenMessage // NEW: Both constraints enforced
}
```

**Pros:**
- ✅ User explicitly chooses behavior
- ✅ All options available
- ✅ Clear intent

**Cons:**
- ⚠️ More configuration surface area
- ⚠️ Users need to understand nuances

**Use case:** Give users full control, accept complexity

---

## BAML's Approach (Inspiration)

**BAML doesn't implement history reduction** - they only track token counts from responses.

**What they DO:**
1. Parse `usage` from API responses
2. Store in unified metadata structure
3. Return to caller
4. **Let the caller decide** what to do with token counts

**Key takeaway:** They provide the **data** (token counts), not the **policy** (when/how to reduce).

**We're going further:** Providing both data (Phase 1 ✅) and policy (Phase 2 - design needed).

---

## Recommendations

### Short-Term (Phase 2)

**Implement Option A: Token Budget Takes Precedence**

**Rationale:**
- Simplest to implement and explain
- Clear behavior: one mode at a time
- Solves the immediate problem (token management)
- Backward compatible (if `MaxTokenBudget = null`, uses message-based)

**Implementation:**
1. Create `TokenBudgetChatReducer`
2. Update `CreateChatReducer()` to check `MaxTokenBudget` first
3. Fix hardcoded threshold bug in `PrepareMessagesAsync`
4. Add token-based trigger logic

**Config usage:**
```csharp
// Token mode (ignores TargetMessageCount)
config.HistoryReduction = new HistoryReductionConfig
{
    Enabled = true,
    MaxTokenBudget = 8000,      // Trigger threshold
    TargetTokenBudget = 6000    // Reduction target
};

// OR Message mode (ignores token budgets)
config.HistoryReduction = new HistoryReductionConfig
{
    Enabled = true,
    Strategy = HistoryReductionStrategy.Summarizing,
    TargetMessageCount = 20
};
```

### Long-Term (Phase 3+)

**If users need hybrid constraints:**
1. Add `HistoryReductionStrategy.HybridTokenMessage`
2. Implement Option B (dual constraints)
3. Document edge cases clearly
4. Provide examples

**Wait for user feedback before implementing** - might not be needed.

---

## Open Questions

1. **Should `MaxTokenBudget` completely disable message-based thresholds?**
   - Proposed: Yes (Option A)
   - Alternative: No, use both (Option B)

2. **How should summarization work with token budgets?**
   - Use `TokenBudgetChatReducer` (simple truncation)?
   - Create `TokenAwareSummarizingReducer` (summarize to token budget)?

3. **Should we support token budgets with `MessageCounting` strategy?**
   - Or force `Strategy = TokenBudget` when `MaxTokenBudget` is set?

4. **What happens in streaming mode?**
   - Phase 1 only stores counts for non-streaming (no `ChatResponse.Usage`)
   - Need to capture usage from streaming final result

5. **Should token budget be per-turn or cumulative conversation?**
   - Current design: per-turn (reduces before each LLM call)
   - Alternative: cumulative (track total conversation tokens)

---

## Next Steps

1. **Design Decision:** Choose Option A, B, C, or D
2. **Implement:** Based on chosen option
3. **Test:** Edge cases documented above
4. **Document:** User-facing docs explaining behavior
5. **Iterate:** Gather feedback, adjust if needed

---

## Related Files

- `HPD-Agent/Conversation/ChatMessageExtensions.cs` - Token counting (Phase 1 ✅)
- `HPD-Agent/Conversation/Conversation.cs` - StoreTokenCounts() (Phase 1 ✅)
- `HPD-Agent/Agent/AgentConfig.cs` - HistoryReductionConfig (Phase 1 ✅)
- `HPD-Agent/Agent/Agent.cs` - MessageProcessor, CreateChatReducer (Phase 2 pending)

---

## References

- BAML token counting pattern: `Reference/baml-canary/engine/baml-runtime/src/internal/llm_client/`
- M.E.AI reducers: `Reference/extensions/src/Libraries/Microsoft.Extensions.AI/ChatReduction/`
- Semantic Kernel token-aware reducers: `Reference/semantic-kernel/dotnet/samples/Concepts/ChatCompletion/ChatHistoryReducers/`
