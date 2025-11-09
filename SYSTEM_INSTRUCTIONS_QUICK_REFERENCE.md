# System Instructions Quick Reference

## TL;DR

HPD-Agent supports **two ways** to provide system instructions:

1. **MessageProcessor** (Primary) - Universal, works with all providers
2. **ChatOptions.Instructions** (Secondary) - Standard M.E.AI pattern, provider-dependent

Both produce identical JSON output. Use MessageProcessor for base instructions, ChatOptions for per-request overrides.

---

## Quick Comparison

| Approach | When to Use | Pros | Cons |
|----------|-------------|------|------|
| **MessageProcessor** | Base agent instructions, Plan Mode | Universal compatibility, integrated features | Requires code change to modify |
| **ChatOptions.Instructions** | Per-request overrides, dynamic scenarios | Standard pattern, flexible | Not all providers support it |

---

## Usage Examples

### Method 1: MessageProcessor (Recommended for Base Instructions)

```csharp
var agent = new AgentBuilder()
    .WithInstructions("You are a helpful coding assistant") // Uses MessageProcessor
    .WithPlanMode() // Automatically augments instructions
    .Build();

await agent.RunAsync(messages);
```

**Flow:**
```
SystemInstructions → PlanMode Augmentation → MessageProcessor → System Message
```

---

### Method 2: ChatOptions.Instructions (Recommended for Per-Request)

```csharp
var agent = new AgentBuilder()
    .WithInstructions("You are a helpful assistant") // Base
    .Build();

var options = new ChatOptions
{
    Instructions = "For this request, focus on TypeScript" // Override/Append
};

await agent.RunAsync(messages, options);
```

**Note:** Behavior depends on provider:
- ✅ **OpenAI/Azure**: Supports Instructions natively
- ✅ **OpenRouter**: Supports Instructions (added 2025-01)
- ❓ **Ollama/Anthropic**: Depends on SDK (unknown)

---

### Method 3: Hybrid (Best Practice)

```csharp
// Base instructions for all requests
var agent = new AgentBuilder()
    .WithInstructions(@"
        You are an expert software engineer.
        Always provide well-documented code.
    ")
    .WithPlanMode()
    .Build();

// Dynamic per-request instructions
var options = new ChatOptions
{
    Instructions = "Focus on performance optimization for this specific request"
};

await agent.RunAsync(messages, options);
```

**Result:** Base + dynamic instructions combined (if provider supports it).

---

## Provider Support Matrix

| Provider | Instructions Support | Implementation Type |
|----------|---------------------|---------------------|
| ✅ **OpenAI** | Native | Microsoft wrapper |
| ✅ **Azure OpenAI** | Native | Microsoft wrapper |
| ✅ **OpenRouter** | Added 2025-01 | HPD custom |
| ❓ **Ollama** | Unknown | OllamaSharp SDK |
| ❓ **Anthropic** | Unknown | Anthropic.SDK |
| ❓ **Others** | TBD | Various |

---

## Context Order (What Gets Sent to LLM)

```
1. ChatOptions.Instructions (if provider supports)
   OR
   MessageProcessor System Message (if no ChatOptions.Instructions)

2. History Reduction Summary (if enabled)

3. Prompt Filter Injections:
   - Project documents (ProjectInjectedMemoryFilter)
   - Static knowledge (StaticMemoryFilter)
   - Dynamic memory (DynamicMemoryFilter)
   - Current plan (AgentPlanFilter)
   - Custom filters

4. Conversation history

5. Current user message
```

---

## Common Scenarios

### Scenario 1: Simple Bot

```csharp
var agent = new AgentBuilder()
    .WithInstructions("You are a friendly chatbot")
    .Build();
```
✅ Uses MessageProcessor, works everywhere

---

### Scenario 2: Multi-Step Task Agent

```csharp
var agent = new AgentBuilder()
    .WithInstructions("You are a software development assistant")
    .WithPlanMode() // Adds plan management instructions
    .Build();
```
✅ MessageProcessor merges base + plan instructions

---

### Scenario 3: Dynamic Context Agent

```csharp
var agent = new AgentBuilder()
    .WithInstructions("You are a customer service agent")
    .Build();

// Per customer, inject their context
var options = new ChatOptions
{
    Instructions = $"Customer tier: {tier}, Language: {language}"
};

await agent.RunAsync(messages, options);
```
✅ Hybrid: Base from MessageProcessor, dynamic from ChatOptions

---

### Scenario 4: Multi-Provider Agent

