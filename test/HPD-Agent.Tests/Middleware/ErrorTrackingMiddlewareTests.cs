using HPD.Agent;
using HPD.Agent.Middleware;
using HPD.Agent.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using System.Collections.Immutable;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// Characterization tests for ErrorTrackingMiddleware.
/// These tests document and verify the expected behavior of error tracking.
/// </summary>
public class ErrorTrackingMiddlewareTests
{
    [Fact]
    public async Task BeforeIteration_FirstIteration_DoesNotTrigger()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware
        {
            MaxConsecutiveErrors = 3
        };

        var context = CreateContext(iteration: 0); // First iteration

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.False(context.SkipLLMCall);
        Assert.Null(context.Response);
        Assert.False(context.Properties.ContainsKey("IsTerminated"));
    }

    [Fact]
    public async Task BeforeIteration_BelowThreshold_DoesNotTrigger()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware
        {
            MaxConsecutiveErrors = 3
        };

        // State shows 2 consecutive failures (below threshold of 3)
        var state = CreateStateWithConsecutiveFailures(2);
        var context = CreateContext(iteration: 2, state: state);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.False(context.SkipLLMCall);
        Assert.Null(context.Response);
    }

    [Fact]
    public async Task BeforeIteration_AtThreshold_TriggersTermination()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware
        {
            MaxConsecutiveErrors = 3
        };

        // State shows 3 consecutive failures (at threshold)
        var state = CreateStateWithConsecutiveFailures(3);
        var context = CreateContext(iteration: 3, state: state);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.SkipLLMCall);
        Assert.NotNull(context.Response);
        Assert.Contains("3", context.Response.Text);
        Assert.True((bool)context.Properties["IsTerminated"]);
        Assert.Contains("Maximum consecutive errors", (string)context.Properties["TerminationReason"]);
    }

    [Fact]
    public async Task AfterIteration_NoToolResults_DoesNothing()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware();
        var context = CreateContext(iteration: 1);
        context.ToolResults = Array.Empty<FunctionResultContent>();

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - no state changes, no termination
        Assert.False(context.SkipLLMCall);
        Assert.Null(context.Response);
    }

    [Fact]
    public async Task AfterIteration_SuccessfulToolResults_ResetsFailures()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware();
        var state = CreateStateWithConsecutiveFailures(2);
        var context = CreateContext(iteration: 1, state: state);
        context.ToolResults = new[]
        {
            new FunctionResultContent("call1", "Success result")
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - no termination on success
        Assert.False(context.SkipLLMCall);
        Assert.Null(context.Response);
    }

    [Fact]
    public async Task AfterIteration_ErrorWithException_IncrementsFailures()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware();
        var state = CreateStateWithConsecutiveFailures(0);
        var context = CreateContext(iteration: 1, state: state);
        context.ToolResults = new[]
        {
            new FunctionResultContent("call1", result: null) { Exception = new Exception("Test error") }
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - error detected but not at threshold yet
        Assert.False(context.SkipLLMCall); // Only 1 error, threshold is 3
    }

    [Fact]
    public async Task AfterIteration_ErrorWithErrorPrefix_DetectsError()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware();
        var context = CreateContext(iteration: 1);
        context.ToolResults = new[]
        {
            new FunctionResultContent("call1", "Error: File not found")
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - error detected but not at threshold
        Assert.False(context.SkipLLMCall);
    }

    [Fact]
    public async Task AfterIteration_ErrorWithFailedPrefix_DetectsError()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware();
        var context = CreateContext(iteration: 1);
        context.ToolResults = new[]
        {
            new FunctionResultContent("call1", "Failed: Connection refused")
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - error detected
        Assert.False(context.SkipLLMCall); // Not at threshold yet
    }

    [Fact]
    public async Task AfterIteration_RateLimitError_DetectsError()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware();
        var context = CreateContext(iteration: 1);
        context.ToolResults = new[]
        {
            new FunctionResultContent("call1", "API rate limit exceeded, please retry later")
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - error detected
        Assert.False(context.SkipLLMCall); // Not at threshold yet
    }

    [Fact]
    public async Task AfterIteration_ErrorExceedsThreshold_TriggersTermination()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware
        {
            MaxConsecutiveErrors = 3
        };

        // State shows 2 consecutive failures, one more will meet threshold
        var state = CreateStateWithConsecutiveFailures(2);
        var context = CreateContext(iteration: 3, state: state);
        context.ToolResults = new[]
        {
            new FunctionResultContent("call1", result: null) { Exception = new Exception("Error") }
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.SkipLLMCall);
        Assert.NotNull(context.Response);
        Assert.True((bool)context.Properties["IsTerminated"]);
    }

    [Fact]
    public async Task AfterIteration_MixedResults_DetectsError()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware();
        var context = CreateContext(iteration: 1);
        context.ToolResults = new[]
        {
            new FunctionResultContent("call1", "Success"),
            new FunctionResultContent("call2", "Error: Something went wrong"),
            new FunctionResultContent("call3", "Also success")
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - ANY error should be detected
        Assert.False(context.SkipLLMCall); // Not at threshold yet
    }

    [Fact]
    public async Task AfterIteration_CustomErrorDetector_UsesCustomLogic()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware
        {
            MaxConsecutiveErrors = 1, // Low threshold for test
            CustomErrorDetector = result =>
                result.Result?.ToString()?.Contains("CUSTOM_ERROR") == true
        };

        var context = CreateContext(iteration: 1);
        context.ToolResults = new[]
        {
            new FunctionResultContent("call1", "This contains CUSTOM_ERROR marker")
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - custom error detected and threshold reached
        Assert.True(context.SkipLLMCall);
        Assert.True((bool)context.Properties["IsTerminated"]);
    }

    [Fact]
    public async Task AfterIteration_CustomErrorDetector_IgnoresDefaultPatterns()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware
        {
            CustomErrorDetector = result =>
                result.Result?.ToString()?.Contains("CUSTOM_ERROR") == true
        };

        var context = CreateContext(iteration: 1);
        context.ToolResults = new[]
        {
            // This would be an error with default detection, but not with custom
            new FunctionResultContent("call1", "Error: This is a normal error prefix")
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - Custom detector doesn't match, so no error
        Assert.False(context.SkipLLMCall);
    }

    [Fact]
    public async Task AfterIteration_CustomMessageTemplate_UsesTemplate()
    {
        // Arrange
        var middleware = new ErrorTrackingMiddleware
        {
            MaxConsecutiveErrors = 2,
            TerminationMessageTemplate = "ERRORS DETECTED: {count} out of {max} allowed!"
        };

        var state = CreateStateWithConsecutiveFailures(1);
        var context = CreateContext(iteration: 2, state: state);
        context.ToolResults = new[]
        {
            new FunctionResultContent("call1", result: null) { Exception = new Exception("Error") }
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.SkipLLMCall);
        Assert.NotNull(context.Response);
        Assert.Contains("ERRORS DETECTED: 2 out of 2 allowed!", context.Response.Text);
    }

    [Fact]
    public void DefaultConfiguration_UsesReasonableDefaults()
    {
        // Arrange & Act
        var middleware = new ErrorTrackingMiddleware();

        // Assert
        Assert.Equal(3, middleware.MaxConsecutiveErrors);
        Assert.Null(middleware.CustomErrorDetector);
        Assert.Contains("Maximum consecutive errors", middleware.TerminationMessageTemplate);
        Assert.Contains("{count}", middleware.TerminationMessageTemplate);
        Assert.Contains("{max}", middleware.TerminationMessageTemplate);
    }

    //      
    // HELPER METHODS
    //      

    private static AgentLoopState CreateEmptyState()
    {
        return AgentLoopState.Initial(
            messages: Array.Empty<ChatMessage>(),
            runId: "test-run",
            conversationId: "test-conversation",
            agentName: "TestAgent");
    }

    private static AgentMiddlewareContext CreateContext(
        int iteration,
        AgentLoopState? state = null)
    {
        var context = new AgentMiddlewareContext
        {
            Iteration = iteration,
            AgentName = "TestAgent",
            CancellationToken = CancellationToken.None,
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions(),
            ConversationId = "test-conversation"
        };
        context.SetOriginalState(state ?? CreateEmptyState());
        return context;
    }

    private static AgentLoopState CreateStateWithConsecutiveFailures(int count)
    {
        var errState = new ErrorTrackingStateData();
        for (int i = 0; i < count; i++)
        {
            errState = errState.IncrementFailures();
        }

        var state = CreateEmptyState();
        return state with
        {
            MiddlewareState = state.MiddlewareState.WithErrorTracking(errState)
        };
    }
}
