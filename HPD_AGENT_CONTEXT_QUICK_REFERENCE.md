# HPD-Agent Context Processing: Quick Reference

## The Short Version: Context Journey Through the System

```
┌─────────────────────────────────────────────────────────────────┐
│ USER CALLS AGENT                                                 │
│  Agent.RunAsync() or Agent.RunStreamingAsync()                  │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ↓
┌─────────────────────────────────────────────────────────────────┐
│ CONTEXT SETUP (RunStreamingAsync: lines 1772-1779)              │
│  • Extract Project from ConversationThread                      │
│  • Build ChatOptions with:                                      │
│    - Project                                                    │
│    - ConversationId                                             │
│    - Thread (AsyncLocal)                                        │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ↓
┌─────────────────────────────────────────────────────────────────┐
│ MESSAGE PREPARATION (MessageProcessor.PrepareMessagesAsync)     │
│  1. Prepend system instructions                                 │
│  2. Merge ChatOptions                                           │
│  3. Apply history reduction (optional)                          │
│  4. Transfer options.AdditionalProperties → context.Properties  │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ↓
┌─────────────────────────────────────────────────────────────────┐
│ PROMPT FILTER PIPELINE (FilterChain.BuildPromptPipeline)        │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Filter 1: ProjectInjectedMemoryFilter (ALWAYS)           │  │
│  │  • Extract context.Properties["Project"]                │  │
│  │  • Get documents from project.DocumentManager            │  │
│  │  • Format as [PROJECT_DOCUMENT: filename]               │  │
│  │  • Prepend as System message                             │  │
│  └─────────────────────┬──────────────────────────────────┘  │
│                        ↓                                       │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Filter 2: DynamicMemoryFilter (if configured)            │  │
│  │  • Get memories from DynamicMemoryStore                 │  │
│  │  • Format as [AGENT_MEMORY_START]...                    │  │
│  │  • Prepend as System message                             │  │
│  └─────────────────────┬──────────────────────────────────┘  │
│                        ↓                                       │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Filter 3: StaticMemoryFilter (if configured)             │  │
│  │  • Get knowledge from StaticMemoryStore                 │  │
│  │  • Merge with existing system message                    │  │
│  └─────────────────────┬──────────────────────────────────┘  │
│                        ↓                                       │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Filter N: Custom Filters (user-defined)                  │  │
│  │  • Applied in registration order                         │  │
│  └─────────────────────┬──────────────────────────────────┘  │
│                        ↓                                       │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Result: Fully Prepared Messages                          │  │
│  │  [Project Documents System Message]                      │  │
│  │  [Agent Memory System Message]                           │  │
│  │  [Static Knowledge System Message]                       │  │
│  │  [System Instructions]                                   │  │
│  │  [User Messages]                                         │  │
│  │  [Assistant Messages]                                    │  │
│  │  [Tool Results]                                          │  │
│  └──────────────────────────────────────────────────────────┘  │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ↓
┌─────────────────────────────────────────────────────────────────┐
│ DECISION ENGINE (AgentDecisionEngine)                            │
│  Decide: Call LLM? Execute Tools? Terminate?                    │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ↓
┌─────────────────────────────────────────────────────────────────┐
│ LLM CALL (AgentTurn.RunAsync → _baseClient.GetStreamingAsync)   │
│  • Apply plugin scoping (if enabled)                            │
│  • Call Provider ChatClient (OpenRouter, OpenAI, Anthropic...) │
│  • Stream response with events                                  │
│  • Yield TextDelta, ToolCall events immediately                │
└────────────────────────┬────────────────────────────────────────┘
                         │
                   [Tool Calls?]
                    /         \
                  YES          NO
                  │             │
                  ↓             │
         ┌─────────────────┐    │
         │ TOOL EXECUTION  │    │
         │ • Apply Scoped  │    │
         │   Filters       │    │
         │ • Check Perms   │    │
         │ • Invoke Func   │    │
         │ • Collect       │    │
         │   Results       │    │
         └────────┬────────┘    │
                  │             │
                  └──────┬──────┘
                         │
                         ↓
┌─────────────────────────────────────────────────────────────────┐
│ POST-INVOKE FILTERS (MessageProcessor.ApplyPostInvokeFiltersAsync)
│  • Called for ALL filters                                       │
│  • Extract memories                                             │
│  • Update knowledge bases                                       │
│  • Log conversation details                                     │
│  • Parameters: request messages, response messages, exception   │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ↓
┌─────────────────────────────────────────────────────────────────┐
│ MESSAGE TURN FILTERS (optional)                                 │
│  • Telemetry                                                    │
│  • Logging                                                      │
│  • Analytics                                                    │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ↓
┌─────────────────────────────────────────────────────────────────┐
│ FINAL RESPONSE                                                   │
│  → User receives conversation result                            │
└─────────────────────────────────────────────────────────────────┘
```

