# HPD-Agent Context Processing Flow: Complete Map

## Executive Summary
This document traces the complete flow of user input/context through the HPD-Agent system from initial entry point to final ChatClient invocation. The flow includes all filters, processors, transformations, and memory injection points.

---

## 1. ENTRY POINTS

### 1.1 Primary Entry Point: `Agent.RunAsync()` (Non-Streaming)
**Location**: `/HPD-Agent/Agent/Agent.cs` (line 1625)

```
Agent.RunAsync(IEnumerable<ChatMessage> messages, AgentThread? thread, AgentRunOptions? options)
  ↓
  Creates: ConversationThread (from thread parameter or new)
  ↓
  Calls: RunAgenticLoopInternal() [line 1662]
```

**Key transformations at entry**:
- Converts `AgentThread` to `ConversationThread` for state management
- Converts `AgentRunOptions` to `ChatOptions`
- Adds messages to thread state

### 1.2 Streaming Entry Point: `Agent.RunStreamingAsync()` 
**Location**: `/HPD-Agent/Agent/Agent.cs` (line 1753)

```
Agent.RunStreamingAsync(IEnumerable<ChatMessage> messages, AgentThread? thread, AgentRunOptions? options)
  ↓
  Sets: Agent.CurrentThread = conversationThread (AsyncLocal)
  ↓
  Extracts: Project context from thread [line 1772]
  ↓
  Builds: ChatOptions with project and thread metadata [lines 1774-1780]
  ↓
  Calls: RunAgenticLoopInternal() [line 1807]
```

**Project Context Injection**:
```csharp
chatOptions.AdditionalProperties["Project"] = project;
chatOptions.AdditionalProperties["ConversationId"] = conversationThread.Id;
chatOptions.AdditionalProperties["Thread"] = conversationThread;
```

---

## 2. CORE AGENTIC LOOP

### 2.1 RunAgenticLoopInternal (Protocol-Agnostic Core)
**Location**: `/HPD-Agent/Agent/Agent.cs` (line 353)

**Function signature**:
```csharp
private async IAsyncEnumerable<InternalAgentEvent> RunAgenticLoopInternal(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    string[]? documentPaths,
    List<ChatMessage> turnHistory,
    TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletionSource,
    TaskCompletionSource<ReductionMetadata?> reductionCompletionSource,
    CancellationToken cancellationToken)
```

### 2.2 Context Setup Phase (Lines 362-404)

1. **OpenTelemetry Setup**: Creates activity source for tracing
2. **Root Agent Tracking**: Sets AsyncLocal for event bubbling in nested agents
3. **Document Processing**: If documentPaths provided, processes documents
4. **Conversation ID**: Extracts or generates conversation ID from options.AdditionalProperties["ConversationId"]

---

## 3. MESSAGE PREPARATION PHASE

### 3.1 MessageProcessor.PrepareMessagesAsync()
**Location**: `/HPD-Agent/Agent/Agent.cs` (line 3456)

**Flow**:
```
PrepareMessagesAsync(messages, options, agentName, cancellationToken)
  ↓
  1. PrependSystemInstructions(messages)
  2. MergeOptions(options)
  3. ApplyHistoryReduction (if configured)
  4. ApplyPromptFiltersAsync() ← KEY FILTER PIPELINE
```

**Return value**:
```csharp
(IEnumerable<ChatMessage> messages, ChatOptions? options, ReductionMetadata? reduction)
```

### 3.2 Prompt Filter Pipeline Execution
**Location**: `/HPD-Agent/Agent/Agent.cs` (line 3646)

**Context Creation** (line 3658):
```csharp
var context = new PromptFilterContext(messages, options, agentName, cancellationToken);

// Transfer additional properties from options to filter context
if (options?.AdditionalProperties != null)
{
    foreach (var kvp in options.AdditionalProperties)
    {
        context.Properties[kvp.Key] = kvp.Value!;
    }
}
```

**Properties transferred to filters include**:
- `Project` - The Project instance
- `ConversationId` - Conversation ID
- `Thread` - The ConversationThread instance
- Any custom user-provided properties

**Pipeline Building** (line 3671):
```csharp
var pipeline = FilterChain.BuildPromptPipeline(_promptFilters, finalAction);
return await pipeline(context);
```

---

## 4. PROMPT FILTERS (Pre-LLM Processing)

### Filters are applied in registration order:

#### 4.1 ProjectInjectedMemoryFilter (Auto-Registered)
**Location**: `/HPD-Agent/Agent/Agent.cs` (line 698)

