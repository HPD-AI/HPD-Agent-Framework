# 3rd-Party SDK Instructions Support Verification

## Summary

Analysis of how popular 3rd-party SDKs that HPD-Agent uses implement `Microsoft.Extensions.AI.IChatClient` and handle `ChatOptions.Instructions`.

---

## OllamaSharp SDK

**Package:** `OllamaSharp` v5.3.4
**Status:** ✅ Implements `IChatClient`
**Instructions Support:** ❓ **Needs runtime verification**

### Implementation Details

```csharp
public class OllamaApiClient : IOllamaApiClient, IChatClient, IEmbeddingGenerator<string, Embedding<float>>
{
    /// <inheritdoc/>
    async Task<ChatResponse> IChatClient.GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var request = AbstractionMapper.ToOllamaSharpChatRequest(
            this, messages, options, stream: false, OutgoingJsonSerializerOptions);
        var response = await ChatAsync(request, cancellationToken).StreamToEndAsync().ConfigureAwait(false);
        return AbstractionMapper.ToChatResponse(response, response?.Model ?? request.Model ?? SelectedModel) ?? new ChatResponse([]);
    }

    /// <inheritdoc/>
    async IAsyncEnumerable<ChatResponseUpdate> IChatClient.GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = AbstractionMapper.ToOllamaSharpChatRequest(
            this, messages, options, stream: true, OutgoingJsonSerializerOptions);

        string responseId = Guid.NewGuid().ToString("N");
        await foreach (var response in ChatAsync(request, cancellationToken).ConfigureAwait(false))
            yield return AbstractionMapper.ToChatResponseUpdate(response, responseId);
    }
}
```

### Key Points

1. **IChatClient Implementation:** ✅ Full implementation
2. **Instructions Handling:** Uses `AbstractionMapper.ToOllamaSharpChatRequest()`
3. **Unknown Factor:** Need to inspect `AbstractionMapper` to see if it handles `options.Instructions`

### What We Need to Verify

The critical method is `AbstractionMapper.ToOllamaSharpChatRequest()`. This method needs to:

```csharp
// What we need to check in AbstractionMapper
static ChatRequest ToOllamaSharpChatRequest(...)
{
    // Does it check options.Instructions?
    if (options?.Instructions is { } instructions && !string.IsNullOrWhiteSpace(instructions))
    {
        // Add system message?
    }
}
```

### Expected Behavior (If Supported)

**If OllamaSharp supports Instructions:**
```csharp
var options = new ChatOptions { Instructions = "You are helpful" };
// → AbstractionMapper adds system message to request
// → Ollama receives messages with system role
```

**If NOT supported:**
```csharp
var options = new ChatOptions { Instructions = "You are helpful" };
// → AbstractionMapper ignores Instructions
// → Ollama only receives user messages
```

### Workaround (Current State)

**Our MessageProcessor approach works regardless:**
```csharp
var agent = new AgentBuilder()
    .WithInstructions("You are helpful") // MessageProcessor adds system message
    .WithProvider("ollama", "llama3.2")
    .Build();
// ✅ Always works - MessageProcessor prepends system message before SDK sees it
```

---

## Anthropic.SDK

**Package:** `Anthropic.SDK` v5.5.2
**Status:** ✅ Provides `IChatClient` implementation
**Instructions Support:** ❓ **Needs runtime verification**

### Implementation Details

```csharp
public class AnthropicClient : IDisposable
{
    public AnthropicClient(
        APIAuthentication apiKeys = null,
        HttpClient client = null,
        IRequestInterceptor requestInterceptor = null)
    {
        HttpClient = SetupClient(client);
        _requestInterceptor = requestInterceptor;
        this.Auth = apiKeys.ThisOrDefault();

        // Messages endpoint implements IChatClient
        Messages = new MessagesEndpoint(this);
        Batches = new BatchesEndpoint(this);
        Models = new ModelsEndpoint(this);
        Files = new FilesEndpoint(this);
        Skills = new SkillsEndpoint(this);
    }

    /// <summary>
    /// Text generation is the core function of the API.
    /// </summary>
    public MessagesEndpoint Messages { get; }
}
```

### Key Points

