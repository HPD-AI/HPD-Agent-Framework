# Workflow Events

Multi-agent workflows emit a live stream that combines graph-level workflow events and normal child-agent events. Use these events when a UI, TUI, host, or dashboard needs to show workflow progress instead of only the final result.

## Projection Layers

Render multi-agent runs in three layers:

1. Workflow graph projection: workflow start/end, node start/end, skipped nodes, layer events, and edge traversal.
2. Child agent runtime projection: normal `AgentEvent` values emitted by the agent running inside a node.
3. Parent tool projection: when a workflow is invoked through `[MultiAgent]`, the parent stream has a normal tool call wrapper with workflow events nested beneath it.

```text
Parent agent
  tool draft_and_review
    DraftAndReview workflow
      draft node
        child agent text/tool events
      review node
        child agent text/tool events
```

## Subscribe Or Iterate

Subscribe before running if projection code should live outside the execution loop:

```csharp
using HPD.Agent;
using HPD.MultiAgent;

using var started = workflow.Subscribe<WorkflowStartedEvent>(evt =>
{
    ui.StartWorkflow(evt.WorkflowName, evt.NodeCount);
});

using var nodeStarted = workflow.Subscribe<WorkflowAgentStartedEvent>(evt =>
{
    ui.StartNode(evt.WorkflowName, evt.AgentId, evt.AgentName);
});

using var childEvents = workflow.SubscribeAny(evt =>
{
    if (evt is AgentEvent agentEvent && evt is not WorkflowStartedEvent)
    {
        ui.AppendAgentEvent(agentEvent);
    }
});

await foreach (var _ in workflow.ExecuteStreamingAsync(
    "Explain what changed in this thread.",
    parentCoordinator: null,
    parentAgentMetadata: null,
    parentChatClient: chatClient,
    cancellationToken: CancellationToken.None))
{
    // Subscriptions handle rendering while the stream is drained.
}
```

The returned async stream and workflow subscriptions expose the same public projection. Render from one path, or de-duplicate if you use both.

Use `RunAsync(...)` when you only need the final workflow result. Use `ExecuteStreamingAsync(...)` when you need streaming iteration, parent event bubbling, parent metadata, or parent chat-client inheritance.

## Event Families

| Event | Use it for |
| --- | --- |
| `WorkflowStartedEvent` | Create the workflow timeline; use `WorkflowName`, `NodeCount`, and `LayerCount` |
| `WorkflowCompletedEvent` | Mark workflow completion; use duration and success/failure/skipped counts |
| `WorkflowAgentStartedEvent` | Start a workflow node row or card |
| `WorkflowAgentCompletedEvent` | Complete a node and show output, progress, duration, or error |
| `WorkflowAgentSkippedEvent` | Show a skipped node caused by routing or conditions |
| `WorkflowEdgeTraversedEvent` | Show route decisions in graph/debug views |
| `WorkflowLayerStartedEvent` | Start a layer group in parallel or layer-based execution |
| `WorkflowLayerCompletedEvent` | Complete a layer group |
| `WorkflowDiagnosticEvent` | Render or log graph diagnostics |

Child agent events pass through as their normal event types. A workflow node can contain text deltas, reasoning events, tool call events, permission requests, structured result events, retries, and diagnostics.

Checkpointing does not add a separate public checkpoint event family. When checkpointing is enabled, checkpoint saves are storage side effects. They may appear as diagnostic messages, but do not rely on a structured `CheckpointSaved` event in the workflow stream.

## Metadata

Workflow-level events carry workflow metadata. `WorkflowAgentStartedEvent` and `WorkflowAgentCompletedEvent` describe a node through their own `AgentId` and `AgentName` properties, but their `Metadata` points to the workflow context.

Child agent events carry child agent metadata:

- `Metadata.AgentChain` shows the nested agent path.
- `Metadata.Depth` identifies nesting depth.
- `MessageId` groups text and reasoning.
- `CallId` groups tool activity.
- request ids such as `PermissionId` group bidirectional interactions.

Use workflow event fields for graph structure. Use child `AgentEvent` metadata for runtime output inside a node.

## Parent Agent Tools

When a parent agent exposes a workflow with `[MultiAgent]`, the parent sees it as a normal tool call with `CallType = MultiAgent`. In the source-generated streaming path, the generated wrapper calls `ExecuteStreamingAsync(...)` with the parent event coordinator, parent metadata, and parent chat client. Workflow events and child events then bubble into the parent stream.

`[MultiAgent]` has `StreamEvents = true` by default. If streaming is disabled, the generated wrapper uses `RunAsync(...)`, so the parent stream does not receive the same live workflow projection.

Generated multi-agent capability tools require permission by default. A parent permission request can appear before workflow events.

## Public Event Surface

The public workflow stream contains selected workflow events plus normal child agent events:

| Runtime moment | Public event |
| --- | --- |
| workflow starts | `WorkflowStartedEvent` |
| workflow completes | `WorkflowCompletedEvent` |
| agent node starts | `WorkflowAgentStartedEvent` |
| agent node completes | `WorkflowAgentCompletedEvent` |
| agent node is skipped | `WorkflowAgentSkippedEvent` |
| parallel layer starts | `WorkflowLayerStartedEvent` |
| parallel layer completes | `WorkflowLayerCompletedEvent` |
| route is traversed | `WorkflowEdgeTraversedEvent` |
| workflow diagnostic is emitted | `WorkflowDiagnosticEvent` |
| child agent emits an event | the child `AgentEvent` |

Other lower-level runtime events are not projected into the public workflow stream.

## Persistence Caveat

Workflow events are live runtime events unless a persistence path maps them into thread history. Do not assume every workflow diagnostic, route, or child event is durable on the parent thread. If your product needs durable workflow replay, validate the session/thread policy and event persistence behavior for that workflow.

## Related Pages

- [Build A Multi-Agent Workflow](build-a-workflow.md)
- [Data Flow Between Nodes](data-flow-between-nodes.md)
- [Routing And Handoffs](routing-and-handoffs.md)
- [Multi-Agent Capabilities](../tools/multi-agent-capabilities.md)
- [Event Streams And Hierarchies](../../concepts/event-streams-and-hierarchies.md)
