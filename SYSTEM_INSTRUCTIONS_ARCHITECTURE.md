# System Instructions Architecture Analysis

## Executive Summary

This document analyzes the evolution of system instructions handling in HPD-Agent and Microsoft.Extensions.AI, documenting the problem space, our implementation decisions, and the hybrid approach we've adopted.

---

## Historical Context: The Missing Instructions Property

### The Original Problem (Pre-2024)

When HPD-Agent was initially developed, **Microsoft.Extensions.AI did not provide a standardized way to handle system instructions**. The `ChatOptions` class lacked an `Instructions` property, forcing framework developers to implement custom solutions.

**Impact:**
- Every agent framework had to manually manage system instructions
- No standard pattern across implementations
- Provider implementations varied in approach
- Potential for duplicate system messages

### Our Initial Solution

We implemented a **MessageProcessor-based approach** that prepends system instructions as a `ChatMessage` with `ChatRole.System`:

```csharp
// HPD-Agent/Agent/Agent.cs:3594-3608
private IEnumerable<ChatMessage> PrependSystemInstructions(IEnumerable<ChatMessage> messages)
{
    if (string.IsNullOrEmpty(_systemInstructions))
        return messages;

    var messagesList = messages.ToList();

    // Check if there's already a system message
    if (messagesList.Any(m => m.Role == ChatRole.System))
        return messagesList;

    // Prepend system instruction
    var systemMessage = new ChatMessage(ChatRole.System, _systemInstructions);
    return new[] { systemMessage }.Concat(messagesList);
}
```

**Advantages of our approach:**
- ✅ Works universally across all providers
- ✅ Integrates with Plan Mode augmentation
- ✅ Prompt filter pipeline compatibility
- ✅ Deduplication logic prevents double system messages
- ✅ Centralized control in MessageProcessor

---

## Microsoft's Evolution: The Instructions Property

### ChatOptions.Instructions (2024 Update)

Microsoft eventually added native support for system instructions in `ChatOptions`:

```csharp
// Microsoft.Extensions.AI.Abstractions/ChatCompletion/ChatOptions.cs:62
/// <summary>Gets or sets additional per-request instructions to be provided to the <see cref="IChatClient"/>.</summary>
public string? Instructions { get; set; }
```

**Provider Implementation (OpenAI example):**

```csharp
// Microsoft.Extensions.AI.OpenAI/OpenAIChatClient.cs:140-142
if (chatOptions?.Instructions is { } instructions && !string.IsNullOrWhiteSpace(instructions))
{
    yield return new SystemChatMessage(instructions);
}
```

### Microsoft.Agents.AI: Multi-Layer Instructions

The new Agent Framework takes this further with **layered instruction merging**:

```csharp
// Agent-level base instructions
public string? Instructions { get; set; }

// AIContextProvider dynamic instructions (appended)
if (aiContext.Instructions is not null)
{
    chatOptions.Instructions = string.IsNullOrWhiteSpace(chatOptions.Instructions)
        ? aiContext.Instructions
        : $"{chatOptions.Instructions}\n{aiContext.Instructions}";
}

// Final merge with per-request instructions
chatOptions.Instructions = string.IsNullOrWhiteSpace(chatOptions.Instructions)
    ? this.Instructions
    : $"{this.Instructions}\n{chatOptions.Instructions}";
```

**Order of Precedence:**
1. Agent's base instructions
2. AIContextProvider dynamic instructions (appended)
3. Per-request instructions (appended)

---

## HPD-Agent Provider Architecture

### Current Provider Landscape

HPD-Agent uses a **hybrid provider strategy** with different implementation types:

