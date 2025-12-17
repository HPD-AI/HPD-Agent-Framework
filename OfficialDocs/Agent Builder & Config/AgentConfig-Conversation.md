# AgentConfig - Conversation Management

## Overview

Configure how conversations are managed, compressed, and optimized for context window limits.

## Properties

### HistoryReduction
Compress conversation history when it exceeds size limits.

Default: Disabled (`Enabled = false`)

[Detailed docs →](./AgentConfig-HistoryReduction.md)

Options:
- **MessageCounting** - Keep only N most recent messages (fast, simple)
- **Summarizing** - Use LLM to summarize older messages (preserves context)

### PreserveReasoningInHistory
Whether to keep reasoning tokens from models like o1, DeepSeek-R1.

Default: `false`

Options:
- `false` - Reasoning shown during streaming but excluded from history (saves tokens)
- `true` - Full reasoning preserved in history (higher cost, more context)

**When to enable:**
- Research/debugging scenarios
- Complex multi-turn reasoning
- When preserving the model's thought process is critical

**Cost Impact:** Reasoning models can produce 10x-50x output length, so preserving adds significant cost to future requests.

## Examples

[Coming soon...]

## Related Topics

- [History Reduction Strategies](./AgentConfig-HistoryReduction.md)
- [Token Management](../Context/TokenManagement.md)
- [Reasoning Token Handling](../LLM/ReasoningTokens.md)
