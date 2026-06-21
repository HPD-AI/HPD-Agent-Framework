# Choose A Composition Pattern

HPD gives you several ways to compose capabilities. Pick the smallest surface that gives the application the control it needs.

## Quick Choice

| Use | When The Decision Belongs To | Best For |
| --- | --- | --- |
| Native tool | The model | One host-side capability, such as search, file lookup, or a domain operation |
| Client tool | The model, with execution in a connected client | UI actions, editor commands, desktop/mobile capabilities, or SDK-side integrations |
| Subagent | The parent model | Delegating to a specialist as a normal tool call |
| Multi-agent workflow | The application or a workflow router | Fixed stages, routing, fan-out, review gates, and explicit role topology |
| `[MultiAgent]` capability | The parent model, after the app defines the workflow | Letting an agent choose when to run a whole workflow as one tool |

## Normal Tools

Use a normal tool when the parent agent should call a single operation and get one result back.

```csharp
public sealed class TicketTools
{
    [AIFunction]
    public Task<Ticket> GetTicket(string id) => tickets.GetAsync(id);
}
```

Normal tools are the easiest surface to reason about. They execute inside the host process, unless the tool is an externally executed client tool.

## Client Tools

Use a client tool when the capability belongs to a connected client runtime instead of the HPD host.

The model still sees a tool declaration. The host emits a client-tool request event. The connected client executes the action and returns the result through the bidirectional event channel.

Client tools are useful for UI actions like opening a panel, highlighting text, reading editor selection, or invoking platform APIs that only exist in the client.

See [Externally Executed Client Tools](../tools/externally-executed-client-tools.md).

## Subagents

Use a subagent when the parent model should decide when to delegate to another agent.

```csharp
public sealed class SpecialistTools
{
    [SubAgent]
    public SubAgent Researcher() =>
        SubAgent.FromConfig(
            "researcher",
            "Researches a question and returns cited notes.",
            researcherConfig);
}
```

The parent sees one generated tool with a `query` argument. HPD runs the child agent, streams child events through the parent event path, and returns the child answer as the tool result.

Subagents are strongest when thread policy matters:

- the child should fork from the parent thread
- the child should use a fresh thread in the same session
- the child should write into a shared specialist session
- the UI should show parent and child histories as related but separate work

See [Subagents](../agents/subagents.md).

## Multi-Agent Workflows

Use a multi-agent workflow when the application should define the shape of the work.

```csharp
var workflow = await AgentWorkflow.Create()
    .WithName("DraftAndReview")
    .AddAgent("draft", draftConfig, node => node.WithOutputKey("draft"))
    .AddAgent("review", reviewConfig, node => node.WithInputKey("draft"))
    .From("draft").To("review")
    .BuildAsync();
```

Multi-agent workflows are strongest when topology matters:

- a fixed sequence must happen
- several agents should run from the same upstream input
- a router should choose one downstream path
- a reviewer should approve, reject, or route work
- node outputs need explicit names
- events should render as a workflow timeline

See [Build A Multi-Agent Workflow](build-a-workflow.md).

## Multi-Agent Capabilities

Use `[MultiAgent]` when a parent agent should choose whether to run an entire workflow.

```csharp
public sealed class WorkflowTools
{
    [MultiAgent("Drafts and reviews an answer.", Name = "draft_and_review")]
    public Task<AgentWorkflowInstance> DraftAndReview() =>
        AgentWorkflow.Create()
            .WithName("DraftAndReview")
            .AddAgent("draft", draftConfig, node => node.WithOutputKey("draft"))
            .AddAgent("review", reviewConfig, node => node.WithInputKey("draft"))
            .From("draft").To("review")
            .BuildAsync();
}
```

The parent sees one workflow tool with an `input` argument. HPD executes the workflow and can bubble workflow events and child-agent events through the parent event stream.

See [Multi-Agent Capabilities](../tools/multi-agent-capabilities.md).

## Rule Of Thumb

Start with a normal tool. Move to a subagent when the operation is really another agent conversation. Move to a multi-agent workflow when the application needs explicit stages, routing, or parallel threads.

Use `[MultiAgent]` only after the workflow shape is useful on its own.

## Related Pages

- [Subagents](../agents/subagents.md)
- [Build A Multi-Agent Workflow](build-a-workflow.md)
- [Workflow Patterns](workflow-patterns.md)
- [Externally Executed Client Tools](../tools/externally-executed-client-tools.md)
