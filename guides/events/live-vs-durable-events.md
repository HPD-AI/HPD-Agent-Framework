# Live Vs Durable Events

The live event stream and durable thread history are related, but they are not the same thing.

Use live events to render what is happening now. Use thread history when you need replay, audit, or persisted conversation state.

## Live Events

Live events are emitted while a run is happening:

```csharp
using var all = agent.SubscribeAny(evt =>
{
    ui.Render(evt);
});

await agent.RunAsync("Draft a short answer.");
```

Live events can include:

- text and reasoning deltas
- tool lifecycle events
- permission requests
- custom progress events
- workflow and subagent events
- retry and diagnostic events
- trace/span metadata

They are the best source for responsive UIs and hosted streaming clients.

## Durable Thread History

Durable thread history stores the events and projections needed to rebuild thread state.

Do not assume an event is durable just because it appears in the live stream. Events persist only when the thread conversion path maps them or the event type opts into thread persistence and the thread store path supports it.

## Common Rule Of Thumb

| Event family | Live stream | Durable thread history |
| --- | --- | --- |
| Text output | yes | generally yes |
| Reasoning output | yes | generally yes |
| Tool calls | yes | generally yes |
| Permission requests/responses | yes | generally no |
| Custom progress events | yes | no by default |
| Retry and diagnostics | yes | generally no |
| Compaction observability | yes | no by itself |
| Thread-history compaction | may be observed indirectly | yes when hard retention is applied |
| Workflow events | yes | validate per workflow path |
| Audio runtime events | yes | policy-dependent |
| Struct events | process-local only | no |

When in doubt, test the exact session and thread path your app uses.

## Compaction Events

`CompactionEvent` is live middleware observability. It tells clients that compaction was skipped or performed and can include counts, reason text, and summary details.

`ThreadHistoryCompactedEvent` is different. It is a durable thread-history event written under hard retention. Thread projection uses it to remove durable compacted message ids and insert replacement messages.

Render `CompactionEvent` in diagnostics. Render projected thread messages as canonical durable history.

## Struct Events

`AgentStructEvent` values are not `AgentEvent` values. They use a process-local `StructEventHub` for hot-path samples such as audio playout frames, queue depth, provider ticks, or other realtime telemetry.

Use struct events when the value is useful to local observers but should not be streamed over hosted APIs, replayed as thread history, or treated as semantic workflow state.

Selected struct events can be serialized with `AgentStructEventSerializer` for explicit export or diagnostics. That is separate from hosted `AgentEvent` streaming. If the same fact needs to appear in a UI stream or durable thread, emit an `AgentEvent` intentionally or project the struct sample into one.

## Custom Events

Custom events are live by default:

```csharp
public sealed record RetrievalProgressEvent(
    string Query,
    int DocumentsScanned,
    int DocumentsMatched) : AgentEvent;
```

That is enough for subscriptions, hosted streams, dashboards, and traces.

If a custom event must be durable, design the storage and replay path deliberately. Overriding `ShouldPersistToThread()` is only event type policy; your thread projection still needs to store, load, and render the event in a way your product understands.

## JSON Shape Caveat

Live event envelopes include `version` and `type` fields:

```json
{
  "version": "1.0",
  "type": "TEXT_DELTA",
  "text": "hello",
  "messageId": "message-id"
}
```

Durable thread event documents are storage records. They may omit live routing and correlation fields. Use live envelopes for SSE/WebSocket examples, and use thread event documents only when documenting storage behavior.

## Related Pages

- [Render An Event Stream](../sessions-and-streaming/render-an-event-stream.md)
- [Thread History And Forking](../sessions-and-streaming/thread-history-and-forking.md)
- [Compaction](../sessions-and-streaming/compaction.md)
- [Events Reference](../../reference/events.md)
