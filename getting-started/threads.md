# Threading

Threads let one session split into alternate conversation paths.

Use a thread when you want to explore a different answer, draft, tool path, or subagent task without overwriting the main conversation.

## Add Program.cs

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("You are a concise product writing assistant.")
    .BuildAsync();

var (sessionId, mainThreadId) = await agent.CreateSessionAsync("getting-started-threads");

await agent.RunAsync(
    "We are launching a developer SDK for agent apps.",
    sessionId,
    mainThreadId);

var forkThreadId = await agent.ForkThreadAsync(
    sessionId,
    sourceThreadId: mainThreadId,
    name: "playful-draft");

var direct = await agent.RunAsync(
    "Write the launch note in a direct professional tone.",
    sessionId,
    mainThreadId);

var playful = await agent.RunAsync(
    "Write the launch note in a warmer, more playful tone.",
    sessionId,
    forkThreadId);

Console.WriteLine("Main thread:");
Console.WriteLine(direct.Text);

Console.WriteLine();
Console.WriteLine("Forked thread:");
Console.WriteLine(playful.Text);
```

Run it:

```bash
dotnet run
```

## What Happens

Both threads start from the same earlier session history.

The main thread receives the direct professional request.

The fork receives the warmer draft request.

Each thread can continue independently after the fork.

## When Threads Help

Use threads for:

- comparing alternate answers
- retrying with different instructions
- letting subagents work in isolated context
- preserving user-visible history while exploring a private path
- compacting or trimming a fork before a specialized run

## Next

Next: return to the primary path with [Save Sessions And State](persistence.md).

Go deeper: for fork options, history projection, and thread compaction, see [Thread History And Forking](../guides/sessions-and-streaming/thread-history-and-forking.md).
