# Sessions, Threads, And Events

HPD Agent uses sessions, threads, and events as the durable coordination model for agent work. They are not only chat-history storage. The same model is used by normal turns, hosted runtimes, subagents, multi-agent workflows, content projection, compaction, replay, and UI navigation.

The short model is:

```text
session: the workspace container for metadata and shared middleware state
thread: a replayable transcript path with thread state, tree metadata, and event history
event: the live and durable vocabulary for rendering, replay, tools, middleware, and nested work
```

In direct in-process code, the built `Agent` owns the runtime and methods such as `CreateSessionAsync(...)`, `RunAsync(...)`, and `ForkThreadAsync(...)` operate against the configured session store.

In ASP.NET Core hosted applications, a runtime scope is selected by:

```text
agentId + sessionId + threadId
```

The `agentId` selects the hosted agent definition or runtime build path. The `sessionId` and `threadId` select the state and thread path used by that runtime agent.

## Sessions

A session is the top-level durable scope for an interaction. It has:

- an id
- creation and last-activity timestamps
- metadata
- session-scoped middleware state

Creating a session also creates the default thread named `main`. Session metadata updates are merge patches: new keys are added, existing keys are overwritten, and null values remove keys.

Use sessions for identity, grouping, UI lists, and state that should be shared across threads. For example, permission choices that apply across a user's thread tree are session-scoped middleware state.

A session can also represent a product workspace. Bots may map a platform thread to one session. A multi-agent workflow can stamp session metadata such as `workspaceKind`, `workflowName`, `workflowExecutionId`, and `conversationMode` so a UI can list the whole workflow run without loading every thread. A normal single-agent chat might use simpler metadata such as customer id, project id, or platform key.

## Threads

A thread is the replayable path inside a session. A thread has:

- an id
- optional name, description, tags, and metadata
- fork metadata such as parent thread, fork point, ancestors, sibling position, and total forks
- thread-scoped middleware state
- a durable thread event log

Threads are the unit for forking, replay, thread-specific state, and hosted runtime ownership. The default `main` thread is protected from deletion.

Forking creates a new thread from a message id on an existing thread. Thread-scoped persistent middleware state is copied at fork time and then diverges between threads.

Threads are also how HPD keeps nested agent work understandable. A subagent can use the parent thread, create a fresh thread, or fork from the parent thread. The default subagent policy forks from the parent session into a hidden child thread, so the child gets the parent's current context without interleaving its transcript with the parent. The child thread metadata points back to the parent session, parent thread, tool call, and subagent run.

Multi-agent workflows use the same thread model, but the application chooses the policy. A workflow can write every node into one shared thread, give each node its own thread, or fork one thread per node from a root workflow thread. Thread metadata marks the workflow name, execution id, node id, agent id, and conversation mode, which gives UI and replay code something stable to group around.

Media-heavy features make this thread scope visible. For example, audio transcript projection, content uploads, and assistant audio artifacts use the active `sessionId + threadId` so replay and forks can keep durable text, media references, and artifacts attached to the correct path. See [Content Upload And Resolution](../guides/content/content-upload-and-resolution.md) for the upload and resolver flow.

## How Features Use The Model

| Feature | Session Use | Thread Use |
|---------|-------------|------------|
| Normal chat | User, case, project, or platform thread container | Active transcript and thread-scoped middleware state |
| Forking | Keeps alternatives grouped together | Creates a new path from a message id |
| Subagents | Parent, new, or shared session depending on policy | Parent, existing, fresh, or forked child thread |
| Multi-agent workflows | Workflow/workspace container with run metadata | Shared, per-agent, or forked node transcripts |
| Compaction | Shared policy and session-scoped state can remain stable | Model-visible history, durable history, or fork target can be reduced |
| Content and audio | Groups durable references by workspace/session | Attaches uploads, artifacts, transcript projection, and replay to one path |
| Hosted runtimes | `agentId + sessionId` selects the runtime scope | `threadId` selects the active path and thread run |

## Thread History Projection

Threads load as projections from durable thread events. The thread event log is the ordered record; the projected thread is the current messages, thread state, and metadata rebuilt from that record.

Thread-history compaction is one projection event family. Default soft compaction preserves durable thread history while reducing what the next model sees. Hard thread-history compaction can remove durable messages from the projected thread and can insert replacement messages such as summaries.

Fork compaction is especially important for nested work. A subagent or multi-agent node can fork from a rich parent/root thread while starting from a smaller model-visible history. That lets a child or node inherit the useful context without copying every durable message into its prompt.

## Events

Events are the framework vocabulary for input, streaming output, lifecycle, tool calls, interactive middleware, retry, background work, and thread-run tracking.

There are two related event surfaces:

- live agent events, used by local subscriptions, hosted SSE, WebSocket, TUI runtimes, and bot adapters
- durable thread events, used to reconstruct thread history and thread projections

Live event envelopes can include routing and correlation fields such as `sessionId`, `threadId`, `channel`, `direction`, trace ids, and metadata when those values are present. Durable thread event JSON is intentionally different and omits many live-routing fields.

Not every live event is written to thread history. Persistence is controlled by each event type's persistence policy and thread-event conversion, not by the event's channel.

This distinction matters for nested agents and workflows. A parent live stream can show subagent or workflow activity as it happens, while the durable transcript may live on a child thread or workflow node thread. Use event metadata for live hierarchy, and use session/thread metadata for durable lookup.

## Runtime Scope Across Hosts

The same model applies across runtime surfaces:

- local agents use the same event vocabulary through typed subscriptions
- hosted APIs expose sessions, threads, thread runs, inputs, SSE, WebSocket, and the bidirectional response route
- TUI runtimes bind UI state to a runtime scope
- bot adapters map platform conversation identity to a session and thread before invoking an agent
- subagents use policy to decide whether child work stays on the parent thread, forks, or moves to another session
- multi-agent workflows use conversation policy to decide where node-agent transcripts are written

This means UI and integration code should treat events as the common rendering and coordination language, while treating hosting routes and local subscriptions as transport choices.

## Concurrency Model

Thread runs and thread operation locks are separate controls.

A hosted thread run is the active model/tool execution for a thread. Only one active hosted run is allowed for a given `sessionId + threadId`, regardless of route `agentId`. A second simultaneous input submission to the same thread returns a conflict, while submissions to different threads can run at the same time.

A thread operation lock protects exclusive thread mutations. Current hosting code uses the explicit thread operation lock for thread deletion. Some other thread operations use broader session-level coordination. Treat operation-lock conflicts as thread mutation conflicts, not as active agent runs.

Middleware responses are also separate from thread runs. A permission, continuation, clarification, or client-tool response is accepted only when the target thread runtime is active and a waiter can accept that response.

Shared thread policies need additional coordination. If two agents write to the same durable thread at once, their thread snapshots and thread middleware state can race. HPD serializes same-thread multi-agent node runs at the conversation route. Separate threads can still run in parallel.

## Related Pages

- [Thread History And Forking](../guides/sessions-and-streaming/thread-history-and-forking.md)
- [Compaction](../guides/sessions-and-streaming/compaction.md)
- [Subagents](../guides/agents/subagents.md)
- [Multi-Agent Conversation Policies](../guides/multi-agent/conversation-policies.md)
- [Hosted Streaming API](../guides/hosting/hosted-streaming-api.md)
- [Hosted Endpoints](../reference/hosted-endpoints.md)
- [Events Reference](../reference/events.md)
- [Audio Overview](../guides/audio/overview.md)
- [Live Evaluation](../guides/evaluations/live-evaluation.md)
