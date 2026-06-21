# Middleware Lifecycle

Middleware participates in an agent run through lifecycle hooks, wrappers, state updates, and events. Use middleware when behavior must sit around the agent loop instead of inside one tool.

Common uses include permission gates, retry policy, error formatting, tool-call timeout, logging, compaction, content handling, and UI-facing request/response events.

## Execution Order

Hooks follow a stack-shaped model:

| Surface | Order |
| --- | --- |
| `Before*` hooks | registration order |
| `After*` hooks | reverse registration order |
| `OnErrorAsync` | reverse registration order |
| model/function wrappers | first registered is outermost |

If middleware is registered as `A`, then `B`, then `C`, wrapper execution is:

```text
A(B(C(core)))
```

This is important for error handling. A retry middleware registered before a timeout middleware wraps the timeout behavior. A formatter registered later is closer to the core operation.

## Main Hook Families

Message-turn hooks run around a user message turn. Iteration hooks run around an agent loop iteration. Function hooks run around tool/function execution. Thread hooks run around thread fork commit. Runtime hooks run around agent start/stop.

Wrapper hooks are different from `Before*` and `After*` hooks. They receive a handler and can decide whether to call it, call it more than once, transform inputs, transform outputs, or catch errors. Function wrappers always participate. Streaming model wrappers opt in by returning a non-null stream.

Runtime start/stop hooks run when an agent is used as a started runtime, such as hosted SSE/WebSocket, bot, TUI, client-tool, or other long-lived input-loop scenarios. Direct one-shot `RunAsync(...)` calls still use the message-turn, iteration, function, thread, and error hooks. See [Agent Runtime And Capabilities](agent-runtime-and-capabilities.md) for the distinction.

## Hook Reference

`IAgentMiddleware` has hooks at each layer of the runtime. Override only the hooks your middleware needs.

| Layer | Hook | When it runs | Common uses |
| --- | --- | --- | --- |
| Runtime | `BeforeStartAsync` | Before the agent runtime input loop starts | Allocate runtime resources, validate startup, configure audio/realtime resources |
| Runtime | `AfterStartedAsync` | After the runtime input loop has started | Availability diagnostics, background work that depends on the running loop |
| Runtime | `BeforeStopAsync` | Before the runtime input loop stops | Graceful drain decisions, buffer flushing, shutdown diagnostics |
| Runtime | `AfterStoppedAsync` | After the runtime input loop has stopped and registered resources were disposed | Final telemetry, cleanup confirmation |
| Message turn | `BeforeMessageTurnAsync` | Before processing one user message turn | RAG injection, memory retrieval, context augmentation, run-config inspection |
| Message turn | `AfterMessageTurnAsync` | After a message turn completes | Memory extraction, analytics, turn-level logging |
| Iteration | `BeforeIterationAsync` | Before each model iteration | Prompt/message modification, chat option tuning, per-iteration policy |
| Model wrapper | `WrapModelTurnStreamingAsync` | Around the streaming model turn when the middleware opts in | Retry, caching, request modification, streaming transformation, progressive metrics |
| Tool iteration | `BeforeToolExecutionAsync` | After the model returns tool calls but before tools execute | Whole-iteration tool validation, permission checks, tool filtering |
| Function batch | `BeforeParallelBatchAsync` | Before a parallel batch of functions executes | Batch-level permissions, rate limiting, batch approval |
| Function | `BeforeFunctionAsync` | Before each individual function executes | Argument validation, per-function permission checks, logging, overrides |
| Function wrapper | `WrapFunctionCallAsync` | Around the actual function body | Retry, caching, timeout, result transformation |
| Function | `AfterFunctionAsync` | After a function completes or throws | Result formatting, function telemetry, exception observation |
| Iteration | `AfterIterationAsync` | After tool results are collected for an iteration | Result aggregation, error recovery, state updates |
| Thread lifecycle | `BeforeThreadForkCommitAsync` | After a target thread has been materialized for a fork, before it is persisted | Compact copied history, stamp thread metadata, rewrite thread-local middleware state |
| Error | `OnErrorAsync` | During the tool/function error path | Function error logging, circuit breakers, graceful degradation |

