# TypeScript Client Events

Use the TypeScript client when a browser, Node app, editor extension, or custom UI needs to render HPD Agent runs.

The client is not another event system. It is the JavaScript/TypeScript consumption surface for hosted agent events: open a session and thread, load durable thread history, subscribe to the live stream, send inputs, and answer interactive requests.

## Install The Client Surface

The main entry point is `AgentClient`:

```typescript
import { AgentClient, EventTypes } from '@hpd/hpd-agent-client';

const client = new AgentClient({
  baseUrl: 'http://localhost:5135',
  transport: 'sse',
});
```

Use `transport: 'sse'` for the default hosted streaming path. Use `transport: 'websocket'` when the host exposes the WebSocket runtime endpoint and you want input and output on the same socket.

## Open A Chat Scope

Most apps should scope UI work to one agent, session, and thread:

```typescript
const chat = await client.chat.open({
  agentId: 'assistant',
  threadId: 'main',
  session: {
    create: {
      metadata: { title: 'New chat' },
    },
  },
});
```

Then load history, install event handlers, subscribe to the live stream, and submit input:

```typescript
for (const event of await chat.getThreadEvents()) {
  projectThreadEvent(event);
}

client.on(EventTypes.TEXT_DELTA, (event) => {
  transcript.append(event.messageId, event.text);
});

client.onAny((event) => {
  projectLiveEvent(event);
});

await chat.subscribeLive();
await chat.submitText('Summarize this thread.');
```

Subscribe before submitting input when the UI needs to render the turn as it happens.

## Typed Handlers And Projection

Use `client.on(...)` for event families your app knows how to handle directly:

```typescript
client.on(EventTypes.TOOL_CALL_START, (event) => {
  tools.start(event.callId, event.name);
});

client.on(EventTypes.TOOL_CALL_ARGS, (event) => {
  tools.appendArgs(event.callId, event.argsJson);
});

client.on(EventTypes.TOOL_CALL_RESULT, (event) => {
  tools.setResult(event.callId, event.result);
});
```

Use `client.onAny(...)` for stream-wide projection, diagnostics, custom events, and unknown event types:

```typescript
client.onAny((event) => {
  timeline.push({
    type: event.type,
    timestamp: event.timestamp,
    flow: event.eventFlowId,
  });
});
```

Typed handlers run before `onAny` handlers for the same event. Handlers are awaited in order, so keep UI projection work fast and move expensive side effects out of the hot path.

## Respond To Interactive Events

Permission, continuation, and clarification requests are bidirectional events. The TypeScript app observes the request, asks the user or host policy, and sends the matching response:

```typescript
client.on(EventTypes.PERMISSION_REQUEST, async (request) => {
  const approved = await permissions.confirm({
    title: request.functionName,
    description: request.description,
    arguments: request.arguments,
  });

  await client.run({
    type: EventTypes.PERMISSION_RESPONSE,
    permissionId: request.permissionId,
    sourceName: request.sourceName,
    approved,
    reason: approved ? undefined : 'Denied by user.',
    choice: 'ask',
  });
});
```

The same pattern applies to `CLARIFICATION_REQUEST` and `CONTINUATION_REQUEST`. With SSE, the client posts response event envelopes to the hosted `/responses` route for the current chat scope. With WebSocket, it sends the same response envelope over the socket. In both cases, preserve the request id from the request event so the hosted runtime can match the pending waiter.

Client tools are the exception. If you register a tool handler, the client automatically answers `CLIENT_TOOL_INVOKE_REQUEST` with `CLIENT_TOOL_INVOKE_RESPONSE`:

```typescript
client.tools.register('get_active_view', () => ({
  activeView: 'chat',
}));
```

This registers the local handler only. To make externally executed tools visible to the model, pass tool harness definitions through `runConfig.clientToolInput`. See [Externally Executed Client Tools](../tools/externally-executed-client-tools.md).

Use explicit request handlers only when your app needs to render the client-tool request before or after the automatic response path.

## Custom And Unknown Events

The TypeScript client preserves events whose `type` is not modeled by the package version you are using. Handle app-owned events through `onAny`:

```typescript
type RetrievalProgress = {
  type: 'RETRIEVAL_PROGRESS';
  query: string;
  documentsScanned: number;
  documentsMatched: number;
};

client.onAny((event) => {
  if (event.type !== 'RETRIEVAL_PROGRESS') return;

  const progress = event as RetrievalProgress;
  retrievalPanel.update(progress);
});
```

Register custom event serialization on the .NET side so hosted streams can produce the event envelope. Add TypeScript types locally when the event belongs to your app; add them to the SDK only when the event becomes a shared protocol event.

## Live Stream Vs Thread History

`chat.subscribeLive()` reads hosted live envelopes from the runtime stream. `chat.getThreadEvents()` reads durable thread history records.

Project them into the same UI state, but do not assume they are the same JSON shape. Live events can include routing, correlation, and transient runtime fields. Thread history contains the stored thread view.

For a typical chat UI:

1. Read thread history into transcript state.
2. Subscribe to live events.
3. Apply live deltas and tool events as they arrive.
4. Refresh or reconcile thread history after a completed run if the app needs durable confirmation.

## What Does Not Reach TypeScript

`AgentStructEvent` values do not flow through the TypeScript client. Struct events are process-local samples on the .NET `StructEventHub`; they are not `AgentEvent` values, not serialized by `AgentEventSerializer`, and not sent over hosted SSE or WebSocket.

`AgentStructEventSerializer` can serialize selected struct events for explicit export or diagnostics, but that is not the hosted agent event stream.

If a process-local sample needs to appear in a hosted UI, summarize or convert it into an intentional `AgentEvent`.

## Transport Notes

With SSE, the live observer connects to:

```text
/agents/{agentId}/sessions/{sessionId}/threads/{threadId}/events/live
```

Inputs are posted separately. Response events are posted to the hosted `/responses` route for the current agent, session, and thread.

With WebSocket, the client connects to:

```text
/agents/{agentId}/sessions/{sessionId}/threads/{threadId}/ws
```

Input events are sent over the open socket.

## Related Pages

- [Render An Event Stream](../sessions-and-streaming/render-an-event-stream.md)
- [Bidirectional Events](bidirectional-events.md)
- [Externally Executed Client Tools](../tools/externally-executed-client-tools.md)
- [Live Vs Durable Events](live-vs-durable-events.md)
- [Serialization And Registration](serialization-and-registration.md)
- [Hosted Streaming API](../hosting/hosted-streaming-api.md)
