using HPD.Agent;
using Spectre.Console;

namespace AgentConsoleTest;

/// <summary>
/// Simplified Multi-Agent Consensus Workflow (No Graph dependency)
///
/// This is the SIMPLE approach that works with your existing setup.
/// For a full Graph-based implementation, see QuantWorkflowExample.cs
///
/// ASCII Workflow:
///
///                    USER QUESTION
///                          │
///          ┌───────────────┼───────────────┐
///          ▼               ▼               ▼
///     Solver1         Solver2         Solver3   (Parallel)
///          │               │               │
///          └───────────────┼───────────────┘
///                          ▼
///                     Verifier
///                          │
///               ┌──────────┴──────────┐
///               ▼                     ▼
///          Consensus              Disagreement
///           → Return            → Retry (max 3x)
///
/// </summary>
public static class SimpleQuantWorkflow
{
    /// <summary>
    /// Run a 3-agent consensus workflow for quant questions
    /// </summary>
    public static async Task<string> RunAsync(string question)
    {
        AnsiConsole.MarkupLine("[cyan]🧮 Multi-Agent Consensus Workflow Starting...[/]");
        AnsiConsole.WriteLine();

        const int MAX_RETRIES = 3;
        int retryCount = 0;
        string feedbackMessage = "";

        while (retryCount < MAX_RETRIES)
        {
            // Step 1: Create 3 solver agents
            var solver1 = await CreateSolverAgent("Solver-1", "Method 1: Use step-by-step analytical approach");
            var solver2 = await CreateSolverAgent("Solver-2", "Method 2: Double-check all calculations");
            var solver3 = await CreateSolverAgent("Solver-3", "Method 3: Use alternative verification methods");

            // Step 2: Solve in parallel
            AnsiConsole.MarkupLine($"[yellow]Round {retryCount + 1}: Solving with 3 agents in parallel...[/]");

            var prompt = string.IsNullOrEmpty(feedbackMessage)
                ? question
                : $"{question}\n\nPrevious feedback: {feedbackMessage}\nPlease reconsider your approach.";

            var tasks = new[]
            {
                GetAgentAnswer(solver1, prompt, "Solver 1"),
                GetAgentAnswer(solver2, prompt, "Solver 2"),
                GetAgentAnswer(solver3, prompt, "Solver 3")
            };

            var answers = await Task.WhenAll(tasks);

            // Display answers
            AnsiConsole.MarkupLine("\n[cyan]Answers received:[/]");
            for (int i = 0; i < answers.Length; i++)
            {
                var shortAnswer = answers[i].Length > 100
                    ? answers[i].Substring(0, 97) + "..."
                    : answers[i];
                AnsiConsole.MarkupLine($"  [dim]Solver {i + 1}:[/] {Markup.Escape(shortAnswer)}");
            }

            // Step 3: Verify consensus
            AnsiConsole.MarkupLine("\n[yellow]Verifying consensus...[/]");

            var verifier = await CreateVerifierAgent();
            var verificationPrompt = $@"Compare these 3 answers to a quant question:

Question: {question}

Solver 1: {answers[0]}
Solver 2: {answers[1]}
Solver 3: {answers[2]}";

            var verification = await GetAgentAnswer(verifier, verificationPrompt, "Verifier");

            // Step 4: Check result
            if (verification.StartsWith("CONSENSUS:", StringComparison.OrdinalIgnoreCase))
            {
                var finalAnswer = verification.Substring("CONSENSUS:".Length).Trim();
                AnsiConsole.MarkupLine($"\n[green]✅ CONSENSUS REACHED (Round {retryCount + 1})![/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[cyan bold]Final Answer:[/] {Markup.Escape(finalAnswer)}");
                return finalAnswer;
            }

            // No consensus - prepare for retry
            retryCount++;
            feedbackMessage = verification.StartsWith("DISAGREEMENT:", StringComparison.OrdinalIgnoreCase)
                ? verification.Substring("DISAGREEMENT:".Length).Trim()
                : verification;

            AnsiConsole.MarkupLine($"\n[yellow]⚠️  Disagreement detected (Attempt {retryCount}/{MAX_RETRIES})[/]");
            AnsiConsole.MarkupLine($"[dim]Feedback: {Markup.Escape(feedbackMessage)}[/]");

            if (retryCount < MAX_RETRIES)
            {
                AnsiConsole.MarkupLine("[yellow]Retrying with feedback...[/]\n");
            }
        }

        AnsiConsole.MarkupLine($"\n[red]❌ Failed to reach consensus after {MAX_RETRIES} attempts[/]");
        return "Unable to reach consensus among agents.";
    }

    private static async Task<Agent> CreateSolverAgent(string name, string methodGuidance)
    {
        var config = new AgentConfig
        {
            Name = name,
            MaxAgenticIterations = 20,
            SystemInstructions = $@"You are a quantitative analyst solving mathematical and quantitative problems.

{methodGuidance}

When solving:
1. Show your work step-by-step
2. Double-check calculations
3. Provide a clear final answer at the end
4. If given feedback about disagreement, carefully reconsider your approach",
        };

        var agent = await new AgentBuilder(config)
            .WithProvider("openrouter", "anthropic/claude-3.5-sonnet")
            .Build();

        return agent;
    }

    private static async Task<Agent> CreateVerifierAgent()
    {
        var config = new AgentConfig
        {
            Name = "Consensus-Verifier",
            MaxAgenticIterations = 10,
            SystemInstructions = @"You are a verification agent that checks consensus among 3 solver agents.

Your job:
1. Compare the 3 answers carefully
2. Look for the FINAL NUMERICAL ANSWER or CONCLUSION from each solver
3. Be STRICT - they must give the same final answer (minor wording differences OK)
4. If they disagree, clearly explain what differs

ALWAYS respond in this EXACT format:
- 'CONSENSUS: [the agreed answer]' if all 3 fundamentally agree
- 'DISAGREEMENT: [clear explanation]' if they differ

Examples:
CONSENSUS: The expected value is 7
DISAGREEMENT: Solver 1 got 42, Solver 2 got 42, but Solver 3 got 43",
        };

        var agent = await new AgentBuilder(config)
            .WithProvider("openrouter", "anthropic/claude-3.5-sonnet")
            .Build();

        return agent;
    }

    private static async Task<string> GetAgentAnswer(Agent agent, string question, string agentName)
    {
        var answer = "";

        await foreach (var evt in agent.RunAsync(question))
        {
            // TextDeltaEvent has a property called Text (not Delta)
            if (evt is TextDeltaEvent textEvt)
            {
                answer += textEvt.Text;
            }
        }

        AnsiConsole.MarkupLine($"  [green]✓[/] [dim]{agentName} completed[/]");
        return answer.Trim();
    }
}