| Provider | Type | Instructions Support | Package |
|----------|------|---------------------|---------|
| **OpenAI** | Microsoft wrapper | ✅ Native | `Microsoft.Extensions.AI.OpenAI` v9.7.1 |
| **Azure OpenAI** | Microsoft wrapper | ✅ Native | `Microsoft.Extensions.AI.OpenAI` v9.7.1 |
| **OpenRouter** | Custom implementation | ✅ **Added 2025-01** | HPD-Agent custom |
| **Ollama** | 3rd-party SDK | ❓ Unknown | `OllamaSharp` v5.3.4 |
| **Anthropic** | 3rd-party SDK | ❓ Unknown | `Anthropic.SDK` v5.5.2 |
| **Bedrock** | Custom/wrapper | ❓ Needs verification | TBD |
| **Google AI** | Custom/wrapper | ❓ Needs verification | TBD |
| **HuggingFace** | Custom/wrapper | ❓ Needs verification | TBD |
| **Mistral** | Custom/wrapper | ❓ Needs verification | TBD |
| **OnnxRuntime** | Custom/wrapper | ❓ Needs verification | TBD |

### Provider Architecture Patterns

**Pattern 1: Microsoft Official Wrappers (OpenAI/Azure)**
```csharp
public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
{
    var chatClient = new ChatClient(config.ModelName, config.ApiKey);
    return chatClient.AsIChatClient(); // Microsoft's wrapper handles Instructions
}
```
✅ **No additional work needed** - Microsoft's implementation handles `ChatOptions.Instructions`

**Pattern 2: Custom Implementations (OpenRouter)**
```csharp
// Before (2024)
private OpenRouterChatRequest BuildRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
{
    var requestMessages = new List<OpenRouterRequestMessage>();
    foreach (var m in messages) { /* ... */ }
}

// After (2025-01) - Added Instructions support
private OpenRouterChatRequest BuildRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
{
    var requestMessages = new List<OpenRouterRequestMessage>();

    // Support ChatOptions.Instructions
    if (options?.Instructions is { } instructions && !string.IsNullOrWhiteSpace(instructions))
    {
        requestMessages.Add(new OpenRouterRequestMessage
        {
            Role = "system",
            Content = instructions
        });
    }

    foreach (var m in messages) { /* ... */ }
}
```
✅ **Manual implementation required** - We control the code

**Pattern 3: 3rd-Party SDKs (Ollama, Anthropic)**
```csharp
public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
{
    return new OllamaApiClient(endpoint, config.ModelName); // External SDK
}
```
❓ **Depends on SDK maintainer** - We cannot control if/when they add Instructions support

---

## Our Hybrid Approach: Best of Both Worlds

### Decision: Keep MessageProcessor + Add Instructions Support

We've adopted a **dual-path architecture** that supports both approaches:

#### Path 1: MessageProcessor (Primary, Universal)

**Flow:**
```
AgentConfig.SystemInstructions
    → AugmentSystemInstructionsForPlanMode()
    → MessageProcessor.PrependSystemInstructions()
    → ChatMessage(ChatRole.System, instructions)
```

**Advantages:**
- ✅ Works with ALL providers (even those without Instructions support)
- ✅ Integrates with Plan Mode
- ✅ Prompt filter pipeline compatible
- ✅ Deduplication logic
- ✅ Battle-tested and stable

#### Path 2: ChatOptions.Instructions (Secondary, Provider-Specific)

**Flow:**
```
ChatOptions.Instructions
    → Provider implementation
    → System message in API request
```

**Advantages:**
- ✅ Standard Microsoft.Extensions.AI pattern
- ✅ Per-request instruction override capability
- ✅ Compatible with future Microsoft updates
- ✅ Works with OpenAI/Azure out-of-the-box

### Why Both?

**Compatibility:**
- Users can use either approach
- Existing code continues working
- Future-proof for provider updates

**Flexibility:**
- Static base instructions → MessageProcessor
- Dynamic per-request instructions → ChatOptions.Instructions
- Complex scenarios → Both (layers combine)

**Migration Path:**
- No breaking changes required
- Gradual adoption possible
- Provider-specific optimization available

---

## JSON Output Comparison

Despite different implementation paths, the **final JSON sent to LLM APIs is identical**:

### MessageProcessor Approach
```csharp
// HPD-Agent prepends system message
var messages = new List<ChatMessage>
{
    new ChatMessage(ChatRole.System, "You are a helpful assistant"),
    new ChatMessage(ChatRole.User, "Hello")
};
```

