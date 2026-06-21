# Events

Events are the common vocabulary for HPD Agent runtime activity. Local subscriptions, hosted SSE, hosted WebSocket, TUI runtimes, bot adapters, middleware, and thread history all use related event types.

This page is family-based. It is not an exhaustive generated schema reference.

## Live Event Envelope

Hosted SSE and WebSocket use `AgentEventSerializer` envelopes:

```json
{
  "version": "1.0",
  "type": "TEXT_DELTA",
  "text": "hello",
  "messageId": "message-id"
}
```

The envelope includes injected `version` and `type` fields. Built-in type names use `SCREAMING_SNAKE_CASE`. Payload properties are camelCase and null values are omitted.

Unknown or missing `type` values cannot be deserialized as known events. HTTP input treats invalid event envelopes as bad requests. WebSocket closes invalid event frames with an invalid-payload close status.

Custom event types must be registered with the event serializer, normally through source generation, and need JSON metadata for AOT-safe serialization. See [Custom Events](../guides/events/custom-events.md).

For the practical serialization path, see [Serialization And Registration](../guides/events/serialization-and-registration.md).

## Channels, Direction, And Persistence

Event channel and direction help route and render events. They are not persistence guarantees.

Common channel meanings:

| Channel | Typical use |
| --- | --- |
| Streaming | text and reasoning deltas |
| Interactive | permission, continuation, clarification, and client-tool request/response flows |
| Control | thread-run lifecycle and interruption handling |
| Default or inherited | lifecycle, diagnostic, and framework-specific events that do not explicitly set a channel |

Response events for permission, continuation, and clarification use upstream direction.

Thread persistence is separate. Events are written to thread history only when thread conversion maps them or the event type opts in to thread persistence. A live event may be important to a UI without being durable thread history.

## Correlation And Projection Fields

Events arrive as a stream. Clients can project that stream into transcripts, timelines, workflow views, prompt queues, or trace trees by using correlation fields when they are present.

Common fields:

| Field | Meaning |
| --- | --- |
| `sessionId`, `threadId` | Durable runtime scope when present |
| `eventFlowId` | Flow grouping used by thread history and replay; often a message-turn flow for persisted events |
| `traceId` | Live execution trace id for a message turn |
| `spanId`, `parentSpanId` | Span-style hierarchy when present |
| `metadata` | Agent attribution, including agent name, id, parent id, chain, and depth |
| `messageId` | Text, reasoning, and tool activity under a message |
| `callId` | Tool-call lifecycle grouping |
| `permissionId`, `continuationId`, `requestId` | Request/response grouping through `IRequestCorrelatedEvent.RequestId` |

Do not require every event to have every field. For example, some tool events carry `traceId` and `callId` without explicit span fields. Use the most specific available id for the projection you are building. See [Event Streams And Hierarchies](../concepts/event-streams-and-hierarchies.md) and [Render An Event Stream](../guides/sessions-and-streaming/render-an-event-stream.md).

## Thread Event JSON Caveat

Live event envelopes and durable thread event JSON are different surfaces.

Live envelopes always include `version` and `type`, and can include routing and correlation fields such as session id, thread id, channel, direction, event flow id, metadata, and trace/span fields when those values are present on the event.

Durable thread event JSON omits many of those fields. Use live envelopes for API/SSE/WebSocket examples. Use thread event documents only when documenting storage or thread projection behavior.

## Event Families

