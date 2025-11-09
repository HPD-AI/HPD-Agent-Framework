# Implementation Locations - Microsoft.Extensions.AI ChatOptions Modernization

## Critical Code Locations

### 1. ConversationId Extraction (Lines 394-404)
**File:** `/HPD-Agent/Agent/Agent.cs`

**Current Code:**
```csharp
// Extract conversation ID from options or generate new one
string conversationId;
if (options?.AdditionalProperties?.TryGetValue("ConversationId", out var convIdObj) == true && convIdObj is string convId)
{
    conversationId = convId;
}
else
{
    conversationId = Guid.NewGuid().ToString();
}

_conversationId = conversationId;
```

**Target (Phase 1):**
```csharp
// Extract conversation ID from ChatOptions first (new), then fallback to AdditionalProperties (legacy)
string conversationId = options?.ConversationId ?? 
                       (options?.AdditionalProperties?.TryGetValue("ConversationId", out var convIdObj) == true && convIdObj is string convId ? convId : null) ??
                       Guid.NewGuid().ToString();

_conversationId = conversationId;
```

---

### 2. ConversationId Assignment to Options (Lines 1154-1157)
**File:** `/HPD-Agent/Agent/Agent.cs`

**Current Code:**
```csharp
chatOptions ??= new ChatOptions();
chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
chatOptions.AdditionalProperties["ConversationId"] = aguiInput.ThreadId;
chatOptions.AdditionalProperties["RunId"] = aguiInput.RunId;
```

**Target (Phase 1):**
```csharp
chatOptions ??= new ChatOptions();
chatOptions.ConversationId = aguiInput.ThreadId;  // NEW: Use typed property
chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
chatOptions.AdditionalProperties["ConversationId"] = aguiInput.ThreadId;  // KEEP: Legacy support
chatOptions.AdditionalProperties["RunId"] = aguiInput.RunId;
```

---

### 3. System Instructions Prepending (Lines 3594-3612)
**File:** `/HPD-Agent/Agent/Agent.cs` (MessageProcessor class)

**Current Code:**
```csharp
private IEnumerable<ChatMessage> PrependSystemInstructions(IEnumerable<ChatMessage> messages)
{
    if (string.IsNullOrEmpty(_systemInstructions))
        return messages;

    // Prepend system instruction
    var systemMessage = new ChatMessage(ChatRole.System, _systemInstructions);
    return new[] { systemMessage }.Concat(messages);
}
```

**Target (Phase 2):**
```csharp
// Method 1: For use in Options building
private void ApplySystemInstructionsToOptions(ChatOptions? options)
{
    if (options != null && !string.IsNullOrEmpty(_systemInstructions))
    {
        var instructions = new StringBuilder();
        if (!string.IsNullOrEmpty(_systemInstructions))
            instructions.AppendLine(_systemInstructions);
        if (!string.IsNullOrEmpty(options.Instructions))
            instructions.AppendLine(options.Instructions);
        
        if (instructions.Length > 0)
            options.Instructions = instructions.ToString();
    }
}

// Method 2: Legacy fallback (keep for compatibility)
private IEnumerable<ChatMessage> PrependSystemInstructions(IEnumerable<ChatMessage> messages)
{
    // Only use if provider doesn't support Instructions property
    if (string.IsNullOrEmpty(_systemInstructions))
        return messages;

    var systemMessage = new ChatMessage(ChatRole.System, _systemInstructions);
    return new[] { systemMessage }.Concat(messages);
}
```

---

### 4. Message Preparation Entry Point (Lines 3456-3488)
**File:** `/HPD-Agent/Agent/Agent.cs` (MessageProcessor class)

**Current Code:**
```csharp
public async Task<(IEnumerable<ChatMessage> messages, ChatOptions? options, ReductionMetadata? reduction)> PrepareMessagesAsync(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    string agentName,
    CancellationToken cancellationToken)
{
    var effectiveMessages = PrependSystemInstructions(messages);
    // ... rest of method
}
```

**Target (Phase 2):**
```csharp
public async Task<(IEnumerable<ChatMessage> messages, ChatOptions? options, ReductionMetadata? reduction)> PrepareMessagesAsync(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    string agentName,
    CancellationToken cancellationToken)
{
    // Apply system instructions to options.Instructions (Phase 2)
    options ??= new ChatOptions();
    ApplySystemInstructionsToOptions(options);
    
    // Prepare messages (may include system message fallback)
    var effectiveMessages = PrependSystemInstructions(messages);
    // ... rest of method
}
```

---

### 5. Plan Mode Instructions Augmentation (Lines 1317-1334)
**File:** `/HPD-Agent/Agent/Agent.cs`

