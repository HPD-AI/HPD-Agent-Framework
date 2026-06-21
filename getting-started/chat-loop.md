# Tiny Console Chat Loop

This page turns the earlier pieces into a small interactive assistant. It uses one agent, one session, one thread, and a loop that keeps reading console input until you press Enter on an empty line.

Continue in the same `HpdAgentQuickstart` folder from [Hello Agent](hello-agent.md), or create a fresh console app with the same packages.

## Add Program.cs

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("You are a concise helpful assistant.")
    .BuildAsync();

var (sessionId, threadId) = await agent.CreateSessionAsync("getting-started-chat-loop");

using var output = agent.Subscribe<TextDeltaEvent>(evt => Console.Write(evt.Text));
using var finished = agent.Subscribe<MessageTurnFinishedEvent>(_ => Console.WriteLine());

while (true)
{
    Console.Write("\nYou: ");
    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
        break;

    Console.Write("Agent: ");
    _ = await agent.RunAsync(input, sessionId, threadId);
}
```

Run it:

```bash
dotnet run
```

Try:

```text
You: My project is called Atlas. Remember that.
You: What is my project called?
```

## You Succeeded If

The second answer should refer to `Atlas`. That means the loop is reusing the same session and thread, so the agent receives prior conversation history.

## What Happens

`CreateSessionAsync(...)` creates the conversation container and default thread.

`Subscribe<TextDeltaEvent>(...)` prints text as it arrives.

The `while` loop reads user input and calls `RunAsync(...)` for each turn. Passing the same `sessionId` and `threadId` keeps the conversation coherent.

The subscriptions observe the run. They do not start the run by themselves; `RunAsync(...)` still performs each turn.

## Common Mistakes

If the agent forgets earlier messages, make sure every `RunAsync(...)` call uses the same `sessionId` and `threadId`.

If nothing prints, make sure the text subscription is registered before the loop calls `RunAsync(...)`.

If the process restart loses history, add [Save Sessions And State](persistence.md).

## Next

Next: save conversations in [Save Sessions And State](persistence.md).

Optional: add behavior around turns in [Middleware](middleware.md).