| Family | Examples | Primary channel | Thread persistence |
| --- | --- | --- | --- |
| Input | `UserMessagesInputEvent`; hosted routes may also accept convenience text bodies and interruption requests | upstream/control | input handling surface; do not assume all are durable |
| Hosted thread run | `ThreadRunStartedEvent`, `ThreadRunCompletedEvent` | control | durable |
| Message turn lifecycle | `MessageTurnStartedEvent`, `MessageTurnFinishedEvent`, `MessageTurnErrorEvent` | default/control | start and finish are durable; failures may be represented through thread failure conversion |
| Agent turn lifecycle | `AgentTurnStartedEvent`, `AgentTurnFinishedEvent` | default | live/runtime-oriented |
| Text streaming | `TextMessageStartEvent`, `TextDeltaEvent`, `TextMessageEndEvent` | streaming | durable |
| Reasoning streaming | `ReasoningMessageStartEvent`, `ReasoningDeltaEvent`, `ReasoningMessageEndEvent` | streaming | durable |
| Tool calls | `ToolCallStartEvent`, `ToolCallArgsEvent`, `ToolCallResultEvent`, `ToolCallEndEvent` | default/streaming | durable |
| Workflow execution | `WorkflowStartedEvent`, `WorkflowAgentStartedEvent`, `WorkflowEdgeTraversedEvent`, `WorkflowCompletedEvent` | default/diagnostic | validate persistence for the workflow path |
| Background work | `ToolCallBackgroundTask*`, `BackgroundOperationStartedEvent`, `BackgroundOperationStatusEvent` | control/default | task events and selected operation statuses are durable |
| Interactive middleware | request events implementing `IRequestEvent` and response events implementing `IResponseEvent` | interactive | generally live only |
| Retry and error policy | `FunctionRetryEvent`, `ModelCallRetryEvent`, middleware error events | diagnostic/default | generally live only |
| Compaction observability | `CompactionEvent` | default/diagnostic | live middleware event; not the durable projection instruction |
| Thread-history compaction | `ThreadHistoryCompactedEvent` | thread history | durable when hard retention is applied |
| Content and references | upload/reference events | default/diagnostic | depends on event and content persistence policy |
| Audio transcripts | `UserAudioTranscriptDeltaEvent`, `UserAudioTranscriptCompletedEvent`, `UserAudioTranscriptFailedEvent` | streaming/default | transcript text projection is policy-dependent; raw audio is not durable by default |
| Assistant audio output | assistant audio output, stream, artifact, failure, and playback events | default/diagnostic | live/runtime-oriented unless explicitly projected by audio runtime policy |
| Struct events | `AgentStructEvent` samples on `StructEventHub` routes | process-local/export | not hosted or durable by default |
| Planning, schema, structured output | framework diagnostics and state changes | default/diagnostic | event-specific |

`CompactionStatus.CacheHit` exists as an enum value, but current public docs should not describe cached compaction reuse as active middleware behavior.

## Struct Events

Struct events are a separate process-local surface for realtime samples. HPD Agent-owned samples implement `AgentStructEvent`, which is an agent-level marker over the lower HPD Events struct-event contract. They do not inherit from `AgentEvent`, do not use `AgentEventSerializer`, and do not appear in hosted SSE/WebSocket streams or thread history unless a component explicitly converts or summarizes them as an `AgentEvent`.

Selected struct events can be exported with `AgentStructEventSerializer`. That serializer uses a separate envelope from hosted `AgentEvent` streaming and is for diagnostics, local capture, replay tooling, or telemetry export. Serializing a struct event does not make it visible to the TypeScript client or durable thread history.

Use `agent.ObserveStruct<TEvent>(...)` or `agent.StructEvents.Route<TEvent>()` for local observation. Tools that accept `FunctionExecutionContext` can access `context.StructEvents` for the same process-local sample lanes. Use this for high-volume diagnostics such as audio playout samples or queue-depth telemetry, not for semantic workflow facts, permissions, or persisted progress.

## Assistant Audio Output Events

Assistant audio output events use the same live envelope as other `AgentEvent` values. The event payload includes `sessionId` when the event has a session scope, plus audio-specific identifiers such as `outputFlowId`, `responseId`, `segmentId`, and provider or playback fields.

Source-registered assistant audio output type names include:

