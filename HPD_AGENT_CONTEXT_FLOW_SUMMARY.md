# HPD-Agent Context Processing Flow - Executive Summary

## Overview

This document summarizes the complete journey of context through the HPD-Agent system from user input to ChatClient invocation. For detailed information, see `HPD_AGENT_CONTEXT_FLOW_MAP.md` and `HPD_AGENT_CONTEXT_QUICK_REFERENCE.md`.

---

## The 7-Step Context Flow

### Step 1: Entry Point
**Where**: `Agent.RunAsync()` or `Agent.RunStreamingAsync()`  
**What happens**:
- User provides `ChatMessage[]` and `ConversationThread`
- Thread contains a `Project` reference
- Thread becomes available via `Agent.CurrentThread` (AsyncLocal)

### Step 2: Context Extraction
**Where**: `RunStreamingAsync()` lines 1772-1779  
**What happens**:
- Project extracted from thread
- `ChatOptions.AdditionalProperties` populated with:
  - `Project` instance
  - `ConversationId` 
  - `Thread` reference
- These properties carry context through the pipeline

### Step 3: Message Preparation
**Where**: `MessageProcessor.PrepareMessagesAsync()` line 3456  
**What happens**:
1. System instructions prepended
2. ChatOptions merged with defaults
3. History reduction applied (optional)
4. **AdditionalProperties transferred to PromptFilterContext.Properties**

### Step 4: Prompt Filter Pipeline
**Where**: `FilterChain.BuildPromptPipeline()` line 5722  
**What happens**:
- Filters execute in order:
  1. **ProjectInjectedMemoryFilter** - Always active, injects project documents
  2. **DynamicMemoryFilter** - Optional, injects editable agent memory
  3. **StaticMemoryFilter** - Optional, injects static knowledge base
  4. **AgentPlanFilter** - Optional, injects current plan
  5. **Custom Filters** - User-defined filters in registration order

- **Each filter**:
  - Receives `PromptFilterContext` with context.Properties
  - Can access Project, ConversationId, Thread via context extension methods
  - Modifies messages by prepending system messages
  - Calls `next()` to continue pipeline

### Step 5: LLM Call
**Where**: `AgentTurn.RunAsync()` line 3783  
**What happens**:
- Prepared messages sent to provider's ChatClient
- Provider-specific implementations handle API details:
  - OpenRouterChatClient
  - OpenAIChatClient
  - AnthropicChatClient
  - etc.
- Response streamed back with real-time events
- Tool calls detected if present

### Step 6: Tool Execution (if needed)
**Where**: `ToolScheduler.ExecuteToolsAsync()` line 765  
**What happens**:
- For each tool call:
  1. Create `FunctionInvocationContext` 
  2. Apply `ScopedFilterManager` (function/plugin-specific filters)
  3. Check `PermissionManager` (authorization)
  4. Apply `IAiFunctionFilter` pipeline
  5. Invoke actual function
  6. Collect results

- Context accessible to function via `Agent.CurrentFunctionContext` (AsyncLocal)

### Step 7: Post-Processing & Response
**Where**: Multiple locations  
**What happens**:
- **PostInvokeFilters**: `MessageProcessor.ApplyPostInvokeFiltersAsync()` line 3684
  - Called after LLM response received
  - Can extract memories, update knowledge bases
  - Filters receive: request messages, response messages, exception (if any)

- **MessageTurnFilters**: Applied after turn completes line 1703
  - For telemetry, logging, analytics
  
- **History Assembly**: Final messages assembled for return
- **Thread Updated**: Messages added back to ConversationThread
- **Response Returned**: Final result to user

---

## Key Mechanisms

### 1. ChatOptions.AdditionalProperties
**Purpose**: Carry context data through the pipeline  
**Usage**: 
- Populated at entry point with Project, ConversationId, Thread
- Transferred to PromptFilterContext.Properties at line 3661-3666
- Accessed by filters via `context.GetProject()`, `context.GetConversationId()`, etc.

### 2. Filter Pipeline Pattern
**Pattern**: Filters wrapped in reverse order for forward execution
```csharp
// Given: [Filter1, Filter2, Filter3]
// Execution order: Filter1 → Filter2 → Filter3 → Messages
// Building: Wrap in reverse so LIFO stack becomes FIFO execution
```

### 3. AsyncLocal Storage
**Mechanism**: Thread-safe context that flows across async calls
- `Agent.CurrentThread`: ConversationThread instance
- `Agent.RootAgent`: Top-level agent for nested calls
- `Agent.CurrentFunctionContext`: Function invocation metadata

