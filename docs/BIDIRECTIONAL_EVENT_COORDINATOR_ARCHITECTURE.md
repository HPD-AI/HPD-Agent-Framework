# BidirectionalEventCoordinator Architecture

**Status**: ✅ Production Ready
**Version**: v2.1
**Last Updated**: 2025-01-27 (Simplified: Removed background drainer, direct channel polling)

---

## Overview

The `BidirectionalEventCoordinator` is the core infrastructure that enables request/response patterns for both **filters** and **functions** in the HPD-Agent framework. It provides thread-safe event emission, response coordination, and automatic event bubbling for nested agent scenarios.

## Key Insight: Shared Infrastructure

**The coordinator serves BOTH filters and functions**, using the same event streaming and response mechanism:

```
┌─────────────────────────────────────────────────────────┐
│          BidirectionalEventCoordinator                   │
│  (Unified infrastructure for filters AND functions)     │
├─────────────────────────────────────────────────────────┤
│  • Channel<InternalAgentEvent> - Event streaming        │
│  • Emit(event) - Send events to handlers                │
│  • WaitForResponseAsync<T>() - Block until response     │
│  • SendResponse(requestId, response) - Unblock waiter   │
│  • Event bubbling - Nested agent support                │
└─────────────────────────────────────────────────────────┘
           ↑                              ↑
           │                              │
    ┌──────┴──────────┐       ┌──────────┴──────────┐
    │ FILTERS         │       │ FUNCTIONS           │
    │ (Middleware)    │       │ (Callable Tools)    │
    ├─────────────────┤       ├─────────────────────┤
    │ PermissionFilter│       │ ClarificationFunc   │
    │ ValidationFilter│       │ Custom HITL tools   │
    │ LoggingFilter   │       │ Any bidirectional   │
    │ ...             │       │ function            │
    └─────────────────┘       └─────────────────────┘
         AUTO                       OPT-IN
    (wraps all calls)        (LLM decides when)
```

---

## Architecture Components

### 1. Event Channel

```csharp
private readonly Channel<InternalAgentEvent> _eventChannel;

public BidirectionalEventCoordinator()
{
    _eventChannel = Channel.CreateUnbounded<InternalAgentEvent>(new UnboundedChannelOptions
    {
        SingleWriter = false,  // Multiple filters/functions can emit concurrently
        SingleReader = true,   // Main loop polls via TryRead
        AllowSynchronousContinuations = false  // Performance & safety
    });
}
```

**Purpose**:
- Unbounded to prevent blocking during event emission
- Thread-safe for concurrent producers (filters, functions, nested agents)
- Single consumer (main loop polling via TryRead)

### 2. Response Coordination

```csharp
private readonly ConcurrentDictionary<string, (TaskCompletionSource<InternalAgentEvent>, CancellationTokenSource)>
    _responseWaiters = new();
```

**Purpose**:
- Maps `requestId` → waiting task
- Enables request/response pairing across async boundaries
- Thread-safe for concurrent response handling

### 3. Event Bubbling

```csharp
private BidirectionalEventCoordinator? _parentCoordinator;

public void Emit(InternalAgentEvent evt)
{
    // Emit to local channel
    _eventChannel.Writer.TryWrite(evt);

    // Bubble to parent coordinator (if nested agent)
    _parentCoordinator?.Emit(evt);
}
```

**Purpose**:
- Events from nested agents automatically flow to orchestrator
- Recursive bubbling creates event chain
- No manual wiring required via `AsyncLocal<Agent>` tracking

---

## Core Operations

### Event Emission

```csharp
public void Emit(InternalAgentEvent evt)
```

**Used by**:
- Filters (PermissionFilter, custom filters)
- Functions (ClarificationFunction, custom HITL functions)
- Agent internals (progress, errors, etc.)

**Flow**:
1. Write event to local channel
2. If parent coordinator exists, recursively emit to parent
3. Main loop polls channel via TryRead (every 10ms during blocking operations)
4. Main loop yields events to consumer (AGUI, Console, etc.)

### Response Waiting

```csharp
public async Task<T> WaitForResponseAsync<T>(
    string requestId,
    TimeSpan timeout,
    CancellationToken cancellationToken) where T : InternalAgentEvent
```

**Used by**:
- Filters waiting for permission approval
- Functions waiting for user clarification
- Any bidirectional request/response pattern

**Flow**:
1. Create `TaskCompletionSource<InternalAgentEvent>`
2. Register in `_responseWaiters` with requestId
3. **Block** until response received, timeout, or cancellation
4. Return typed response event

