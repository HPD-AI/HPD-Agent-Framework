#:package HPD-Agent.Framework@0.5.5
#:package HPD-Agent.Providers.OpenAI@0.5.5
#:package HPD-Agent.MultiAgent@0.5.5
#:property TargetFramework=net10.0

// This sample connects two agents into one workflow.

using HPD.Agent;
using HPD.Agent.Providers.OpenAI;
using HPD.MultiAgent;

// A workflow is a directed graph of agents. Here the researcher produces
// source material, then the writer turns that material into the final answer.
var workflow = await AgentWorkflow.Create()
                .WithName("launch-note")
                .AddAgent("researcher", agent =>
                {
                    agent.WithInstructions("Find the most important points. Keep the research concise.")
                        .WithOpenAI("gpt-5-mini");
                })
                .AddAgent("writer", agent =>
                {
                    agent.WithInstructions("Turn the research into a short, friendly launch note.")
                        .WithOpenAI("gpt-5-mini");
                })
                .From("researcher").To("writer")
                .BuildAsync();

// Workflow events are optional, but they are useful for progress output,
// tracing, and building UIs that show which node is currently running.
using var workflowStarted = workflow.Subscribe<WorkflowStartedEvent>(evt =>
{
    Console.WriteLine($"{evt.WorkflowName} started with {evt.NodeCount} agents");
});

using var agentStarted = workflow.Subscribe<WorkflowAgentStartedEvent>(evt =>
{
    Console.WriteLine($"{evt.AgentId} started");
});

using var agentCompleted = workflow.Subscribe<WorkflowAgentCompletedEvent>(evt =>
{
    Console.WriteLine($"{evt.AgentId} finished");
});

using var edgeTraversed = workflow.Subscribe<WorkflowEdgeTraversedEvent>(evt =>
{
    Console.WriteLine($"{evt.FromNodeId} -> {evt.ToNodeId}");
});

using var workflowCompleted = workflow.Subscribe<WorkflowCompletedEvent>(evt =>
{
    Console.WriteLine($"{evt.WorkflowName} finished in {evt.Duration.TotalSeconds:N1}s");
});

// RunAsync starts at the graph entry node and follows edges until the workflow
// completes. The final answer comes from the terminal agent.
var result = await workflow.RunAsync("Explain why HPD Agent sessions and threads matter.");

if (!result.Success)
{
    // Surface workflow failures as normal .NET exceptions in this sample.
    throw new InvalidOperationException(result.Error, result.Exception);
}

Console.WriteLine(result.FinalAnswer);
