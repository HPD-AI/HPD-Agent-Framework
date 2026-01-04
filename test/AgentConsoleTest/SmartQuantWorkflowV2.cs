using HPD.Agent;
using HPD.Events;
using HPD.MultiAgent;
using HPDAgent.Graph.Abstractions.Context;
using HPDAgent.Graph.Abstractions.Events;
using Spectre.Console;

namespace AgentConsoleTest;

/// <summary>
/// Smart Quant Workflow V2 - Using HPD.MultiAgent
///
/// This is the equivalent of SmartQuantWorkflow but using the new
/// AgentWorkflowBuilder API instead of raw HPD.Graph handlers.
///
/// Flow:
///                    START
///                      |
///                 Classifier  (determines: math or general)
///                      |
///         +------------+------------+
///         v                         v
///    [is_math_question]      [is_general_question]
///         v                         v
///   Multi-Agent              Single Agent
///   Consensus                Direct Answer
///   (3 solvers)
///         |                         |
///        END                       END
/// </summary>
public static class SmartQuantWorkflowV2
{
    public static async Task<string> RunAsync(string question)
    {
        AnsiConsole.MarkupLine("[cyan]V2 Smart Workflow with HPD.MultiAgent[/]");
        AnsiConsole.WriteLine();

        // Create agents
        AnsiConsole.MarkupLine("[dim]Creating agents...[/]");

        var classifier = await CreateClassifierAgent();
        var solver1 = await CreateSolverAgent("Solver-1-Claude", "anthropic/claude-sonnet-4.5");
        var solver2 = await CreateSolverAgent("Solver-2-GPT", "openai/gpt-5.2");
        var solver3 = await CreateSolverAgent("Solver-3-Gemini", "google/gemini-3-pro-preview");
        var verifier = await CreateVerifierAgent();
        var generalAgent = await CreateGeneralAgent();

        AnsiConsole.MarkupLine("[green]All agents ready[/]");
        AnsiConsole.WriteLine();

        // Build workflow using the fluent API
        AnsiConsole.MarkupLine("[dim]Building workflow graph...[/]");

        var workflow = await new AgentWorkflowBuilder()
            .WithName("SmartQuantV2")

            // Add all agents
            .AddAgent("classifier", classifier)
            .AddAgent("solver1", solver1)
            .AddAgent("solver2", solver2)
            .AddAgent("solver3", solver3)
            .AddAgent("verifier", verifier)
            .AddAgent("general", generalAgent)

            // Entry: START → classifier
            .From("START").To("classifier")

            // Edges from classifier with conditions (parallel to solvers if MATH)
            .From("classifier").To("solver1").WhenContains("answer", "MATH")
            .From("classifier").To("solver2").WhenContains("answer", "MATH")
            .From("classifier").To("solver3").WhenContains("answer", "MATH")
            .From("classifier").To("general").WhenContains("answer", "GENERAL")

            // Solvers converge to verifier
            .From("solver1", "solver2", "solver3").To("verifier")

            // Exit: terminal nodes → END
            .From("verifier").To("END")
            .From("general").To("END")

            .BuildAsync();

        AnsiConsole.MarkupLine("[dim]Graph built successfully[/]");

        // Debug: Show graph structure
        var layers = workflow.Graph.GetExecutionLayers();
        AnsiConsole.MarkupLine($"[dim]Graph has {layers.Count} layers:[/]");
        for (int i = 0; i < layers.Count; i++)
        {
            AnsiConsole.MarkupLine($"[dim]  Layer {i}: {string.Join(", ", layers[i].NodeIds)}[/]");
        }
        AnsiConsole.MarkupLine($"[dim]Edges:[/]");
        foreach (var edge in workflow.Graph.Edges)
        {
            var condDesc = edge.Condition?.GetDescription() ?? "(unconditional)";
            AnsiConsole.MarkupLine($"[dim]  {edge.From} → {edge.To}: {condDesc}[/]");
        }
        AnsiConsole.WriteLine();

        // Execute with streaming
        AnsiConsole.MarkupLine("[yellow]Analyzing question...[/]");
        AnsiConsole.WriteLine();

        string? finalAnswer = null;
        string? classifierOutput = null;
        var solverAnswers = new Dictionary<string, string>();

        // Use AgentUIRenderer for nice event display
        var uiRenderer = new AgentUIRenderer();

        await foreach (var evt in workflow.ExecuteStreamingAsync(question))
        {
            // Uncomment to debug all event types:
            // var evtType = evt.GetType().Name;
            // if (!evtType.Contains("TextDelta") && !evtType.Contains("Reasoning"))
            // {
            //     AnsiConsole.MarkupLine($"[grey]EVENT: {evtType}[/]");
            // }

            // Render agent events (TextDelta, ToolCalls, etc.)
            if (evt is AgentEvent agentEvt)
            {
                uiRenderer.RenderEvent(agentEvt);
            }

            switch (evt)
            {
                case HPDAgent.Graph.Abstractions.Events.NodeExecutionStartedEvent nodeStarted:
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[yellow]▶ Starting:[/] {nodeStarted.NodeId}");
                    break;

                case HPDAgent.Graph.Abstractions.Events.NodeExecutionCompletedEvent nodeCompleted:
                    AnsiConsole.MarkupLine($"[green]✓ Completed:[/] {nodeCompleted.NodeId}");

                    // Debug: Show all outputs
                    if (nodeCompleted.Outputs != null && nodeCompleted.Outputs.Count > 0)
                    {
                        AnsiConsole.MarkupLine($"[dim]  Outputs ({nodeCompleted.Outputs.Count}):[/]");
                        foreach (var (key, value) in nodeCompleted.Outputs)
                        {
                            var displayValue = value?.ToString() ?? "(null)";
                            if (displayValue.Length > 100)
                                displayValue = displayValue.Substring(0, 100) + "...";
                            AnsiConsole.MarkupLine($"[dim]    {key}: {Markup.Escape(displayValue)}[/]");
                        }
                    }

                    // Capture outputs
                    if (nodeCompleted.Outputs != null)
                    {
                        if (nodeCompleted.NodeId == "classifier" &&
                            nodeCompleted.Outputs.TryGetValue("answer", out var classOut))
                        {
                            classifierOutput = classOut?.ToString();
                            AnsiConsole.MarkupLine($"[cyan]Classifier result:[/] '{Markup.Escape(classifierOutput ?? "(null)")}'");
                        }

                        if (nodeCompleted.NodeId.StartsWith("solver") &&
                            nodeCompleted.Outputs.TryGetValue("answer", out var solverOut))
                        {
                            solverAnswers[nodeCompleted.NodeId] = solverOut?.ToString() ?? "";
                        }

                        if ((nodeCompleted.NodeId == "verifier" || nodeCompleted.NodeId == "general") &&
                            nodeCompleted.Outputs.TryGetValue("answer", out var answer))
                        {
                            finalAnswer = answer?.ToString();
                        }
                    }
                    break;

                case HPDAgent.Graph.Abstractions.Events.EdgeTraversedEvent edgeTraversed:
                    AnsiConsole.MarkupLine($"[blue]→ Edge:[/] {edgeTraversed.FromNodeId} → {edgeTraversed.ToNodeId}" +
                        (edgeTraversed.HasCondition ? $" [dim]({edgeTraversed.ConditionDescription})[/]" : ""));
                    break;

                case HPDAgent.Graph.Abstractions.Events.EdgeConditionFailedEvent edgeFailed:
                    AnsiConsole.MarkupLine($"[red]✗ Edge failed:[/] {edgeFailed.FromNodeId} → {edgeFailed.ToNodeId}");
                    AnsiConsole.MarkupLine($"[dim]  Condition: {edgeFailed.ConditionDescription}[/]");
                    AnsiConsole.MarkupLine($"[dim]  Actual: '{Markup.Escape(edgeFailed.ActualValue ?? "(null)")}' Expected: '{Markup.Escape(edgeFailed.ExpectedValue ?? "(null)")}'[/]");
                    break;

                case HPDAgent.Graph.Abstractions.Events.NodeSkippedEvent nodeSkipped:
                    AnsiConsole.MarkupLine($"[dim]⊘ Skipped:[/] {nodeSkipped.NodeId} - {nodeSkipped.Reason}");
                    break;

                // Uncomment to enable diagnostic logging for debugging:
                // case GraphDiagnosticEvent diagnostic:
                //     // Filter: only show Debug and above (skip Trace)
                //     if ((int)diagnostic.Level >= (int)LogLevel.Debug)
                //     {
                //         var color = diagnostic.Level switch
                //         {
                //             LogLevel.Debug => "grey",
                //             LogLevel.Information => "blue",
                //             LogLevel.Warning => "yellow",
                //             LogLevel.Error => "red",
                //             LogLevel.Critical => "red bold",
                //             _ => "dim"
                //         };
                //         var nodeInfo = diagnostic.NodeId != null ? $"({Markup.Escape(diagnostic.NodeId)}) " : "";
                //         AnsiConsole.MarkupLine($"[{color}]📋 {diagnostic.Level}: {nodeInfo}{Markup.Escape(diagnostic.Source)}: {Markup.Escape(diagnostic.Message)}[/]");
                //     }
                //     break;

            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[cyan bold]Final Answer:[/]");
        AnsiConsole.MarkupLine(Markup.Escape(finalAnswer ?? "(no answer)"));
        AnsiConsole.WriteLine();

        return finalAnswer ?? "";
    }

    private static readonly string ClassifierPrompt = @"You are a classifier. Your ONLY job is to output exactly one word.

OUTPUT RULES:
- Output ONLY the word 'MATH' or 'GENERAL'
- Do NOT solve the problem
- Do NOT explain anything
- Do NOT add any other text

CLASSIFICATION:
- MATH = any question involving numbers, calculations, percentages, money, statistics
- GENERAL = conversational, identity questions, non-numerical questions

CRITICAL: Your entire response must be exactly 4 letters: either 'MATH' or 'GENERAL'. Nothing else.";

    private static async Task<Agent> CreateClassifierAgent()
    {
        var config = new AgentConfig
        {
            Name = "Classifier",
            MaxAgenticIterations = 1,
            SystemInstructions = ClassifierPrompt,
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", "mistralai/mistral-small-creative")
            .Build();
    }

    private static readonly string SolverPrompt = @"You are a quantitative analyst.
Solve the problem step-by-step and provide a clear final answer.";

    private static async Task<Agent> CreateSolverAgent(string name, string model)
    {
        var config = new AgentConfig
        {
            Name = name,
            MaxAgenticIterations = 15,
            SystemInstructions = SolverPrompt,
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", model)
            .Build();
    }

    private static readonly string VerifierPrompt = @"Compare the solver answers and return the consensus.
Just return the agreed answer directly (no prefix needed).";

    private static async Task<Agent> CreateVerifierAgent()
    {
        var config = new AgentConfig
        {
            Name = "Verifier",
            MaxAgenticIterations = 5,
            SystemInstructions = VerifierPrompt,
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