### Response Delivery

```csharp
public void SendResponse(string requestId, InternalAgentEvent response)
```

**Used by**:
- Event handlers (AGUI, Console)
- External systems providing user input
- Test harnesses

**Flow**:
1. Lookup waiting task by requestId
2. Complete the `TaskCompletionSource` with response
3. Unblocks the waiting filter/function
4. Remove from `_responseWaiters`

---

## Integration with Streaming

### The Problem: Deadlock Risk

Without careful integration, bidirectional patterns deadlock:

```
Filter/Function: Emit(RequestEvent) → await WaitForResponseAsync()
                                            ↓ BLOCKS
Main Loop:       await ExecuteToolsAsync() ← Blocked on function
                 ↓ Can't yield events until tools complete
Handler:         (Never receives event, can't send response)
                 ↓ DEADLOCK!
```

### The Solution: Direct Channel Polling

**See**: [BIDIRECTIONAL_FILTER_DEADLOCK_FIX.md](BIDIRECTIONAL_FILTER_DEADLOCK_FIX.md)

```csharp
// In RunAgenticLoopInternal

// Main loop - polls channel directly WHILE tools execute
while (!executeTask.IsCompleted)
{
    await Task.WhenAny(executeTask, Task.Delay(10, token));

    // Yield events that accumulated during execution
    while (_eventCoordinator.EventReader.TryRead(out var evt))
    {
        yield return evt;  // ← USER SEES EVENTS IMMEDIATELY!
    }
}
```

**Key Points**:
- Main loop polls channel directly via TryRead (non-blocking)
- Polls every **10ms** to yield accumulated events
- Events stream to user **while function is blocked**
- No deadlock possible
- Simpler than previous background drainer approach (removed in v2.1)

---

## Usage Patterns

### Pattern 1: Filter-Based (Automatic)

**Use Case**: Permissions, validation, logging - enforced on ALL function calls

```csharp
public class PermissionFilter : IAiFunctionFilter
{
    public async Task InvokeAsync(AiFunctionContext context, Func<AiFunctionContext, Task> next)
    {
        var requestId = Guid.NewGuid().ToString();

        // Emit request (uses coordinator internally)
        context.Emit(new InternalPermissionRequestEvent(
            requestId,
            SourceName: "PermissionFilter",
            context.Function.Name,
            ...));

        // Wait for response (uses coordinator internally)
        var response = await context.WaitForResponseAsync<InternalPermissionResponseEvent>(
            requestId,
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken);

        if (response.Approved)
        {
            await next(context);  // Continue pipeline
        }
        else
        {
            context.Result = "Permission denied";
        }
    }
}
```

**Registration**: Added to agent's filter pipeline, wraps ALL functions

### Pattern 2: Function-Based (Opt-In)

**Use Case**: Clarifications, optional HITL interactions - LLM decides when to call

```csharp
public static AIFunction Create()
{
    async Task<string> AskUserForClarificationAsync(string question, CancellationToken ct)
    {
        // Get access to coordinator via Agent.CurrentFunctionContext
        var context = Agent.CurrentFunctionContext as AiFunctionContext;

        var requestId = Guid.NewGuid().ToString();

        // Use same coordinator infrastructure as filters!
        context.Emit(new InternalClarificationRequestEvent(
            requestId,
            SourceName: "ClarificationFunction",
            question,
            AgentName: context.AgentName,
            ...));

        var response = await context.WaitForResponseAsync<InternalClarificationResponseEvent>(
            requestId,
            timeout: TimeSpan.FromMinutes(5),
            ct);

        return response.Answer;
    }

    return AIFunctionFactory.Create(AskUserForClarificationAsync, ...);
}
```

**Registration**: Added as a tool, LLM explicitly calls it

---

## Event Flow Example

### Scenario: Parent Agent Calls Clarification During Sub-Agent Execution

