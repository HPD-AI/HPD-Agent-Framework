using Microsoft.Extensions.AI;
using System.Reflection;
using Xunit;

namespace HPD_Agent.Tests.Core;

/// <summary>
/// Unit tests for Agent helper methods (FilterContainerResults, etc.)
/// Demonstrates fast unit testing without I/O dependencies.
/// </summary>
public class AgentHelperMethodsTests
{
    // Test helper to access private static method via reflection
    private static List<AIContent> FilterContainerResults(
        IList<AIContent> contents,
        IList<FunctionCallContent> toolRequests,
        ChatOptions? options)
    {
        var agentType = typeof(Agent);
        var method = agentType.GetMethod("FilterContainerResults", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        if (method == null)
            throw new InvalidOperationException("FilterContainerResults method not found");
            
        var result = method.Invoke(null, new object?[] { contents, toolRequests, options });
        return (List<AIContent>)result!;
    }

    #region FilterContainerResults Tests

    [Fact]
    public void FilterContainerResults_RemovesContainerExpansions()
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
        var filtered = FilterContainerResults(contents, toolRequests, options);

        // Assert
        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain(filtered, c => c is FunctionResultContent frc && frc.CallId == "call1");
        Assert.Contains(filtered, c => c is FunctionResultContent frc && frc.CallId == "call2");
        Assert.Contains(filtered, c => c is FunctionResultContent frc && frc.CallId == "call3");
    }

    [Fact]
    public void FilterContainerResults_PreservesNonFunctionContent()
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
        var filtered = FilterContainerResults(contents, toolRequests, options);

        // Assert: Text content should pass through
        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, c => c is TextContent);
        Assert.Contains(filtered, c => c is FunctionResultContent);
    }

    [Fact]
    public void FilterContainerResults_HandlesEmptyLists()
    {
        // Arrange
        var contents = new List<AIContent>();
        var toolRequests = new List<FunctionCallContent>();
        var options = new ChatOptions();

        // Act
        var filtered = FilterContainerResults(contents, toolRequests, options);

        // Assert
        Assert.Empty(filtered);
    }

    [Fact]
    public void FilterContainerResults_HandlesNullOptions()
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
        var filtered = FilterContainerResults(contents, toolRequests, options: null);

        // Assert: Without options, can't determine containers, so all results pass through
        Assert.Single(filtered);
        Assert.Contains(filtered, c => c is FunctionResultContent frc && frc.CallId == "call1");
    }

    [Fact]
    public void FilterContainerResults_MultipleContainersRemoved()
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
        var filtered = FilterContainerResults(contents, toolRequests, options);

        // Assert: Only the regular function result should remain
        Assert.Single(filtered);
        var result = Assert.IsType<FunctionResultContent>(filtered[0]);
        Assert.Equal("call3", result.CallId);
    }

    [Fact]
    public void FilterContainerResults_MixedContentTypes()
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
        var filtered = FilterContainerResults(contents, toolRequests, options);

        // Assert: Container result removed, but text content and regular result preserved
        Assert.Equal(3, filtered.Count);
        Assert.Contains(filtered, c => c is TextContent tc && tc.Text == "Thinking...");
        Assert.Contains(filtered, c => c is TextContent tc && tc.Text == "Done!");
        Assert.Contains(filtered, c => c is FunctionResultContent frc && frc.CallId == "call2");
        Assert.DoesNotContain(filtered, c => c is FunctionResultContent frc && frc.CallId == "call1");
    }

    [Fact]
    public void FilterContainerResults_NoContainers_AllResultsPreserved()
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
        var filtered = FilterContainerResults(contents, toolRequests, options);

        // Assert: No containers, so all results preserved
        Assert.Equal(3, filtered.Count);
    }

    #endregion
}