| Type | Purpose |
| --- | --- |
| `ASSISTANT_AUDIO_OUTPUT_STARTED` | Assistant audio output flow started. |
| `ASSISTANT_AUDIO_OUTPUT_STREAM_STARTED` | A synthesized audio stream segment started. |
| `ASSISTANT_AUDIO_OUTPUT_CHUNK_READY` | An audio chunk is ready. |
| `ASSISTANT_AUDIO_PUSH_TEXT_STREAM_OPENING` | Push-text TTS stream is opening. |
| `ASSISTANT_AUDIO_PUSH_TEXT_STREAM_OPENED` | Push-text TTS stream opened. |
| `ASSISTANT_AUDIO_PUSH_TEXT_INPUT_SENT` | Text input was sent to a push-text TTS stream. |
| `ASSISTANT_AUDIO_OUTPUT_STREAM_COMPLETED` | An audio stream segment completed. |
| `ASSISTANT_AUDIO_OUTPUT_ARTIFACT_CAPTURED` | Audio output was captured as an artifact. |
| `ASSISTANT_AUDIO_OUTPUT_SEGMENT_FAILED` | One audio segment failed. |
| `ASSISTANT_AUDIO_OUTPUT_COMPLETED` | Assistant audio output flow completed. |
| `ASSISTANT_AUDIO_OUTPUT_FAILED` | Assistant audio output flow failed. |
| `ASSISTANT_AUDIO_PLAYBACK_QUEUED` | Audio playback was queued. |
| `ASSISTANT_AUDIO_PLAYBACK_STARTED` | Audio playback started. |
| `ASSISTANT_AUDIO_PLAYBACK_PROGRESS` | Audio playback progress was reported. |
| `ASSISTANT_AUDIO_PLAYBACK_COMPLETED` | Audio playback completed. |
| `ASSISTANT_AUDIO_PLAYBACK_INTERRUPTED` | Audio playback was interrupted. |
| `ASSISTANT_AUDIO_PLAYBACK_FAILED` | Audio playback failed. |

Value round-trip tests cover `sessionId`, output flow, response, segment, chunk, artifact, error, and playback fields for these event families.

## UI Rendering Guidance

For text output, render `TextMessageStartEvent`, append `TextDeltaEvent`, and close the block on `TextMessageEndEvent`.

For reasoning output, use the same start/delta/end pattern but render it separately from final user-visible text.

For tool calls, use start/args/result/end events to show activity, arguments, and results. Tool events may arrive interleaved with streaming text depending on the model and middleware.

For interactive middleware, render request events and answer with the matching `IResponseEvent` through WebSocket, the hosted HTTP `/responses` route, or `agent.RespondAsync(...)` in a local app. Permission, continuation, clarification, and client-tool responses are coordination events, not ordinary output. See [Bidirectional Events](../guides/events/bidirectional-events.md).

For JavaScript and TypeScript apps, use `AgentClient` to load thread history, subscribe to live events, handle unknown custom events through `onAny(...)`, and send response events back to the hosted runtime. See [TypeScript Client Events](../guides/events/typescript-client.md).

For retry events, be careful with already-rendered partial output. A `ModelCallRetryEvent` can arrive after partial model content, so UIs may need to clear or mark stale partial text before rendering retry output.

For lifecycle, retry, and failure rendering, see [Lifecycle, Retry, And Error Events](../guides/events/lifecycle-retry-and-error-events.md).

For thread runs, use thread-run started/completed events to track active hosted work. Do not use thread operation lock conflicts as thread-run state.

For compaction, distinguish live observability from durable projection. `CompactionEvent` can explain why middleware skipped or performed compaction. `ThreadHistoryCompactedEvent` changes the thread projection by removing durable compacted messages and inserting replacements when present.

For workflows, render workflow lifecycle, layer, node, edge, and diagnostic events as a timeline or graph projection. Child agent, tool, and permission events may also appear in the same live stream when the workflow or subagent execution bubbles events through a parent coordinator.

For tool progress emitted from inside a function body, see [Tool And Function Events](../guides/events/tool-and-function-events.md).

## Persistence Summary

Persisting families verified for first docs:

- thread-run started and completed
- message-turn started and finished
- text streaming start, delta, and end
- reasoning start, delta, and end
- tool start, args, result, and end
- tool background task events
- background operation started, plus terminal or meaningful status events

Live-only or caveated families:

- audio transcript streaming is not durable by default
- interactive request/response events are live bidirectional flows
- workflow and subagent child-event persistence depends on the execution path and session/thread policy
- observability and diagnostic events generally do not persist unless explicitly mapped
- compaction observability is live, while thread-history compaction is durable only when hard retention is applied
- message-turn errors have special thread failure conversion behavior rather than the same simple persistence override as successful turn events
