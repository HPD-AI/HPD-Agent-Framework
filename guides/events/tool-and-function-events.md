# Tool And Function Events

Tool events let a UI show what the model asked a tool to do. Function execution context lets the tool report what it is doing while it runs.

Use both when a tool can take noticeable time, call external systems, process files, or run a workflow-like operation behind one model tool call.

## Subscribe To Tool Activity

Subscribe before the agent run starts:

```csharp
using var toolStart = agent.Subscribe<ToolCallStartEvent>(evt =>
{
    ui.StartTool(evt.CallId, evt.Name);
});

using var toolArgs = agent.Subscribe<ToolCallArgsEvent>(evt =>
{
    ui.ShowToolArguments(evt.CallId, evt.ArgsJson);
});

using var toolResult = agent.Subscribe<ToolCallResultEvent>(evt =>
{
    ui.ShowToolResult(evt.CallId, evt.Result);
});

using var toolEnd = agent.Subscribe<ToolCallEndEvent>(evt =>
{
    ui.EndTool(evt.CallId);
});

await agent.RunAsync("Search the support docs and summarize the answer.");
```

Group tool lifecycle events by `CallId`. If a model emits text while tools are running, text and tool events can be interleaved in the live stream.

`ToolCallStartEvent.Name` is the function/tool name. `ToolCallResultEvent.Result` is a `ToolResultPayload`, which can carry text, JSON, or richer client-tool content.

## Emit Progress From The Tool

When the tool itself knows meaningful progress, accept `FunctionExecutionContext` as a runtime-only parameter and emit events from inside the function body:

```csharp
using HPD.Agent;
using HPD.Agent.Middleware;

public sealed record RetrievalProgressEvent(
    string Query,
    int DocumentsScanned,
    int DocumentsMatched) : AgentEvent;

public sealed class RetrievalTools
{
    [AIFunction(Name = "search_documents")]
    public async Task<string> SearchDocuments(
        string query,
        FunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        context.Emit(new RetrievalProgressEvent(query, 0, 0));

        var scanned = 12;
        var matched = 3;

        context.Emit(new RetrievalProgressEvent(query, scanned, matched));
        return $"Matched {matched} documents.";
    }
}
```

`FunctionExecutionContext` and `CancellationToken` are supplied by HPD Agent. They can appear in the C# function signature, but they are excluded from the generated tool schema and argument DTO, so the model does not see them and cannot provide values for them. In the example above, the model only sees real tool arguments such as `query`. Unsupported runtime-like parameters such as `HookContext`, `AgentContext`, `AgentLoopState`, `IEventCoordinator`, `IEventFlowRegistry`, or `ToolResultMetadata` produce generator diagnostic `HPD020`; use `FunctionExecutionContext` instead.

Subscribe to the custom progress event like any other event:

```csharp
using var progress = agent.Subscribe<RetrievalProgressEvent>(evt =>
{
    ui.UpdateRetrieval(evt.Query, evt.DocumentsScanned, evt.DocumentsMatched);
});
```

## When To Use Middleware

Use tool code for domain progress the tool already knows:

- records scanned
- files processed
- external jobs started
- retrieval stages
- provider-specific status

Use middleware for cross-cutting behavior around functions:

- permission checks
- retry and timeout policy
- argument redaction
- audit events
- shared logging

Middleware can emit with `context.Emit(...)` before or after function execution. Tool code can emit with `FunctionExecutionContext.Emit(...)` during function execution.

`Emit(...)` publishes to the event stream and subscriber mailboxes. Subscriber handlers may process the event asynchronously, so do not use `Emit(...)` as proof that every handler has already finished.

## Runtime Context

`FunctionExecutionContext` exposes a narrow runtime surface:

- `FunctionCallId`, `FunctionName`, and invocation metadata
- `TraceId`, `SessionId`, and `ThreadId` when present
- `RunConfig` and per-call `ResultMetadata`
- `Services`, `ContentStore`, and runtime capabilities
- `Emit(...)`, `TryEmit(...)`, and `RequestAsync(...)`
- `StructEvents` for process-local realtime sample lanes
- background task registration when the active runtime supports it

It does not expose mutable agent state or hook contexts. Use middleware when the behavior needs to mutate middleware state or wrap scheduler-owned phases.

`StructEvents` is separate from `AgentEvent` streaming. Use it for local hot-path samples, frames, or queue-depth style telemetry that should not be serialized, persisted, replayed, or shown to the model as semantic progress.

## Background Work From Tools

Use `RegisterBackgroundTask(...)` when a tool needs to return a normal tool result but keep runtime-owned work alive after the function body returns. This is useful for follow-up indexing, long-running cleanup, async provider jobs, or post-processing that should stay attached to the tool call.

```csharp
[AIFunction(Name = "start_indexing")]
public Task<string> StartIndexing(
    string collection,
    FunctionExecutionContext context,
    CancellationToken cancellationToken)
{
    if (!context.CanRegisterBackgroundTasks)
        return Task.FromResult("Indexing could not be started in the background.");

    context.RegisterBackgroundTask("index-documents", async (background, runtimeToken) =>
    {
        await indexer.RebuildAsync(collection, runtimeToken);
    });

    return Task.FromResult($"Started indexing {collection}.");
}
```

The registered task receives a `FunctionBackgroundContext` with an assigned task id, the task name, the original function invocation snapshot, event access, and services.

The runtime emits lifecycle events for registered function background work:

- `ToolCallBackgroundTaskStartedEvent`
- `ToolCallBackgroundTaskCompletedEvent`
- `ToolCallBackgroundTaskCancelledEvent`
- `ToolCallBackgroundTaskFaultedEvent`

Group these events by `TaskId`, and use `Invocation.FunctionCallId` when you want to attach the background task back to the original tool call. The runtime waits for registered background tasks during cleanup and cancels them through the provided `CancellationToken` when the runtime is stopping.

## Related Pages

- [Custom Events](custom-events.md)
- [Bidirectional Events](bidirectional-events.md)
- [Author A Tool Harness](../tools/author-a-tool-harness.md)
- [Render An Event Stream](../sessions-and-streaming/render-an-event-stream.md)
