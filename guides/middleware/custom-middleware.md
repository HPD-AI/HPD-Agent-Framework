# Custom Middleware

Middleware is for cross-cutting behavior that belongs inside the agent loop: logging, timing, permission checks, rate limits, request shaping, retry wrappers, result formatting, and thread/session state updates. Start with `IAgentMiddleware`, override only the hook you need, and register the instance before `BuildAsync()`.

Use the core framework package and middleware namespace:

```bash
dotnet add package HPD-Agent.Framework --version 0.5.5
```

```csharp
using HPD.Agent;
using HPD.Agent.Middleware;
```

If your sample builds an agent with OpenAI, also reference the provider package and namespace:

```bash
dotnet add package HPD-Agent.Providers.OpenAI --version 0.5.5
```

```csharp
using HPD.Agent.Providers.OpenAI;
```

## Small Timing Middleware

This middleware times each tool/function call. It uses `WrapFunctionCallAsync` because timing should surround the actual function body, including exceptions.

```csharp
using System.Diagnostics;
using HPD.Agent.Middleware;

public sealed class ToolTimingMiddleware : IAgentMiddleware
{
    public async Task<object?> WrapFunctionCallAsync(
        FunctionRequest request,
        Func<FunctionRequest, Task<object?>> handler,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            return await handler(request).ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            Console.WriteLine(
                $"{request.FunctionName} took {stopwatch.ElapsedMilliseconds} ms");
        }
    }
}
```

Register it with the builder:

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("You are concise.")
    .WithMiddleware(new ToolTimingMiddleware())
    .BuildAsync();

var result = await agent.RunAsync("Say hello.");
Console.WriteLine(result.Text);
```

## Hook Picker

| Need | Hook |
| --- | --- |
| Allocate or validate resources before a long-running runtime input loop starts | `BeforeStartAsync` |
| Run diagnostics or background work after the runtime input loop has started | `AfterStartedAsync` |
| Flush buffers or make graceful drain decisions before the runtime stops | `BeforeStopAsync` |
| Emit final telemetry after the runtime has stopped and resources were disposed | `AfterStoppedAsync` |
| Add context, rewrite the current user message, or inspect run config once per user turn | `BeforeMessageTurnAsync` |
| Inspect final response, persisted turn history, or total usage for a turn | `AfterMessageTurnAsync` |
| Add model context, tune chat options, skip a model call, or enforce per-iteration policies | `BeforeIterationAsync` |
| Wrap streaming model output for retries, sanitization, token accounting, or stream transforms | `WrapModelTurnStreamingAsync` |
| Inspect all tool calls the model requested before any tool runs | `BeforeToolExecutionAsync` |
| Skip all pending tools for an iteration | `BeforeToolExecutionAsync` with `SkipToolExecution = true` |
| Check a parallel batch as a batch before individual functions are prepared | `BeforeParallelBatchAsync` |
| Block or override one function call | `BeforeFunctionAsync` with `BlockExecution = true` and `OverrideResult` |
| Time, retry, time out, or transform actual function execution | `WrapFunctionCallAsync` |
| Format a function result or observe a function exception after execution | `AfterFunctionAsync` |
| Inspect tool results after an iteration | `AfterIterationAsync` |
| Adjust middleware state before a thread fork is committed | `BeforeThreadForkCommitAsync` |
| React to function/tool execution errors | `OnErrorAsync` |

`BeforeToolExecutionAsync` and `BeforeParallelBatchAsync` are easy to confuse. Use `BeforeToolExecutionAsync` when the model response has been parsed and you want a whole-iteration decision across all pending tool calls. Use `BeforeParallelBatchAsync` when HPD Agent has identified a batch of functions that will execute in parallel and your policy depends on the batch shape, batch id, or model order before per-function hooks run.

## Ordering

`Before*` hooks run in registration order:

```text
First.BeforeIteration()
Second.BeforeIteration()
```

`After*` hooks and `OnErrorAsync` run in reverse registration order:

```text
Second.AfterIteration()
First.AfterIteration()
```

Wrapper hooks are nested with the first registered middleware outermost:

```csharp
var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithMiddleware(new OuterMiddleware())
    .WithMiddleware(new InnerMiddleware())
    .BuildAsync();
```

The wrapper call shape is:

```text
OuterMiddleware(InnerMiddleware(core))
```

This matters for wrappers such as retry, timeout, and formatting. If retry is registered before timeout, retry sees timeout failures from the inner wrapper and can decide whether to run the function again.

## Tool And Function Decisions

To skip every tool call requested in the current model response, set the control fields on `BeforeToolExecutionContext`:

```csharp
public Task BeforeToolExecutionAsync(
    BeforeToolExecutionContext context,
    CancellationToken cancellationToken)
{
    if (context.ToolCalls.Any(call => call.Name == "delete_project"))
    {
        context.SkipToolExecution = true;
        context.OverrideResponse = new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant,
            "I cannot run destructive tools in this mode.");
    }

    return Task.CompletedTask;
}
```

To block one function call and let the rest of the iteration continue, use `BeforeFunctionAsync`:

```csharp
public Task BeforeFunctionAsync(
    BeforeFunctionContext context,
    CancellationToken cancellationToken)
{
    if (context.Function?.Name == "delete_project")
    {
        context.BlockExecution = true;
        context.OverrideResult = "Blocked by middleware policy.";
    }

    return Task.CompletedTask;
}
```

Use `BeforeParallelBatchAsync` when the policy is about the whole batch, such as "no more than three network tools at once" or "ask once for approval before this batch runs." The built-in permission flow uses this hook to evaluate a parallel batch before individual function hooks.

## Memory And Context Pattern

Middleware is the right place for app-owned memory and retrieval. The usual shape is:

1. Use `BeforeMessageTurnAsync` to inspect the user message, session id, thread id, tenant, or run config.
2. Retrieve relevant context from your application store, vector index, content store, cache, or service.
3. Add a small system message to the turn's thread history.
4. Use `AfterMessageTurnAsync` to extract useful memory signals from the final result.
5. Store durable memory bodies in your application storage, and store only small ids or cursors in `[MiddlewareState]`.

```csharp
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;

