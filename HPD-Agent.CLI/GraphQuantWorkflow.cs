using HPD.Agent;
using HPDAgent.Graph.Abstractions.Graph;
using HPDAgent.Graph.Abstractions.Handlers;
using HPDAgent.Graph.Abstractions.Execution;
using HPDAgent.Graph.Abstractions.Context;
using HPDAgent.Graph.Core.Builders;
using HPDAgent.Graph.Core.Context;
using HPDAgent.Graph.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace AgentConsoleTest;

/// <summary>
/// REAL Graph-Based Multi-Agent Consensus Workflow
///
/// This uses HPD.Graph for orchestration with proper:
/// - Graph nodes and edges
/// - Conditional routing
/// - Parallel execution via graph layers
/// - Channel-based state management
///
/// Workflow:
///                    START
///                      │
///          ┌───────────┼───────────┐
///          ▼           ▼           ▼
///      Solver1     Solver2     Solver3  (Layer 1 - Parallel)
///          │           │           │
///          └───────────┼───────────┘
///                      ▼
///                  Verifier             (Layer 2)
///                      ▼
///                   Router              (Layer 3 - Conditional)
///         ┌────────────┼────────────┐
///         ▼            ▼            ▼
///   (consensus)  (disagree)   (max_retries)
///    Return      Feedback         END
///      │             │
///      END      (loops back to solvers)
/// </summary>
public static class GraphQuantWorkflow
{
    public static async Task<string> RunAsync(string question)
    {
        AnsiConsole.MarkupLine("[cyan]🧮 Graph-Based Multi-Agent Consensus Workflow[/]");
        AnsiConsole.WriteLine();

        // Step 1: Create the graph
        var graph = BuildConsensusGraph();

        // Step 2: Create solver and verifier agents
        var solver1 = await CreateSolverAgent("Solver-1", "Method 1: Analytical approach");
        var solver2 = await CreateSolverAgent("Solver-2", "Method 2: Verification approach");
        var solver3 = await CreateSolverAgent("Solver-3", "Method 3: Alternative approach");
        var verifier = await CreateVerifierAgent();

        // Step 3: Setup DI with handlers
        var services = new ServiceCollection()
            .AddSingleton(solver1)
            .AddSingleton(solver2)
            .AddSingleton(solver3)
            .AddSingleton(verifier)
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new Solver1Handler(solver1))
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new Solver2Handler(solver2))
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new Solver3Handler(solver3))
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new VerifierHandler(verifier))
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new RouterHandler())
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new ReturnAnswerHandler())
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new FeedbackHandler())
            .BuildServiceProvider();

        // Step 4: Create graph context and set initial state
        var context = new GraphContext("consensus-exec", graph, services);

        // Initialize state channels
        context.Channels["question"].Set(question);
        context.Channels["retry_count"].Set(0);
        context.Channels["has_consensus"].Set(false);
        context.Channels["final_answer"].Set("");

        // Step 5: Execute the graph
        var orchestrator = new GraphOrchestrator<GraphContext>(services);
        await orchestrator.ExecuteAsync(context);

        // Step 6: Return result
        return context.Channels["final_answer"].Get<string>();
    }

    private static Graph BuildConsensusGraph()
    {
        var builder = new GraphBuilder()
            .WithName("QuantConsensusWorkflow")
            .WithVersion("1.0.0")
            .WithMaxIterations(10); // Allow up to 10 iterations (3 retries with feedback)

        // START node
        builder.AddStartNode();

        // Solver nodes (will execute in parallel - Layer 1)
        builder.AddHandlerNode("solver1", "Solver1Handler", "Quant Solver 1");
        builder.AddHandlerNode("solver2", "Solver2Handler", "Quant Solver 2");
        builder.AddHandlerNode("solver3", "Solver3Handler", "Quant Solver 3");

        // Verifier node (Layer 2)
        builder.AddHandlerNode("verifier", "VerifierHandler", "Consensus Verifier");

        // Router node (Layer 3 - decides next step)
        builder.AddHandlerNode("router", "RouterHandler", "Consensus Router");

        // Return answer node
        builder.AddHandlerNode("return", "ReturnAnswerHandler", "Return Answer");

        // Feedback node (for retry loop)
        builder.AddHandlerNode("feedback", "FeedbackHandler", "Provide Feedback");

        // END node
        builder.AddEndNode();

        // Edges: START → All 3 solvers (creates parallel layer)
        builder.AddEdge("START", "solver1");
        builder.AddEdge("START", "solver2");
        builder.AddEdge("START", "solver3");

        // Edges: All solvers → Verifier (graph waits for all 3)
        builder.AddEdge("solver1", "verifier");
        builder.AddEdge("solver2", "verifier");
        builder.AddEdge("solver3", "verifier");

        // Edge: Verifier → Router
        builder.AddEdge("verifier", "router");

        // Conditional edges from router
        builder.AddEdge("router", "return", edge =>
        {
            edge.WithCondition(new EdgeCondition
            {
                Type = ConditionType.FieldEquals,
                Field = "has_consensus",
                Value = true
            });
        });

        builder.AddEdge("router", "feedback", edge =>
        {
            edge.WithCondition(new EdgeCondition
            {
                Type = ConditionType.FieldEquals,
                Field = "has_consensus",
                Value = false
            });
        });

        // Edge: Feedback → back to solvers (creates cycle)
        builder.AddEdge("feedback", "solver1");
        builder.AddEdge("feedback", "solver2");
        builder.AddEdge("feedback", "solver3");

        // Edge: Return → END
        builder.AddEdge("return", "END");

        return builder.Build();
    }

    private static async Task<Agent> CreateSolverAgent(string name, string methodGuidance)
    {
        var config = new AgentConfig
        {
            Name = name,
            MaxAgenticIterations = 20,
            SystemInstructions = $@"You are a quantitative analyst solving mathematical problems.

{methodGuidance}

When solving:
1. Show your work step-by-step
2. Double-check calculations
3. Provide a clear final answer
4. If given feedback, reconsider your approach",
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", "openai/gpt-5.2")
            .Build();
    }

    private static async Task<Agent> CreateVerifierAgent()
    {
        var config = new AgentConfig
        {
            Name = "Consensus-Verifier",
            MaxAgenticIterations = 10,
            SystemInstructions = @"You verify consensus among 3 solver agents.

Compare the 3 answers carefully. They must give the same final numerical answer or conclusion.

ALWAYS respond in this EXACT format:
- 'CONSENSUS: [the agreed answer]' if all 3 fundamentally agree
- 'DISAGREEMENT: [clear explanation]' if they differ",
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", "anthropic/claude-4.5")
            .Build();
    }
}

#region Graph Node Handlers

public class Solver1Handler : IGraphNodeHandler<GraphContext>
{
    private readonly Agent _agent;
    public string HandlerName => "Solver1Handler";

    public Solver1Handler(Agent agent) => _agent = agent;

    public async Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var question = context.Channels["question"].Get<string>();
        var feedbackMessage = inputs.TryGet<string>("feedback", out var fb) ? fb : "";

        var prompt = string.IsNullOrEmpty(feedbackMessage)
            ? question
            : $"{question}\n\nFeedback: {feedbackMessage}\nPlease reconsider.";

        AnsiConsole.MarkupLine("[dim]  Solver 1 working...[/]");
        var answer = await GetAgentAnswer(_agent, prompt);
        AnsiConsole.MarkupLine("[green]  ✓[/] [dim]Solver 1 completed[/]");

        // Store answer in output
        var outputs = new Dictionary<string, object>
        {
            ["answer"] = answer,
            ["solver_name"] = "Solver 1"
        };

        return NodeExecutionResult.Success.Single(
            output: outputs,
            duration: TimeSpan.FromSeconds(1),
            metadata: new NodeExecutionMetadata()
        );
    }

    private static async Task<string> GetAgentAnswer(Agent agent, string question)
    {
        var answer = "";
        await foreach (var evt in agent.RunAsync(question))
        {
            if (evt is TextDeltaEvent textEvt)
            {
                answer += textEvt.Text;
            }
        }
        return answer.Trim();
    }
}

