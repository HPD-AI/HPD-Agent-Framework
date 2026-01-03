# Multi-Agent Quant Workflow - Usage Guide

## ✅ You're All Set!

The multi-agent consensus workflow is now integrated into your console app.

## 🚀 How to Run

```bash
cd /Users/einsteinessibu/Documents/HPD-Agent
dotnet run --project test/AgentConsoleTest
```

## 💬 Commands Available

### 1. `/quant-graph <question>` - Graph-Based Workflow (REAL HPD.Graph)

Uses the full HPD.Graph orchestration engine with:
- Automatic parallel execution
- Conditional routing
- Cyclic feedback loops
- Channel-based state management

**Example:**
```
You: /quant-graph What is the expected value of rolling two dice?

🧮 Graph-Based Multi-Agent Consensus Workflow

  Solver 1 working...
  Solver 2 working...
  Solver 3 working...
  ✓ Solver 1 completed
  ✓ Solver 2 completed
  ✓ Solver 3 completed

Answers received:
  Solver 1: The expected value is 7...
  Solver 2: E(X) = 7...
  Solver 3: 7

Verifying consensus...

✅ CONSENSUS REACHED (Round 1)!

Final Answer: The expected value is 7
```

### 2. `/quant <question>` - Simple Workflow (No Graph)

Uses basic parallelism with `Task.WhenAll()` - simpler implementation.

**Example:**
```
You: /quant What is 15% of 240?

🧮 Multi-Agent Consensus Workflow Starting...

Round 1: Solving with 3 agents in parallel...
  ✓ Solver 1 completed
  ✓ Solver 2 completed
  ✓ Solver 3 completed

Answers received:
  Solver 1: 36
  Solver 2: 36
  Solver 3: 36

Verifying consensus...

✅ CONSENSUS REACHED (Round 1)!

Final Answer: 36
```

## 📊 When to Use Which?

| Feature | `/quant` (Simple) | `/quant-graph` (Graph) |
|---------|-------------------|------------------------|
| Uses HPD.Graph | ❌ | ✅ |
| Parallel Execution | Manual `Task.WhenAll()` | Automatic graph layers |
| Conditional Routing | `if/else` | `EdgeCondition` |
| Feedback Loop | `while` loop | Cyclic graph edges |
| State Management | Local variables | Channels |
| Extensibility | Low | High |
| Complexity | ~150 LOC | ~420 LOC |
| **Use for** | Quick tests, learning | Production, complex workflows |

## 🎯 Example Questions

### Math/Statistics
```
/quant-graph What is the probability of getting exactly 2 heads in 5 coin flips?
/quant-graph Calculate the standard deviation of [1, 2, 3, 4, 5]
/quant-graph What is 15% of 240?
```

### Finance
```
/quant-graph If I invest $1000 at 5% annual interest compounded monthly for 10 years, how much will I have?
/quant-graph What is the present value of $10000 received 5 years from now at 8% discount rate?
```

### Combinatorics
```
/quant-graph How many ways can you arrange 5 people in a line?
/quant-graph What is the number of combinations of choosing 3 items from 10?
```

## 🔍 How It Works

### Both Workflows

1. **3 Solver Agents** solve the problem **in parallel**
2. **Verifier Agent** compares all 3 answers
3. **If consensus** → Return the agreed answer
4. **If disagreement** → Provide feedback and retry (max 3 times)

### Key Differences

**Simple Version:**
- Uses C# `Task.WhenAll()` for parallelism
- Manual retry loop with `while`
- State in local variables
- Direct method calls

**Graph Version:**
- HPD.Graph orchestrator handles execution
- Graph layers provide automatic parallelism
- Cyclic edges create feedback loop
- State in channels
- Declarative workflow structure

## ⚡ Features

### Consensus Detection
- ✅ All 3 agents must agree on the final answer
- ✅ Strict verification (same numerical result required)
- ✅ Minor wording differences are OK

### Automatic Retry
- ✅ Up to 3 retry attempts on disagreement
- ✅ Feedback includes specific differences found
- ✅ Agents reconsider their approach based on feedback

### No Session Required
- ✅ Completely stateless - each run is independent
- ✅ No session persistence needed
- ✅ No cleanup required

## 🎨 Output Examples

### Successful Consensus (Round 1)
```
✅ CONSENSUS REACHED (Round 1)!
Final Answer: 7
```

### Disagreement with Retry
```
⚠️  Disagreement detected (Attempt 1/3)
Feedback: Solver 2 calculated 24 while others got 36
Retrying with feedback...

[Retry happens automatically]

✅ CONSENSUS REACHED (Round 2)!
Final Answer: 36
```

### Max Retries Exceeded
```
❌ Max retries (3) exceeded - no consensus
Final Answer: Unable to reach consensus after 3 attempts
```

## 🔧 Configuration

Both workflows use:
- **LLM Model**: `anthropic/claude-3.5-sonnet` (via OpenRouter)
- **Max Retries**: 3 attempts
- **Max Iterations per Agent**: 20 (solvers), 10 (verifier)
- **Parallelism**: All 3 solvers run simultaneously

To change these, edit the workflow files:
- [SimpleQuantWorkflow.cs](SimpleQuantWorkflow.cs)
- [GraphQuantWorkflow.cs](GraphQuantWorkflow.cs)

## 📝 Notes

### Events
- ✅ Graph automatically emits events via HPD.Events
- ✅ No event handling required (console output is enough)
- ✅ Can add event observers if you want progress tracking

### Sessions
- ✅ Both workflows are **stateless**
- ✅ No session needed - each run is independent
- ✅ Your main agent still uses sessions normally

### Performance
- ⚡ 3 solvers run **in parallel** (not sequential)
- ⚡ Total time ≈ slowest solver + verifier time
- ⚡ Typically 10-30 seconds per round

## 🎓 Learning the Graph System

The `GraphQuantWorkflow` is a great example to learn HPD.Graph:

1. **Graph Building** - See how to use `GraphBuilder`
2. **Node Handlers** - See how to implement `IGraphNodeHandler<GraphContext>`
3. **Parallel Layers** - Multiple edges from START create parallel execution
4. **Conditional Routing** - `EdgeCondition` with `FieldEquals`
5. **Cyclic Graphs** - Feedback loops for retry logic
6. **Channels** - State management via `context.Channels`
7. **DI Integration** - Service registration and resolution

## 🚀 Next Steps

Want to extend the workflow? Easy with the Graph version:

### Add More Solvers
```csharp
builder.AddHandlerNode("solver4", "Solver4Handler", "Solver 4");
builder.AddEdge("START", "solver4");
builder.AddEdge("solver4", "verifier");
```

### Change Consensus Logic
Implement a `MajorityVerifierHandler` instead of requiring all 3 to agree.

### Add Specialized Solvers
Route to different solvers based on question type (stats, finance, combinatorics).

### Enable Checkpointing
Add a checkpoint store to resume interrupted workflows.

### Enable Caching
Add a cache store to avoid re-solving identical questions.

---

**Enjoy your multi-agent consensus workflow! 🎉**