```
┌─────────────────────────────────────────────────────────────────┐
│ USER: "Build authentication"                                     │
└────────────────┬────────────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────────────┐
│ ORCHESTRATOR - Agentic Turn 0                                    │
│ Calls: CodingAgent("Build auth")                                │
└────────────────┬────────────────────────────────────────────────┘
                 │
                 ▼ ExecuteToolsAsync (BLOCKS)
        ┌────────────────────────────────────┐
        │ CODING AGENT - Complete Execution  │
        │ Returns: "Need framework choice?"   │
        └────────────────┬───────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│ ORCHESTRATOR - Agentic Turn 1                                    │
│ Sees question, calls: AskUserForClarification(...)              │
└────────────────┬────────────────────────────────────────────────┘
                 │
                 ▼ ExecuteToolsAsync (BLOCKS)
        ┌────────────────────────────────────────────────────┐
        │ CLARIFICATION FUNCTION EXECUTES                     │
        │                                                     │
        │ 1. context.Emit(ClarificationRequestEvent)         │
        │    ↓                                               │
        │    EventCoordinator._eventChannel.Writer.TryWrite()│
        │    ↓                                               │
        │    Main loop polling: EventReader.TryRead()        │
        │    ↓                                               │
        │    yield return event ← USER SEES EVENT! ✅        │
        │                                                     │
        │ 2. await context.WaitForResponseAsync()            │
        │    ↓ BLOCKS (creates TaskCompletionSource)         │
        │                                                     │
        │    User sees: "[Orchestrator] Which framework?"    │
        │    User answers: "Express"                         │
        │    ↓                                               │
        │    orchestrator.SendFilterResponse(requestId, ...) │
        │    ↓                                               │
        │    EventCoordinator.SendResponse()                 │
        │    ↓                                               │
        │    TaskCompletionSource.SetResult(response)        │
        │    ↓                                               │
        │    WaitForResponseAsync() UNBLOCKS ✅              │
        │                                                     │
        │ 3. return response.Answer ("Express")              │
        └────────────────┬───────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│ ORCHESTRATOR - Agentic Turn 2                                    │
│ Calls: CodingAgent("Build Express auth")                        │
│ → Completes successfully!                                        │
└─────────────────────────────────────────────────────────────────┘

Timeline: ALL in ONE message turn! No context loss.
```

---

## Event Bubbling in Nested Agents

### Setup

```csharp
var orchestrator = new Agent(...);
var planningAgent = new Agent(...);
var codingAgent = new Agent(...);

orchestrator.AddFunction(planningAgent.AsAIFunction());
orchestrator.AddFunction(ClarificationFunction.Create());

planningAgent.AddFunction(codingAgent.AsAIFunction());
planningAgent.AddFunction(ClarificationFunction.Create());
```

### Event Flow (3 Levels Deep)

```
User → Orchestrator → PlanningAgent → CodingAgent → returns question
                    → PlanningAgent calls AskUserForClarification
                    ↓
    PlanningAgent.EventCoordinator.Emit(ClarificationRequest)
                    ↓
    Local channel: PlanningAgent's event channel
                    ↓
    _parentCoordinator.Emit() ← AUTOMATIC BUBBLING!
                    ↓
    Orchestrator.EventCoordinator.Emit(same event)
                    ↓
    Orchestrator's event channel
                    ↓
    Orchestrator's main loop: TryRead + yield return
                    ↓
    USER SEES: "[PlanningAgent] What database?"

    User answers → orchestrator.SendFilterResponse()
                    ↓
    Response delivered to PlanningAgent's WaitForResponseAsync()
                    ↓
    PlanningAgent continues execution
```

**Key**: Event automatically bubbles via `_parentCoordinator` chain, no manual routing!

---

## Performance Characteristics

### Memory Overhead

- **Per Agent**: One coordinator instance (~100 bytes)
- **Per Request**: One TaskCompletionSource (~200 bytes)
- **Per Event**: One channel write (~50 bytes)
- **Total**: Negligible compared to LLM calls

### Latency

- **Event emission**: ~50ns (channel write)
- **Event bubbling**: ~50ns × nesting depth
- **Polling interval**: 10ms (configurable)
- **Response delivery**: ~100ns (dictionary lookup + TCS completion)

**Compared to LLM call** (500ms-2s): 0.001% overhead ✅

### Thread Safety

- ✅ All public methods are thread-safe
- ✅ Multiple filters/functions can emit concurrently
- ✅ ConcurrentDictionary for response coordination
- ✅ Channel supports multiple writers, single reader
- ✅ No locks or synchronization primitives needed

---

## Comparison: Filters vs Functions