**Execution Flow**:
```
InvokeAsync(context, next)
  ↓
  1. Extract Project from context.Properties["Project"] [line 27]
  2. Check cache (2-minute TTL)
  3. If cache miss:
     a. Call project.DocumentManager.GetDocumentsAsync()
     b. Build document tag with format: "[PROJECT_DOCUMENT: {filename}]\n{text}\n[/PROJECT_DOCUMENT]"
  4. Inject documents into messages as System message
  5. Call next() filter
```

**Output**: System message prepended with project documents

#### 4.2 DynamicMemoryFilter (Optional - User Configured)
**Location**: `/HPD-Agent/Memory/Agent/DynamicMemory/DynamicMemoryFilter.cs`

**Execution Flow**:
```
InvokeAsync(context, next)
  ↓
  1. Get memoryId from context.AgentName or filter config
  2. Check cache (1-minute TTL)
  3. If cache miss:
     a. Call _store.GetMemoriesAsync(storageKey)
     b. Build memory tag with format:
        [AGENT_MEMORY_START]
        --- (for each memory)
        Id: {id}
        Title: {title}
        Content: {content}
        [AGENT_MEMORY_END]
  4. Inject memories as System message
  5. Call next() filter
```

**Memory Sources**:
- `JsonDynamicMemoryStore` (file-based)
- Custom stores implementing `DynamicMemoryStore`

#### 4.3 StaticMemoryFilter (Optional - User Configured)
**Location**: `/HPD-Agent/Memory/Agent/StaticMemory/StaticMemoryFilter.cs`

**Execution Flow**:
```
InvokeAsync(context, next)
  ↓
  1. Get knowledgeId from filter config
  2. Check cache (5-minute TTL - longer for static content)
  3. If cache miss:
     a. Call _store.GetCombinedKnowledgeTextAsync(knowledgeId, maxTokens)
     b. Build knowledge section
  4. Inject into existing system message or create new one
  5. Call next() filter
```

**Knowledge Sources**:
- `JsonStaticMemoryStore` (file-based)
- Custom stores implementing `StaticMemoryStore`

#### 4.4 AgentPlanFilter (Optional - User Configured)
**Location**: `/HPD-Agent/Memory/Agent/PlanMode/AgentPlanFilter.cs`

**Execution Flow**:
- Retrieves current plan from store
- Injects plan context into system message
- Called next() filter

#### 4.5 Custom User Filters
- Any filters registered via `builder.WithPromptFilter()`
- Applied in registration order

### Filter Pipeline Pattern (Line 5722)
```csharp
// Filters execute in ORDER (reverse wrapped for LIFO pipeline)
// Example: [Filter1, Filter2, Filter3] → Filter1 → Filter2 → Filter3 → Messages

// Building (reverse order for execution in forward order):
Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> pipeline = finalAction;
foreach (var filter in filters.Reverse())  // Reverse iteration
{
    var previous = pipeline;
    pipeline = ctx => filter.InvokeAsync(ctx, previous);  // Wrap
}
```

---

## 5. MESSAGE STATE AFTER FILTERS

After all prompt filters execute, messages contain:

```
[System Message from ProjectInjectedMemoryFilter]
[PROJECT_DOCUMENTS_START]
[PROJECT_DOCUMENT: file1.pdf]
...extracted text...
[/PROJECT_DOCUMENT]
[PROJECT_DOCUMENTS_END]

[System Message from DynamicMemoryFilter]
[AGENT_MEMORY_START]
---
Id: mem1
Title: ...
Content: ...
[AGENT_MEMORY_END]

[System Message from StaticMemoryFilter]
[KNOWLEDGE_START]
...knowledge content...
[KNOWLEDGE_END]

[System Message: System Instructions]
...agent persona...

[Original System Messages from user]
[User Messages from conversation]
[Assistant Messages from conversation]
[Tool Results from previous iterations]
```

---

## 6. DECISION ENGINE

### 6.1 AgentDecisionEngine (Line 437)
**Location**: `/HPD-Agent/Agent/Agent.cs`

```
DecideNextAction(state, lastResponse, config)
  ↓
  Determines:
  - CallLLM → Send to ChatClient
  - ExecuteTools → Run function calls
  - Terminate → End conversation
```

---

## 7. LLM CALL EXECUTION

### 7.1 AgentTurn.RunAsync() (Inline Execution at Line 559)
**Location**: `/HPD-Agent/Agent/Agent.cs` (line 3783)

**Execution**:
```
RunAsync(messagesToSend, scopedOptions, cancellationToken)
  ↓
  1. Applies plugin scoping (if enabled)
  2. Calls _baseClient.GetStreamingResponseAsync(messages, options, cancellationToken)
  3. _baseClient is the provider's ChatClient:
     - OpenRouterChatClient (if provider="openrouter")
     - OpenAIChatClient (if provider="openai")
     - AnthropicChatClient (if provider="anthropic")
     - etc.
```