---

## Key Concepts: One Sentence Each

| Concept | Explanation |
|---------|-------------|
| **Entry Point** | `RunAsync()` or `RunStreamingAsync()` where user provides messages and thread context |
| **ConversationThread** | Container for conversation state, holds Project reference and message history |
| **Project** | Document container - provides documents to be injected into context |
| **ChatOptions.AdditionalProperties** | Dictionary carrying context data (Project, ConversationId, Thread) to filters |
| **PromptFilterContext** | Bridge object that transfers AdditionalProperties to filters via context.Properties |
| **Filter Pipeline** | Chain of filters executing in order, each can modify messages before next filter |
| **Injection** | Filters prepend system messages containing project docs, memories, or knowledge |
| **AsyncLocal** | Thread-safe storage that flows across async calls (Agent.CurrentThread, etc.) |
| **MessageProcessor** | Orchestrates message preparation: system instructions, reduction, filters |
| **AgentTurn** | Makes single streaming call to provider's ChatClient, yields events immediately |
| **Provider ChatClient** | Implementation for OpenRouter, OpenAI, Anthropic, etc. - handles API details |
| **Tool Execution** | When LLM calls a function: ScopedFilters → Permissions → Invocation → Results |
| **Post-Invoke Filters** | Called AFTER LLM response to extract learned information, update memory |

---

## Where Does Context Flow?

### Path 1: Project Context
```
ConversationThread.GetProject()
  → ChatOptions.AdditionalProperties["Project"]
    → PromptFilterContext.Properties["Project"]
      → context.GetProject() in filters
        → ProjectInjectedMemoryFilter accesses documents
          → Formatted as [PROJECT_DOCUMENT] tags
            → Prepended as System message
              → Sent to LLM
```

### Path 2: Memory Context
```
DynamicMemoryStore.GetMemoriesAsync()
  → Formatted as [AGENT_MEMORY_START]...
    → Prepended as System message (via filter)
      → Sent to LLM
        → LLM can reference and modify memory
          → PostInvokeAsync extracts new memories
            → Stored back to DynamicMemoryStore
```

### Path 3: Function Invocation Context
```
Agent.CurrentFunctionContext (AsyncLocal)
  ← Set by FunctionCallProcessor
    → Accessible in plugins/filters during execution
      → Flows across async calls automatically
        → Cleared when function completes
```

---

## Filter Execution Order

**PRE-LLM (in order)**:
1. ProjectInjectedMemoryFilter (always)
2. StaticMemoryFilter (if WithStaticMemory)
3. DynamicMemoryFilter (if WithDynamicMemory)
4. AgentPlanFilter (if WithPlanMode)
5. Custom filters (WithPromptFilter)

**POST-LLM (reverse order)**:
6. Each filter's PostInvokeAsync callback (optional)

---

## Critical Line Numbers

| What | File | Lines |
|------|------|-------|
| Main entry | Agent.cs | 1625, 1753 |
| Project context injection | Agent.cs | 1772-1779 |
| Message preparation | Agent.cs | 3456 |
| Filter context creation | Agent.cs | 3658-3667 |
| Filter pipeline execution | Agent.cs | 3671-3672 |
| Prompt filters application | Agent.cs | 3646-3673 |
| LLM call | Agent.cs | 559 |
| Post-invoke filters | Agent.cs | 3684-3729 |
| Filter pipeline builder | Agent.cs | 5722-5739 |
| ProjectInjectedMemoryFilter | ProjectInjectedMemoryFilter.cs | 22-69 |
| Document injection | ProjectInjectedMemoryFilter.cs | 101-109 |