**Current Code:**
```csharp
private static string? AugmentSystemInstructionsForPlanMode(AgentConfig config)
{
    var baseInstructions = config.SystemInstructions;
    
    if (config.PlanMode?.Enabled != true)
        return baseInstructions;
    
    var planInstructions = config.PlanMode.CustomInstructions ?? GetDefaultPlanModeInstructions();
    
    if (string.IsNullOrEmpty(baseInstructions))
        return planInstructions;
    
    return $"{baseInstructions}\n\n{planInstructions}";
}
```

**Target (Phase 2):**
```csharp
private ChatOptions? ApplyPlanModeToOptions(ChatOptions? baseOptions, AgentConfig config)
{
    if (config.PlanMode?.Enabled != true)
        return baseOptions;
    
    var options = baseOptions ?? new ChatOptions();
    var instructions = new StringBuilder();
    
    // Add plan mode instructions
    var planInstructions = config.PlanMode.CustomInstructions ?? GetDefaultPlanModeInstructions();
    instructions.AppendLine(planInstructions);
    
    // Append base instructions if present
    if (!string.IsNullOrEmpty(config.SystemInstructions))
        instructions.AppendLine(config.SystemInstructions);
    
    // Append any existing instruction from options
    if (!string.IsNullOrEmpty(options.Instructions))
        instructions.AppendLine(options.Instructions);
    
    if (instructions.Length > 0)
        options.Instructions = instructions.ToString();
    
    return options;
}
```

---

### 6. Options Merging (Lines 3613-3641)
**File:** `/HPD-Agent/Agent/Agent.cs` (MessageProcessor class)

**Current Code:**
```csharp
private ChatOptions? MergeOptions(ChatOptions? providedOptions)
{
    if (_defaultOptions == null)
        return providedOptions;

    if (providedOptions == null)
        return _defaultOptions;

    // Merge options - provided options take precedence
    return new ChatOptions
    {
        // ... many properties
        AdditionalProperties = MergeDictionaries(_defaultOptions.AdditionalProperties, providedOptions.AdditionalProperties)
    };
}
```

**Target (Phase 1-2):**
```csharp
private ChatOptions? MergeOptions(ChatOptions? providedOptions)
{
    if (_defaultOptions == null)
        return providedOptions;

    if (providedOptions == null)
        return _defaultOptions;

    // Merge options - provided options take precedence
    return new ChatOptions
    {
        // ... existing properties
        Instructions = providedOptions.Instructions ?? _defaultOptions.Instructions,  // NEW (Phase 2)
        ConversationId = providedOptions.ConversationId ?? _defaultOptions.ConversationId,  // NEW (Phase 1)
        ResponseFormat = providedOptions.ResponseFormat ?? _defaultOptions.ResponseFormat,  // NEW (Phase 3)
        AdditionalProperties = MergeDictionaries(_defaultOptions.AdditionalProperties, providedOptions.AdditionalProperties)
    };
}
```

---

### 7. Filter Context Creation (Lines 3658-3672)
**File:** `/HPD-Agent/Agent/Agent.cs` (MessageProcessor class)

**Current Code:**
```csharp
private async Task<IEnumerable<ChatMessage>> ApplyPromptFiltersAsync(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    string agentName,
    CancellationToken cancellationToken)
{
    if (!_promptFilters.Any())
        return messages;

    // Create filter context
    var context = new PromptFilterContext(messages, options, agentName, cancellationToken);

    // Transfer additional properties to filter context
    if (options?.AdditionalProperties != null)
    {
        foreach (var kvp in options.AdditionalProperties)
        {
            context.Properties[kvp.Key] = kvp.Value!;
        }
    }
    // ... rest of method
}
```

**Target (Phase 1-3):**
```csharp
private async Task<IEnumerable<ChatMessage>> ApplyPromptFiltersAsync(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    string agentName,
    CancellationToken cancellationToken)
{
    if (!_promptFilters.Any())
        return messages;

    // Create filter context using builder (Phase 1+)
    var context = PromptFilterContextBuilder.Create(messages, options, agentName, cancellationToken);
    // ... rest of method
}
```

---

### 8. Project/Thread Context Assignment (Lines 1775-1779)
**File:** `/HPD-Agent/Agent/Agent.cs`

**Current Code:**
```csharp
chatOptions ??= new ChatOptions();
chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
chatOptions.AdditionalProperties["Project"] = project;
chatOptions.AdditionalProperties["ConversationId"] = conversationThread.Id;
chatOptions.AdditionalProperties["Thread"] = conversationThread;
```

**Target (Phase 0):**
```csharp
chatOptions ??= new ChatOptions();
chatOptions
    .WithProject(project)
    .WithThread(conversationThread)
    .WithConversationId(conversationThread.Id);  // NEW (Phase 1)
```