`BeforeThreadForkCommitAsync` is the fork hook. It sees both the source thread and the not-yet-persisted target thread, plus the fork point and `ThreadForkOptions`. Use it when the target thread should start differently from a raw copy, such as compacting copied history, adding thread-local metadata, or adjusting copied middleware state before the new thread becomes durable.

Fork events are a different surface. After a fork is committed, thread event projection can include durable events such as `THREAD_FORKED` and `THREAD_MIDDLEWARE_STATE_COMMITTED`. Those events describe what was committed; they are not pre-commit mutation hooks.

## Streaming Wrapper Probe

`WrapModelTurnStreamingAsync(...)` uses nullable return semantics. Returning `null` means the middleware does not intercept the streaming model turn.

During pipeline construction, the framework probes each middleware once to see whether it returns a stream. If it opts in, the middleware is called again when the stream actually executes. Avoid side effects before returning the `IAsyncEnumerable`.

Prefer this shape:

```csharp
public IAsyncEnumerable<AgentModelUpdate>? WrapModelTurnStreamingAsync(
    AgentModelTurnRequest request,
    Func<AgentModelTurnRequest, IAsyncEnumerable<AgentModelUpdate>> next,
    CancellationToken cancellationToken)
{
    return RunAsync(request, next, cancellationToken);
}

private async IAsyncEnumerable<AgentModelUpdate> RunAsync(
    AgentModelTurnRequest request,
    Func<AgentModelTurnRequest, IAsyncEnumerable<AgentModelUpdate>> next,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
{
    // Side effects here run during enumeration, not during the probe.
    Console.WriteLine("Model stream started.");

    await foreach (var update in next(request).WithCancellation(cancellationToken))
    {
        yield return update;
    }
}
```

Do not increment counters, emit events, mutate request-related state, or start background work before returning the stream. That work can happen during the probe even if no tokens have been enumerated yet.

## State Updates

Middleware state is immutable. Use `UpdateState(...)` for core state or multi-state atomic updates. Use `UpdateMiddlewareState<TState>(...)` for simple updates to one middleware state record.

```csharp
public sealed class TurnCountingMiddleware : IAgentMiddleware
{
    public Task BeforeIterationAsync(
        BeforeIterationContext context,
        CancellationToken cancellationToken)
    {
        context.UpdateMiddlewareState<TurnCounterState>(state => state with
        {
            Count = state.Count + 1
        });

        return Task.CompletedTask;
    }
}

[MiddlewareState(Persistent = true, Scope = StateScope.Thread)]
public sealed record TurnCounterState
{
    public int Count { get; init; }
}
```

Do not read state, await unrelated work, and then write a derived value. Put the read inside the update lambda so the update is based on the current state at the time of mutation.

Use `UpdateState(...)` when you need to update loop state directly:

```csharp
context.UpdateState(state => state with
{
    IsTerminated = true,
    TerminationReason = "Stopped by middleware policy"
});
```

## Error Scope

`OnErrorAsync` currently belongs to the tool/function error path. A function body exception is routed through `OnErrorAsync` and then through `AfterFunctionAsync`.

Do not assume `OnErrorAsync` catches every provider, model-call, streaming, or whole message-turn exception. Model-call errors are handled by model streaming wrappers such as retry and error formatting. Message-turn failures are surfaced through message-turn error events.

## Built-In Middleware Order

The builder also registers built-in middleware for configured features. Source-checked built-ins include content upload/reference/image middleware, retry, function timeout, error formatting, container/collapsing, client tools, and logging.

Because wrappers are first-registered-is-outermost, the recommended error-handling stack is:

```text
RetryMiddleware(FunctionTimeoutMiddleware(ErrorFormattingMiddleware(core)))
```

See [Error Handling](../guides/middleware/error-handling.md) for details.
