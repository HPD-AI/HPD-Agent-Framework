# Serialization And Registration

Use `AgentEventSerializer` when events cross a process, hosted API, log, replay file, or client boundary.

Local subscriptions receive typed event objects. Hosted SSE and WebSocket send serialized event envelopes.

## Live Envelope

`AgentEventSerializer.ToJson(...)` writes an event envelope:

```json
{
  "version": "1.0",
  "type": "TEXT_DELTA",
  "text": "hello",
  "messageId": "message-id"
}
```

The serializer injects `version` and `type`. Payload properties use camelCase and omit null values.

## Serialize Events

```csharp
using var all = agent.SubscribeAny(evt =>
{
    var json = AgentEventSerializer.ToJson(evt);
    stream.Write(json);
});
```

For input events such as `UserMessagesInputEvent`, use the same serializer:

```csharp
var json = AgentEventSerializer.ToJson(new UserMessagesInputEvent([
    new ChatMessage(ChatRole.User, "hello")
]));
```

## Deserialize Events

```csharp
var evt = AgentEventSerializer.FromJson(json);

if (evt is TextDeltaEvent delta)
    Console.Write(delta.Text);
```

Use stricter methods when you know the expected surface:

```csharp
AgentEvent output = AgentEventSerializer.DeserializeEventJson(json);
AgentInputEvent? input = AgentEventSerializer.FromInputJson(json);
```

Unknown or missing `type` values cannot be deserialized into known event types.

## Custom Event Registration

For ordinary app projects, define a concrete `AgentEvent` record. The custom event source generator registers its discriminator automatically:

```csharp
public sealed record RetrievalProgressEvent(
    string Query,
    int DocumentsScanned,
    int DocumentsMatched) : AgentEvent;
```

By default, `RetrievalProgressEvent` uses `RETRIEVAL_PROGRESS`.

Use `[EventType(...)]` when you need a stable wire name:

```csharp
[EventType("RETRIEVAL_PROGRESS_V2")]
public sealed record RetrievalProgressEvent(
    string Query,
    int DocumentsScanned,
    int DocumentsMatched) : AgentEvent;
```

## AOT JSON Metadata

The custom event generator registers the discriminator. For Native AOT or strict source-generated JSON paths, also provide JSON metadata and register it manually:

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
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

System.Text.Json source generation does not consume JSON context attributes emitted by another source generator, so user assemblies own their custom event JSON contexts.

## Related Pages

- [Custom Events](custom-events.md)
- [Events Reference](../../reference/events.md)
- [Hosted Streaming API](../hosting/hosted-streaming-api.md)