public class Solver2Handler : IGraphNodeHandler<GraphContext>
{
    private readonly Agent _agent;
    public string HandlerName => "Solver2Handler";

    public Solver2Handler(Agent agent) => _agent = agent;

    public async Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var question = context.Channels["question"].Get<string>();
        var feedbackMessage = inputs.TryGet<string>("feedback", out var fb) ? fb : "";

        var prompt = string.IsNullOrEmpty(feedbackMessage)
            ? question
            : $"{question}\n\nFeedback: {feedbackMessage}\nPlease reconsider.";

        AnsiConsole.MarkupLine("[dim]  Solver 2 working...[/]");
        var answer = await GetAgentAnswer(_agent, prompt);
        AnsiConsole.MarkupLine("[green]  ✓[/] [dim]Solver 2 completed[/]");

        var outputs = new Dictionary<string, object>
        {
            ["answer"] = answer,
            ["solver_name"] = "Solver 2"
        };

        return NodeExecutionResult.Success.Single(
            output: outputs,
            duration: TimeSpan.FromSeconds(1),
            metadata: new NodeExecutionMetadata()
        );
    }

    private static async Task<string> GetAgentAnswer(Agent agent, string question)
    {
        var answer = "";
        await foreach (var evt in agent.RunAsync(question))
        {
            if (evt is TextDeltaEvent textEvt)
            {
                answer += textEvt.Text;
            }
        }
        return answer.Trim();
    }
}