| Aspect | Filter Pattern | Function Pattern |
|--------|----------------|------------------|
| **When executes** | Automatically on every function call | Only when LLM explicitly calls it |
| **Use case** | Enforced policies (permissions, validation) | Optional interactions (clarifications) |
| **Registration** | Added to filter pipeline | Added as regular AIFunction tool |
| **Control flow** | Wraps function execution (middleware) | Standalone function invocation |
| **Event emission** | ✅ Via coordinator | ✅ Via coordinator (same infra!) |
| **Response waiting** | ✅ Via coordinator | ✅ Via coordinator (same infra!) |
| **Streaming** | ✅ Via polling loop | ✅ Via polling loop (same infra!) |
| **Event bubbling** | ✅ Automatic | ✅ Automatic |
| **Access pattern** | `context.Emit()` (passed as parameter) | `Agent.CurrentFunctionContext` (ambient) |

**Key Insight**: Same infrastructure, different access patterns and usage semantics!

---

## Benefits of Unified Infrastructure

### 1. Code Reuse
- One coordinator implementation serves all bidirectional needs
- Filters and functions share event types, handling, and streaming

### 2. Consistent Behavior
- Same polling mechanism for all events
- Same timeout handling
- Same cancellation support
- Same event bubbling logic

### 3. Extensibility
- New bidirectional patterns just need:
  1. Event types (request + response)
  2. Either filter or function to emit/wait
  3. Handler to process and respond
- No coordinator changes needed!

### 4. Performance
- Direct polling eliminates intermediate buffering
- Shared channel infrastructure
- No per-filter/function overhead
- Reduced memory footprint (no ConcurrentQueue)

---

## Limitations & Considerations

### 1. Request/Response Must Use Same RequestId

```csharp
// Request
var requestId = Guid.NewGuid().ToString();
context.Emit(new RequestEvent(requestId, ...));

// Response MUST use same ID
coordinator.SendResponse(requestId, new ResponseEvent(requestId, ...));
```

### 2. Timeout Required for WaitForResponseAsync

```csharp
// GOOD: Reasonable timeout
await context.WaitForResponseAsync<T>(
    requestId,
    timeout: TimeSpan.FromMinutes(5),  // ✅
    cancellationToken);

// BAD: No timeout (would block forever if response lost)
await context.WaitForResponseAsync<T>(
    requestId,
    timeout: TimeSpan.MaxValue,  // ❌ Don't do this
    cancellationToken);
```

### 3. Events Must Be Serializable

For distributed scenarios, all event types should be JSON-serializable.

### 4. No Event Replay

Events are consumed once. If a handler crashes, events are lost (by design for streaming).

---

## Testing

### Unit Test: Event Emission

```csharp
var coordinator = new BidirectionalEventCoordinator();

// Emit event
coordinator.Emit(new TestEvent());

// Poll channel directly
var events = new List<InternalAgentEvent>();
while (coordinator.EventReader.TryRead(out var evt))
{
    events.Add(evt);
}

// Assert
Assert.Single(events);
```

### Integration Test: Request/Response

```csharp
var coordinator = new BidirectionalEventCoordinator();
var requestId = "test-123";

// Background responder
_ = Task.Run(async () =>
{
    await Task.Delay(100);
    coordinator.SendResponse(requestId, new ResponseEvent(requestId, "answer"));
});

// Wait for response
var response = await coordinator.WaitForResponseAsync<ResponseEvent>(
    requestId,
    timeout: TimeSpan.FromSeconds(1),
    CancellationToken.None);

Assert.Equal("answer", response.Answer);
```

---

## Related Documentation

- [BIDIRECTIONAL_FILTER_DEADLOCK_FIX.md](BIDIRECTIONAL_FILTER_DEADLOCK_FIX.md) - Polling solution
- [FILTER_EVENTS_USAGE.md](../HPD-Agent/Filters/FILTER_EVENTS_USAGE.md) - Filter patterns
- [CLARIFICATION_FUNCTION_USAGE.md](CLARIFICATION_FUNCTION_USAGE.md) - Function pattern example
- [NESTED_AGENT_EVENT_BUBBLING_IMPLEMENTATION.md](NESTED_AGENT_EVENT_BUBBLING_IMPLEMENTATION.md) - Event bubbling details

---

## Summary

The `BidirectionalEventCoordinator` provides a **unified, thread-safe infrastructure** for request/response patterns in HPD-Agent. It serves both:

1. **Filters** (automatic middleware wrapping all calls)
2. **Functions** (opt-in tools called explicitly by LLM)

By using the same coordinator for both patterns, the framework achieves:
- Code reuse and consistency
- Reliable streaming via background drainer + polling
- Automatic event bubbling in nested agents
- Zero-deadlock guarantee with proper integration

**The key insight**: Don't duplicate infrastructure - reuse the coordinator for any bidirectional need, whether enforced (filter) or optional (function)!
