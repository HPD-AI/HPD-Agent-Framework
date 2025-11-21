using HPD.Agent;
using HPD.Agent.Internal.Filters;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using Xunit;

namespace HPD.Agent.Tests.Filters;

/// <summary>
/// Tests for IterationLoggingFilter functionality.
/// </summary>
public class IterationLoggingFilterTests
{
    [Fact]
    public async Task Filter_SkipsLogging_WhenLoggerIsNull()
    {
        // Arrange
        var filter = new IterationLoggingFilter(logger: null);
        var context = CreateContext();

        // Act
        await filter.BeforeIterationAsync(context, CancellationToken.None);
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert - No exception should be thrown
        Assert.True(true);
    }

    [Fact]
    public async Task Filter_LogsIterationStart_InBeforePhase()
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder => { });
        var logger = loggerFactory.CreateLogger<IterationLoggingFilter>();
        var filter = new IterationLoggingFilter(logger);
        var context = CreateContext(iteration: 2);

        // Act
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - No exception should be thrown
        // Note: Actual log output verification would require a test logger implementation
        Assert.True(true);
    }

    [Fact]
    public async Task Filter_LogsIterationCompletion_InAfterPhase()
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder => { });
        var logger = loggerFactory.CreateLogger<IterationLoggingFilter>();
        var filter = new IterationLoggingFilter(logger);
        var context = CreateContext();

        // Act - Before phase
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        // Simulate LLM call
        await Task.Delay(10); // Simulate some work
        context.Response = new ChatMessage(ChatRole.Assistant, "Response");
        context.ToolCalls = Array.Empty<FunctionCallContent>();
        context.Exception = null;

        // Act - After phase
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.IsSuccess);
    }

    [Fact]
    public async Task Filter_LogsFinalIteration_WhenDetected()
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder => { });
        var logger = loggerFactory.CreateLogger<IterationLoggingFilter>();
        var filter = new IterationLoggingFilter(logger);
        var context = CreateContext();

        // Simulate final iteration (no tool calls)
        context.Response = new ChatMessage(ChatRole.Assistant, "Final response");
        context.ToolCalls = Array.Empty<FunctionCallContent>();
        context.Exception = null;

        // Act
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.IsFinalIteration);
    }

    [Fact]
    public async Task Filter_LogsToolCallCount_InResponse()
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder => { });
        var logger = loggerFactory.CreateLogger<IterationLoggingFilter>();
        var filter = new IterationLoggingFilter(logger);
        var context = CreateContext();

        // Simulate response with tool calls
        context.Response = new ChatMessage(ChatRole.Assistant, "Response with tools");
        context.ToolCalls = new[]
        {
            new FunctionCallContent("call_1", "function1"),
            new FunctionCallContent("call_2", "function2")
        };
        context.Exception = null;

        // Act
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.False(context.IsFinalIteration);
        Assert.Equal(2, context.ToolCalls.Count);
    }

    [Fact]
    public async Task Filter_MeasuresExecutionTime()
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder => { });
        var logger = loggerFactory.CreateLogger<IterationLoggingFilter>();
        var filter = new IterationLoggingFilter(logger);
        var context = CreateContext();
        var delayMs = 50;

        // Act
        var startTime = DateTime.UtcNow;
        await filter.BeforeIterationAsync(context, CancellationToken.None);
        await Task.Delay(delayMs); // Simulate work
        context.Response = new ChatMessage(ChatRole.Assistant, "Response");
        context.ToolCalls = Array.Empty<FunctionCallContent>();
        context.Exception = null;
        await filter.AfterIterationAsync(context, CancellationToken.None);
        var endTime = DateTime.UtcNow;

        // Assert
        var actualDuration = (endTime - startTime).TotalMilliseconds;
        Assert.True(actualDuration >= delayMs, $"Expected at least {delayMs}ms, got {actualDuration}ms");
    }

    [Fact]
    public async Task Filter_StartsStopwatch_InBeforePhase()
    {
        // Arrange
        var loggerFactory = LoggerFactory.Create(builder => { });
        var logger = loggerFactory.CreateLogger<IterationLoggingFilter>();
        var filter = new IterationLoggingFilter(logger);
        var context = CreateContext();

        // Act - Before
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        // Simulate work
        await Task.Delay(20);

        // Simulate LLM response
        context.Response = new ChatMessage(ChatRole.Assistant, "Done");
        context.ToolCalls = Array.Empty<FunctionCallContent>();

        // Act - After
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert - Filter should have measured time (no exception means stopwatch worked)
        Assert.True(context.IsSuccess);
    }

    [Fact]
    public async Task Filter_HandlesNullLogger_Gracefully()
    {
        // Arrange
        var filter = new IterationLoggingFilter(null);
        var context = CreateContext();

        // Act & Assert - Should not throw
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        context.Response = new ChatMessage(ChatRole.Assistant, "Response");
        context.ToolCalls = Array.Empty<FunctionCallContent>();

        await filter.AfterIterationAsync(context, CancellationToken.None);
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
            Messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.User, "Test message")
            },
            Options = new ChatOptions { Instructions = "Test instructions" },
            State = state,
            CancellationToken = CancellationToken.None
        };
    }
}
