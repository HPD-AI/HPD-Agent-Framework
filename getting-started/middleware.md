# Middleware

Middleware wraps agent lifecycle steps. Use it to add context, enforce policy, emit events, track state, or change behavior around turns and tool calls.

This page adds a tiny retrieval step before the agent answers.

## Add Program.cs

```csharp
using HPD.Agent;
using HPD.Agent.Middleware;
using HPD.Agent.Providers.OpenAI;
using Microsoft.Extensions.AI;

var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("Use retrieved context when it is relevant.")
    .WithMiddleware(new ProductDocsContext())
    .BuildAsync();

var result = await agent.RunAsync("What is HPD Agent good for?");
Console.WriteLine(result.Text);

public class ProductDocsContext : IAgentMiddleware
{
    private static readonly Dictionary<string, string> Docs = new()
    {
        ["hpd agent"] = "HPD Agent is a .NET agent framework for building agents with providers, tools, events, sessions, threads, and middleware.",
        ["tools"] = "Tool harnesses expose local C# methods as model-callable tools.",
        ["sessions"] = "Sessions keep multi-turn conversation history. Threads fork a session into alternate paths."
    };

    public Task BeforeMessageTurnAsync(BeforeMessageTurnContext context, CancellationToken cancellationToken)
    {
        var query = context.UserMessage?.Text ?? string.Empty;
        var matches = Docs
            .Where(doc => query.Contains(doc.Key, StringComparison.OrdinalIgnoreCase))
            .Select(doc => doc.Value)
            .ToArray();

        if (matches.Length > 0)
        {
            context.ThreadHistory.Add(new ChatMessage(
                ChatRole.System,
                "Retrieved context:\n" + string.Join("\n", matches)));
        }

        return Task.CompletedTask;
    }
}
```

Run it:

```bash
dotnet run
```

## What Happens

`WithMiddleware(...)` registers behavior around each turn.

`BeforeMessageTurnAsync(...)` runs before the model call.

The middleware inspects the user message, finds matching snippets, and adds a system message to the thread history for this turn.

This is the same shape you can use for retrieval, request shaping, audit logs, permission checks, usage tracking, tool policies, or custom events.

## Next

Next: return to the primary path with [Save Sessions And State](persistence.md).

Go deeper: for middleware ordering, lifecycle hooks, tool-scoped middleware, permissions, and middleware state, see [Middleware Lifecycle](../concepts/middleware-lifecycle.md) and [Middleware Overview](../guides/middleware/overview.md).