public class Solver3Handler : IGraphNodeHandler<GraphContext>
{
    private readonly Agent _agent;
    public string HandlerName => "Solver3Handler";

    public Solver3Handler(Agent agent) => _agent = agent;

    public async Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var question = context.Channels["question"].Get<string>();
        var feedbackMessage = inputs.TryGet<string>("feedback", out var fb) ? fb : "";

        var prompt = string.IsNullOrEmpty(feedbackMessage)
            ? question
            : $"{question}\n\nFeedback: {feedbackMessage}\nPlease reconsider.";

        AnsiConsole.MarkupLine("[dim]  Solver 3 working...[/]");
        var answer = await GetAgentAnswer(_agent, prompt);
        AnsiConsole.MarkupLine("[green]  ✓[/] [dim]Solver 3 completed[/]");

        var outputs = new Dictionary<string, object>
        {
            ["answer"] = answer,
            ["solver_name"] = "Solver 3"
        };

        return NodeExecutionResult.Success.Single(
            output: outputs,
            duration: TimeSpan.FromSeconds(1),
            metadata: new NodeExecutionMetadata()
        );
    }

    private static async Task<string> GetAgentAnswer(Agent agent, string question)
    {
        var answer = "";
        await foreach (var evt in agent.RunAsync(question))
        {
            if (evt is TextDeltaEvent textEvt)
            {
                answer += textEvt.Text;
            }
        }
        return answer.Trim();
    }
}

public class VerifierHandler : IGraphNodeHandler<GraphContext>
{
    private readonly Agent _agent;
    public string HandlerName => "VerifierHandler";

    public VerifierHandler(Agent agent) => _agent = agent;

    public async Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        // Get all 3 answers from inputs
        var allInputs = inputs.GetAll().ToList();
        var answers = allInputs
            .Where(kvp => kvp.Key == "answer")
            .Select(kvp => kvp.Value.ToString())
            .ToList();

        AnsiConsole.MarkupLine("\n[cyan]Answers received:[/]");
        for (int i = 0; i < answers.Count; i++)
        {
            var shortAnswer = answers[i]!.Length > 100
                ? answers[i]!.Substring(0, 97) + "..."
                : answers[i];
            AnsiConsole.MarkupLine($"  [dim]Solver {i + 1}:[/] {Markup.Escape(shortAnswer!)}");
        }

