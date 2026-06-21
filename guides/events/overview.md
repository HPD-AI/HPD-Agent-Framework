# Events Overview

Events are the runtime language of HPD Agent. They are how local apps, hosted clients, TUIs, bot adapters, middleware, sessions, workflows, and subagents observe what is happening during a run.

The simplest event experience is streaming text. The broader event model lets you build:

- transcripts from text and reasoning events
- tool timelines from tool-call events
- permission prompts from bidirectional interactive events
- workflow timelines from workflow events
- subagent and multi-agent views from metadata and child events
- trace views from trace/span fields
- thread replay from durable thread events

## What To Read

Start here:

- [Streaming Events](../../getting-started/streaming-events.md): first local subscription.
- [Event Streams And Hierarchies](../../concepts/event-streams-and-hierarchies.md): the mental model for flat streams and nested projections.
- [Render An Event Stream](../sessions-and-streaming/render-an-event-stream.md): practical rendering guidance for UIs and clients.
- [Tool And Function Events](tool-and-function-events.md): render tool calls and emit progress from tool code.
- [Bidirectional Events](bidirectional-events.md): request/response event flows for permissions, clarification, and host decisions.
- [TypeScript Client Events](typescript-client.md): consume live streams, thread history, custom events, and interactive requests from TypeScript apps.
- [Custom Events](custom-events.md): define app-specific events, emit them from middleware, and stream them through the serializer.
- [Serialization And Registration](serialization-and-registration.md): live envelopes, custom discriminators, and AOT metadata.
- [Live Vs Durable Events](live-vs-durable-events.md): what streams live and what becomes thread history.
- [Lifecycle, Retry, And Error Events](lifecycle-retry-and-error-events.md): turn lifecycle, retries, and failure rendering.
- [Testing Event-Driven Code](testing-event-driven-code.md): capture and assert event-driven behavior.
- [Logging And Telemetry](../observability/logging-and-telemetry.md): export agent events and usage into logs and metrics.

Then use focused pages:

- [Workflow Events](../multi-agent/workflow-events.md): render workflow progress, nodes, layers, and routes.
- [Permissions Middleware](../middleware/permissions.md): answer bidirectional permission events.
- [Hosted Streaming API](../hosting/hosted-streaming-api.md): receive events over SSE or WebSocket.
- [Thread History And Forking](../sessions-and-streaming/thread-history-and-forking.md): read durable thread event history.
- [Events Reference](../../reference/events.md): event families, envelopes, and persistence caveats.

## Core Rule

HPD sends events linearly. Your app projects them into the shape it needs.

```text
live event stream
  -> transcript
  -> tool timeline
  -> permission queue
  -> workflow tree
  -> subagent view
  -> trace/debug log
```

For hierarchy, use the most specific correlation fields available: `MessageId` for message content, `CallId` for tools, request ids such as `PermissionId` for bidirectional flows, workflow node ids for workflows, agent metadata for child-agent labels, and trace/span fields for trace views.

Do not treat live rendering as persistence. Durable thread history only includes events that are mapped or opt in to thread persistence.
