#:package HPD-Agent.Framework@0.5.5
#:package HPD-Agent.Providers.OpenAI@0.5.5
#:property TargetFramework=net10.0

// This sample uses middleware to add retrieved context before the agent answers.

using HPD.Agent;
using HPD.Agent.Middleware;
using HPD.Agent.Providers.OpenAI;
using Microsoft.Extensions.AI;

// Register the context provider with the agent. Middleware runs around each
// turn, so it can inspect the incoming message and modify the model context.
var agent = await new AgentBuilder()
                    .WithInstructions("You are a concise assistant. Use retrieved context when it is relevant.")
                    .WithOpenAI("gpt-5-mini")
                    .WithMiddleware(new ProductDocsContext())
                    .BuildAsync();

var result = await agent.RunAsync("What is HPD Agent good for?");

Console.WriteLine(result.Text);

// This class is the RAG step: it finds relevant text and adds it to the turn.
public class ProductDocsContext : IAgentMiddleware
{
    // A real app would usually query a vector database, search index, or
    // document store. This sample keeps the retrieval source in memory.
    private static readonly Dictionary<string, string> Docs = new()
    {
        ["hpd agent"] = "HPD Agent is a .NET agent framework for building agents with providers, tools, events, sessions, threads, and middleware.",
        ["tools"] = "Tool harnesses let HPD Agent expose local C# methods as model-callable tools.",
        ["sessions"] = "Sessions keep multi-turn conversation history. Threads let one session fork into alternative conversation paths."
    };

    public Task BeforeMessageTurnAsync(BeforeMessageTurnContext context, CancellationToken cancellationToken)
    {
        // Look at the user's message and collect any matching snippets.
        var query = context.UserMessage?.Text ?? string.Empty;
        var matches = Docs
            .Where(doc => query.Contains(doc.Key, StringComparison.OrdinalIgnoreCase))
            .Select(doc => doc.Value)
            .ToArray();

        if (matches.Length > 0)
        {
            // Add retrieved context as a system message for this turn. The
            // persisted user/assistant history remains separate from retrieval.
            context.ThreadHistory.Add(new ChatMessage(
                ChatRole.System,
                "Retrieved context:\n" + string.Join("\n", matches)));
        }

        return Task.CompletedTask;
    }
}
