# The "Glass Box" Orchestration Framework

Welcome to the "Glass Box" Orchestration Framework — a powerful, flexible, and transparent system for building multi-agent workflows in .NET. The framework makes simple things simple and complex things possible, while giving you clarity and control to build robust, debuggable, and scalable AI applications.

Our guiding philosophy is the "Glass Box": powerful yet transparent, so developers always feel in control and can easily see what's happening under the hood.

## Core Concepts

The framework is built on a few simple, powerful ideas:

- **Conversation**: A lightweight class that holds the state of a conversation — its history and metadata. It delegates all control flow logic to an orchestrator.  
- **Agent**: Represents an AI entity, built on `Microsoft.Extensions.AI.IChatClient`. Agents are the "workers" in your workflows.  
- **IOrchestrator**: The "brain" of the operation. This interface defines the strategy for how agents collaborate. A family of orchestrators is provided so you can choose the right tool for the job.

## Getting Started: The Right Tool for the Job

Not every problem requires a complex graph. The framework provides different orchestrators for different levels of complexity.

### 1. For Single-Agent Tasks: `DirectOrchestrator`

When you just need to call a single agent, this is your go-to. It's simple, efficient, and requires no setup.

```csharp
var orchestrator = new DirectOrchestrator();
var conversation = new Conversation(orchestrator, new[] { mySingleAgent });
var response = await conversation.SendAsync("What is the weather like?");
```

### 2. For Simple Sequences: `SequenceOrchestrator`

Use this when agents must run in a fixed, linear order.

```csharp
// Define the StateNodes for each step
var researchNode = new ResearchNode(researchAgent);
var writeNode = new WriteNode(writerAgent);

// Create the orchestrator with the sequence
var orchestrator = new SequenceOrchestrator<ArticleState>(researchNode, writeNode);
var conversation = new Conversation(orchestrator, ...);
```

### 3. For Complex, Conditional Workflows: `GraphOrchestrator`

Use `GraphOrchestrator` when your workflow requires branching, looping, or complex state-driven logic. Typically configured with `AgentWorkflowBuilder`.

## Building Workflows: The `AgentWorkflowBuilder`

For most multi-agent tasks, `AgentWorkflowBuilder` is the recommended entry point. It provides a high-level, fluent, and type-safe API to define complex workflows by focusing on agents and their interactions rather than plumbing.

### Example: A Collaborative Writing Workflow

Build a workflow where a Researcher finds data, a Writer drafts an article, and a Reviewer approves or requests revisions.

#### Step 1: Define the Shared State

```csharp
public record ArticleState(
    string Topic,
    string ResearchData,
    string Draft,
    string Feedback,
    bool NeedsRevision,
    int RevisionCount
);
```

#### Step 2: Define the Workflow

The `AgentWorkflowBuilder` automates creation of the underlying graph, nodes, and conditions.

```csharp
// (Assume researchAgent, writerAgent, and reviewerAgent are already defined)

var orchestrator = AgentWorkflowBuilder.Create<ArticleState>("ArticleWorkflow")

    // Define the "Research" node. The agent's text response will be mapped to ResearchData.
    .AddAgent(researchAgent, "Research", state => state.ResearchData)

    // Define the "Write" node
    .AddAgent(writerAgent, "Write", state => state.Draft)

    // Define the "Review" node with a custom mapping function
    .AddAgent(reviewerAgent, "Review", (response, currentState) =>
    {
        var feedbackText = response.GetTextContent();
        return currentState with
        {
            Feedback = feedbackText,
            NeedsRevision = feedbackText.Contains("REVISION", StringComparison.OrdinalIgnoreCase),
            RevisionCount = currentState.RevisionCount + 1
        };
    })

    // Define transitions
    .From("Research").GoTo("Write")
    .From("Write").GoTo("Review")

    // Conditional branch from "Review"
    .From("Review").Branch(
        when: state => state.NeedsRevision && state.RevisionCount < 3,
        ifTrue: "Write",  // Loop back to writer if revisions needed
        ifFalse: "End"    // Otherwise, end the workflow
    )

    // Finalize to get an orchestrator
    .BuildOrchestrator();

// Use the orchestrator in a conversation
var conversation = new Conversation(orchestrator, ...);
var finalArticle = await conversation.SendAsync("Write an article about the future of AI.");
```

## Debugging: The Glass Box in Action

Every workflow executed by the `GraphOrchestrator` is fully transparent. `WorkflowContext` maintains an immutable trace of every step taken. Inspect the trace rather than guessing what happened.

```csharp
// After a workflow runs, inspect its history
var finalContext = orchestrator.GetFinalContext(conversation.Id);

foreach (var step in finalContext.Trace)
{
    Console.WriteLine($"--- Step: {step.NodeKey} ---");
    Console.WriteLine($"Duration: {step.Duration.TotalMilliseconds}ms");
    Console.WriteLine($"Input State: {step.InputState}");
    Console.WriteLine($"Output State: {step.OutputState}");
    Console.WriteLine($"Transition Decision: '{step.ConditionResult}' led to next node.");
}
```

## Advanced Features

### Persistence and Resilience

Make workflows resilient to interruptions by providing a checkpoint store. The orchestrator saves state after every step.

```csharp
var checkpointStore = new InMemoryCheckpointStore<ArticleState>();
var orchestrator = builder.BuildOrchestrator(checkpointStore);

// If the application restarts, a conversation with the same ID resumes from last completed step.
```

### Global Coordination with Aggregators

For global coordination (voting, counting), use aggregators. Nodes can add values during their turn and the results are available to subsequent nodes.

```csharp
// Inside a StateNode's ExecuteAsync method:
var tokenAggregator = context.Aggregators.GetOrCreate<long, SumAggregator>("total_tokens");
tokenAggregator.Add(response.Usage.TotalTokens);
```

## Dropping Down: The `WorkflowBuilder` for Custom Logic

`AgentWorkflowBuilder` is a facade over `WorkflowBuilder` and `WorkflowRegistry`. Use the lower-level APIs to integrate custom, non-agent logic (database queries, external APIs).

### Example: Adding a Database Save Step

Create a custom `StateNode`:

```csharp
public class SaveToDatabaseNode : StateNode<ArticleState>
{
    public override async Task<ArticleState> ExecuteAsync(WorkflowContext<ArticleState> context, ...)
    {
        // ... logic to save context.State.Draft to the database ...
        Console.WriteLine("Article saved to database.");
        return context.State; // State is unchanged
    }
}
```

Use `WorkflowBuilder` and `WorkflowRegistry`:

```csharp
// Manually create the registry and builder
var registry = new WorkflowRegistry<ArticleState>();
var builder = WorkflowBuilder.Create("HybridWorkflow");

// Register your custom node
registry.RegisterNode("SaveToDb", new SaveToDatabaseNode());
builder.AddNode("SaveStep", "SaveToDb");

// Combine with AgentWorkflowBuilder-created nodes or define the graph manually.
```

This layered approach gives you the simplicity of `AgentWorkflowBuilder` for most tasks while enabling the full power of the underlying graph engine for custom logic and integrations.