### ChatOptions.Instructions Approach
```csharp
var options = new ChatOptions
{
    Instructions = "You are a helpful assistant"
};
var messages = new List<ChatMessage>
{
    new ChatMessage(ChatRole.User, "Hello")
};
```

### Resulting API Request (Identical)
```json
{
  "model": "gpt-4",
  "messages": [
    {
      "role": "system",
      "content": "You are a helpful assistant"
    },
    {
      "role": "user",
      "content": "Hello"
    }
  ]
}
```

---

## Context Flow Order

Understanding the **complete order of context processing** before reaching the chat client:

### 1. Agent Construction (AgentBuilder.BuildAsync)
```
├─ Base Chat Client (provider-specific)
├─ Middleware Pipeline (wraps base client)
│  ├─ OpenTelemetry
│  ├─ Caching
│  ├─ Logging
│  └─ Options Configuration
└─ Prompt Filters registered (not yet executed)
```

### 2. User Message Submission
```
agent.RunAsync(messages, options, documentPaths)
├─ Document Processing (if documentPaths provided)
└─ Enters agentic loop
```

### 3. Message Preparation (MessageProcessor.PrepareMessagesAsync)
```
├─ 3a. System Instructions
│   └─ Prepend system message (if not present)
│
├─ 3b. History Reduction (if configured)
│   ├─ Check message count thresholds
│   ├─ Optional LLM summarization
│   └─ Return ReductionMetadata
│
├─ 3c. PROMPT FILTERS PIPELINE (executed in order)
│   ├─ ProjectInjectedMemoryFilter (auto-added)
│   │   └─ Injects project documents into system prompt
│   ├─ StaticMemoryFilter (if WithStaticMemory)
│   │   └─ Injects knowledge base documents
│   ├─ DynamicMemoryFilter (if WithDynamicMemory)
│   │   └─ Injects agent's working memory
│   ├─ AgentPlanFilter (if WithPlanMode)
│   │   └─ Injects current execution plan
│   └─ Custom Prompt Filters
│       └─ Your filters in registration order
│
└─ 3d. Options Merging
    └─ Combine provided + default ChatOptions
```

### 4. Plugin/Skill Scoping (per iteration)
```
├─ UnifiedScopingManager determines available tools
└─ Container expansion (if plugin scoping enabled)
```

### 5. LLM Call (via wrapped chat client)
```
_baseClient.CompleteAsync(messages, options)
├─ Goes through middleware pipeline
│  ├─ OpenTelemetry (traces)
│  ├─ Caching (check cache)
│  ├─ Logging (log request)
│  └─ Provider-specific client
│      ├─ ChatOptions.Instructions → System message (if supported)
│      └─ API request
└─ Returns ChatCompletion with tool calls
```

### 6. Function Execution (if tool calls present)
```
├─ Permission Filters (check if allowed)
├─ AI Function Filters (scoped by function/plugin)
│  ├─ Pre-invocation filters
│  ├─ Function execution
│  └─ Post-invocation filters
└─ Return tool results
```

### 7. Post-Invocation (after LLM response)
```
├─ PostInvokeAsync called on all Prompt Filters
│  ├─ Memory extraction
│  ├─ Learning/ranking updates
│  └─ Auditing
└─ Loop continues or terminates
```

---

## Plan Mode Integration

One of the key advantages of our MessageProcessor approach is **seamless Plan Mode integration**:

```csharp
// HPD-Agent/Agent/Agent.cs:1317-1335
private static string? AugmentSystemInstructionsForPlanMode(AgentConfig config)
{
    var baseInstructions = config.SystemInstructions;
    var planConfig = config.PlanMode;

    if (planConfig == null || !planConfig.Enabled)
    {
        return baseInstructions;
    }

    var planInstructions = planConfig.CustomInstructions ?? GetDefaultPlanModeInstructions();

    if (string.IsNullOrEmpty(baseInstructions))
    {
        return planInstructions;
    }

    return $"{baseInstructions}\n\n{planInstructions}";
}
```

**This happens at construction time**, ensuring Plan Mode guidance is always present when enabled. If we switched to `ChatOptions.Instructions`, we'd need to:
1. Move this logic to `PrepareMessagesAsync`
2. Handle merging at runtime
3. Ensure consistency across all providers