### 7.2 Chat Client Implementation
**Providers**: Located in `/HPD-Agent.Providers/` packages

Each provider implements `IChatClient` and handles:
- Authentication
- API request formatting
- Streaming response handling
- Error handling

---

## 8. STREAMING & EVENT PROCESSING

### 8.1 Real-time Event Yielding (Lines 559-656)
```
As LLM streams response:
  → TextReasoningContent yielded
  → TextContent yielded
  → FunctionCallContent yielded
  
Events emitted immediately:
  - InternalReasoningStartEvent
  - InternalTextMessageStartEvent
  - InternalTextDeltaEvent
  - InternalToolCallStartEvent
  - InternalToolCallArgsEvent
```

### 8.2 Filter Event Polling (Lines 636-640, 772-782)
```
During LLM streaming:
  While (_eventCoordinator.EventReader.TryRead(out var filterEvt))
  {
      yield return filterEvt;
  }
```

Allows filters to emit events (e.g., permission requests) during execution.

---

## 9. TOOL EXECUTION PHASE

### 9.1 ToolScheduler.ExecuteToolsAsync() (Line 765)

**Flow**:
```
ExecuteToolsAsync(messages, toolRequests, options, state, cancellationToken)
  ↓
  1. Create FunctionInvocationContext for each tool
  2. Apply ScopedFilterManager filters
  3. Check PermissionManager for authorization
  4. Apply IAiFunctionFilter pipeline
  5. Invoke actual function
  6. Collect results
  7. Emit InternalToolResultEvent
```

### 9.2 ScopedFilterManager
- Applies filters based on function/plugin scope
- Supports function-level, plugin-level, and global filters
- Allows different filter chains per function

### 9.3 Permission Checking
```
PermissionManager.CheckPermissionAsync()
  ↓
  Applies IPermissionFilter pipeline
  ↓
  May yield: InternalPermissionRequestEvent (human-in-the-loop)
```

---

## 10. POST-INVOCATION FILTERS

### 10.1 MessageProcessor.ApplyPostInvokeFiltersAsync() (Line 3684)
**Called after**: LLM response received

**Parameters**:
```csharp
- requestMessages: Messages sent to LLM
- responseMessages: Messages returned by LLM (or null if failed)
- exception: Exception that occurred (or null)
- options: ChatOptions used
- agentName: Agent name
- cancellationToken: Cancellation token
```

**Use cases**:
- Extract memories from assistant responses
- Update knowledge bases
- Log conversation details
- Analyze context usefulness

---

## 11. MESSAGE TURN FILTERS

### 11.1 Applied after complete turn (Line 1703)

```csharp
if (_messageTurnFilters.Any())
{
    var userMessage = currentMessages.LastOrDefault(m => m.Role == ChatRole.User);
    await ApplyMessageTurnFilters(userMessage, finalHistory, chatOptions, cancellationToken);
}
```

**Use cases**:
- Telemetry and observability
- Turn-level logging
- Analytics

---

## 12. HISTORY REDUCTION (Optional)

### 12.1 ChatReducer (Line 274)
**When enabled**:
```
Triggered when:
- Message count exceeds threshold
- Token budget exhausted (future)
- Context window percentage exceeded (future)

Reduction strategies:
- MessageCounting: Keep last N messages
- Summarizing: Use LLM to compress old messages
```

**Output**: Summary message injected into conversation

---

## 13. RESPONSE BUILDING

### 13.1 Turn History Assembly (Line 1701)
```
turnHistory += assistant message (without reasoning)
turnHistory += tool results (if any)
```

### 13.2 Final History Completion (Line 3505)
```
historyCompletionSource.TrySetResult(finalMessages)
↓
Consumed by RunAsync at line 1678
```

---

## 14. MIDDLEWARE PIPELINE (Wrapping ChatClient)

### 14.1 Applied During Agent.Build()
**Location**: `/HPD-Agent/Agent/AgentBuilder.cs` (line 567)

**Order of application**:
```
Base ChatClient (from provider)
  ↓ (wrapped by)
  1. LoggingChatClient (if .WithLogging())
  ↓
  2. OpenTelemetryChatClient (if .WithOpenTelemetry())
  ↓
  3. DistributedCachingChatClient (if .WithCaching())
  ↓
  4. ConfigureOptionsChatClient (if .WithOptionsConfiguration())
  ↓
  Final wrapped client passed to Agent
```

---

