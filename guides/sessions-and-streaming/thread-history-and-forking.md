# Thread History And Forking

Threads let one session hold multiple replayable paths. A thread can be created directly, forked from a message id, updated for UI metadata, listed with sibling/tree information, and deleted subject to thread protection rules.

There are two public ways to work with threads:

- Direct in-process `Agent` APIs, where your code calls methods on a built `Agent` that has a session store.
- ASP.NET Core hosted APIs, where clients use HTTP routes and DTOs exposed by `MapHPDAgentApi(...)`.

## Direct Agent API

Use direct APIs when your application owns the `Agent` instance:

```csharp
var sessionId = await agent.CreateSessionAsync("review-session");

await agent.RunAsync(
    "Draft the first answer.",
    sessionId: sessionId,
    threadId: "main");

var forkId = await agent.ForkThreadAsync(
    sessionId,
    sourceThreadId: "main",
    newThreadId: "short-answer",
    fromMessageId: messageId);
```

Direct APIs require a configured session store. `CreateSessionAsync(...)` creates the default `main` thread. `ForkThreadAsync(...)` returns the new thread id and can also accept `ThreadForkOptions` for programmatic fork behavior such as fork compaction.

## ASP.NET Core Hosted Routes

Thread read, update, delete, content, and event-log routes are scoped by `sessionId` and `threadId`. Thread create and fork routes also include `agentId` because they need the hosted runtime configuration for that agent scope.

| Operation | Route |
| --- | --- |
| List threads | `GET /sessions/{sessionId}/threads` |
| Get thread | `GET /sessions/{sessionId}/threads/{threadId}` |
| Create thread | `POST /agents/{agentId}/sessions/{sessionId}/threads` |
| Fork thread | `POST /agents/{agentId}/sessions/{sessionId}/threads/{threadId}/fork` |
| Update thread | `PATCH /sessions/{sessionId}/threads/{threadId}` |
| Delete thread | `DELETE /sessions/{sessionId}/threads/{threadId}?recursive=false` |
| Get thread events | `GET /sessions/{sessionId}/threads/{threadId}/events` |
| Get siblings | `GET /sessions/{sessionId}/threads/{threadId}/siblings` |

## Hosted Create A Thread

Creating a thread accepts a `CreateThreadRequest` with a thread id plus optional name, description, tags, and metadata. If the thread id is blank, the service can generate one.

Creating a thread with an existing id returns a conflict. A thread created this way starts as its own path rather than a fork from another thread.

## Hosted Fork From A Message

Forking accepts a `ForkThreadRequest`:

```json
{
  "newThreadId": "try-short-answer",
  "fromMessageId": "message-id-to-fork-from",
  "name": "Short answer",
  "description": "A shorter response path",
  "tags": ["draft"],
  "metadata": {
    "uiColor": "green"
  }
}
```

`fromMessageId` is required. If the message id is not present on the source thread, the hosted API returns a validation error.

The new thread records its source thread and fork point. Persistent thread-scoped middleware state is copied to the new thread when the fork is committed and then evolves independently.

## Hosted Update Thread Metadata

Thread updates accept optional name, description, tags, and metadata. Metadata is merged: values add or overwrite keys, and null removes keys.

Use thread metadata for UI labels, filters, annotations, and runtime hints that belong to a single path.

## Delete Threads

The `main` thread is protected and cannot be deleted.

In the hosted API, deleting a thread with children returns a conflict unless `recursive=true`. Recursive deletion must also be enabled by server configuration. If recursive deletion is disabled, the API returns a validation error even when the request includes `recursive=true`.

Hosted thread deletion uses a thread operation lock. A lock conflict means another exclusive thread mutation is in progress; it does not mean an agent run is active.

## Read Thread Events

`GET /sessions/{sessionId}/threads/{threadId}/events` returns normalized polymorphic `AgentEvent` JSON for the thread log. This is useful for rebuilding thread views and debugging history.

Do not assume the thread event JSON is identical to live SSE or WebSocket event envelopes. Live event envelopes can include routing and correlation fields when they are present on the event. Durable thread event JSON may omit live-routing fields and only contains events that were mapped or opted into thread persistence.

In direct in-process code, read thread history through the configured session store or higher-level runtime that wraps it. In ASP.NET Core hosted clients, use the thread events route.

For UI projection guidance, see [Render An Event Stream](render-an-event-stream.md). For the live-vs-durable model, see [Event Streams And Hierarchies](../../concepts/event-streams-and-hierarchies.md).

## Thread History Compaction

Hard thread-history compaction changes the projected thread. A `THREAD_HISTORY_COMPACTED` durable thread event removes compacted durable message ids from projection and inserts replacement messages when the retention mode produced them.

Soft compaction does not change the durable thread projection. It only reduces what the next model turn sees.

For the full model, see [Compaction](compaction.md).

## Forking After Compaction

Forking uses a message id on the projected source thread. If hard compaction removed an older message from the projection, that original message id may no longer be a valid fork point. The framework can surface replacement candidates when a compacted-away id is detected, but clients should not assume automatic recovery.

Use projected thread messages as the source of forkable message ids.

## Fork And Compact

Fork compaction is a separate pre-commit behavior. When configured, the target thread is compacted after it is copied from the source thread and before the target thread is saved.

The source thread is unchanged. The target thread starts with the already-compacted initial history. Fork compaction does not write a standalone `THREAD_HISTORY_COMPACTED` event.

Hosted `ForkThreadRequest` does not expose a per-request compaction intent today. Hosted fork compaction is controlled by server-side agent and middleware configuration.

The middleware hook for this phase is `BeforeThreadForkCommitAsync`. It receives the source thread, the in-memory target thread, the fork point, and `ThreadForkOptions`. This is the place for middleware that needs to change the target thread before it becomes durable. After the fork is committed, durable thread events such as `THREAD_FORKED` describe the committed thread state; they are not mutation hooks.

## Sibling Navigation

The hosted sibling endpoint returns thread DTOs with tree and sibling fields such as sibling index, total siblings, previous sibling id, next sibling id, original thread id, and total forks.

Current implementation returns full `ThreadDto` values for siblings. Avoid depending on a separate lightweight sibling DTO until that contract is reconciled.