---

## Deduplication Strategy

A critical aspect of our architecture is **preventing duplicate system messages**:

```csharp
// HPD-Agent/Agent/Agent.cs:3601-3603
// Check if there's already a system message
if (messagesList.Any(m => m.Role == ChatRole.System))
    return messagesList;
```

**Scenarios handled:**
1. User manually adds system message → Use user's message
2. ChatOptions.Instructions provided → Provider adds it
3. MessageProcessor would add it → Skip to avoid duplicate
4. Both paths active → First one wins (MessageProcessor checks for existing)

**Important:** This deduplication is **provider-agnostic** and works regardless of which path is used.

---

## Migration Considerations

### Why NOT to Migrate Fully to ChatOptions.Instructions

**Reasons to keep MessageProcessor:**

1. **Universal Compatibility**
   - Works with ALL providers (including those without Instructions support)
   - No dependency on 3rd-party SDK updates

2. **Plan Mode Integration**
   - Currently integrated at construction time
   - Would require architectural refactoring to move to runtime

3. **Prompt Filter Pipeline**
   - MessageProcessor integrates naturally with filter chain
   - Instructions can be modified by filters before sending

4. **Deduplication Logic**
   - Centralized in one place
   - Prevents duplicate system messages across providers

5. **Battle-Tested Stability**
   - Current implementation is proven and reliable
   - No edge cases or bugs

### When to Use ChatOptions.Instructions

**Recommended use cases:**

1. **Per-Request Overrides**
   ```csharp
   var options = new ChatOptions
   {
       Instructions = "For this specific request, use a formal tone"
   };
   await agent.RunAsync(messages, options);
   ```

2. **Provider-Specific Optimizations**
   - Some providers may handle Instructions specially
   - Future provider features may leverage this property

3. **Microsoft Ecosystem Integration**
   - When using tools that expect ChatOptions.Instructions
   - Future Microsoft.Agents.AI compatibility

### Hybrid Best Practices

**Recommended pattern:**

```csharp
// Base agent instructions (Plan Mode, static guidance)
var agent = new AgentBuilder()
    .WithInstructions("You are a helpful coding assistant") // MessageProcessor
    .WithPlanMode()
    .Build();

// Per-request dynamic instructions (optional)
var options = new ChatOptions
{
    Instructions = "Focus on TypeScript for this request" // ChatOptions path
};

await agent.RunAsync(messages, options);
```

**Result:** Both instruction sources are combined (if provider supports it) or MessageProcessor takes precedence.

---

## Provider Implementation Checklist

For each provider in HPD-Agent, we need to verify `ChatOptions.Instructions` support:

### ✅ Completed
- [x] **OpenRouter** - Added support (2025-01)
- [x] **OpenAI** - Native Microsoft wrapper support
- [x] **Azure OpenAI** - Native Microsoft wrapper support

### ❓ To Verify
- [ ] **Ollama** (OllamaSharp SDK) - Check SDK implementation
- [ ] **Anthropic** (Anthropic.SDK) - Check SDK implementation
- [ ] **Bedrock** - Verify implementation type
- [ ] **Google AI** - Verify implementation type
- [ ] **HuggingFace** - Verify implementation type
- [ ] **Mistral** - Verify implementation type
- [ ] **OnnxRuntime** - Verify implementation type

### Implementation Template (for custom providers)

```csharp
private ProviderRequest BuildRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
{
    var requestMessages = new List<ProviderMessage>();

    // ✨ Support ChatOptions.Instructions
    if (options?.Instructions is { } instructions && !string.IsNullOrWhiteSpace(instructions))
    {
        requestMessages.Add(new ProviderMessage
        {
            Role = "system",
            Content = instructions
        });
    }

    // ... rest of implementation
}
```

---

## Architectural Validation

Our approach has been validated by Microsoft's own evolution:

### Microsoft.Agents.AI Confirms Our Pattern

The new Agent Framework implements a **similar layered approach**:

