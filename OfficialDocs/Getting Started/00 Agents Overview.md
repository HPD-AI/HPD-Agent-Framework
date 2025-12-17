# Agents Overview

An **agent** is an AI-powered program that can think, plan, and take actions to accomplish tasks. Instead of just responding to your input, the agent can call tools, evaluate results, and decide what to do next.

## What Can Agents Do?

Agents can:
- **Understand context** - Process complex requests and conversations
- **Call tools** - Execute functions to interact with files, APIs, databases, etc.
- **Reason about results** - Evaluate what happened and decide next steps
- **Iterate** - Keep trying different approaches until the task is complete
- **Remember context** - Maintain conversation history across turns

## Your First Agent

### Step 1: Install NuGet Package

```bash
dotnet add package HPD.Agent
```

### Step 2: Create an Agent

```csharp
using HPD.Agent;

// Create and configure an agent
// API key is auto-detected from OPENAI_API_KEY environment variable
var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithSystemInstructions("You are a helpful assistant.")
    .Build();
```

### Step 2b: Create an Agent with Tools

Tools give your agent the ability to take actions. Define tools as classes with methods:

```csharp
using HPD.Agent;

// Define a tool with functions
public class CalculatorTool
{
    [AIFunction(Description = "Add two numbers")]
    public int Add(int a, int b) => a + b;

    [AIFunction(Description = "Multiply two numbers")]
    public int Multiply(int a, int b) => a * b;
}

// Create an agent with tools
var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithSystemInstructions("You are a helpful assistant with math capabilities.")
    .WithTools<CalculatorTool>()  // Register the tool
    .Build();
```

Now the agent can call `Add` and `Multiply` when needed. For more info on tools: [03 Tool Calling.md](03%20Tool%20Calling.md)

### Step 3a: Run the Agent (Stateless)

```csharp
// Run the agent - no session needed for simple queries
var message = "Add 5 and 3";  // Uses the CalculatorTool
await foreach (var evt in agent.RunAsync(message))
{
    if (evt is TextDeltaEvent textDelta)
        Console.Write(textDelta.Text);
}
```

That's it! Your agent is running and can use tools.

### Step 3b: Run the Agent (Stateful)

For multi-turn conversations, use a session to maintain history:

```csharp
// Create a session to maintain conversation history
var session = new AgentSession();

// Each call reuses the same session - agent remembers previous messages
var userMessages = new[] 
{
    "Add 10 and 20",      // First tool call
    "Now multiply the result by 5"  // References previous result
};

foreach (var message in userMessages)
{
    await foreach (var evt in agent.RunAsync(message, session))
    {
        if (evt is TextDeltaEvent textDelta)
            Console.Write(textDelta.Text);
    }
    Console.WriteLine();
}
```

The session maintains conversation history across turns, so the agent remembers the previous calculation.

## Key Concepts

### Agent Session
The session holds your conversation state - messages, metadata, and execution context. It lets you resume conversations and persist them to storage.

→ Learn more: [02 Multi-Turn Conversations.md](02%20Multi-Turn%20Conversations.md)

### Tools
Tools are functions your agent can call. Add them to give your agent capabilities - read files, call APIs, interact with databases, etc.

→ Learn more: [03 Tool Calling.md](03%20Tool%20Calling.md)

### Configuration
Agents can be customized extensively - error handling, history reduction, validation, observability, and much more.

→ Learn more: [01 Customizing an Agent.md](01%20Customizing%20an%20Agent.md)

## Event-Driven Architecture

The agent doesn't just return a final answer - it streams **events** as it works:

```csharp
await foreach (var evt in agent.RunAsync("Do something", session))
{
    if (evt is TextDeltaEvent textDelta)
    {
        Console.Write(textDelta.Text);  // Streaming text output
    }
}
```

This means you can:
- Show real-time progress to users
- Cancel mid-execution
- Respond to what the agent is doing
- Build interactive experiences

→ Complete event reference: [04 Event Handling.md](04%20Event%20Handling.md)

## Stateless vs. Persistent

### Stateless (Default)
```csharp
var session = new AgentSession();
await foreach (var evt in agent.RunAsync("First message", session)) { }
await foreach (var evt in agent.RunAsync("Second message", session)) { }
// Session is lost when process ends
```

Use this for: Testing, scripts, one-off tasks.

### Persistent
```csharp
var store = new JsonSessionStore("./sessions");
var agent = new AgentBuilder()
    .WithSessionStore(store, persistAfterTurn: true)
    .Build();

// Agent auto-loads, runs, and saves
await foreach (var evt in agent.RunAsync("Message", sessionId: "user-123")) { }
```

Use this for: Web apps, long-running services, conversation resumption.

→ Details: [02 Multi-Turn Conversations.md](02%20Multi-Turn%20Conversations.md)

## Next Steps

1. **Add tools** - Give your agent capabilities
   - See: [03 Tool Calling.md](03%20Tool%20Calling.md)

2. **Persist conversations** - Save and resume sessions
   - See: [02 Multi-Turn Conversations.md](02%20Multi-Turn%20Conversations.md)

3. **Customize behavior** - Configure error handling, history reduction, etc.
   - See: [01 Customizing an Agent.md](01%20Customizing%20an%20Agent.md)

4. **Handle events** - Respond to agent activity and streaming output
   - See: [04 Event Handling.md](04%20Event%20Handling.md)
