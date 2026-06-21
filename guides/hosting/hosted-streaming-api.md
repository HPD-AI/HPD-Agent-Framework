# Hosted Streaming API

The hosted streaming API is for UI and integration clients that submit input, observe live events, interrupt active runs, and answer interactive middleware requests.

All runtime routes use:

```text
/agents/{agentId}/sessions/{sessionId}/threads/{threadId}
```

That scope identifies the hosted runtime agent and thread path.

## Submit Input

Submit simple text:

```http
POST /agents/{agentId}/sessions/{sessionId}/threads/{threadId}/inputs
Content-Type: application/json

{
  "text": "Explain this thread in one sentence.",
  "runConfig": {}
}
```

If the JSON body has no `type` property and has a non-empty `text` property, hosting treats it as a convenience text request and converts it to a `UserMessagesInputEvent`.

You can also submit an event envelope:

```json
{
  "version": "1.0",
  "type": "USER_MESSAGES_INPUT",
  "messages": [
    {
      "role": "user",
      "text": "Continue from here."
    }
  ]
}
```

Accepted input returns `202 Accepted`. That means the input was submitted. Observe SSE or WebSocket events to render output, completion, errors, and interactive requests.

Only one active hosted run is allowed per thread. A second input submitted to the same `sessionId + threadId` while a run is active returns a conflict with a thread-run-active error.

## Observe With SSE

SSE is observer-only:

```http
GET /agents/{agentId}/sessions/{sessionId}/threads/{threadId}/events/live
Accept: text/event-stream
```

The server sends one serialized event envelope per `data:` frame:

```text
data: {"version":"1.0","type":"TEXT_DELTA","text":"hello","messageId":"..."}
```

If an exception occurs after SSE headers have been sent, the stream writes a serialized error event instead of changing the HTTP status.

## Use WebSocket For Bidirectional Events

WebSocket connects at:

```text
GET /agents/{agentId}/sessions/{sessionId}/threads/{threadId}/ws
```

Client-to-server frames must be text frames containing either:

- an input event envelope, such as `USER_MESSAGES_INPUT`, or an interruption request where supported by the route
- a response event envelope implementing `IResponseEvent`, such as `PERMISSION_RESPONSE`, `CONTINUATION_RESPONSE`, `CLARIFICATION_RESPONSE`, or `CLIENT_TOOL_INVOKE_RESPONSE`

Server-to-client frames are live event envelopes for the same runtime scope. WebSocket is bidirectional and can observe after a valid client frame initializes the subscription. Use SSE for observer-only clients.

Invalid event envelopes close with an invalid-payload status. Input that conflicts with an active thread run closes with a policy-violation status.

## Render Nested Activity

SSE and WebSocket deliver a linear stream. A hosted client can project that stream into a transcript, timeline, workflow tree, subagent view, or permission queue by grouping events with fields such as `messageId`, `callId`, `permissionId`, `traceId`, `spanId`, `parentSpanId`, `eventFlowId`, and agent `metadata`.

Workflow and subagent events may appear in the same live stream as parent agent events when child execution is routed through the parent event coordinator. Treat the transport as delivery only; use [Render An Event Stream](../sessions-and-streaming/render-an-event-stream.md) for UI projection guidance.

## Fork Threads

Hosted clients fork threads with:

```http
POST /agents/{agentId}/sessions/{sessionId}/threads/{threadId}/fork
Content-Type: application/json

{
  "newThreadId": "try-short-answer",
  "fromMessageId": "message-id-to-fork-from",
  "name": "Short answer"
}
```

`ForkThreadRequest` does not include a per-request compaction intent. If fork compaction is enabled for the hosted agent, it is controlled by the server-side agent and middleware configuration. Clients should read the created thread, thread projection, or thread events after the fork instead of assuming the target thread retained every raw copied message.

## Interrupt A Run

Interrupt the active thread run:

```http
POST /agents/{agentId}/sessions/{sessionId}/threads/{threadId}/interrupt
Content-Type: application/json

{
  "reason": "User cancelled the request.",
  "eventFlowId": "optional-flow-id"
}
```

The body can also be an `INTERRUPTION_REQUEST` event envelope. Omitting a JSON object at the service layer can create a default user interruption reason; HTTP clients should send `{}` or a `reason` body unless an empty-body integration test is added.

Interrupt requires an active thread run. If none exists, the route returns a conflict with a thread-run-not-active error.

## Answer Interactive Middleware

HTTP clients answer bidirectional requests through one response route:

```http
POST /agents/{agentId}/sessions/{sessionId}/threads/{threadId}/responses
Content-Type: application/json

{
  "version": "1.0",
  "type": "PERMISSION_RESPONSE",
  "permissionId": "permission-id-from-request",
  "sourceName": "PermissionMiddleware",
  "approved": true,
  "choice": "ask"
}
```

The body must be a serialized `AgentEvent` envelope whose event implements `IResponseEvent`, such as `PermissionResponseEvent`, `ContinuationResponseEvent`, `ClarificationResponseEvent`, `ClientToolInvokeResponseEvent`, or an app-owned custom response event.

Middleware responses are not thread runs. The route requires an existing session/thread scope, an active thread runtime, and a waiter that accepts the response. If the scope does not exist, it returns `404`. If no runtime is active, the route returns a thread-runtime-not-active conflict. If the runtime exists but no waiter accepts the response, it returns `409` with the `RespondResult` status.

## Event Bodies

Hosted streaming uses `AgentEventSerializer` envelopes: `version`, `type`, and camelCase payload properties, with null values omitted.

This page intentionally shows contract-level examples only. For event families and persistence caveats, see [Events](../../reference/events.md).