1. **IChatClient Implementation:** ✅ Via `MessagesEndpoint`
2. **Instructions Handling:** Likely in `MessagesEndpoint.GetResponseAsync()`
3. **Unknown Factor:** Need to inspect `MessagesEndpoint` implementation

### What We Need to Verify

The `MessagesEndpoint` class needs to implement:

```csharp
// What MessagesEndpoint should implement
class MessagesEndpoint : IChatClient
{
    async Task<ChatResponse> IChatClient.GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        // Does it check options.Instructions?
        // Anthropic API uses "system" parameter, not system messages
    }
}
```

### Anthropic API Specifics

**Important:** Anthropic's API has a **different structure** than OpenAI:

**OpenAI Format (system message):**
```json
{
  "messages": [
    { "role": "system", "content": "You are helpful" },
    { "role": "user", "content": "Hello" }
  ]
}
```

**Anthropic Format (system parameter):**
```json
{
  "system": "You are helpful",
  "messages": [
    { "role": "user", "content": "Hello" }
  ]
}
```

**This means:** Anthropic.SDK's `MessagesEndpoint` likely needs special handling to convert system messages or `ChatOptions.Instructions` into the `system` parameter.

### Expected Behavior (If Supported)

**If Anthropic.SDK supports Instructions:**
```csharp
var options = new ChatOptions { Instructions = "You are helpful" };
// → MessagesEndpoint converts to { "system": "You are helpful" }
// → Anthropic API receives system parameter
```

**If NOT supported:**
```csharp
var options = new ChatOptions { Instructions = "You are helpful" };
// → MessagesEndpoint ignores Instructions
// → No system parameter sent to Anthropic
```

### Workaround (Current State)

**Our MessageProcessor approach:**
```csharp
var agent = new AgentBuilder()
    .WithInstructions("You are helpful") // MessageProcessor adds system message
    .WithProvider("anthropic", "claude-3-5-sonnet-20241022")
    .Build();

// Anthropic.SDK should:
// 1. Receive messages with ChatRole.System
// 2. Convert system message to "system" API parameter
// ✅ Should work if SDK handles system messages correctly
```

---

## Azure Communication Chat SDK

**Package:** `Azure.Communication.Chat`
**Status:** ❌ **NOT an IChatClient implementation**
**Purpose:** Azure Communication Services (chat rooms, not LLM)

### Important Note

This SDK is **NOT for LLM chat completions**. It's for Azure Communication Services real-time chat (like Teams/Slack).

**Do NOT confuse with:**
- `Azure.AI.OpenAI` - LLM chat (✅ what we want)
- `Azure.Communication.Chat` - Communication services (❌ not relevant)

---

## Verification Action Items

### Priority 1: OllamaSharp (High Usage)

**Steps:**
1. Create test project with OllamaSharp v5.3.4
2. Test `ChatOptions.Instructions`:
   ```csharp
   var client = new OllamaApiClient("http://localhost:11434", "llama3.2");
   var options = new ChatOptions { Instructions = "Test" };
   var response = await client.GetResponseAsync(messages, options);
   // Check if system message appears in Ollama logs
   ```
3. Inspect network traffic to Ollama API
4. Verify system message in request payload

**Expected Result:**
- ✅ If supported: System message in Ollama request
- ❌ If not: No system message, only user messages

**Fallback:** MessageProcessor always works

### Priority 2: Anthropic.SDK (Medium Usage)

**Steps:**
1. Create test project with Anthropic.SDK v5.5.2
2. Test `ChatOptions.Instructions`:
   ```csharp
   var client = new AnthropicClient("api-key");
   var options = new ChatOptions { Instructions = "Test" };
   var response = await client.Messages.GetResponseAsync(messages, options);
   // Check if "system" parameter appears in API request
   ```
3. Inspect network traffic to Anthropic API
4. Verify `system` parameter in request

**Expected Result:**
- ✅ If supported: `{"system": "Test", "messages": [...]}`
- ❌ If not: `{"messages": [...]}` (no system parameter)

**Fallback:** MessageProcessor should work if SDK converts system messages

### Priority 3: Source Code Analysis

