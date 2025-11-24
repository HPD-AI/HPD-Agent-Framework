using Microsoft.Extensions.AI;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using Xunit;
using HPD.Agent;
namespace HPD_Agent.Tests.Core;

/// <summary>
/// Unit tests for Agent helper methods (MiddlewareContainerResults, etc.)
/// Demonstrates fast unit testing without I/O dependencies.
/// </summary>
public class AgentHelperMethodsTests
{
    // Test helper to access private static method via reflection
    private static List<AIContent> MiddlewareContainerResults(
        IList<AIContent> contents,
        IList<FunctionCallContent> toolRequests,
        ChatOptions? options)
    {
        var agentType = typeof(AgentCore);
#pragma warning disable IL2065
        var method = agentType.GetMethod("MiddlewareContainerResults", 
            BindingFlags.NonPublic | BindingFlags.Static);
#pragma warning restore IL2065
        
        if (method == null)
            throw new InvalidOperationException("MiddlewareContainerResults method not found");
            
        var result = method.Invoke(null, new object?[] { contents, toolRequests, options });
        return (List<AIContent>)result!;
    }

    #region MiddlewareContainerResults Tests

    [Fact]
    public void MiddlewareContainerResults_RemovesContainerExpansions()
    {
        // Arrange
        var contents = new List<AIContent>
        {
            new FunctionResultContent("call1", result: "PluginExpanded"),  // Container
            new FunctionResultContent("call2", result: "42"),              // Regular
            new FunctionResultContent("call3", result: "Hello")            // Regular
        };

        var toolRequests = new List<FunctionCallContent>
        {
            new("call1", "ExpandPlugin", null),
            new("call2", "Calculate", null),
            new("call3", "GetGreeting", null)
        };

        var containerFunc = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("expanded"),
            new HPDAIFunctionFactoryOptions 
            { 
                Name = "ExpandPlugin", 
                Description = "Expands plugin",
                AdditionalProperties = new Dictionary<string, object?> { ["IsContainer"] = true }
            });