[MiddlewareState(Persistent = true, Scope = StateScope.Thread)]
public sealed record MemoryPointerState
{
    public string? MemoryScopeId { get; init; }
}

// IMemoryStore is your app-owned abstraction over a database, vector store, or service.
public sealed class MemoryContextMiddleware(IMemoryStore memoryStore) : IAgentMiddleware
{
    public async Task BeforeMessageTurnAsync(
        BeforeMessageTurnContext context,
        CancellationToken cancellationToken)
    {
        var scopeId = context.GetMiddlewareState<MemoryPointerState>()?.MemoryScopeId
            ?? context.ThreadId
            ?? context.SessionId
            ?? "default";

        var snippets = await memoryStore.SearchAsync(
            scopeId,
            context.UserMessage?.Text ?? string.Empty,
            cancellationToken);

        if (snippets.Count > 0)
        {
            context.ThreadHistory.Add(new ChatMessage(
                ChatRole.System,
                "Relevant memory:\n" + string.Join("\n", snippets)));
        }
    }
}
```

Keep this middleware boring. It should choose and inject context; the model should still decide what to do with that context. If the agent needs to intentionally save, delete, or edit memory as an action, expose that as a tool.

## Model Streaming Wrappers

`WrapModelTurnStreamingAsync` is opt-in. Return `null` when the middleware does not need streaming access. If you return an `IAsyncEnumerable<AgentModelUpdate>`, keep side effects inside the async iterator body.

The pipeline probes the wrapper during chain construction to see whether it returns `null`, then calls it again when the stream is actually enumerated. Counters, logs, event emits, and request mutations placed before returning the stream can run during the probe instead of real execution.

```csharp
using System.Runtime.CompilerServices;
using HPD.Agent.Middleware;

public sealed class ModelUpdateCountingMiddleware : IAgentMiddleware
{
    public IAsyncEnumerable<AgentModelUpdate>? WrapModelTurnStreamingAsync(
        AgentModelTurnRequest request,
        Func<AgentModelTurnRequest, IAsyncEnumerable<AgentModelUpdate>> handler,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        return CountUpdatesAsync(request, handler, cancellationToken);
    }

    private static async IAsyncEnumerable<AgentModelUpdate> CountUpdatesAsync(
        AgentModelTurnRequest request,
        Func<AgentModelTurnRequest, IAsyncEnumerable<AgentModelUpdate>> handler,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = 0;

        await foreach (var update in handler(request).WithCancellation(cancellationToken))
        {
            count++;
            yield return update;
        }

        Console.WriteLine($"Model stream produced {count} updates.");
    }
}
```

## State Updates

For middleware-local state, define a record with `[MiddlewareState]` and update it through `UpdateMiddlewareState<TState>(...)`.

```csharp
using HPD.Agent.Middleware;

[MiddlewareState(Persistent = false)]
public sealed record TimingState
{
    public int FunctionsSeen { get; init; }
}

public sealed class FunctionCountingMiddleware : IAgentMiddleware
{
    public Task BeforeFunctionAsync(
        BeforeFunctionContext context,
        CancellationToken cancellationToken)
    {
        context.UpdateMiddlewareState<TimingState>(state => state with
        {
            FunctionsSeen = state.FunctionsSeen + 1
        });

        return Task.CompletedTask;
    }
}
```

State updates are immediate: later middleware in the same phase can see them. Do not read state, `await`, and then write a derived value. Keep the read inside the update lambda so the value is based on the current state.

## What Not To Use Middleware For

Do not use middleware as a replacement for a tool. If the model should intentionally choose an action, make it a function or tool harness.

Do not use middleware as the only application-level error boundary. `OnErrorAsync` is source-checked for the function/tool error path, but it is not a global catch-all for provider errors, streaming model errors, or every message-turn failure. Handle `RunAsync(...)` failures in application code and subscribe to events for runtime reporting.

Do not mutate live agent state from function bodies. Function bodies receive a narrow `FunctionExecutionContext`; scheduler-owned state changes belong in middleware hooks.

Do not put irreversible side effects in model streaming wrapper setup. Put them inside the async iterator so they run during enumeration.

Do not overuse `BeforeToolExecutionAsync` for per-function decisions. If only one function should be blocked or overridden, `BeforeFunctionAsync` is the narrower hook.

## Common Errors

**Missing namespace:** `IAgentMiddleware`, typed hook contexts, `FunctionRequest`, `AgentModelTurnRequest`, and `UpdateMiddlewareState<TState>(...)` are in `HPD.Agent.Middleware`.

**Wrong wrapper order:** the first registered wrapper is outermost. Do not rely on the stale interface XML comment that says last registered is outermost.

**Forgetting to call the handler:** wrapper hooks must call `handler(request)` unless they intentionally replace execution.

**Returning `null` from a function wrapper:** `WrapFunctionCallAsync` returns `Task<object?>`; call the handler and return its result unless your middleware intentionally supplies an override.

**Putting streaming side effects before the returned iterator:** `WrapModelTurnStreamingAsync` may be called once as a probe and once for real execution.

**Expecting `BeforeParallelBatchAsync` for single tool calls:** it is a parallel-batch hook. Use `BeforeFunctionAsync` for behavior that must run for every individual function.