## 15. COMPLETE CONTEXT FLOW DIAGRAM

```
USER INPUT
  ↓
RunAsync/RunStreamingAsync (Entry Point)
  ↓
ConversationThread Setup
  ↓
Project/Context Extraction
  ├─ Project from thread
  ├─ ConversationId
  └─ Thread metadata
  ↓
MessageProcessor.PrepareMessagesAsync()
  ├─ Prepend System Instructions
  ├─ Merge Options
  ├─ History Reduction (optional)
  └─ Apply Prompt Filters Pipeline:
      ├─ ProjectInjectedMemoryFilter
      │   └─ Injects project documents
      ├─ DynamicMemoryFilter (optional)
      │   └─ Injects editable agent memory
      ├─ StaticMemoryFilter (optional)
      │   └─ Injects static knowledge base
      ├─ AgentPlanFilter (optional)
      │   └─ Injects current plan
      └─ Custom Filters...
  ↓
AgentDecisionEngine.DecideNextAction()
  ↓
AgentTurn.RunAsync()
  ├─ Apply Plugin Scoping (if enabled)
  └─ _baseClient.GetStreamingResponseAsync()
      ├─ Middleware pipeline wraps request
      ├─ Provider ChatClient makes API call
      │   (OpenRouterChatClient, OpenAIChatClient, etc.)
      └─ Stream response with events
      ↓
      Emit Events (real-time):
      ├─ InternalTextMessageStartEvent
      ├─ InternalTextDeltaEvent
      ├─ InternalToolCallStartEvent
      ├─ InternalToolCallArgsEvent
      └─ InternalTextMessageEndEvent
  ↓
Tool Request Detection
  ├─ Create FunctionInvocationContext
  ├─ Apply ScopedFilterManager
  ├─ Check PermissionManager
  ├─ Apply IAiFunctionFilter pipeline
  └─ Execute functions
  ↓
Tool Result Collection
  ↓
MessageProcessor.ApplyPostInvokeFiltersAsync()
  ├─ DynamicMemoryFilter.PostInvokeAsync()
  ├─ StaticMemoryFilter.PostInvokeAsync()
  └─ Custom Filters.PostInvokeAsync()
  ↓
Message Turn Filters (optional)
  ├─ Logging/Telemetry
  └─ Custom turn observers
  ↓
History Assembly
  ├─ Add assistant message (without reasoning)
  └─ Add tool results
  ↓
Final Response → User
```

---

## 16. KEY CONTEXT PASSING MECHANISMS

### 16.1 ChatOptions.AdditionalProperties
**Primary mechanism for context injection**:
- Populated at RunStreamingAsync (line 1776-1779)
- Transferred to PromptFilterContext (lines 3661-3666)
- Accessed by filters via context.Properties

### 16.2 AsyncLocal Storage
**Flows context across async boundaries**:
- `Agent.CurrentThread`: ConversationThread (ThreadLocal)
- `Agent.RootAgent`: Root agent for nested calls
- `Agent.CurrentFunctionContext`: Function invocation metadata

### 16.3 PromptFilterContext.Properties
**Strongly-typed access via extensions**:
```csharp
context.GetProject()           // Retrieve Project
context.GetConversationId()    // Retrieve ConversationId
context.GetThread()            // Retrieve ConversationThread
```

---

## 17. FILTER REGISTRATION ORDER (CRITICAL)

**Automatic (in AgentBuilder.BuildCoreAsync)**:
1. ProjectInjectedMemoryFilter (line 698) - ALWAYS

**Optional (via extension methods)**:
2. StaticMemoryFilter (if WithStaticMemory)
3. DynamicMemoryFilter (if WithDynamicMemory)
4. AgentPlanFilter (if WithPlanMode)
5. Custom filters (via WithPromptFilter)

**Post-LLM** (not in main pipeline):
6. PostInvokeAsync callbacks (optional per filter)

---

## 18. ERROR HANDLING & REDUCTION

### 18.1 Error Handler (Provider-Specific)
**Created at**: AgentBuilder.BuildCoreAsync (line 558)
**Used for**: Normalizing provider-specific errors

### 18.2 History Reduction Metadata
**Passed to**: ConversationThread.ApplyReductionAsync() (line 1714)
**Contains**:
- SummaryMessage: Compressed conversation summary
- MessagesRemovedCount: How many messages were summarized

---

## 19. CONFIGURATION & CUSTOMIZATION POINTS

### Filters:
- `builder.WithPromptFilter()`
- `builder.WithPromptFilter<T>()`
- `builder.WithPromptFilters(params)`

### Memory:
- `builder.WithDynamicMemory()`
- `builder.WithStaticMemory()`
- Custom DynamicMemoryStore
- Custom StaticMemoryStore

