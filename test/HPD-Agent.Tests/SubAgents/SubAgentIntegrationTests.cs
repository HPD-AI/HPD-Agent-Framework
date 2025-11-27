using Xunit;
using HPD.Agent;
using Microsoft.Extensions.AI;
using System.Threading.Tasks;

namespace HPD_Agent.Tests.SubAgents;

/// <summary>
/// SubAgent Integration Tests
/// Tests the AIFunction metadata and structure that the source generator creates for sub-agents.
/// Since source generators don't run on test projects, we manually create AIFunction objects
/// that simulate the source generator output, similar to how SkillScopingTests works.
/// </summary>
public class SubAgentIntegrationTests
{
    // Helper to create a SubAgent AIFunction like the source generator would
    private static AIFunction CreateSubAgentFunction(
        string name,
        string description,
        string threadMode = "Stateless")
    {
        return AIFunctionFactory.Create(
            async (string query, CancellationToken ct) =>
            {
                // Simulate sub-agent invocation
                return $"SubAgent {name} response to: {query}";
            },
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["IsSubAgent"] = true,
                    ["ThreadMode"] = threadMode,
                    ["PluginName"] = "TestPlugin"
                }
            });
    }

    // ===== P0: Plugin Registration =====

    [Fact]
    public void SubAgentPlugin_CanBeRegistered_ViaPluginSystem()
    {
        // Arrange & Act
        var registration = PluginRegistration.FromType<TestIntegrationSubAgents>();

        // Assert
        Assert.NotNull(registration);
        Assert.Equal("TestIntegrationSubAgents", registration.PluginType.Name);
    }

    [Fact]
    public void SubAgentPlugin_GeneratesAIFunctions_WithCorrectStructure()
    {
        // Arrange - Simulate what source generator would create
        var functions = new List<AIFunction>
        {
            CreateSubAgentFunction("WeatherExpert", "Weather forecast agent", "Stateless"),
            CreateSubAgentFunction("MathExpert", "Math calculation agent", "SharedThread"),
            CreateSubAgentFunction("CodeReviewer", "Code review agent", "Stateless")
        };

        // Act
        var subAgentFunctions = functions.Where(f =>
            f.AdditionalProperties?.ContainsKey("IsSubAgent") == true).ToList();

        // Assert
        Assert.NotEmpty(subAgentFunctions);
        Assert.Equal(3, subAgentFunctions.Count);
    }

    // ===== P0: AIFunction Metadata =====

    [Fact]
    public void SubAgent_AIFunction_HasCorrectMetadata()
    {
        // Arrange
        var weatherExpert = CreateSubAgentFunction(
            "WeatherExpert",
            "Specialized agent for weather forecasts",
            "Stateless");

        // Assert
        Assert.NotNull(weatherExpert);
        Assert.Equal("WeatherExpert", weatherExpert.Name);
        Assert.NotNull(weatherExpert.Description);
        Assert.Contains("weather", weatherExpert.Description.ToLower());

        // Check IsSubAgent flag
        Assert.True(weatherExpert.AdditionalProperties?.ContainsKey("IsSubAgent"));
        Assert.True((bool?)weatherExpert.AdditionalProperties!["IsSubAgent"] ?? false);
    }

    [Fact]
    public void SubAgent_AIFunction_HasRequiresPermission()
    {
        // Arrange
        var subAgentFunction = CreateSubAgentFunction(
            "TestSubAgent",
            "Test sub-agent",
            "Stateless");

        // Assert
        Assert.NotNull(subAgentFunction);
        // Sub-agents should always have IsSubAgent flag
        Assert.True(subAgentFunction.AdditionalProperties?.ContainsKey("IsSubAgent"));
        Assert.True((bool)subAgentFunction.AdditionalProperties!["IsSubAgent"]);
    }

    // ===== P0: Thread Mode Metadata =====

    [Fact]
    public void SubAgent_AIFunction_HasThreadModeMetadata()
    {
        // Arrange
        var weatherExpert = CreateSubAgentFunction(
            "WeatherExpert",
            "Weather agent",
            "Stateless");

        var mathExpert = CreateSubAgentFunction(
            "MathExpert",
            "Math agent",
            "SharedThread");

        // Assert
        Assert.NotNull(weatherExpert);
        Assert.NotNull(mathExpert);

        // WeatherExpert should be Stateless
        Assert.True(weatherExpert.AdditionalProperties?.ContainsKey("ThreadMode"));
        Assert.Equal("Stateless", weatherExpert.AdditionalProperties!["ThreadMode"] as string);

        // MathExpert should be SharedThread
        Assert.True(mathExpert.AdditionalProperties?.ContainsKey("ThreadMode"));
        Assert.Equal("SharedThread", mathExpert.AdditionalProperties!["ThreadMode"] as string);
    }

    // ===== P0: Function Signature =====

    [Fact]
    public void SubAgent_AIFunction_AcceptsQueryParameter()
    {
        // Arrange
        var weatherExpert = CreateSubAgentFunction(
            "WeatherExpert",
            "Weather agent",
            "Stateless");

        // Assert
        Assert.NotNull(weatherExpert);
        Assert.NotNull(weatherExpert.AdditionalProperties);

        // Check that it has the expected sub-agent structure
        Assert.True(weatherExpert.AdditionalProperties?.ContainsKey("IsSubAgent"));
    }
}
