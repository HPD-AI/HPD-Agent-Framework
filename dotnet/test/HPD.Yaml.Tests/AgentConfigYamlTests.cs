using HPD.Agent;
using HPD.Agent.Serialization;

namespace HPD.Yaml.Tests;

public class AgentConfigYamlTests
{
    [Fact]
    public void Deserialize_MinimalAgentConfig_ParsesDefaults()
    {
        var yaml = @"
name: test-agent
systemInstructions: You are a test agent.
";

        var config = AgentConfigYaml.FromYaml(yaml);

        Assert.Equal("test-agent", config.Name);
        Assert.Equal("You are a test agent.", config.SystemInstructions);
        Assert.Equal(10, config.MaxAgenticIterations); // default
    }

    [Fact]
    public void Deserialize_FullAgentConfig_ParsesAllFields()
    {
        var yaml = @"
name: research-agent
systemInstructions: You are a research assistant.
maxAgenticIterations: 20
continuationExtensionAmount: 5
provider:
  providerKey: openai
  modelName: gpt-4o
  endpoint: https://api.openai.com/v1
collapsing:
  enabled: true
defaultReasoning:
  effort: High
  output: Summary
preserveReasoningInHistory: true
coalesceDeltas: false
";

        var config = AgentConfigYaml.FromYaml(yaml);

        Assert.Equal("research-agent", config.Name);
        Assert.Equal(20, config.MaxAgenticIterations);
        Assert.Equal(5, config.ContinuationExtensionAmount);
        Assert.NotNull(config.Provider);
        Assert.Equal("openai", config.Provider!.ProviderKey);
        Assert.Equal("gpt-4o", config.Provider!.ModelName);
        Assert.True(config.PreserveReasoningInHistory);
        Assert.NotNull(config.DefaultReasoning);
        Assert.Equal(ReasoningEffort.High, config.DefaultReasoning!.Effort);
        Assert.Equal(ReasoningOutput.Summary, config.DefaultReasoning.Output);
    }

    [Fact]
    public void RoundTrip_AgentConfig_PreservesValues()
    {
        var original = new AgentConfig
        {
            Name = "round-trip-agent",
            SystemInstructions = "Test instructions",
            MaxAgenticIterations = 15,
            Provider = new ProviderConfig
            {
                ProviderKey = "ollama",
                ModelName = "llama3"
            }
        };

        var yaml = AgentConfigYaml.ToYaml(original);
        var deserialized = AgentConfigYaml.FromYaml(yaml);

        Assert.Equal(original.Name, deserialized.Name);
        Assert.Equal(original.SystemInstructions, deserialized.SystemInstructions);
        Assert.Equal(original.MaxAgenticIterations, deserialized.MaxAgenticIterations);
        Assert.Equal(original.Provider.ProviderKey, deserialized.Provider?.ProviderKey);
        Assert.Equal(original.Provider.ModelName, deserialized.Provider?.ModelName);
    }

    [Fact]
    public void Deserialize_WithToolkits_ParsesList()
    {
        var yaml = @"
name: tool-agent
systemInstructions: Agent with tools
toolkits:
  - name: MathToolkit
  - name: SearchToolkit
    functions:
      - Search
      - Browse
";

        var config = AgentConfigYaml.FromYaml(yaml);

        Assert.Equal(2, config.Toolkits.Count);
        Assert.Equal("MathToolkit", config.Toolkits[0].Name);
        Assert.Equal("SearchToolkit", config.Toolkits[1].Name);
    }
}
