using HPD.Agent;
using HPD.Agent.Internal.Filters;
using Microsoft.Extensions.AI;
using System.Collections.Immutable;
using Xunit;

namespace HPD.Agent.Tests.Filters;

/// <summary>
/// Tests for IterationFilterContext functionality.
/// </summary>
public class IterationFilterContextTests
{
    [Fact]
    public void Context_IsFirstIteration_ReturnsTrue_WhenIterationIsZero()
    {
        // Arrange
        var context = CreateContext(iteration: 0);

        // Act & Assert
        Assert.True(context.IsFirstIteration);
    }

    [Fact]
    public void Context_IsFirstIteration_ReturnsFalse_WhenIterationIsNonZero()
    {
        // Arrange
        var context = CreateContext(iteration: 1);

        // Act & Assert
        Assert.False(context.IsFirstIteration);
    }

    [Fact]
    public void Context_IsSuccess_ReturnsTrue_WhenResponseIsNotNullAndNoException()
    {
        // Arrange
        var context = CreateContext();
        context.Response = new ChatMessage(ChatRole.Assistant, "Test response");
        context.Exception = null;

        // Act & Assert
        Assert.True(context.IsSuccess);
    }

    [Fact]
    public void Context_IsSuccess_ReturnsFalse_WhenExceptionExists()
    {
        // Arrange
        var context = CreateContext();
        context.Response = new ChatMessage(ChatRole.Assistant, "Test response");
        context.Exception = new Exception("Test error");

        // Act & Assert
        Assert.False(context.IsSuccess);
    }

    [Fact]
    public void Context_IsFailure_ReturnsTrue_WhenExceptionExists()
    {
        // Arrange
        var context = CreateContext();
        context.Exception = new Exception("Test error");

        // Act & Assert
        Assert.True(context.IsFailure);
    }

    [Fact]
    public void Context_IsFinalIteration_ReturnsTrue_WhenSuccessAndNoToolCalls()
    {
        // Arrange
        var context = CreateContext();
        context.Response = new ChatMessage(ChatRole.Assistant, "Final response");
        context.ToolCalls = Array.Empty<FunctionCallContent>();
        context.Exception = null;

        // Act & Assert
        Assert.True(context.IsFinalIteration);
    }

    [Fact]
    public void Context_IsFinalIteration_ReturnsFalse_WhenToolCallsExist()
    {
        // Arrange
        var context = CreateContext();
        context.Response = new ChatMessage(ChatRole.Assistant, "Response with tools");
        context.ToolCalls = new[] { new FunctionCallContent("test_call_id", "test_function") };
        context.Exception = null;

        // Act & Assert
        Assert.False(context.IsFinalIteration);
    }

    [Fact]
    public void Context_Messages_CanBeModified()
    {
        // Arrange
        var context = CreateContext();
        var originalCount = context.Messages.Count;

        // Act
        context.Messages.Add(new ChatMessage(ChatRole.User, "New message"));

        // Assert
        Assert.Equal(originalCount + 1, context.Messages.Count);
    }

    [Fact]
    public void Context_Options_CanBeModified()
    {
        // Arrange
        var context = CreateContext();
        context.Options = new ChatOptions { Instructions = "Original instructions" };

        // Act
        context.Options.Instructions += "\nAdditional instructions";

        // Assert
        Assert.Contains("Additional instructions", context.Options.Instructions);
    }

    [Fact]
    public void Context_Properties_CanStoreCustomData()
    {
        // Arrange
        var context = CreateContext();

        // Act
        context.Properties["CustomKey"] = "CustomValue";
        context.Properties["CustomNumber"] = 42;

        // Assert
        Assert.Equal("CustomValue", context.Properties["CustomKey"]);
        Assert.Equal(42, context.Properties["CustomNumber"]);
    }

    [Fact]
    public void Context_SkipLLMCall_DefaultsToFalse()
    {
        // Arrange
        var context = CreateContext();

        // Act & Assert
        Assert.False(context.SkipLLMCall);
    }

    private static IterationFilterContext CreateContext(int iteration = 0)
    {
        var state = AgentLoopState.Initial(
            messages: new List<ChatMessage>(),
            runId: "test-run-id",
            conversationId: "test-conv-id",
            agentName: "TestAgent");

        return new IterationFilterContext
        {
            Iteration = iteration,
            AgentName = "TestAgent",
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions(),
            State = state,
            CancellationToken = CancellationToken.None
        };
    }
}
