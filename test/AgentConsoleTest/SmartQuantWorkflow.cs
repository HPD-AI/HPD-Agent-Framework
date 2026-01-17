using HPD.Agent;
using HPDAgent.Graph.Abstractions.Graph;
using HPDAgent.Graph.Abstractions.Handlers;
using HPDAgent.Graph.Abstractions.Execution;
using HPDAgent.Graph.Abstractions.Context;
using HPDAgent.Graph.Core.Builders;
using HPDAgent.Graph.Core.Context;
using HPDAgent.Graph.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace AgentConsoleTest;

/// <summary>
/// Smart Quant Workflow with Classifier
///
/// Flow:
///                    START
///                      ↓
///                 Classifier  (determines: math or general)
///                      ↓
///         ┌────────────┴────────────┐
///         ▼                         ▼
///    [is_math_question]      [is_general_question]
///         ▼                         ▼
///   Multi-Agent              Single Agent
///   Consensus                Direct Answer
///   (3 solvers)
///         ↓                         ↓
///        END                       END
/// </summary>
public static class SmartQuantWorkflow
{
    public static async Task<string> RunAsync(string question)
    {
        AnsiConsole.MarkupLine("[cyan]🧠 Smart Workflow with Classifier[/]");
        AnsiConsole.WriteLine();

        // Build graph
        AnsiConsole.MarkupLine("[dim]Building intelligent routing graph...[/]");
        var graph = BuildSmartGraph();

        // Create agents
        AnsiConsole.MarkupLine("[dim]Creating classifier agent...[/]");
        var classifier = await CreateClassifierAgent();

        AnsiConsole.MarkupLine("[dim]Creating solver agents (GPT, Claude, Gemini)...[/]");
        var solver1 = await CreateSolver1Agent();
        var solver2 = await CreateSolver2Agent();
        var solver3 = await CreateSolver3Agent();

        AnsiConsole.MarkupLine("[dim]Creating verifier and general agent...[/]");
        var verifier = await CreateVerifierAgent();
        var generalAgent = await CreateGeneralAgent();

        AnsiConsole.MarkupLine("[green]✓[/] [dim]All agents ready[/]");
        AnsiConsole.WriteLine();

        // Setup DI
        var services = new ServiceCollection()
            .AddSingleton(classifier)
            .AddSingleton(solver1)
            .AddSingleton(solver2)
            .AddSingleton(solver3)
            .AddSingleton(verifier)
            .AddSingleton(generalAgent)
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new ClassifierHandler(classifier))
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new QuickSolver1Handler(solver1))
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new QuickSolver2Handler(solver2))
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new QuickSolver3Handler(solver3))
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new QuickVerifierHandler(verifier))
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new GeneralAgentHandler(generalAgent))
            .AddTransient<IGraphNodeHandler<GraphContext>>(sp => new QuickReturnHandler())
            .BuildServiceProvider();

        // Create context
        var context = new GraphContext("smart-exec", graph, services);
        context.Channels["question"].Set(question);
        context.Channels["is_math_question"].Set(false);
        context.Channels["final_answer"].Set("");

        // Execute
        AnsiConsole.MarkupLine("[yellow]Analyzing question...[/]");
        AnsiConsole.WriteLine();

        var orchestrator = new GraphOrchestrator<GraphContext>(services);
        await orchestrator.ExecuteAsync(context);

        return context.Channels["final_answer"].Get<string>();
    }

    private static Graph BuildSmartGraph()
    {
        var builder = new GraphBuilder()
            .WithName("SmartQuantWorkflow")
            .WithVersion("1.0.0")
            .WithMaxIterations(10);

        // START
        builder.AddStartNode();

        // Classifier node
        // AddHandlerNode(id, displayName, handlerName)
        builder.AddHandlerNode("classifier", "Question Classifier", "ClassifierHandler");

        // Math path nodes
        builder.AddHandlerNode("solver1", "Math Solver 1", "QuickSolver1Handler");
        builder.AddHandlerNode("solver2", "Math Solver 2", "QuickSolver2Handler");
        builder.AddHandlerNode("solver3", "Math Solver 3", "QuickSolver3Handler");
        builder.AddHandlerNode("verifier", "Consensus Verifier", "QuickVerifierHandler");

        // General path node
        builder.AddHandlerNode("general", "General Assistant", "GeneralAgentHandler");

        // Return node
        builder.AddHandlerNode("return", "Return Answer", "QuickReturnHandler");

        // END
        builder.AddEndNode();

        // Edges
        builder.AddEdge("START", "classifier");

        // Conditional routing from classifier
        builder.AddEdge("classifier", "solver1", edge =>
        {
            edge.WithCondition(new EdgeCondition
            {
                Type = ConditionType.FieldEquals,
                Field = "is_math_question",
                Value = true
            });
        });

        builder.AddEdge("classifier", "solver2", edge =>
        {
            edge.WithCondition(new EdgeCondition
            {
                Type = ConditionType.FieldEquals,
                Field = "is_math_question",
                Value = true
            });
        });

        builder.AddEdge("classifier", "solver3", edge =>
        {
            edge.WithCondition(new EdgeCondition
            {
                Type = ConditionType.FieldEquals,
                Field = "is_math_question",
                Value = true
            });
        });

        builder.AddEdge("classifier", "general", edge =>
        {
            edge.WithCondition(new EdgeCondition
            {
                Type = ConditionType.FieldEquals,
                Field = "is_math_question",
                Value = false
            });
        });

        // Math path to verifier
        builder.AddEdge("solver1", "verifier");
        builder.AddEdge("solver2", "verifier");
        builder.AddEdge("solver3", "verifier");

        // Both paths to return
        builder.AddEdge("verifier", "return");
        builder.AddEdge("general", "return");

        // Return to end
        builder.AddEdge("return", "END");

        return builder.Build();
    }

    private static async Task<Agent> CreateClassifierAgent()
    {
        var config = new AgentConfig
        {
            Name = "Classifier",
            MaxAgenticIterations = 1,
            SystemInstructions = @"You are a classifier. Your ONLY job is to output exactly one word.

OUTPUT RULES:
- Output ONLY the word 'MATH' or 'GENERAL'
- Do NOT solve the problem
- Do NOT explain anything
- Do NOT add any other text

CLASSIFICATION:
- MATH = any question involving numbers, calculations, percentages, money, statistics
- GENERAL = conversational, identity questions, non-numerical questions

CRITICAL: Your entire response must be exactly 4 letters: either 'MATH' or 'GENERAL'. Nothing else.",
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", "mistralai/mistral-small-creative")
            .Build();
    }

    private static readonly string SolverPrompt = @"You are a quantitative analyst.

Solve the problem step-by-step and provide a clear final answer.";

    private static async Task<Agent> CreateSolver1Agent()
    {
        var config = new AgentConfig
        {
            Name = "Solver-1-GPT",
            MaxAgenticIterations = 15,
            SystemInstructions = SolverPrompt,
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", "anthropic/claude-sonnet-4.5")
            .Build();
    }

    private static async Task<Agent> CreateSolver2Agent()
    {
        var config = new AgentConfig
        {
            Name = "Solver-2-Claude",
            MaxAgenticIterations = 15,
            SystemInstructions = SolverPrompt,
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", "openai/gpt-5.2")
            .Build();
    }

    private static async Task<Agent> CreateSolver3Agent()
    {
        var config = new AgentConfig
        {
            Name = "Solver-3-Gemini",
            MaxAgenticIterations = 15,
            SystemInstructions = SolverPrompt,
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", "google/gemini-3-pro-preview")
            .Build();
    }

    private static async Task<Agent> CreateVerifierAgent()
    {
        var config = new AgentConfig
        {
            Name = "Verifier",
            MaxAgenticIterations = 5,
            SystemInstructions = @"Compare 3 solver answers and return the consensus.

Just return the agreed answer directly (no prefix needed).",
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", "anthropic/claude-haiku-4.5")
            .Build();
    }

    private static async Task<Agent> CreateGeneralAgent()
    {
        var config = new AgentConfig
        {
            Name = "GeneralAssistant",
            MaxAgenticIterations = 10,
            SystemInstructions = @"You are a helpful AI assistant. Answer questions clearly and concisely.",
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", "anthropic/claude-3.5-sonnet")
            .Build();
    }
}

#region Handlers

public class ClassifierHandler : IGraphNodeHandler<GraphContext>
{
    private readonly Agent _agent;
    public string HandlerName => "ClassifierHandler";

    public ClassifierHandler(Agent agent) => _agent = agent;

    public async Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        // Get question from either inputs (if passed from previous node) or from channel (initial)
        var question = inputs.GetOrDefault<string?>("question", null)
            ?? context.Channels["question"].Get<string>();

        AnsiConsole.MarkupLine($"[dim]Classifier - Question: '{Markup.Escape(question ?? "<null>")}'[/]");

        var classification = "";
        var messages = new[] { new ChatMessage(ChatRole.User, question) };
        await foreach (var evt in _agent.RunAsync(messages))
        {
            if (evt is TextDeltaEvent textEvt)
            {
                classification += textEvt.Text;
            }
        }

        var trimmed = classification.Trim();
        AnsiConsole.MarkupLine($"[dim]Raw classification response: '{Markup.Escape(trimmed)}'[/]");

        // Validate response - should be exactly "MATH" or "GENERAL"
        bool isValidResponse = trimmed.Equals("MATH", StringComparison.OrdinalIgnoreCase)
                            || trimmed.Equals("GENERAL", StringComparison.OrdinalIgnoreCase);

        if (!isValidResponse)
        {
            AnsiConsole.MarkupLine("[red]⚠ Classifier did not return MATH or GENERAL![/]");
            AnsiConsole.MarkupLine("[yellow]Falling back to keyword detection...[/]");
        }

        // Check for MATH anywhere in response (fallback for non-compliant models)
        bool isMath = trimmed.Equals("MATH", StringComparison.OrdinalIgnoreCase)
                   || (!isValidResponse && classification.Contains("MATH", StringComparison.OrdinalIgnoreCase));

        // IMPORTANT: Set in BOTH channels AND outputs
        // - Channels: for other nodes to read
        // - Outputs: for EdgeCondition to evaluate AND pass to next nodes
        context.Channels["is_math_question"].Set(isMath);

        if (isMath)
        {
            AnsiConsole.MarkupLine("[cyan]→ Classified as:[/] [yellow]MATH[/] [dim](routing to 3-agent consensus)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[cyan]→ Classified as:[/] [green]GENERAL[/] [dim](routing to general assistant)[/]");
        }

        AnsiConsole.WriteLine();

        // EdgeCondition checks OUTPUTS, not channels!
        // ALSO pass the question forward to next nodes via outputs
        var outputs = new Dictionary<string, object>
        {
            ["is_math_question"] = isMath,
            ["question"] = question  // ← PASS QUESTION FORWARD!
        };

        return NodeExecutionResult.Success.Single(
            output: outputs,
            duration: TimeSpan.FromSeconds(1),
            metadata: new NodeExecutionMetadata()
        );
    }
}

public class GeneralAgentHandler : IGraphNodeHandler<GraphContext>
{
    private readonly Agent _agent;
    public string HandlerName => "GeneralAgentHandler";

    public GeneralAgentHandler(Agent agent) => _agent = agent;

    public async Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        // NOTE: No skip boilerplate needed!
        // The orchestrator now skips this handler when is_math_question=true

        // Get question from inputs (passed from ClassifierHandler) or fallback to channel
        var question = inputs.GetOrDefault<string>("question", null)
            ?? context.Channels["question"].Get<string>();

        AnsiConsole.MarkupLine("[dim]General assistant answering...[/]");

        var answer = "";
        var messages = new[] { new ChatMessage(ChatRole.User, question) };
        await foreach (var evt in _agent.RunAsync(messages))
        {
            if (evt is TextDeltaEvent textEvt)
            {
                answer += textEvt.Text;
            }
        }

        context.Channels["final_answer"].Set(answer.Trim());

        AnsiConsole.MarkupLine("[green]✓[/] [dim]Answer ready[/]");

        return NodeExecutionResult.Success.Single(
            output: new Dictionary<string, object>(),
            duration: TimeSpan.FromSeconds(1),
            metadata: new NodeExecutionMetadata()
        );
    }
}

public class QuickSolver1Handler : IGraphNodeHandler<GraphContext>
{
    private readonly Agent _agent;
    public string HandlerName => "QuickSolver1Handler";

    public QuickSolver1Handler(Agent agent) => _agent = agent;

    public async Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        // NOTE: No skip boilerplate needed!
        // The orchestrator now skips this handler when is_math_question=false

        // Get question from inputs (passed from ClassifierHandler)
        var question = inputs.Get<string>("question");

        var answer = "";
        var messages = new[] { new ChatMessage(ChatRole.User, question) };
        await foreach (var evt in _agent.RunAsync(messages))
        {
            if (evt is TextDeltaEvent textEvt)
            {
                answer += textEvt.Text;
            }
        }

        AnsiConsole.MarkupLine("[green]✓[/] [dim]Solver 1 completed[/]");

        // Use natural key "answer" - namespacing is automatic (becomes "solver1.answer")
        return NodeExecutionResult.Success.Single(
            output: new Dictionary<string, object> { ["answer"] = answer.Trim() },
            duration: TimeSpan.FromSeconds(1),
            metadata: new NodeExecutionMetadata()
        );
    }
}

public class QuickSolver2Handler : IGraphNodeHandler<GraphContext>
{
    private readonly Agent _agent;
    public string HandlerName => "QuickSolver2Handler";

    public QuickSolver2Handler(Agent agent) => _agent = agent;

    public async Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        // NOTE: No skip boilerplate needed!
        // The orchestrator now skips this handler when is_math_question=false

        // Get question from inputs (passed from ClassifierHandler)
        var question = inputs.Get<string>("question");

        var answer = "";
        var messages = new[] { new ChatMessage(ChatRole.User, question) };
        await foreach (var evt in _agent.RunAsync(messages))
        {
            if (evt is TextDeltaEvent textEvt)
            {
                answer += textEvt.Text;
            }
        }

        AnsiConsole.MarkupLine("[green]✓[/] [dim]Solver 2 completed[/]");

        // Use natural key "answer" - namespacing is automatic (becomes "solver2.answer")
        return NodeExecutionResult.Success.Single(
            output: new Dictionary<string, object> { ["answer"] = answer.Trim() },
            duration: TimeSpan.FromSeconds(1),
            metadata: new NodeExecutionMetadata()
        );
    }
}

public class QuickSolver3Handler : IGraphNodeHandler<GraphContext>
{
    private readonly Agent _agent;
    public string HandlerName => "QuickSolver3Handler";

    public QuickSolver3Handler(Agent agent) => _agent = agent;

    public async Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        // NOTE: No skip boilerplate needed!
        // The orchestrator now skips this handler when is_math_question=false

        // Get question from inputs (passed from ClassifierHandler)
        var question = inputs.Get<string>("question");

        var answer = "";
        var messages = new[] { new ChatMessage(ChatRole.User, question) };
        await foreach (var evt in _agent.RunAsync(messages))
        {
            if (evt is TextDeltaEvent textEvt)
            {
                answer += textEvt.Text;
            }
        }

        AnsiConsole.MarkupLine("[green]✓[/] [dim]Solver 3 completed[/]");

        // Use natural key "answer" - namespacing is automatic (becomes "solver3.answer")
        return NodeExecutionResult.Success.Single(
            output: new Dictionary<string, object> { ["answer"] = answer.Trim() },
            duration: TimeSpan.FromSeconds(1),
            metadata: new NodeExecutionMetadata()
        );
    }
}