        var prompt = $@"Compare these 3 answers:

Solver 1: {answers[0]}
Solver 2: {answers[1]}
Solver 3: {answers[2]}";

        AnsiConsole.MarkupLine("\n[yellow]Verifying consensus...[/]");
        var verification = "";
        await foreach (var evt in _agent.RunAsync(prompt))
        {
            if (evt is TextDeltaEvent textEvt)
            {
                verification += textEvt.Text;
            }
        }

        // Parse verification
        bool hasConsensus = verification.StartsWith("CONSENSUS:", StringComparison.OrdinalIgnoreCase);
        var message = hasConsensus
            ? verification.Substring("CONSENSUS:".Length).Trim()
            : verification.Substring("DISAGREEMENT:".Length).Trim();

        // Update channels
        context.Channels["has_consensus"].Set(hasConsensus);
        context.Channels["verification_message"].Set(message);

        if (hasConsensus)
        {
            context.Channels["final_answer"].Set(message);
        }

        var outputs = new Dictionary<string, object>
        {
            ["has_consensus"] = hasConsensus,
            ["message"] = message
        };

        return NodeExecutionResult.Success.Single(
            output: outputs,
            duration: TimeSpan.FromSeconds(1),
            metadata: new NodeExecutionMetadata()
        );
    }
}

public class RouterHandler : IGraphNodeHandler<GraphContext>
{
    public string HandlerName => "RouterHandler";

    public Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var hasConsensus = context.Channels["has_consensus"].Get<bool>();
        var retryCount = context.Channels["retry_count"].Get<int>();

        // Check max retries
        if (!hasConsensus && retryCount >= 3)
        {
            AnsiConsole.MarkupLine("[red]Max retries (3) exceeded - no consensus[/]");
            context.Channels["final_answer"].Set("Unable to reach consensus after 3 attempts");
            hasConsensus = true; // Force to return path
        }

        var outputs = new Dictionary<string, object>
        {
            ["has_consensus"] = hasConsensus
        };

        return Task.FromResult<NodeExecutionResult>(NodeExecutionResult.Success.Single(
            output: outputs,
            duration: TimeSpan.FromMilliseconds(1),
            metadata: new NodeExecutionMetadata()
        ));
    }
}

public class ReturnAnswerHandler : IGraphNodeHandler<GraphContext>
{
    public string HandlerName => "ReturnAnswerHandler";

    public Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var answer = context.Channels["final_answer"].Get<string>();
        var retryCount = context.Channels["retry_count"].Get<int>();

        AnsiConsole.MarkupLine($"\n[green] CONSENSUS REACHED (Round {retryCount + 1})![/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan bold]Final Answer:[/] {Markup.Escape(answer)}");

        return Task.FromResult<NodeExecutionResult>(NodeExecutionResult.Success.Single(
            output: new Dictionary<string, object>(),
            duration: TimeSpan.FromMilliseconds(1),
            metadata: new NodeExecutionMetadata()
        ));
    }
}

public class FeedbackHandler : IGraphNodeHandler<GraphContext>
{
    public string HandlerName => "FeedbackHandler";

    public Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var feedbackMessage = context.Channels["verification_message"].Get<string>();
        var retryCount = context.Channels["retry_count"].Get<int>();

        // Increment retry count
        context.Channels["retry_count"].Set(retryCount + 1);

        AnsiConsole.MarkupLine($"\n[yellow] Disagreement detected (Attempt {retryCount + 1}/3)[/]");
        AnsiConsole.MarkupLine($"[dim]Feedback: {Markup.Escape(feedbackMessage)}[/]");
        AnsiConsole.MarkupLine("[yellow]Retrying with feedback...[/]\n");

        // Pass feedback to next iteration
        var outputs = new Dictionary<string, object>
        {
            ["feedback"] = feedbackMessage
        };

        return Task.FromResult<NodeExecutionResult>(NodeExecutionResult.Success.Single(
            output: outputs,
            duration: TimeSpan.FromMilliseconds(1),
            metadata: new NodeExecutionMetadata()
        ));
    }
}

#endregion
