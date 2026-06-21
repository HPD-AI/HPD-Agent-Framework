#:package HPD-Agent.Framework@0.5.5
#:package HPD-Agent.Providers.OpenAI@0.5.5
#:property TargetFramework=net10.0

// This sample forks one conversation into two threads with shared history.

using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

// Build the agent normally.
var agent = await new AgentBuilder()
                    .WithInstructions("You are a concise naming assistant.")
                    .WithOpenAI("gpt-5-mini")
                    .BuildAsync();

// A session can contain multiple threads. The first thread is always "main".
await agent.CreateSessionAsync("cookbook-threading");

// Start on main. The fork below will copy this shared setup history.
var setup = await agent.RunAsync(
    "I am naming a coffee shop. The name should feel calm, modern, and friendly.",
    sessionId: "cookbook-threading",
    threadId: "main");

Console.WriteLine("Shared setup:");
Console.WriteLine(setup.Text);
Console.WriteLine();

// Fork main from its latest message into a new thread named "playful".
// From here on, each thread can evolve independently.
await agent.ForkThreadAsync(
    sessionId: "cookbook-threading",
    sourceThreadId: "main",
    newThreadId: "playful");

// Continue the original thread with the neutral naming direction.
var mainResult = await agent.RunAsync(
    "Suggest one name.",
    sessionId: "cookbook-threading",
    threadId: "main");

// Continue the forked thread from the same shared setup, but ask for a
// different tone. This is useful for comparing alternatives without losing history.
var playfulResult = await agent.RunAsync(
    "Suggest one name with a more playful tone.",
    sessionId: "cookbook-threading",
    threadId: "playful");

Console.WriteLine("Main thread:");
Console.WriteLine(mainResult.Text);
Console.WriteLine();
Console.WriteLine("Playful thread:");
Console.WriteLine(playfulResult.Text);