public class QuickVerifierHandler : IGraphNodeHandler<GraphContext>
{
    private readonly Agent _agent;
    public string HandlerName => "QuickVerifierHandler";

    public QuickVerifierHandler(Agent agent) => _agent = agent;

    public async Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        // NOTE: No skip boilerplate needed!
        // The orchestrator skips this handler when GENERAL path is taken

        // Debug: Show source nodes that contributed inputs
        var sourceNodes = inputs.GetSourceNodeIds();
        AnsiConsole.MarkupLine($"[dim]Verifier received inputs from: {string.Join(", ", sourceNodes)}[/]");

        // Use pattern matching to collect all answers from solver nodes
        // Each solver outputs "answer" which becomes "solver1.answer", "solver2.answer", "solver3.answer"
        var answersWithSource = inputs.GetAllMatchingWithKeys<string>("*.answer");

        // Show each solver's answer with attribution
        AnsiConsole.MarkupLine($"\n[cyan]Solver Answers ({answersWithSource.Count}):[/]");
        foreach (var (key, value) in answersWithSource)
        {
            var solverName = key.Split('.')[0]; // Extract "solver1" from "solver1.answer"
            var truncated = value.Length > 200 ? value.Substring(0, 200) + "..." : value;
            AnsiConsole.MarkupLine($"[dim]{solverName}:[/] {Markup.Escape(truncated)}");
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine($"[yellow]Verifying consensus from {answersWithSource.Count} solvers...[/]");

        // Build prompt with solver attribution
        var answerLines = answersWithSource.Select(kv =>
        {
            var solverName = kv.Key.Split('.')[0];
            return $"{solverName}: {kv.Value}";
        });
        var prompt = $@"Here are {answersWithSource.Count} answers to the same question. Return the consensus answer:

{string.Join("\n", answerLines)}";

        var answer = "";
        var messages = new[] { new ChatMessage(ChatRole.User, prompt) };
        await foreach (var evt in _agent.RunAsync(messages))
        {
            if (evt is TextDeltaEvent textEvt)
            {
                answer += textEvt.Text;
            }
        }

        context.Channels["final_answer"].Set(answer.Trim());

        AnsiConsole.MarkupLine("[green]✓[/] [dim]Consensus reached[/]");

        return NodeExecutionResult.Success.Single(
            output: new Dictionary<string, object>(),
            duration: TimeSpan.FromSeconds(1),
            metadata: new NodeExecutionMetadata()
        );
    }
}

public class QuickReturnHandler : IGraphNodeHandler<GraphContext>
{
    public string HandlerName => "QuickReturnHandler";

    public Task<NodeExecutionResult> ExecuteAsync(
        GraphContext context,
        HandlerInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var answer = context.Channels["final_answer"].Get<string>();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[cyan bold]Answer:[/]");
        AnsiConsole.MarkupLine(Markup.Escape(answer));
        AnsiConsole.WriteLine();

        return Task.FromResult<NodeExecutionResult>(NodeExecutionResult.Success.Single(
            output: new Dictionary<string, object>(),
            duration: TimeSpan.FromMilliseconds(1),
            metadata: new NodeExecutionMetadata()
        ));
    }
}

#endregion
