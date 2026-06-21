#:package HPD-Agent.Framework@0.5.5
#:package HPD-Agent.Providers.OpenAI@0.5.5
#:property TargetFramework=net10.0

// This sample subscribes to agent events while still getting the final turn result.

using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

// Build the agent normally.
var agent = await new AgentBuilder()
                    .WithInstructions("You are a concise assistant.")
                    .WithOpenAI("gpt-5-mini")
                    .BuildAsync();

// Subscribe to the events you want to observe. The using statements unsubscribe at the end.
using var turnStarted = agent.Subscribe<MessageTurnStartedEvent>(evt => 
    Console.WriteLine($"Turn started: {evt.MessageTurnId}"));

using var messageStarted = agent.Subscribe<TextMessageStartEvent>(evt => 
    Console.WriteLine($"Assistant message started: {evt.MessageId}"));

using var reasoningStarted = agent.Subscribe<ReasoningMessageStartEvent>(evt => 
    Console.WriteLine($"Reasoning started: {evt.MessageId}"));

using var reasoning = agent.Subscribe<ReasoningDeltaEvent>(evt => 
    Console.Write(evt.Text));

using var reasoningEnded = agent.Subscribe<ReasoningMessageEndEvent>(evt => 
    Console.WriteLine($"Reasoning ended: {evt.MessageId}"));

using var output = agent.Subscribe<TextDeltaEvent>(evt => 
    Console.Write(evt.Text));

using var messageEnded = agent.Subscribe<TextMessageEndEvent>(evt => 
    Console.WriteLine($"\nAssistant message ended: {evt.MessageId}"));

using var turnFinished = agent.Subscribe<MessageTurnFinishedEvent>(evt => 
    Console.WriteLine($"Turn finished in {evt.Duration.TotalMilliseconds:N0} ms"));

// Run the turn. TextDeltaEvent prints as the model streams, and result still has the final text.
var result = await agent.RunAsync("Write a three-line welcome for someone learning HPD Agent.");
