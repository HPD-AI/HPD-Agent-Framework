#:package HPD-Agent.Framework@0.5.5
#:package HPD-Agent.Providers.OpenAI@0.5.5
#:property TargetFramework=net10.0

// This sample registers one local tool and lets the agent call it.

using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

// Register one method from the harness. The model sees get_weather and the harness handles the call.
var agent = await new AgentBuilder()
                    .WithInstructions("You are a helpful assistant. When weather is requested, call get_weather and answer using the tool result.")
                    .WithOpenAI("gpt-5-mini")
                    .WithTool<WeatherToolHarness>("get_weather")
                    .BuildAsync();

var result = await agent.RunAsync("What is the weather in Chicago, IL, USA?");

Console.WriteLine(result.Text);

// A ToolHarness can hold one tool. The next sample shows a grouped harness.
public class WeatherToolHarness
{
    [AIFunction(Name = "get_weather")]
    [AIDescription("Gets the current weather for a location.")]
    public string GetWeather([AIDescription("The city or location to get weather for.")] string location)
    {
        return $"It is sunny and 72 F in {location}.";
    }
}
