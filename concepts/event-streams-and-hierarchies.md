# Event Streams And Hierarchies

HPD Agent emits events as a stream. A console can print that stream in arrival order, a chat UI can project it into messages, a TUI can project it into panels, and an orchestration view can project it into a tree of workflows, agents, tools, and middleware prompts.

The important model is:

```text
event stream: ordered runtime events
event projection: the view your app builds from those events
hierarchy: one useful projection, built from correlation fields
```

Do not assume every event is born as a perfect tree node. Some events carry full span data, some carry message or tool ids, some carry agent metadata, and some are live-only coordination events. Use the fields that match the view you are building.

## Flat Stream, Structured View

A live stream might arrive like this:

```text
WorkflowStartedEvent
WorkflowAgentStartedEvent
MessageTurnStartedEvent
TextMessageStartEvent
TextDeltaEvent
ToolCallStartEvent
PermissionRequestEvent
PermissionResponseEvent
ToolCallResultEvent
ToolCallEndEvent
TextMessageEndEvent
WorkflowAgentCompletedEvent
WorkflowCompletedEvent
```

A UI can project the same stream like this:

```text
DraftAndReview workflow
  Drafter node
    message turn
      assistant message
      tool call
        permission request
        result
  workflow complete
```

That projection is a client responsibility. HPD gives you the event families and correlation fields; your app decides whether to render a transcript, timeline, trace tree, approval queue, or debug log.

## Correlation Fields

Use these fields to build projections:

| Field | Use it for |
| --- | --- |
| `SessionId` and `ThreadId` | Durable runtime scope and thread history lookup |
| `EventFlowId` | Thread/replay grouping, often a message-turn flow in persisted events |
| `TraceId` | Live execution trace for a message turn |
| `SpanId` and `ParentSpanId` | Span-style parent/child relationships when present |
| `Metadata.AgentChain` | Agent/workflow ancestry labels |
| `Metadata.Depth` | Nesting depth for agent-attributed events |
| `MessageId` | Text, reasoning, and tool activity under one assistant message |
| `CallId` | Tool-call lifecycle grouping |
| `PermissionId` | Permission request/response grouping |
| `WorkflowName`, `AgentId`, `LayerIndex`, `FromNodeId`, `ToNodeId` | Workflow timeline and graph rendering |

`TraceId`, `SpanId`, and `ParentSpanId` are strongest for turn, iteration, and tool-start hierarchy. Not every event has a span. Tool args, result, and end events should usually be grouped with their `CallId`. `EventFlowId` is useful for thread history and replay, but it is not the same thing as span hierarchy.

## Common Projections

| Projection | Primary fields |
| --- | --- |
| Chat transcript | `MessageId`, text/reasoning start/delta/end events |
| Tool activity | `CallId`, `ToolHarnessName`, `CallType`, `Name` |
| Permission queue | `PermissionId`, `CallId`, `FunctionName`, interactive channel |
| Workflow timeline | `WorkflowName`, workflow node ids, layer events, edge events |
| Agent tree | `Metadata.AgentChain`, `Metadata.Depth`, `Metadata.ParentAgentId` |
| Trace view | `TraceId`, `SpanId`, `ParentSpanId` |
| Thread replay | `SessionId`, `ThreadId`, `EventFlowId`, thread event sequence |

For most product UIs, start with transcript and tool activity. Add workflow and agent projections only when the app actually exposes workflows, subagents, or multi-agent capabilities.

## Workflows And Subagents

Subagents and workflow capabilities are still tools from the parent agent's point of view. Their parent run can emit normal tool events with `CallType` set to `SubAgent` or `MultiAgent`. The child execution can also emit its own events into the parent event path when the runtime links the child coordinator to the parent coordinator.

Workflows add dedicated workflow events such as:

- `WorkflowStartedEvent`
- `WorkflowCompletedEvent`
- `WorkflowAgentStartedEvent`
- `WorkflowAgentCompletedEvent`
- `WorkflowAgentSkippedEvent`
- `WorkflowEdgeTraversedEvent`
- `WorkflowLayerStartedEvent`
- `WorkflowLayerCompletedEvent`
- `WorkflowDiagnosticEvent`

Generated workflow and subagent wrappers forward parent context for event coordination. Keep exact model-mediated permission and child-event assertions in your own integration tests until your app validates the full flow it depends on.

## Live Events And Durable History

Live events and durable thread events are related, but they are not the same surface.

Live streams are for rendering current activity and handling interactive requests. Durable thread events are for replaying thread history and reconstructing thread projections. A live event may be important to a UI without being written to thread history.

Subagent durable history also depends on the subagent execution policy. A child may use the parent session, a fresh thread, a forked thread, an existing thread, or a new session. Document product behavior in terms of the policy you chose rather than assuming all child events persist on the parent thread.

## Rendering Rule

Prefer this order when building a nested view:

```text
session + thread
  event flow or trace
    agent/workflow metadata
      message
        tool call
          interactive request
```

When a field is missing, fall back to the nearest useful parent. For example, a `ToolCallResultEvent` may not have span data, but it has `CallId`, so attach it to the matching `ToolCallStartEvent`.

## Related Pages

- [Streaming Events](../getting-started/streaming-events.md)
- [Render An Event Stream](../guides/sessions-and-streaming/render-an-event-stream.md)
- [Workflow Events](../guides/multi-agent/workflow-events.md)
- [Events Reference](../reference/events.md)