**OllamaSharp:**
- Inspect `AbstractionMapper.ToOllamaSharpChatRequest()`
- Check if it handles `options.Instructions`
- Check if it converts to Ollama system messages

**Anthropic.SDK:**
- Inspect `MessagesEndpoint.GetResponseAsync()`
- Check if it handles `options.Instructions`
- Check if it converts to Anthropic `system` parameter
- Check if it converts system messages to `system` parameter

---

## Update Provider Support Matrix

Based on verification results:

### Before Verification

| Provider | Instructions Support | Status |
|----------|---------------------|--------|
| Ollama | ❓ Unknown | OllamaSharp SDK |
| Anthropic | ❓ Unknown | Anthropic.SDK |

### After Verification (To Be Updated)

| Provider | Instructions Support | Implementation Details |
|----------|---------------------|------------------------|
| Ollama | ✅ / ❌ TBD | `AbstractionMapper` handles/ignores it |
| Anthropic | ✅ / ❌ TBD | `MessagesEndpoint` converts to system param |

---

## Recommendations

### Short Term

1. **Test both SDKs** with `ChatOptions.Instructions`
2. **Document findings** in this file
3. **Update architecture docs** with verified status

### Medium Term

If SDKs don't support Instructions:

**Option A: Keep MessageProcessor (Recommended)**
- ✅ Already works
- ✅ No code changes needed
- ✅ Universal compatibility

**Option B: Contribute to SDKs**
- Fork OllamaSharp / Anthropic.SDK
- Add Instructions handling to AbstractionMapper / MessagesEndpoint
- Submit PR upstream

**Option C: Custom Wrapper**
- Wrap SDK clients with our own `IChatClient` implementation
- Handle Instructions conversion ourselves
- More maintenance overhead

### Long Term

Monitor SDK updates:
- Watch for Instructions support in releases
- Update documentation when added
- Consider removing MessageProcessor if all providers support it

---

## Code Examples for Testing

### OllamaSharp Test

```csharp
using OllamaSharp;
using Microsoft.Extensions.AI;

var client = new OllamaApiClient("http://localhost:11434", "llama3.2");

// Test 1: ChatOptions.Instructions
var options1 = new ChatOptions
{
    Instructions = "You are a helpful coding assistant"
};
var messages1 = new List<ChatMessage>
{
    new ChatMessage(ChatRole.User, "Write a hello world in C#")
};
var response1 = await client.GetResponseAsync(messages1, options1);

// Test 2: System Message (Fallback)
var messages2 = new List<ChatMessage>
{
    new ChatMessage(ChatRole.System, "You are a helpful coding assistant"),
    new ChatMessage(ChatRole.User, "Write a hello world in C#")
};
var response2 = await client.GetResponseAsync(messages2);

// Compare: Both should produce same result if Instructions is supported
```

### Anthropic.SDK Test

```csharp
using Anthropic.SDK;
using Microsoft.Extensions.AI;

var client = new AnthropicClient("sk-ant-...");

// Test 1: ChatOptions.Instructions
var options1 = new ChatOptions
{
    Instructions = "You are a helpful coding assistant"
};
var messages1 = new List<ChatMessage>
{
    new ChatMessage(ChatRole.User, "Write a hello world in C#")
};
var response1 = await client.Messages.GetResponseAsync(messages1, options1);

// Test 2: System Message (Fallback)
var messages2 = new List<ChatMessage>
{
    new ChatMessage(ChatRole.System, "You are a helpful coding assistant"),
    new ChatMessage(ChatRole.User, "Write a hello world in C#")
};
var response2 = await client.Messages.GetResponseAsync(messages2);

// Compare: Both should produce same result if SDK handles system messages
```

---

## Conclusion

Both OllamaSharp and Anthropic.SDK implement `IChatClient`, which is great! However:

**Unknown:** Whether they handle `ChatOptions.Instructions` in their internal mappers
**Safe Bet:** Our MessageProcessor approach works regardless
**Next Steps:** Runtime verification and source code inspection

**Bottom Line:** Our hybrid approach (MessageProcessor + ChatOptions support in OpenRouter) gives us maximum compatibility until we verify all SDKs.

---

**Document Version:** 1.0
**Last Updated:** 2025-01-09
**Status:** Pending Verification
