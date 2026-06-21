# Build A Multi-Agent Workflow

This page shows the normal construction path for workflow graphs. For model-callable workflow tools, see [Multi-Agent Capabilities](../tools/multi-agent-capabilities.md).

## Add Agents

A workflow can contain config-backed agents, prebuilt agents, or agents configured inline.

```csharp
var workflow = await AgentWorkflow.Create()
    .WithName("ResearchAndWrite")
    .AddAgent("research", new AgentConfig
    {
        Name = "Researcher",
        SystemInstructions = "Collect the key facts needed to answer the request.",
    }, node => node.WithOutputKey("research"))
    .AddAgent("write", new AgentConfig
    {
        Name = "Writer",
        SystemInstructions = "Write a concise final answer using the provided research.",
    }, node => node.WithInputKey("research"))
    .From("research").To("write")
    .BuildAsync();
```

`AgentWorkflow.Create()` returns the fluent builder. `BuildAsync()` creates an `AgentWorkflowInstance`, which is the runnable workflow object used by `RunAsync(...)`, `ExecuteStreamingAsync(...)`, subscriptions, and config export.

Agent insertion order is not the execution order. Declare edges for the order you want. A workflow with two unrelated agents and no declared edge has two entry nodes, not an automatic `first -> second` chain.

## Construction Forms

Use `AgentConfig` when the workflow should build the child agent at execution time:

```csharp
.AddAgent("review", new AgentConfig
{
    Name = "Reviewer",
    SystemInstructions = "Review the answer for accuracy.",
})
```

Use a prebuilt `Agent` when the child has already been constructed:

```csharp
.AddAgent("review", reviewAgent)
```

Use inline builder configuration when the node needs local builder customization:

```csharp
.AddAgent("review", builder =>
{
    builder.WithInstructions("Review the answer for accuracy.");
    builder.WithToolHarness<ReviewTools>();
})
```

Config-backed and inline-built agents are created lazily when the workflow executes. If they do not configure their own provider, they can inherit the parent chat client passed to `ExecuteStreamingAsync(...)`.

## Conversation Policy

Workflow outputs are separate from durable conversation history. Add a conversation policy when each node agent's transcript should be saved into HPD sessions and threads:

```csharp
var workflow = await AgentWorkflow.Create()
    .WithName("ResearchAndWrite")
    .WithSessionStore(new JsonSessionStore("App_Data/sessions"))
    .WithConversation(MultiAgentConversationPolicies.ForkThreadPerAgent())
    .AddAgent("research", researchConfig, node => node.WithOutputKey("research"))
    .AddAgent("write", writeConfig, node => node.WithInputKey("research"))
    .From("research").To("write")
    .BuildAsync();
```

`WithSessionStore(...)` stores conversations and threads. It is different from `WithJsonWorkflowStore(...)`, which stores workflow definitions and checkpoints.

Use `SharedWorkflowThread()` for one transcript, `ThreadPerAgent()` for durable agent-local threads, or `ForkThreadPerAgent()` when agents should start from the same request and write into separate threads.

## Edges

Edges define the graph:

```csharp
.From("research").To("write")
```

Multiple sources and targets are allowed:

```csharp
.From("draft", "facts").To("review")
```

If you do not declare a node's incoming edge, HPD adds an edge from `START`. If you do not declare a node's outgoing edge, HPD adds an edge to `END`.

```text
START -> first node without incoming declared edge
last node without outgoing declared edge -> END
```

You can still declare `START` and `END` yourself when you want the entry and exit points to be explicit:

```csharp
.From("START").To("classify")
.From("write").To("END")
```

Use `.From(...).To(...)` for sequencing whenever one agent must run after another.

## Execute

Subscribe before execution if the application needs live events:

```csharp
using var events = workflow.SubscribeAny(evt =>
{
    timeline.Project(evt);
});

await foreach (var evt in workflow.ExecuteStreamingAsync(
    "Compare the two implementation options.",
    parentCoordinator: null,
    parentAgentMetadata: null,
    parentChatClient: chatClient,
    cancellationToken: CancellationToken.None))
{
    // Render from this stream, or from subscriptions, but avoid double-rendering both.
}
```

`ExecuteStreamingAsync(...)` emits workflow graph events and child agent events. `RunAsync(...)` drains the workflow and returns a `WorkflowResult`.

## Runtime Options

Node options control input, output, structured result handling, retry, timeout, context, approvals, and handoff targets.

```csharp
.AddAgent("analyze", analyzerConfig, node => node
    .WithInputKey("research")
    .WithOutputKey("analysis")
    .WithTimeout(TimeSpan.FromSeconds(30))
    .WithRetry(maxAttempts: 3)
    .WithInstructions("Prefer concrete evidence over speculation."))
```

`WithTimeout(...)` is applied to the graph node and the child agent run. `WithRetry(...)` is wired through the graph node retry policy.

Error-mode helpers such as `OnErrorFallback(...)` are part of the configuration/export surface, but validate execution behavior in your workflow before relying on them as runtime policy.

## Checkpointing

Checkpointing is opt-in. Enable it on the workflow and choose a workflow store when the execution should keep resumable checkpoints.

```csharp
var workflow = await AgentWorkflow.Create()
    .WithName("DurableReview")
    .WithCheckpointing()
    .WithInMemoryWorkflowStore()
    .AddAgent("draft", draftConfig, node => node.WithOutputKey("draft"))
    .AddAgent("review", reviewConfig, node => node.WithInputKey("draft"))
    .From("draft").To("review")
    .BuildAsync();
```

For file-backed development storage:

```csharp
.WithJsonWorkflowStore(
    rootDirectory: "App_Data/workflows",
    retentionMode: MultiAgentCheckpointRetention.LatestOnly)
```

Use `MultiAgentCheckpointRetention.FullHistory` when the application needs every checkpoint for debugging or audit history.

## Related Pages

- [Choose A Composition Pattern](choose-a-pattern.md)
- [Execution Model](execution-model.md)
- [Workflow Patterns](workflow-patterns.md)
- [Data Flow Between Nodes](data-flow-between-nodes.md)
- [Routing And Handoffs](routing-and-handoffs.md)
- [Conversation Policies](conversation-policies.md)
- [Checkpointing](checkpointing.md)
- [Workflow Events](workflow-events.md)