---

## Decision: Which Mechanism to Use for Your Context?

**If you need context accessible to filters before LLM call:**
→ Put it in `ChatOptions.AdditionalProperties` at entry point

**If you need context during function execution:**
→ Use `Agent.CurrentFunctionContext` (AsyncLocal)

**If you need to inject content into the LLM context:**
→ Create an `IPromptFilter` and register with `builder.WithPromptFilter()`

**If you need to extract learned information after LLM response:**
→ Implement `IPromptFilter.PostInvokeAsync()`

**If you need to observe completed turns:**
→ Register `IMessageTurnFilter` with `builder.WithMessageTurnFilter()`

---

## Common Implementation Patterns

### Pattern 1: Accessing Project in a Filter
```csharp
public class MyFilter : IPromptFilter
{
    public async Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptFilterContext context,
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> next)
    {
        var project = context.GetProject();
        if (project != null)
        {
            var documents = await project.DocumentManager.GetDocumentsAsync();
            // Process documents...
        }
        return await next(context);
    }
}
```

### Pattern 2: Accessing Function Context During Execution
```csharp
public class MyFunction
{
    public void DoSomething()
    {
        var ctx = Agent.CurrentFunctionContext;
        if (ctx != null)
        {
            var functionName = ctx.FunctionName;
            var callId = ctx.CallId;
            // Use context...
        }
    }
}
```

### Pattern 3: Extracting Memory After LLM
```csharp
public class MyFilter : IPromptFilter
{
    public async Task PostInvokeAsync(PostInvokeContext context, CancellationToken cancellationToken)
    {
        if (context.Exception == null && context.ResponseMessages != null)
        {
            // Extract learning from response
            var learnings = ExtractMemories(context.ResponseMessages);
            await StoreMemories(learnings, cancellationToken);
        }
    }
}
```

---

## Troubleshooting: Context Not Flowing?

**Check if:**
1. Project is in ConversationThread? → `thread.GetProject()` returns non-null?
2. ChatOptions.AdditionalProperties is populated? → Set at RunStreamingAsync entry?
3. Filter is registered? → `builder.WithPromptFilter()` called?
4. Filter reads context.Properties? → Not context.Options?
5. Filter calls `next(context)` to continue pipeline? → Or pipeline breaks?

---

## Quick Stats

- **Total Prompt Filters**: Usually 4-5 (ProjectInjected + optional 3-4 + custom)
- **Filter Pipeline Pattern**: All filters are wrapped in reverse order but execute in forward order
- **AsyncLocal Properties**: 3 main ones (CurrentThread, RootAgent, CurrentFunctionContext)
- **AdditionalProperties Keys**: Project, ConversationId, Thread (plus any custom)
- **Caching TTLs**: Project docs 2min, Dynamic memory 1min, Static knowledge 5min
- **Provider ChatClients**: OpenRouter, OpenAI, Anthropic, Ollama, GoogleAI, VertexAI, Bedrock, etc.

---

## One-Minute Explanation

The HPD-Agent takes user input and wraps it with context before sending to an LLM. The context journey: 
1. User calls `RunAsync()` with messages and a thread (which has a project)
2. Entry point extracts project and puts it in `ChatOptions.AdditionalProperties`
3. `MessageProcessor` creates a `PromptFilterContext` and copies those properties into `context.Properties`
4. **Filter pipeline** executes: each filter can inject system messages (project docs, memories, knowledge)
5. Final prepared messages go to the **LLM via provider's ChatClient**
6. If LLM calls tools, execution uses **ScopedFilterManager** and **PermissionManager**
7. After response, **PostInvokeFilters** can extract learned information
8. Final response goes back to user

**Key insight**: All context flows through **AdditionalProperties → FilterContext.Properties → Filter access**, using **AsyncLocal** for function execution context.

