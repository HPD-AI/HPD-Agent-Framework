# Checkpointing

Checkpointing lets a multi-agent workflow save execution state through a workflow store. It is opt-in.

The workflow store is separate from the session store. Checkpointing saves workflow progress; conversation policies save node agent transcripts into HPD sessions and threads.

## Enable Checkpointing

Enable checkpointing on the workflow and choose a store:

```csharp
var workflow = await AgentWorkflow.Create()
    .WithName("DurableReview")
    .WithCheckpointing()
    .WithJsonWorkflowStore("App_Data/workflows")
    .AddAgent("draft", draftConfig, node => node.WithOutputKey("draft"))
    .AddAgent("review", reviewConfig, node => node.WithInputKey("draft"))
    .From("draft").To("review")
    .BuildAsync();
```

Use `WithInMemoryWorkflowStore()` for tests and short-lived runs:

```csharp
.WithCheckpointing()
.WithInMemoryWorkflowStore()
```

Use `WithJsonWorkflowStore(...)` when local workflow definitions and checkpoints should survive process restarts:

```csharp
.WithCheckpointing()
.WithJsonWorkflowStore(
    rootDirectory: "App_Data/workflows",
    retentionMode: MultiAgentCheckpointRetention.LatestOnly)
```

## Retention

`MultiAgentCheckpointRetention.LatestOnly` keeps only the latest checkpoint for a workflow execution.

```csharp
.WithJsonWorkflowStore(
    "App_Data/workflows",
    MultiAgentCheckpointRetention.LatestOnly)
```

`MultiAgentCheckpointRetention.FullHistory` keeps every checkpoint.

```csharp
.WithJsonWorkflowStore(
    "App_Data/workflows",
    MultiAgentCheckpointRetention.FullHistory)
```

Use `LatestOnly` for normal durability. Use `FullHistory` for debugging, audit trails, and workflow-development traces.

## Events

Checkpoint saves are storage side effects. Multi-agent workflows do not expose a separate public checkpoint event family.

You may see diagnostic workflow events related to storage or execution, but product code should rely on the public workflow event families:

- `WorkflowStartedEvent`
- `WorkflowAgentStartedEvent`
- `WorkflowAgentCompletedEvent`
- `WorkflowAgentSkippedEvent`
- `WorkflowEdgeTraversedEvent`
- `WorkflowLayerStartedEvent`
- `WorkflowLayerCompletedEvent`
- `WorkflowCompletedEvent`
- `WorkflowDiagnosticEvent`

See [Workflow Events](workflow-events.md).

## Resume Boundary

Checkpoint storage is available through the multi-agent workflow builder. A higher-level resume API for multi-agent workflows should be documented only by the host or runtime surface that exposes it.

For direct `AgentWorkflowInstance` execution, treat checkpointing as execution durability and diagnostics storage. Do not promise process-restart resume behavior in product docs unless the host surface being used exposes and tests that flow.

## Related Pages

- [Build A Multi-Agent Workflow](build-a-workflow.md)
- [Conversation Policies](conversation-policies.md)
- [Config And Export](config-and-export.md)
- [Workflow Events](workflow-events.md)