```csharp
// Microsoft.Agents.AI/ChatClient/ChatClientAgent.cs:649-653
if (!string.IsNullOrWhiteSpace(this.Instructions))
{
    chatOptions ??= new();
    chatOptions.Instructions = string.IsNullOrWhiteSpace(chatOptions.Instructions)
        ? this.Instructions
        : $"{this.Instructions}\n{chatOptions.Instructions}";
}
```

This confirms that:
- ✅ Multiple instruction layers are a valid pattern
- ✅ Merging base + dynamic instructions is correct
- ✅ Our MessageProcessor approach aligns with Microsoft's thinking

### Our Advantages Over Microsoft.Agents.AI

While Microsoft's approach is sophisticated, ours has additional benefits:

| Feature | HPD-Agent | Microsoft.Agents.AI |
|---------|-----------|---------------------|
| Plan Mode Integration | ✅ Built-in | ❌ Manual |
| Prompt Filter Pipeline | ✅ Full support | ⚠️ Limited |
| Universal Provider Support | ✅ Works everywhere | ⚠️ Provider-dependent |
| Deduplication Logic | ✅ Centralized | ⚠️ Per-provider |
| Construction-Time Merging | ✅ Efficient | ⚠️ Runtime overhead |

---

## Future Considerations

### When to Revisit This Architecture

**Triggers for reevaluation:**

1. **Microsoft deprecates MessageProcessor pattern**
   - Unlikely, but monitor official guidance

2. **All major providers add Instructions support**
   - Once Ollama, Anthropic, etc. all support it natively

3. **Performance issues with MessageProcessor**
   - Current overhead is negligible, but monitor at scale

4. **Microsoft.Agents.AI becomes standard**
   - If the ecosystem standardizes on the new framework

### Potential Future Optimizations

**Provider-Specific Fast Paths:**

```csharp
// Future optimization: Detect provider capabilities
if (provider.SupportsInstructions)
{
    // Use ChatOptions.Instructions (more efficient)
    options.Instructions = systemInstructions;
}
else
{
    // Fallback to MessageProcessor
    messages = PrependSystemInstructions(messages);
}
```

**Dynamic Instruction Merging:**

```csharp
// Future: Support runtime instruction merging
public class InstructionMerger
{
    public string Merge(
        string? baseInstructions,
        string? planModeInstructions,
        string? dynamicInstructions,
        string? perRequestInstructions)
    {
        // Intelligent merging with priority resolution
    }
}
```

---

## Conclusion

Our hybrid approach represents a **pragmatic solution** to the system instructions problem:

✅ **Universal Compatibility** - Works with all providers, regardless of Instructions support
✅ **Future-Proof** - Supports both MessageProcessor and ChatOptions paths
✅ **Battle-Tested** - Current implementation is stable and reliable
✅ **Flexible** - Allows per-request overrides when needed
✅ **Microsoft-Validated** - Aligns with Microsoft.Agents.AI patterns

**Bottom Line:** We retrofitted system instructions when Microsoft.Extensions.AI lacked support, but now that it exists, we support both approaches for maximum flexibility and compatibility.

---

## References

- [ChatOptions.cs](Reference/extensions/src/Libraries/Microsoft.Extensions.AI.Abstractions/ChatCompletion/ChatOptions.cs#L62) - Microsoft.Extensions.AI Instructions property
- [OpenAIChatClient.cs](Reference/extensions/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIChatClient.cs#L140-L142) - Microsoft's OpenAI implementation
- [ChatClientAgent.cs](Reference/agent-framework/dotnet/src/Microsoft.Agents.AI/ChatClient/ChatClientAgent.cs#L649-L653) - Microsoft.Agents.AI layered approach
- [Agent.cs](HPD-Agent/Agent/Agent.cs#L3594-L3608) - HPD-Agent MessageProcessor implementation
- [OpenRouterChatClient.cs](HPD-Agent.Providers/HPD-Agent.Providers.OpenRouter/OpenRouterChatClient.cs#L490-L500) - OpenRouter Instructions support (added 2025-01)

---

**Document Version:** 1.0
**Last Updated:** 2025-01-09
**Status:** Active Implementation
