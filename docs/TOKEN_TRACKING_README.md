# Token Tracking in HPD-Agent

## TL;DR

**Token tracking for per-message history reduction is NOT implemented** - all token tracking methods return 0.

**Why?** It's architecturally impossible with standard LLM APIs due to:
- Cumulative input reporting (can't decompose into per-message)
- Prompt caching (makes costs non-deterministic)
- Ephemeral context (system prompts, RAG, memory not in message history)
- Reasoning tokens (hidden "thinking" tokens in o1/Gemini)

**What works instead:** Message-count based history reduction (same as LangChain, Semantic Kernel, AutoGen).

## Current Implementation

### ❌ What's Disabled
- Per-message token tracking (all methods in `ChatMessageTokenExtensions.cs` return 0)
- Token-based history reduction triggers (`ShouldReduceByTokens`, `ShouldReduceByPercentage`)

### ✅ What Works
- **Message-count reduction**: Keep last N messages, summarize older ones
- **Turn-level token reporting**: `ChatResponse.Usage` reports total tokens per API call
- **Cost tracking**: Full usage data available for billing/monitoring

## Configuration

```csharp
var agent = new AgentBuilder()
    .WithHistoryReduction(config =>
    {
        config.Enabled = true;
        config.Strategy = HistoryReductionStrategy.MessageCounting; // Only this works
        config.TargetMessageCount = 20; // Keep last 20 messages
        config.SummarizationThreshold = 5; // Summarize when 25+ messages

        // These settings exist but are IGNORED (token tracking disabled):
        config.MaxTokenBudget = null;
        config.TokenBudgetTriggerPercentage = null;
        config.ContextWindowSize = null;
    })
    .Build();
```

## Why Token Tracking Was Removed

See the full analysis: [TOKEN_TRACKING_PROBLEM_SPACE.md](./TOKEN_TRACKING_PROBLEM_SPACE.md)

**Executive Summary of Problems:**

1. **Tool results** - Created locally, provider never reports their token cost
2. **Input tokens** - Cumulative total, can't be decomposed per-message
3. **Prompt caching** - Same message costs different amounts (cache hit = 90% cheaper)
4. **Ephemeral context** - System prompts/RAG/memory add 50-200% overhead
5. **Reasoning tokens** - o1/Gemini "thinking" can be 50x larger than output
6. **Cross-turn attribution** - Can't mutate messages after returning to user

**All attempted solutions failed:**
- ❌ Character estimation (±20% error, doesn't account for caching/ephemeral/reasoning)
- ❌ Delta attribution (requires decomposing cumulative sum - mathematically impossible)
- ❌ Output token storage (only tracks past cost, not future reuse cost)

## Industry Context

**Other frameworks use the same approach:**
- **LangChain**: Message-count or no reduction
- **Semantic Kernel**: Message-count or no reduction
- **AutoGen**: Message-count or no reduction
- **Gemini CLI**: Character estimation (±20% acknowledged error)
- **Claude Code (Codex)**: Iterative removal (retry until success)

**Key insight:** Even frameworks with privileged API access (Google, Anthropic) don't solve per-message token tracking - they work around it.

## For Users

**Q: Why can't I use token-based history reduction?**
A: Standard LLM APIs don't provide per-message token breakdowns. This is an industry-wide limitation affecting all third-party frameworks.

**Q: How do I prevent context overflow?**
A: Use message-count based reduction with conservative limits:
```csharp
config.TargetMessageCount = 20;  // Adjust based on your typical message sizes
```

**Q: Can I track token usage for billing?**
A: Yes! Turn-level usage is reported in `ChatResponse.Usage`:
```csharp
var response = await agent.CompleteAsync(messages);
Console.WriteLine($"Input: {response.Usage.InputTokenCount}");
Console.WriteLine($"Output: {response.Usage.OutputTokenCount}");
Console.WriteLine($"Cached: {response.Usage.CachedInputTokenCount}");
```

**Q: What about the config options `MaxTokenBudget` and `TokenBudgetTriggerPercentage`?**
A: These exist for future compatibility but are currently ignored. The documentation in `AgentConfig.cs` is outdated and will be updated.

## Action Items

- [ ] Update `AgentConfig.cs` lines 426-477 to clarify token tracking is disabled
- [ ] Consider removing disabled config options in next major version
- [ ] Evaluate Codex-style iterative removal as alternative to message-count

## Related Documentation

- Full analysis: [TOKEN_TRACKING_PROBLEM_SPACE.md](./TOKEN_TRACKING_PROBLEM_SPACE.md)
- History reduction guide: (TODO: create user-facing guide)
- Configuration reference: [AgentConfig.cs](../../HPD-Agent/Agent/AgentConfig.cs)