### 4. Injection Strategy
**How context reaches LLM**:
1. Filters extract data from context.Properties
2. Format data as system messages (e.g., `[PROJECT_DOCUMENT: file]`)
3. Prepend to message list
4. Final list sent to LLM

---

## Filter Details

### ProjectInjectedMemoryFilter (Always Present)
**Location**: `/HPD-Agent/Project/DocumentHandling/FullTextInjection/ProjectInjectedMemoryFilter.cs`  
**Cache**: 2-minute TTL  
**Operation**:
1. Extract Project from context.Properties
2. Get documents from project.DocumentManager
3. Format as `[PROJECT_DOCUMENT: filename]` tags
4. Prepend as System message

### DynamicMemoryFilter (Optional)
**Location**: `/HPD-Agent/Memory/Agent/DynamicMemory/DynamicMemoryFilter.cs`  
**Cache**: 1-minute TTL  
**Storage**: JsonDynamicMemoryStore or custom implementation  
**Operation**:
1. Get memories from store
2. Format as `[AGENT_MEMORY_START]...[AGENT_MEMORY_END]`
3. Prepend as System message
4. PostInvokeAsync: Extract learned information back to store

### StaticMemoryFilter (Optional)
**Location**: `/HPD-Agent/Memory/Agent/StaticMemory/StaticMemoryFilter.cs`  
**Cache**: 5-minute TTL (longer - static content)  
**Storage**: JsonStaticMemoryStore or custom implementation  
**Operation**:
1. Get knowledge from store
2. Merge with existing system message
3. Prepend if no system message exists

### Custom Filters
**Registration**: `builder.WithPromptFilter(filter)` or `builder.WithPromptFilter<T>()`  
**Execution Order**: In registration order, after built-in filters  
**Capability**: Can modify messages, access context data, emit events

---

## Context Flow Paths

### Path 1: Project Document Flow
```
Project (in ConversationThread)
  ↓ [RunStreamingAsync:1772]
ChatOptions.AdditionalProperties["Project"]
  ↓ [MessageProcessor:3665]
PromptFilterContext.Properties["Project"]
  ↓ [ProjectInjectedMemoryFilter:27]
context.GetProject()
  ↓ [ProjectInjectedMemoryFilter:52]
project.DocumentManager.GetDocumentsAsync()
  ↓ [ProjectInjectedMemoryFilter:93]
Formatted as [PROJECT_DOCUMENT: file] tags
  ↓ [ProjectInjectedMemoryFilter:104]
InjectDocuments() prepends System message
  ↓ [MessageProcessor:3672]
Final prepared messages
  ↓ [RunAgenticLoopInternal:559]
Sent to LLM
```

### Path 2: Function Invocation Context Flow
```
FunctionCallProcessor creates FunctionInvocationContext
  ↓ [Agent.CurrentFunctionContext set]
Available during tool execution
  ↓ [AsyncLocal propagates across async calls]
Function implementation access via Agent.CurrentFunctionContext
  ↓ [Function executes with full context]
Context cleared when function completes
```

### Path 3: Memory Extraction Flow
```
LLM returns response messages
  ↓ [Response parsed and stored]
MessageProcessor.ApplyPostInvokeFiltersAsync()
  ↓ [All filters' PostInvokeAsync called]
Filter extracts learned information from response
  ↓ [Custom logic per filter]
Learned data stored to store (DynamicMemoryStore, etc.)
  ↓ [Persistent storage updated]
Available in next conversation
```

---

## Critical Code Locations

| Component | File | Key Lines |
|-----------|------|-----------|
| Entry Point (Non-Streaming) | Agent.cs | 1625-1742 |
| Entry Point (Streaming) | Agent.cs | 1753-1850 |
| Context Setup | Agent.cs | 1772-1779 |
| Message Preparation | Agent.cs | 3456-3515 |
| Filter Context Creation | Agent.cs | 3658-3667 |
| Filter Pipeline Building | Agent.cs | 5722-5739 |
| Filter Pipeline Execution | Agent.cs | 3671-3672 |
| LLM Call | Agent.cs | 559-656 |
| Tool Execution | Agent.cs | 765-790 |
| Post-Invoke Filters | Agent.cs | 3684-3729 |
| Project Filter | ProjectInjectedMemoryFilter.cs | 22-109 |
| Dynamic Memory Filter | DynamicMemoryFilter.cs | 32-68 |
| Static Memory Filter | StaticMemoryFilter.cs | 38-78 |