```csharp
var openAIAgent = new AgentBuilder()
    .WithProvider("openai", "gpt-4")
    .WithInstructions("You are a coding assistant")
    .Build();

var ollamaAgent = new AgentBuilder()
    .WithProvider("ollama", "llama3.2")
    .WithInstructions("You are a coding assistant")
    .Build();

// Both work identically because MessageProcessor is universal
```
✅ MessageProcessor ensures consistent behavior

---

## Plan Mode Integration

Plan Mode **automatically augments** system instructions:

```csharp
var agent = new AgentBuilder()
    .WithInstructions("You are a senior engineer")
    .WithPlanMode()
    .Build();

// Final instructions sent to LLM:
// "You are a senior engineer
//
// [PLAN MODE ENABLED]
// You have access to plan management tools...
// - create_plan(goal, steps[])
// - update_plan_step(...)
// ..."
```

This happens **at construction time**, not runtime.

---

## Troubleshooting

### Issue: Duplicate System Messages

**Problem:**
```csharp
var messages = new List<ChatMessage>
{
    new ChatMessage(ChatRole.System, "Manual system message")
};

var agent = new AgentBuilder()
    .WithInstructions("Agent system message")
    .Build();

// Result: Two system messages? ❌
```

**Solution:** MessageProcessor has deduplication logic
```csharp
// MessageProcessor checks for existing system messages
if (messagesList.Any(m => m.Role == ChatRole.System))
    return messagesList; // Skip prepending
```
✅ Your manual system message takes precedence

---

### Issue: Instructions Not Working with Custom Provider

**Problem:** ChatOptions.Instructions ignored by provider

**Diagnosis:**
```csharp
// Check if provider supports Instructions
var provider = GetProvider("your-provider");
// Look at implementation - does it check options.Instructions?
```

**Solution:** Either:
1. Use MessageProcessor (always works)
2. Add Instructions support to provider (see template below)

**Template:**
```csharp
private ProviderRequest BuildRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
{
    var requestMessages = new List<ProviderMessage>();

    // Add this code
    if (options?.Instructions is { } instructions && !string.IsNullOrWhiteSpace(instructions))
    {
        requestMessages.Add(new ProviderMessage
        {
            Role = "system",
            Content = instructions
        });
    }

    // ... rest of code
}
```

---

## Best Practices

### ✅ DO

- Use MessageProcessor for base agent instructions
- Use Plan Mode for complex multi-step tasks
- Use ChatOptions.Instructions for per-request overrides
- Document which approach you're using in code comments

### ❌ DON'T

- Mix manual system messages with MessageProcessor (causes deduplication)
- Assume ChatOptions.Instructions works with all providers
- Forget to check provider support matrix
- Use ChatOptions.Instructions as primary mechanism (not all providers support it)

---

## Migration Guide

### From Manual System Messages

**Before:**
```csharp
var messages = new List<ChatMessage>
{
    new ChatMessage(ChatRole.System, "You are an assistant")
};
await client.CompleteAsync(messages);
```

**After (Recommended):**
```csharp
var agent = new AgentBuilder()
    .WithInstructions("You are an assistant")
    .Build();

await agent.RunAsync(messages); // MessageProcessor adds system message
```

---

### From MessageProcessor to ChatOptions.Instructions

**Only do this if:**
- You need per-request dynamic instructions
- Your provider supports it (check matrix)
- You don't use Plan Mode

**Migration:**
```csharp
// Before
var agent = new AgentBuilder()
    .WithInstructions("Base instructions")
    .Build();

// After (if provider supports)
var agent = new AgentBuilder().Build();

var options = new ChatOptions
{
    Instructions = "Base instructions"
};

await agent.RunAsync(messages, options);
```

⚠️ **Warning:** Loses Plan Mode integration, prompt filter benefits, universal compatibility

---

## Final JSON Comparison

Both approaches produce **identical JSON**:

### MessageProcessor
```json
{
  "messages": [
    { "role": "system", "content": "You are an assistant" },
    { "role": "user", "content": "Hello" }
  ]
}
```

### ChatOptions.Instructions
```json
{
  "messages": [
    { "role": "system", "content": "You are an assistant" },
    { "role": "user", "content": "Hello" }
  ]
}
```

**No difference to the LLM** - only differs in how you configure it.

---

## See Also

- [Full Architecture Analysis](SYSTEM_INSTRUCTIONS_ARCHITECTURE.md) - Complete technical analysis
- [AgentBuilder.cs](HPD-Agent/Agent/AgentBuilder.cs) - Builder implementation
- [MessageProcessor](HPD-Agent/Agent/Agent.cs#L3420) - Processor implementation
- [Provider Support Matrix](SYSTEM_INSTRUCTIONS_ARCHITECTURE.md#hpd-agent-provider-architecture) - Detailed provider status

---

**Quick Reference Version:** 1.0
**Last Updated:** 2025-01-09