---

## Files to Create

### New File: ChatOptionsContextExtensions.cs
**Location:** `/HPD-Agent/Filters/PromptFiltering/ChatOptionsContextExtensions.cs`

```csharp
public static class ChatOptionsContextExtensions
{
    public static ChatOptions WithProject(this ChatOptions options, Project project)
    {
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties["Project"] = project;
        return options;
    }
    
    public static ChatOptions WithThread(this ChatOptions options, ConversationThread thread)
    {
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties["Thread"] = thread;
        return options;
    }
    
    public static ChatOptions WithConversationId(this ChatOptions options, string conversationId)
    {
        options.ConversationId = conversationId;
        return options;
    }
}
```

---

### New File: PromptFilterContextBuilder.cs
**Location:** `/HPD-Agent/Filters/PromptFiltering/PromptFilterContextBuilder.cs`

```csharp
public static class PromptFilterContextBuilder
{
    public static PromptFilterContext Create(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string agentName,
        CancellationToken cancellationToken)
    {
        var context = new PromptFilterContext(messages, options, agentName, cancellationToken);
        
        // Populate properties from typed ChatOptions properties (Phase 1+)
        if (options != null)
        {
            if (options.ConversationId != null)
                context.Properties["ConversationId"] = options.ConversationId;
            
            if (options.ResponseFormat != null)
                context.Properties["ResponseFormat"] = options.ResponseFormat;
        }
        
        // Legacy: copy AdditionalProperties for backward compatibility
        if (options?.AdditionalProperties != null)
        {
            foreach (var kvp in options.AdditionalProperties)
            {
                context.Properties[kvp.Key] = kvp.Value!;
            }
        }
        
        return context;
    }
}
```

---

## Files to Modify

### AgentConfig.cs
**Location:** `/HPD-Agent/Agent/AgentConfig.cs`

**Add after line 84 (after AgentMessagesConfig):**
```csharp
/// <summary>
/// Response format mode (Text, JSON, StructuredJSON)
/// Default is null (use provider default, typically Text)
/// </summary>
public ChatResponseFormat? ResponseFormat { get; set; }

/// <summary>
/// JSON schema for StructuredJSON mode (provider-specific format)
/// Only applicable when ResponseFormat is StructuredJSON
/// </summary>
public object? ResponseSchema { get; set; }
```

---

## Related Code Locations

### Project Context Usage in Filters
- **ProjectInjectedMemoryFilter.cs** (Lines 26-27): Reads Project from context.Properties
- **StaticMemoryFilter.cs**: Memory injection (doesn't use Project)
- **DynamicMemoryFilter.cs**: Memory injection (doesn't use Project)

### AGUI Input Handling
- **Agent.cs** (Lines 1147-1157): AGUI conversion and ConversationId assignment
- **Agent.cs** (Lines 1922-1932): AGUI tools handling with ConversationId

### Provider Implementations
- **OpenRouterChatClient.cs** (Lines 680-847): AdditionalProperties parameter extraction
- **OpenRouterProvider.cs**: Client creation
- Other providers in `/HPD-Agent.Providers/`

---

## Implementation Order

1. **Create Extension Methods** (Low risk, no existing code changes)
   - ChatOptionsContextExtensions.cs
   - PromptFilterContextBuilder.cs

2. **Phase 1: ConversationId** (Low risk, backward compatible)
   - Agent.cs lines 394-404
   - Agent.cs lines 1154-1157
   - MessageProcessor.MergeOptions()

3. **Phase 2: Instructions** (Medium risk, requires coordination with providers)
   - Agent.cs lines 3594-3612
   - MessageProcessor.PrepareMessagesAsync()
   - Agent.AugmentSystemInstructionsForPlanMode()

4. **Phase 3: ResponseFormat** (Medium risk, new feature)
   - AgentConfig.cs (add properties)
   - Agent.cs (apply in message preparation)

5. **Phase 4: Provider Updates** (High risk, touches external code)
   - OpenRouter, Anthropic, OpenAI providers

6. **Phase 5: Legacy Cleanup** (Low risk, after proving stability)
   - Remove AdditionalProperties["ConversationId"] fallback
   - Remove system message prepending for compatible providers

---

## Testing Locations

### Existing Tests to Verify
- Tests in `/AgentConsoleTest/` for agent behavior
- Tests for ConversationId extraction and storage
- Tests for message preparation and system instructions

### New Tests to Create
- ConversationId property flow tests
- Instructions property population tests
- ResponseFormat application tests
- Backward compatibility tests for AdditionalProperties
- Provider-specific optimization tests