---

## Configuration Points

### Via AgentBuilder
```csharp
// Add prompt filters
builder.WithPromptFilter(myFilter);
builder.WithPromptFilters(filter1, filter2);

// Add memory systems
builder.WithDynamicMemory(opts => { /* config */ });
builder.WithStaticMemory(opts => { /* config */ });

// Add plan mode
builder.WithPlanMode(opts => { /* config */ });

// Add function filters
builder.WithFilter(functionFilter);
builder.WithPermissionFilter(permissionFilter);
builder.WithMessageTurnFilter(turnFilter);

// Add middleware
builder.WithLogging();
builder.WithOpenTelemetry();
builder.WithCaching();
```

---

## Common Patterns

### Accessing Project in Custom Filter
```csharp
public class CustomFilter : IPromptFilter
{
    public async Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptFilterContext context,
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> next)
    {
        var project = context.GetProject();
        if (project != null)
        {
            // Process project
        }
        return await next(context);
    }
}
```

### Accessing Function Context
```csharp
public class MyFunction
{
    public void Execute()
    {
        var ctx = Agent.CurrentFunctionContext;
        if (ctx != null)
        {
            var name = ctx.FunctionName;
            var args = ctx.Arguments;
        }
    }
}
```

### Extracting Memory After LLM
```csharp
public class MyFilter : IPromptFilter
{
    public async Task PostInvokeAsync(
        PostInvokeContext context, 
        CancellationToken cancellationToken)
    {
        if (context.Exception == null && 
            context.ResponseMessages != null)
        {
            // Extract learned information
            var memories = ExtractMemories(context.ResponseMessages);
            await StoreMemories(memories, cancellationToken);
        }
    }
}
```

---

## Decision Tree: Where Should Your Context Go?

```
Does the context need to be visible to filters before LLM?
├─ YES: Put in ChatOptions.AdditionalProperties at entry
└─ NO: Go to next question

Does the context need to be visible during function execution?
├─ YES: Use Agent.CurrentFunctionContext (AsyncLocal)
└─ NO: Go to next question

Does the context need to modify the LLM input?
├─ YES: Create an IPromptFilter and inject content
└─ NO: Go to next question

Does the context need to be extracted from LLM output?
├─ YES: Implement IPromptFilter.PostInvokeAsync()
└─ NO: Go to next question

Does the context need to observe turn completion?
└─ YES: Register IMessageTurnFilter with builder.WithMessageTurnFilter()
```

---

## Performance Considerations

### Caching
- ProjectInjectedMemoryFilter: 2-minute cache (mutable content)
- DynamicMemoryFilter: 1-minute cache (mutable content)
- StaticMemoryFilter: 5-minute cache (static content)
- Cache invalidation: Automatic on document/memory updates

### Filter Pipeline
- Filters execute sequentially (not parallel)
- Each filter modifies messages before passing to next
- Order matters: earlier filters' output is input to later filters

### Message Streaming
- Events yielded immediately from LLM
- No buffering (zero latency streaming)
- Filter events polled during execution

---

## Troubleshooting Checklist

- Is Project in ConversationThread?
- Is ChatOptions.AdditionalProperties populated at entry?
- Is filter registered with builder?
- Is filter reading context.Properties (not context.Options)?
- Is filter calling next(context) to continue pipeline?
- Is PromptFilterContext created with messages and options?
- Is PostInvokeAsync being called after LLM response?
- Is AsyncLocal context accessible in nested calls?

---

## Summary

The HPD-Agent context flow is a carefully orchestrated pipeline:

1. **Context Extraction** → Properties collected from thread
2. **Context Transport** → Properties flow via ChatOptions.AdditionalProperties
3. **Context Bridging** → Properties mapped to PromptFilterContext.Properties
4. **Context Injection** → Filters access properties and inject content as system messages
5. **Context Utilization** → LLM receives fully prepared context
6. **Context Execution** → Functions access context via AsyncLocal
7. **Context Learning** → PostInvokeAsync extracts learned information

All mechanisms are thread-safe, composable, and testable.

---

## Related Documentation

- **HPD_AGENT_CONTEXT_FLOW_MAP.md** - Detailed flow with line numbers and code
- **HPD_AGENT_CONTEXT_QUICK_REFERENCE.md** - Visual diagrams and quick lookups
- **Agent.cs** - Core implementation (primary resource)
- **AgentBuilder.cs** - Configuration and building
- **IPromptFilter.cs** - Filter interface definition

