# Build A Multi-Agent Workflow

A workflow connects multiple agents into a graph.

Use a workflow when the work is naturally split across roles, stages, or handoffs.

This page is an orchestration detour after the single-agent basics. If you are still on the first path, finish [Save Sessions And State](persistence.md) and [ASP.NET Hosting](aspnet-hosting.md) first.

## Add The Package

```bash
dotnet add package HPD-Agent.MultiAgent --version 0.5.5
```

## Add Program.cs

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;
using HPD.MultiAgent;

var workflow = await AgentWorkflow.Create()
    .WithName("launch-note")
    .AddAgent("researcher", agent =>
    {
        agent.WithOpenAI(model: "gpt-5-mini")
             .WithInstructions("Find the most important points. Keep the research concise.");
    })
    .AddAgent("writer", agent =>
    {
        agent.WithOpenAI(model: "gpt-5-mini")
             .WithInstructions("Turn the research into a short, friendly launch note.");
    })
    .From("researcher").To("writer")
    .BuildAsync();

using var started = workflow.Subscribe<WorkflowAgentStartedEvent>(evt =>
    Console.WriteLine($"{evt.AgentId} started"));

using var completed = workflow.Subscribe<WorkflowAgentCompletedEvent>(evt =>
    Console.WriteLine($"{evt.AgentId} finished"));

var result = await workflow.RunAsync("Explain why HPD Agent sessions and threads matter.");

if (!result.Success)
{
    throw new InvalidOperationException(result.Error, result.Exception);
}

Console.WriteLine(result.FinalAnswer);
```

Run it:

```bash
dotnet run
```

## What Happens

`AgentWorkflow.Create()` starts a workflow graph.

`AddAgent(...)` adds named agent nodes.

`From("researcher").To("writer")` connects the researcher output to the writer input.

`RunAsync(...)` starts at the graph entry node and follows the graph until the workflow completes.

Workflow events let a UI or log show which node is running while the workflow executes.

## Workflow Or Subagent

Use a workflow when the orchestration is explicit and graph-shaped.

Use a subagent when one agent should call another specialist as a tool during its own turn.

## Next

Next: expose an agent over HTTP in [ASP.NET Hosting](aspnet-hosting.md), or return to the broader [Getting Started](index.md) path.

Go deeper: for workflow patterns, routing, handoffs, events, and configuration, see [Multi-Agent Overview](../guides/multi-agent/overview.md).
