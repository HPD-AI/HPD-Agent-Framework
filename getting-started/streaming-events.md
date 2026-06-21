# Streaming Events

This page prints assistant text as it arrives during a run.

The public first-reader path uses direct in-process typed event subscriptions with `RunAsync(...)`.

Continue in the same `HpdAgentQuickstart` folder from [Hello Agent](hello-agent.md). This page uses the same agent, but adds event subscriptions before the run.

## Add Program.cs

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("You are a concise helpful assistant.")
    .BuildAsync();

using var output = agent.Subscribe<TextDeltaEvent>(evt => Console.Write(evt.Text));
using var finished = agent.Subscribe<MessageTurnFinishedEvent>(evt =>
    Console.WriteLine($"\nFinished in {evt.Duration.TotalMilliseconds:N0} ms"));

_ = await agent.RunAsync("Write a three-line welcome for someone learning HPD Agent.");
```

Run it:

```bash
dotnet run
```

## What Happens

`agent.Subscribe<TextDeltaEvent>(...)` registers a handler for progressive assistant text. The sample writes each text delta to the console as the turn runs.

`agent.Subscribe<MessageTurnFinishedEvent>(...)` registers a handler for turn completion. The sample prints the elapsed duration when the run finishes.

The `using var` declarations keep each subscription active until the end of the program and dispose the subscriptions afterward.

`RunAsync(...)` still performs the turn. Event subscriptions observe what happens during the turn; they do not replace the run call.

This page shows the direct `Agent` API. ASP.NET Core hosted clients observe the same live activity through SSE or WebSocket and submit input through hosted routes instead of calling `agent.Subscribe(...)` or `RunAsync(...)`.

`result.Text` is still available after the run. In this sample, the text has already been written by the `TextDeltaEvent` handler, so the code does not print `result.Text` again.

## When To Use Events

Use typed subscriptions when in-process code such as a console app, local UI, TUI runtime, hosted server implementation, or bot adapter needs to react while an agent run is happening.

Text streaming is the simplest event projection. Richer clients can group the same event stream into transcripts, tool timelines, permission prompts, workflow nodes, subagent activity, or trace views.

Use `SubscribeAny(...)` when you want to inspect or route every event:

```csharp
using var all = agent.SubscribeAny(evt =>
{
    Console.WriteLine($"{evt.GetType().Name} {evt.Metadata?.AgentChain.LastOrDefault()}");
});
```

This page keeps to two event types. More event families exist for sessions, threads, tools, middleware, permissions, retries, workflows, and model calls. See [Events Overview](../guides/events/overview.md), [Event Streams And Hierarchies](../concepts/event-streams-and-hierarchies.md), and [Events](../reference/events.md).

Middleware can also emit or respond to lifecycle behavior around turns and functions. See [Middleware Lifecycle](../concepts/middleware-lifecycle.md).

## Common Mistakes

If no streaming text appears, make sure the subscription is registered before `RunAsync(...)`.

If you subscribed but nothing happens, remember that subscriptions only observe a run. `RunAsync(...)` still starts the turn.

## Next

Next: register one local function in [Add A Tool](add-a-tool.md).