### Reduction:
- `builder.WithHistoryReduction()`
- `builder.WithMessageCountingReduction()`
- `builder.WithSummarizingReduction()`

### Functions/Tools:
- `builder.WithFilter()` - IAiFunctionFilter
- `builder.WithPermissionFilter()` - IPermissionFilter
- `builder.WithMessageTurnFilter()` - IMessageTurnFilter

### Middleware:
- `builder.WithOpenTelemetry()`
- `builder.WithCaching()`
- `builder.WithLogging()`
- `builder.WithOptionsConfiguration()`

---

## 20. CONTEXT FLOW SUMMARY TABLE

| Stage | Input | Processing | Output | Key Classes |
|-------|-------|-----------|--------|-------------|
| Entry | ChatMessage[], AgentRunOptions | Thread setup | Context-enriched options | RunStreamingAsync |
| Preparation | Messages + Options | Merge, reduce, prepend | Prepared messages | MessageProcessor |
| Filters | PromptFilterContext | Chain execution | Modified messages | ProjectInjectedMemoryFilter, DynamicMemoryFilter, etc. |
| Decision | State + Response | Pure decision logic | Next action | AgentDecisionEngine |
| LLM Call | ChatMessage[] + ChatOptions | API invocation | Streaming updates | AgentTurn + Provider ChatClient |
| Tool Exec | FunctionCall[] | Filter → Permission → Invoke | Tool results | ToolScheduler, FunctionCallProcessor |
| Post-Invoke | Request/Response | Memory extraction | Stored memories | MessageProcessor.ApplyPostInvokeFiltersAsync |
| History | Turn events | Assembly | Final message list | Agent.RunAsync return |

---

## 21. CRITICAL FILES REFERENCE

| File | Purpose | Key Methods |
|------|---------|------------|
| `/HPD-Agent/Agent/Agent.cs` | Core agent logic | RunAsync, RunStreamingAsync, RunAgenticLoopInternal |
| `/HPD-Agent/Agent/Agent.cs` (line 3420) | Message preparation | MessageProcessor.PrepareMessagesAsync |
| `/HPD-Agent/Filters/PromptFiltering/IPromptFilter.cs` | Filter interface | InvokeAsync, PostInvokeAsync |
| `/HPD-Agent/Project/DocumentHandling/FullTextInjection/ProjectInjectedMemoryFilter.cs` | Project documents | InjectDocuments |
| `/HPD-Agent/Memory/Agent/DynamicMemory/DynamicMemoryFilter.cs` | Dynamic memory injection | InjectMemories |
| `/HPD-Agent/Memory/Agent/StaticMemory/StaticMemoryFilter.cs` | Static knowledge | InjectKnowledge |
| `/HPD-Agent/Agent/AgentBuilder.cs` | Agent construction | BuildCoreAsync |

---

## 22. TRACING CONTEXT THROUGH EXECUTION

**Example: Where does a project document end up?**

```
1. Thread.GetProject() [RunStreamingAsync:1772]
   ↓
2. chatOptions.AdditionalProperties["Project"] [RunStreamingAsync:1777]
   ↓
3. PromptFilterContext.Properties[Project] [MessageProcessor:3665]
   ↓
4. context.GetProject() [ProjectInjectedMemoryFilter:27]
   ↓
5. project.DocumentManager.GetDocumentsAsync() [ProjectInjectedMemoryFilter:52]
   ↓
6. Formatted as [PROJECT_DOCUMENT] tags [ProjectInjectedMemoryFilter:93]
   ↓
7. InjectDocuments() prepends System message [ProjectInjectedMemoryFilter:104]
   ↓
8. context.Messages updated [ProjectInjectedMemoryFilter:64]
   ↓
9. Returned from filter pipeline [MessageProcessor:3672]
   ↓
10. Sent to LLM as part of effectiveMessages [RunAgenticLoopInternal:559]
```

---

## Conclusion

The HPD-Agent context processing flow is highly modular, with clear separation of concerns:

1. **Entry** → ConversationThread + Project context setup
2. **Preparation** → System instructions, history reduction
3. **Filtering** → Multi-stage injection of project docs, memories, knowledge, plans
4. **Decision** → Determine next action (LLM or tool execution)
5. **Execution** → Real-time streaming with event polling
6. **Tool Execution** → Scoped filters + permissions + function invocation
7. **Post-Processing** → Memory extraction and observability

All context flows through **ChatOptions.AdditionalProperties** and **AsyncLocal storage**, making it accessible to every component in the pipeline while maintaining thread safety and composability.
