# Custom Events

Custom events let your app put product-specific progress, audit, and UI signals into the same stream as text, tool calls, permissions, workflows, and diagnostics.

Use them when a host, TUI, dashboard, workflow view, or integration needs to observe something that is not a built-in HPD event.

## Define The Event

Create a concrete, non-generic record that inherits from `AgentEvent`:

```csharp
using HPD.Agent;

public sealed record RetrievalProgressEvent(
    string Query,
    int DocumentsScanned,
    int DocumentsMatched) : AgentEvent;
```

The event type name becomes the wire discriminator. `RetrievalProgressEvent` becomes `RETRIEVAL_PROGRESS`.

Use `[EventType(...)]` when you need a stable custom discriminator or need to resolve a naming collision:

```csharp
using HPD.Agent;
using HPD.Agent.Serialization;

[EventType("RETRIEVAL_PROGRESS_V2")]
public sealed record RetrievalProgressEvent(
    string Query,
    int DocumentsScanned,
    int DocumentsMatched) : AgentEvent;
```

Keep discriminator names in `SCREAMING_SNAKE_CASE`.

## Emit It

Middleware and hook contexts can emit custom events the same way they emit built-in events:

```csharp
public async Task BeforeFunctionAsync(
    BeforeFunctionContext context,
    CancellationToken cancellationToken)
{
    if (context.Function?.Name == "search_documents")
    {
        context.Emit(new RetrievalProgressEvent(
            Query: "user query",
            DocumentsScanned: 0,
            DocumentsMatched: 0));
    }

    await Task.CompletedTask;
}
```

Events emitted from middleware are published to the agent event stream immediately. Subscriber handlers may process from their mailboxes asynchronously. During a message turn, the runtime stamps trace information onto middleware-emitted events when the event does not already carry it.

## Emit From A Tool

Tool functions can emit custom events too. Add `FunctionExecutionContext` as a runtime-only parameter on an `[AIFunction]` method:

```csharp
using HPD.Agent;
using HPD.Agent.Middleware;

public sealed class RetrievalTools
{
    [AIFunction(Name = "search_documents")]
    public async Task<string> SearchDocuments(
        string query,
        FunctionExecutionContext context,
        CancellationToken cancellationToken)
    {
        context.Emit(new RetrievalProgressEvent(query, 0, 0));

        var scanned = 0;
        var matched = 0;

        await foreach (var document in SearchIndexAsync(query, cancellationToken))
        {
            scanned++;

            if (document.IsMatch)
                matched++;

            context.Emit(new RetrievalProgressEvent(query, scanned, matched));
        }

        return $"Matched {matched} documents.";
    }
}
```

`FunctionExecutionContext` is not shown to the model as a tool argument. The source generator treats it as a runtime parameter, excludes it from the generated tool schema and DTO, and supplies it when the agent invokes the function. Use it when the tool itself knows meaningful progress that middleware cannot infer from the outside.

The context also carries the current function call id, function name, run config, result metadata, services, content store, background-task registry, and event coordinator. `Emit(...)` stamps the current trace id when the event does not already have one.

## Subscribe To It

Subscribe before the run starts:

```csharp
using var retrievalProgress = agent.Subscribe<RetrievalProgressEvent>(evt =>
{
    ui.UpdateRetrieval(evt.Query, evt.DocumentsScanned, evt.DocumentsMatched);
});

await agent.RunAsync("Find the latest support article for this issue.");
```

Use `SubscribeAny(...)` when direct in-process code is building a generic event router, logger, hosted stream implementation, or trace view:

```csharp
using var allEvents = agent.SubscribeAny(evt =>
{
    var json = AgentEventSerializer.ToJson(evt);
    stream.Write(json);
});
```

Workflow subscriptions can receive custom events too when child agent or middleware events bubble into the workflow coordinator:

```csharp
using var progress = workflow.Subscribe<RetrievalProgressEvent>(evt =>
{
    ui.UpdateWorkflowNode(evt.Query, evt.DocumentsScanned);
});
```

## Serialization

Custom events use the same live event envelope as built-in events:

```json
{
  "version": "1.0",
  "type": "RETRIEVAL_PROGRESS",
  "query": "refund policy",
  "documentsScanned": 12,
  "documentsMatched": 3
}
```

For ordinary app projects, define the event record and let source generation handle the rest. The custom event generator discovers concrete `AgentEvent` records outside HPD framework namespaces and generates:

- assembly-local discriminator constants
- module-initializer registration for `AgentEventSerializer`

That means `AgentEventSerializer` can use the right discriminator without manual registration.

For Native AOT or other strict source-generated JSON paths, register JSON metadata manually with `AgentEventSerializer.RegisterEventType(...)` as shown below. System.Text.Json source generation does not consume JSON context attributes emitted by another source generator, so the custom event generator cannot safely create that context for you.

## Generator Rules

The source generator accepts concrete, non-generic records that inherit from `AgentEvent`.

It reports:

| Diagnostic | Meaning |
| --- | --- |
| `HPD010` | Two custom events resolve to the same discriminator. Rename one event or add `[EventType("...")]`. |
| `HPD011` | A custom event is generic. Create a concrete event type instead. |
| `HPD012` | An abstract event inherits from `AgentEvent`; it is valid as a base type but is not registered. |

Prefer top-level public event records for events that need to cross assembly, hosted, or serialized boundaries.

## Manual Registration

Use manual registration only for special package boundaries or libraries where the source generator is not participating.

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using HPD.Agent;
using HPD.Agent.Serialization;

[JsonSerializable(typeof(RetrievalProgressEvent))]
public partial class AppEventJsonContext : JsonSerializerContext
{
}

internal static class AppEventRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void RegisterEvents()
#pragma warning restore CA2255
    {
        AgentEventSerializer.RegisterEventType(
            typeof(RetrievalProgressEvent),
            "RETRIEVAL_PROGRESS",
            AppEventJsonContext.Default.RetrievalProgressEvent);
    }
}
```

## Persistence

Custom events are live runtime events by default. They are useful for UI progress, traces, and hosted clients even when they are not written to thread history.

If a custom event must become durable thread history, treat that as a separate persistence design. Overriding `ShouldPersistToThread()` is event type policy, but your thread projection and replay path still need to know how to store, load, and render that event correctly.

## Related Pages

- [Events Overview](overview.md)
- [Event Streams And Hierarchies](../../concepts/event-streams-and-hierarchies.md)
- [Render An Event Stream](../sessions-and-streaming/render-an-event-stream.md)
- [Middleware Overview](../middleware/overview.md)
- [Workflow Events](../multi-agent/workflow-events.md)
