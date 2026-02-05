# Multi-Agent Quant Consensus Workflow

## Overview

This implements a **3-agent consensus workflow** for solving quantitative questions with verification.

```
USER QUESTION
      │
  ┌───┼───┐
  ▼   ▼   ▼
 S1  S2  S3   (3 solvers in parallel)
  │   │   │
  └───┼───┘
      ▼
  Verifier   (checks consensus)
      │
  ┌───┴───┐
  ▼       ▼
 Same   Different
  │       │
Return  Retry (max 3x)
```

## How It Works

1. **Question** → Sent to 3 independent solver agents **in parallel**
2. **Solvers** → Each solves the problem independently
3. **Verifier** → Compares all 3 answers
4. **Consensus?**
   -  **YES** → Return the agreed answer
   - **NO** → Provide feedback and retry (up to 3 attempts)

## Usage

### Option 1: Direct Usage

Add this to your [Program.cs](Program.cs):

```csharp
// Somewhere in your main loop, detect quant questions
if (userInput.StartsWith("/quant ", StringComparison.OrdinalIgnoreCase))
{
    var question = userInput.Substring(7).Trim();
    await SimpleQuantWorkflow.RunAsync(question);
    AnsiConsole.WriteLine();
    continue;
}
```

### Option 2: As Slash Command

Add this to Program.cs after line 118 (after `commandContextData` is created):

```csharp
// Register /quant command
ui.CommandRegistry.Register(new SlashCommand
{
    Name = "quant",
    Description = "Solve quant question with 3-agent consensus",
    Execute = async (args, context) =>
    {
        var question = string.Join(" ", args);

        if (string.IsNullOrWhiteSpace(question))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /quant <question>[/]");
            AnsiConsole.MarkupLine("[dim]Example: /quant What is 15% of 240?[/]");
            return new CommandResult { Success = true };
        }

        await SimpleQuantWorkflow.RunAsync(question);
        return new CommandResult { Success = true };
    }
});
```

## Example Usage

```bash
$ dotnet run --project test/AgentConsoleTest

You: /quant What is the expected value of rolling two dice?

🧮 Multi-Agent Consensus Workflow Starting...

Round 1: Solving with 3 agents in parallel...
  ✓ Solver 1 completed
  ✓ Solver 2 completed
  ✓ Solver 3 completed

Answers received:
  Solver 1: The expected value is 7 (each die has expected value 3.5...)
  Solver 2: E(X) = 7 (sum of two uniform distributions...)
  Solver 3: Expected value = 7 (by linearity of expectation...)

Verifying consensus...
  ✓ Verifier completed

 CONSENSUS REACHED (Round 1)!

Final Answer: The expected value is 7
```

## Example with Disagreement

```bash
You: /quant What is 15% of 240?

🧮 Multi-Agent Consensus Workflow Starting...

Round 1: Solving with 3 agents in parallel...
  ✓ Solver 1 completed
  ✓ Solver 2 completed
  ✓ Solver 3 completed

Answers received:
  Solver 1: 36
  Solver 2: 24  ← Wrong!
  Solver 3: 36

Verifying consensus...
  ✓ Verifier completed

 Disagreement detected (Attempt 1/3)
Feedback: Solver 2 calculated 24 (10% instead of 15%), while Solvers 1 and 3 correctly got 36

Retrying with feedback...

Round 2: Solving with 3 agents in parallel...
  ✓ Solver 1 completed
  ✓ Solver 2 completed
  ✓ Solver 3 completed

Answers received:
  Solver 1: 36
  Solver 2: 36  ← Fixed!
  Solver 3: 36

Verifying consensus...
  ✓ Verifier completed

 CONSENSUS REACHED (Round 2)!

Final Answer: 36
```

## Files

- **[SimpleQuantWorkflow.cs](SimpleQuantWorkflow.cs)** - The complete implementation (simple, no Graph dependency)

## Configuration

The workflow uses these settings:

- **Max Retries**: 3 attempts
- **LLM Model**: `anthropic/claude-3.5-sonnet` (via OpenRouter)
- **Solver Iterations**: 20 max per agent
- **Verifier Iterations**: 10 max

You can modify these in the code by editing `SimpleQuantWorkflow.cs`.

## Why This Approach Works

1. **Parallel Execution** - All 3 solvers work simultaneously for speed
2. **Independent Agents** - Each has different methodology guidance
3. **Strict Verification** - Verifier checks final numerical answers
4. **Feedback Loop** - Failed attempts include specific feedback
5. **Self-Correction** - Agents can reconsider based on peer disagreement

## Future: Graph-Based Version

For a more sophisticated version using HPD.Graph for orchestration, see the architecture proposal in `InternalDocs/HPD.Graph/`. The graph-based version would add:

- Automatic checkpointing
- Caching of solver results
- Complex conditional routing
- Support for more sophisticated workflows

## Questions?

The code is in [SimpleQuantWorkflow.cs](SimpleQuantWorkflow.cs) - feel free to customize it for your needs!
