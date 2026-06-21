#:package HPD-Agent.Framework@0.5.5
#:package HPD-Agent.Providers.OpenAI@0.5.5
#:property TargetFramework=net10.0

// This sample creates the smallest useful HPD Agent and reads the final response.

using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

// Build an agent backed by OpenAI.
var agent = await new AgentBuilder()
                    .WithInstructions("You are a concise assistant.")
                    .WithOpenAI("gpt-5-mini")
                    .BuildAsync();

// Run one turn and print the final text.
var result = await agent.RunAsync("hello how are you doing");

Console.WriteLine(result.Text);
