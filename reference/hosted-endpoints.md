# Hosted Endpoints

This reference lists the ASP.NET Core hosted runtime endpoints at contract level. Routes are relative to the route group prefix configured by `MapHPDAgentApi(...)`. Created-resource `Location` headers are currently prefixless even when routes are mapped under a prefix.

Endpoint examples use DTO names and status families. Event payload fields are delegated to [Events](events.md), because exhaustive event schemas should be generated from source rather than hand-authored.

## Scope Rules

The hosted runtime scope is:

```text
agentId + sessionId + threadId
```

Routes are intentionally mixed:

- session routes do not include `agentId`
- thread read, update, delete, content, and event-log routes are session/thread scoped
- thread create, fork, thread runs, input, interrupt, SSE, WebSocket, and the bidirectional response route include `agentId`
- stored agent definitions live under `/agents`

## Sessions

| Method | Route | Body | Success | Common failures |
| --- | --- | --- | --- | --- |
| `POST` | `/sessions` | `CreateSessionRequest?` | `201 SessionDto` | `400` validation problem |
| `GET` | `/sessions` | none | `200 List<SessionDto>` | `400` |
| `POST` | `/sessions/search` | `SearchSessionsRequest?` | `200 List<SessionDto>` | `400` |
| `GET` | `/sessions/{sessionId}` | none | `200 SessionDto` | `404`, `400` |
| `PATCH` | `/sessions/{sessionId}` | `UpdateSessionRequest` | `200 SessionDto` | `404`, `400` |
| `DELETE` | `/sessions/{sessionId}` | none | `204` | `404`, `400` |

`CreateSessionRequest` has optional `sessionId` and metadata. Missing ids are generated. Creating a session creates thread `main`.

`SearchSessionsRequest` filters by exact stringified metadata value and supports offset and limit. `UpdateSessionRequest` merges metadata and removes keys whose values are null.

## Threads

| Method | Route | Body | Success | Common failures |
| --- | --- | --- | --- | --- |
| `GET` | `/sessions/{sid}/threads` | none | `200 List<ThreadDto>` | `404`, `400` |
| `GET` | `/sessions/{sid}/threads/{bid}` | none | `200 ThreadDto` | `404`, `400` |
| `POST` | `/agents/{agentId}/sessions/{sid}/threads` | `CreateThreadRequest` | `201 ThreadDto` | `404`, `409`, `400` |
| `POST` | `/agents/{agentId}/sessions/{sid}/threads/{bid}/fork` | `ForkThreadRequest` | `201 ThreadDto` | `404`, `400` |
| `PATCH` | `/sessions/{sid}/threads/{bid}` | `UpdateThreadRequest` | `200 ThreadDto` | `404`, `400` |
| `DELETE` | `/sessions/{sid}/threads/{bid}?recursive=false` | none | `204` | `404`, `409`, `400` |
| `GET` | `/sessions/{sid}/threads/{bid}/events` | none | `200 List<AgentEvent>` | `404`, `400` |
| `GET` | `/sessions/{sid}/threads/{bid}/siblings` | none | `200 List<ThreadDto>` | `404`, `400` |

`ForkThreadRequest` has `newThreadId`, `fromMessageId`, optional display metadata, tags, and metadata. It does not have a per-request fork-compaction field. Hosted fork compaction is controlled by the configured server-side agent and middleware pipeline.

`ForkThreadRequest` requires `fromMessageId`. `main` is protected from deletion. Recursive deletion requires both `recursive=true` and server configuration that allows recursive thread delete.

## Thread Runs

| Method | Route | Body | Success | Common failures |
| --- | --- | --- | --- | --- |
| `GET` | `/agents/{agentId}/sessions/{sid}/threads/{bid}/runs` | none | `200 List<ThreadRunDto>` | `404`, `400` |
| `GET` | `/agents/{agentId}/sessions/{sid}/threads/{bid}/runs/active` | none | `200 ThreadRunDto?` | `404`, `400` |
| `GET` | `/agents/{agentId}/sessions/{sid}/threads/{bid}/runs/{runtimeRunId}` | none | `200 ThreadRunDto` | `404`, `400` |

Thread run status values are currently `active`, `completed`, `cancelled`, and `failed`.

Only one active hosted thread run is allowed for a given `sessionId + threadId`.

## Agent Definitions

| Method | Route | Body | Success | Common failures |
| --- | --- | --- | --- | --- |
| `POST` | `/agents` | `CreateAgentRequest` | `201 StoredAgentDto` | `400` |
| `GET` | `/agents` | none | `200 List<AgentSummaryDto>` | `400` |
| `GET` | `/agents/{agentId}` | none | `200 StoredAgentDto` | `404`, `400` |
| `PUT` | `/agents/{agentId}` | `UpdateAgentRequest` | `200 StoredAgentDto` | `404`, `400` |
| `DELETE` | `/agents/{agentId}` | none | `204` | `404`, `400` |

Create and update validate `AgentConfig`. Listing returns summaries that omit config. Updating or deleting a stored definition evicts cached runtime agents for that `agentId`.

## Content

| Method | Route | Body | Success | Common failures |
| --- | --- | --- | --- | --- |
| `POST` | `/sessions/{sid}/threads/{bid}/content` | `multipart/form-data`, field `file` | `201 ContentDto` | `404`, `400` |
| `GET` | `/sessions/{sid}/threads/{bid}/content` | none | `200 List<ContentDto>` | `404`, `400` |
| `GET` | `/sessions/{sid}/threads/{bid}/content/{contentId}` | none | binary file | `404`, `400` |
| `DELETE` | `/sessions/{sid}/threads/{bid}/content/{contentId}` | none | `204` | `404`, `400` |

Default hosted content storage is in-memory. Do not assume durability unless the host configures durable content storage.

## Streaming And Responses

| Method | Route | Body or protocol | Success | Common failures |
| --- | --- | --- | --- | --- |
| `POST` | `/agents/{agentId}/sessions/{sid}/threads/{bid}/inputs` | `StreamTextRequest` or input event envelope | `202` | `400`, `404`, `409`, `500` |
| `GET` | `/agents/{agentId}/sessions/{sid}/threads/{bid}/events/live` | SSE | `text/event-stream` | `404` before headers |
| `POST` | `/agents/{agentId}/sessions/{sid}/threads/{bid}/interrupt` | optional interruption envelope or reason body | `202` | `404`, `409`, `400`, `500` |
| `GET` | `/agents/{agentId}/sessions/{sid}/threads/{bid}/ws` | WebSocket text frames | WebSocket stream | pre-upgrade `400`, `404`, `409`; post-upgrade invalid-payload or policy-violation close statuses |
| `POST` | `/agents/{agentId}/sessions/{sid}/threads/{bid}/responses` | serialized `AgentEvent` envelope implementing `IResponseEvent` | `200` | `404`, `409`, `400` |

SSE sends one live event envelope per `data:` frame. WebSocket accepts text frames containing input envelopes or response event envelopes and sends live event envelopes back after a valid client frame initializes the subscription. HTTP clients post any serialized `AgentEvent` envelope implementing `IResponseEvent` to the single `/responses` route.

## Concurrency

Thread runs and thread operation locks are separate:

- thread run ownership allows one active hosted run per `sessionId + threadId`
- thread operation locks protect exclusive thread mutations such as deletion
- middleware responses require an active thread runtime but are not thread runs