        var regularFunc1 = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult(42),
            new HPDAIFunctionFactoryOptions { Name = "Calculate", Description = "Calculates" });
        
        var regularFunc2 = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Hello"),
            new HPDAIFunctionFactoryOptions { Name = "GetGreeting", Description = "Gets greeting" });

        var options = new ChatOptions
        {
            Tools = new AITool[] { containerFunc, regularFunc1, regularFunc2 }
        };

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options);

        // Assert
        Assert.Equal(2, Middlewareed.Count);
        Assert.DoesNotContain(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call1");
        Assert.Contains(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call2");
        Assert.Contains(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call3");
    }

    [Fact]
    public void MiddlewareContainerResults_PreservesNonFunctionContent()
    {
        // Arrange
        var contents = new List<AIContent>
        {
            new TextContent("Some text"),
            new FunctionResultContent("call1", "Result")
        };

        var toolRequests = new List<FunctionCallContent>
        {
            new("call1", "RegularFunction", null)
        };

        var options = new ChatOptions
        {
            Tools = new AITool[]
            {
                HPDAIFunctionFactory.Create(
                    async (args, ct) => await Task.FromResult("Result"),
                    new HPDAIFunctionFactoryOptions { Name = "RegularFunction", Description = "Regular" })
            }
        };

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options);

        // Assert: Text content should pass through
        Assert.Equal(2, Middlewareed.Count);
        Assert.Contains(Middlewareed, c => c is TextContent);
        Assert.Contains(Middlewareed, c => c is FunctionResultContent);
    }

    [Fact]
    public void MiddlewareContainerResults_HandlesEmptyLists()
    {
        // Arrange
        var contents = new List<AIContent>();
        var toolRequests = new List<FunctionCallContent>();
        var options = new ChatOptions();

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options);

        // Assert
        Assert.Empty(Middlewareed);
    }

    [Fact]
    public void MiddlewareContainerResults_HandlesNullOptions()
    {
        // Arrange
        var contents = new List<AIContent>
        {
            new FunctionResultContent("call1", "Result")
        };

        var toolRequests = new List<FunctionCallContent>
        {
            new("call1", "SomeFunction", null)
        };

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options: null);

        // Assert: Without options, can't determine containers, so all results pass through
        Assert.Single(Middlewareed);
        Assert.Contains(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call1");
    }

    [Fact]
    public void MiddlewareContainerResults_MultipleContainersRemoved()
    {
        // Arrange
        var contents = new List<AIContent>
        {
            new FunctionResultContent("call1", "PluginA expanded"),
            new FunctionResultContent("call2", "PluginB expanded"),
            new FunctionResultContent("call3", "Actual result")
        };

        var toolRequests = new List<FunctionCallContent>
        {
            new("call1", "ExpandPluginA", null),
            new("call2", "ExpandPluginB", null),
            new("call3", "DoWork", null)
        };

        var containerA = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("expanded"),
            new HPDAIFunctionFactoryOptions 
            { 
                Name = "ExpandPluginA", 
                Description = "Expands A",
                AdditionalProperties = new Dictionary<string, object?> { ["IsContainer"] = true }
            });

        var containerB = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("expanded"),
            new HPDAIFunctionFactoryOptions 
            { 
                Name = "ExpandPluginB", 
                Description = "Expands B",
                AdditionalProperties = new Dictionary<string, object?> { ["IsContainer"] = true }
            });

        var regularFunc = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("result"),
            new HPDAIFunctionFactoryOptions { Name = "DoWork", Description = "Does work" });

        var options = new ChatOptions
        {
            Tools = new AITool[] { containerA, containerB, regularFunc }
        };

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options);

        // Assert: Only the regular function result should remain
        Assert.Single(Middlewareed);
        var result = Assert.IsType<FunctionResultContent>(Middlewareed[0]);
        Assert.Equal("call3", result.CallId);
    }

    [Fact]
    public void MiddlewareContainerResults_MixedContentTypes()
    {
        // Arrange
        var contents = new List<AIContent>
        {
            new TextContent("Thinking..."),
            new FunctionResultContent("call1", "Container expanded"),
            new FunctionResultContent("call2", "Actual result"),
            new TextContent("Done!")
        };

        var toolRequests = new List<FunctionCallContent>
        {
            new("call1", "ExpandPlugin", null),
            new("call2", "Calculate", null)
        };

        var container = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("expanded"),
            new HPDAIFunctionFactoryOptions 
            { 
                Name = "ExpandPlugin", 
                Description = "Expands",
                AdditionalProperties = new Dictionary<string, object?> { ["IsContainer"] = true }
            });

        var regular = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult(42),
            new HPDAIFunctionFactoryOptions { Name = "Calculate", Description = "Calculates" });

        var options = new ChatOptions
        {
            Tools = new AITool[] { container, regular }
        };

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options);

        // Assert: Container result removed, but text content and regular result preserved
        Assert.Equal(3, Middlewareed.Count);
        Assert.Contains(Middlewareed, c => c is TextContent tc && tc.Text == "Thinking...");
        Assert.Contains(Middlewareed, c => c is TextContent tc && tc.Text == "Done!");
        Assert.Contains(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call2");
        Assert.DoesNotContain(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call1");
    }

    [Fact]
    public void MiddlewareContainerResults_NoContainers_AllResultsPreserved()
    {
        // Arrange
        var contents = new List<AIContent>
        {
            new FunctionResultContent("call1", "Result 1"),
            new FunctionResultContent("call2", "Result 2"),
            new FunctionResultContent("call3", "Result 3")
        };

        var toolRequests = new List<FunctionCallContent>
        {
            new("call1", "Func1", null),
            new("call2", "Func2", null),
            new("call3", "Func3", null)
        };

        var options = new ChatOptions
        {
            Tools = new AITool[]
            {
                HPDAIFunctionFactory.Create(
                    async (args, ct) => await Task.FromResult("Result 1"),
                    new HPDAIFunctionFactoryOptions { Name = "Func1", Description = "Function 1" }),
                HPDAIFunctionFactory.Create(
                    async (args, ct) => await Task.FromResult("Result 2"),
                    new HPDAIFunctionFactoryOptions { Name = "Func2", Description = "Function 2" }),
                HPDAIFunctionFactory.Create(
                    async (args, ct) => await Task.FromResult("Result 3"),
                    new HPDAIFunctionFactoryOptions { Name = "Func3", Description = "Function 3" })
            }
        };

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options);

        // Assert: No containers, so all results preserved
        Assert.Equal(3, Middlewareed.Count);
    }

    #endregion

    #region Skill Container Middlewareing Tests

    [Fact]
    public void MiddlewareContainerResults_SkillContainers_AreMiddlewareed()
    {
        // Arrange - ALL containers (including skill containers) are now Middlewareed
        // Container activation messages are only relevant within the current turn
        var contents = new List<AIContent>
        {
            new FunctionResultContent("call1", "Skill expanded. Available functions: Func1, Func2"), // Skill container
            new FunctionResultContent("call2", "42")  // Regular function
        };

        var toolRequests = new List<FunctionCallContent>
        {
            new("call1", "QuickAnalysis", null),  // Skill container
            new("call2", "Calculate", null)        // Regular function
        };

        var skillContainer = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Skill expanded"),
            new HPDAIFunctionFactoryOptions
            {
                Name = "QuickAnalysis",
                Description = "Analysis skill",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["IsSkill"] = true  // This marks it as a skill container
                }
            });

        var regularFunc = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult(42),
            new HPDAIFunctionFactoryOptions { Name = "Calculate", Description = "Calculates" });

        var options = new ChatOptions
        {
            Tools = new AITool[] { skillContainer, regularFunc }
        };

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options);

        // Assert - Skill container result SHOULD be Middlewareed (new behavior)
        Assert.Single(Middlewareed);
        Assert.DoesNotContain(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call1");
        Assert.Contains(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call2");
    }

    [Fact]
    public void MiddlewareContainerResults_ScopedPluginContainers_AreMiddlewareed()
    {
        // Arrange - Scoped plugin containers SHOULD be Middlewareed
        var contents = new List<AIContent>
        {
            new FunctionResultContent("call1", "Plugin expanded"),  // Scoped plugin container
            new FunctionResultContent("call2", "Result")            // Regular function
        };

        var toolRequests = new List<FunctionCallContent>
        {
            new("call1", "MathPlugin", null),      // Scoped plugin container
            new("call2", "Calculate", null)         // Regular function
        };

        var scopedPluginContainer = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Plugin expanded"),
            new HPDAIFunctionFactoryOptions
            {
                Name = "MathPlugin",
                Description = "Math plugin",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["IsScope"] = true  // This marks it as a scoped plugin container
                }
            });

        var regularFunc = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Result"),
            new HPDAIFunctionFactoryOptions { Name = "Calculate", Description = "Calculates" });

        var options = new ChatOptions
        {
            Tools = new AITool[] { scopedPluginContainer, regularFunc }
        };

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options);

        // Assert - Scoped plugin container result SHOULD be Middlewareed
        Assert.Single(Middlewareed);
        Assert.DoesNotContain(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call1");
        Assert.Contains(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call2");
    }

    [Fact]
    public void MiddlewareContainerResults_MixedSkillAndPluginContainers()
    {
        // Arrange - Mixed scenario with both skill and plugin containers
        var contents = new List<AIContent>
        {
            new FunctionResultContent("call1", "Plugin expanded"),  // Scoped plugin container (should be Middlewareed)
            new FunctionResultContent("call2", "Skill expanded"),   // Skill container (should ALSO be Middlewareed - new behavior)
            new FunctionResultContent("call3", "Result")            // Regular function
        };

        var toolRequests = new List<FunctionCallContent>
        {
            new("call1", "MathPlugin", null),       // Scoped plugin container
            new("call2", "QuickAnalysis", null),    // Skill container
            new("call3", "Calculate", null)         // Regular function
        };

        var scopedPluginContainer = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Plugin expanded"),
            new HPDAIFunctionFactoryOptions
            {
                Name = "MathPlugin",
                Description = "Math plugin",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["IsScope"] = true
                }
            });

        var skillContainer = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Skill expanded"),
            new HPDAIFunctionFactoryOptions
            {
                Name = "QuickAnalysis",
                Description = "Analysis skill",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["IsSkill"] = true
                }
            });

        var regularFunc = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Result"),
            new HPDAIFunctionFactoryOptions { Name = "Calculate", Description = "Calculates" });

        var options = new ChatOptions
        {
            Tools = new AITool[] { scopedPluginContainer, skillContainer, regularFunc }
        };

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options);

        // Assert - Both containers Middlewareed, only regular function remains
        Assert.Single(Middlewareed);
        // Plugin container should be Middlewareed
        Assert.DoesNotContain(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call1");
        // Skill container should ALSO be Middlewareed (new behavior)
        Assert.DoesNotContain(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call2");
        // Regular function should remain
        Assert.Contains(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call3");
    }

    [Fact]
    public void MiddlewareContainerResults_LegacyScopedPluginContainer_IsMiddlewareed()
    {
        // Arrange - Legacy scoped plugin container (no IsScope flag, just IsContainer)
        var contents = new List<AIContent>
        {
            new FunctionResultContent("call1", "Plugin expanded"),  // Legacy scoped plugin
            new FunctionResultContent("call2", "Result")            // Regular function
        };

        var toolRequests = new List<FunctionCallContent>
        {
            new("call1", "LegacyPlugin", null),
            new("call2", "Calculate", null)
        };

        var legacyContainer = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Plugin expanded"),
            new HPDAIFunctionFactoryOptions
            {
                Name = "LegacyPlugin",
                Description = "Legacy plugin",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true
                    // No IsScope or IsSkill flag - this is a legacy scoped plugin
                }
            });

        var regularFunc = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Result"),
            new HPDAIFunctionFactoryOptions { Name = "Calculate", Description = "Calculates" });

        var options = new ChatOptions
        {
            Tools = new AITool[] { legacyContainer, regularFunc }
        };

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options);

        // Assert - Legacy scoped plugin should be Middlewareed
        Assert.Single(Middlewareed);
        Assert.DoesNotContain(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call1");
        Assert.Contains(Middlewareed, c => c is FunctionResultContent frc && frc.CallId == "call2");
    }

    [Fact]
    public void MiddlewareContainerResults_MultipleSkillContainers_AllMiddlewareed()
    {
        // Arrange - Multiple skill containers should all be Middlewareed (new behavior)
        var contents = new List<AIContent>
        {
            new FunctionResultContent("call1", "Skill A expanded"),
            new FunctionResultContent("call2", "Skill B expanded"),
            new FunctionResultContent("call3", "Skill C expanded")
        };

        var toolRequests = new List<FunctionCallContent>
        {
            new("call1", "SkillA", null),
            new("call2", "SkillB", null),
            new("call3", "SkillC", null)
        };

        var skillA = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Skill A expanded"),
            new HPDAIFunctionFactoryOptions
            {
                Name = "SkillA",
                Description = "Skill A",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["IsSkill"] = true
                }
            });

        var skillB = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Skill B expanded"),
            new HPDAIFunctionFactoryOptions
            {
                Name = "SkillB",
                Description = "Skill B",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["IsSkill"] = true
                }
            });

        var skillC = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Skill C expanded"),
            new HPDAIFunctionFactoryOptions
            {
                Name = "SkillC",
                Description = "Skill C",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["IsSkill"] = true
                }
            });

        var options = new ChatOptions
        {
            Tools = new AITool[] { skillA, skillB, skillC }
        };

        // Act
        var Middlewareed = MiddlewareContainerResults(contents, toolRequests, options);

        // Assert - All skill container results should be Middlewareed (new behavior)
        Assert.Empty(Middlewareed);
    }

    #endregion
}
