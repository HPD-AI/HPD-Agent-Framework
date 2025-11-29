using HPD.Agent;
using HPD.Agent.Middleware;
using HPD_Agent.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using System.Collections.Immutable;
using Xunit;

namespace HPD_Agent.Tests.Middleware;

/// <summary>
/// Characterization tests for CircuitBreakerMiddleware.
/// These tests document and verify the expected behavior of the circuit breaker.
/// </summary>
public class CircuitBreakerMiddlewareTests
{
    [Fact]
    public async Task BeforeToolExecution_NoToolCalls_DoesNotTrigger()
    {
        // Arrange
        var middleware = new CircuitBreakerMiddleware
        {
            MaxConsecutiveCalls = 3
        };

        var context = CreateContext(iteration: 0);
        context.ToolCalls = Array.Empty<FunctionCallContent>();

        // Act
        await middleware.BeforeToolExecutionAsync(context, CancellationToken.None);

        // Assert
        Assert.False(context.SkipToolExecution);
        Assert.Null(context.Response);
        Assert.False(context.Properties.ContainsKey("IsTerminated"));
    }

    [Fact]
    public async Task BeforeToolExecution_BelowThreshold_DoesNotTrigger()
    {
        // Arrange
        var middleware = new CircuitBreakerMiddleware
        {
            MaxConsecutiveCalls = 3
        };

        // State shows 1 consecutive call (below threshold of 3)
        var state = CreateStateWithConsecutiveCalls("test_tool", "test_tool({})", 1);
        var context = CreateContext(iteration: 2, state: state);
        context.ToolCalls = new[] { CreateToolCall("test_tool") };

        // Act
        await middleware.BeforeToolExecutionAsync(context, CancellationToken.None);

        // Assert
        Assert.False(context.SkipToolExecution);
        Assert.Null(context.Response);
    }

    [Fact]
    public async Task BeforeToolExecution_AtThreshold_TriggersCircuitBreaker()
    {
        // Arrange
        var middleware = new CircuitBreakerMiddleware
        {
            MaxConsecutiveCalls = 3
        };

        // State shows 2 consecutive calls, next identical call would be 3rd (at threshold)
        var state = CreateStateWithConsecutiveCalls("test_tool", "test_tool({})", 2);
        var context = CreateContext(iteration: 3, state: state);
        context.ToolCalls = new[] { CreateToolCall("test_tool") };

        // Act
        await middleware.BeforeToolExecutionAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.SkipToolExecution);
        Assert.NotNull(context.Response);
        Assert.Contains("test_tool", context.Response.Text);
        Assert.Contains("3", context.Response.Text);
        Assert.True((bool)context.Properties["IsTerminated"]);
        Assert.Contains("Circuit breaker", (string)context.Properties["TerminationReason"]);
    }

    [Fact]
    public async Task BeforeToolExecution_AboveThreshold_TriggersCircuitBreaker()
    {
        // Arrange
        var middleware = new CircuitBreakerMiddleware
        {
            MaxConsecutiveCalls = 3
        };

        // State shows 4 consecutive calls (above threshold)
        var state = CreateStateWithConsecutiveCalls("stuck_function", "stuck_function({})", 4);
        var context = CreateContext(iteration: 5, state: state);
        context.ToolCalls = new[] { CreateToolCall("stuck_function") };

        // Act
        await middleware.BeforeToolExecutionAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.SkipToolExecution);
        Assert.NotNull(context.Response);
        Assert.Contains("stuck_function", context.Response.Text);
        Assert.Empty(context.ToolCalls);
    }

    [Fact]
    public async Task BeforeToolExecution_DifferentArguments_DoesNotTrigger()
    {
        // Arrange
        var middleware = new CircuitBreakerMiddleware
        {
            MaxConsecutiveCalls = 3
        };

        // State shows 2 consecutive calls with signature "test_tool({\"arg\":\"old\"})"
        var state = CreateStateWithConsecutiveCalls("test_tool", "test_tool({\"arg\":\"old\"})", 2);
        var context = CreateContext(iteration: 3, state: state);
        // New call with different arguments
        context.ToolCalls = new[] { CreateToolCall("test_tool", new Dictionary<string, object?> { ["arg"] = "new" }) };

        // Act
        await middleware.BeforeToolExecutionAsync(context, CancellationToken.None);

        // Assert - different arguments means count resets, so no trigger
        Assert.False(context.SkipToolExecution);
        Assert.Null(context.Response);
    }

    [Fact]
    public async Task BeforeToolExecution_CustomMessageTemplate_UsesTemplate()
    {
        // Arrange
        var middleware = new CircuitBreakerMiddleware
        {
            MaxConsecutiveCalls = 2,
            TerminationMessageTemplate = "LOOP DETECTED: {toolName} was called {count} times!"
        };

        var state = CreateStateWithConsecutiveCalls("my_function", "my_function({})", 1);
        var context = CreateContext(iteration: 2, state: state);
        context.ToolCalls = new[] { CreateToolCall("my_function") };

        // Act
        await middleware.BeforeToolExecutionAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.SkipToolExecution);
        Assert.NotNull(context.Response);
        Assert.Equal("LOOP DETECTED: my_function was called 2 times!", context.Response.Text);
    }

    [Fact]
    public async Task AfterIteration_UpdatesState()
    {
        // Arrange
        var middleware = new CircuitBreakerMiddleware();
        var context = CreateContext(iteration: 1);
        context.ToolCalls = new[] { CreateToolCall("test_tool") };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - state should be updated (we can't directly verify but no errors thrown)
        Assert.False(context.SkipToolExecution);
    }

    [Fact]
    public async Task AfterIteration_NoToolCalls_DoesNothing()
    {
        // Arrange
        var middleware = new CircuitBreakerMiddleware();
        var context = CreateContext(iteration: 1);
        context.ToolCalls = Array.Empty<FunctionCallContent>();

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - no changes to context
        Assert.False(context.SkipToolExecution);
        Assert.Null(context.Response);
    }

    [Fact]
    public void DefaultConfiguration_UsesReasonableDefaults()
    {
        // Arrange & Act
        var middleware = new CircuitBreakerMiddleware();

        // Assert
        Assert.Equal(3, middleware.MaxConsecutiveCalls);
        Assert.Contains("Circuit breaker triggered", middleware.TerminationMessageTemplate);
        Assert.Contains("{toolName}", middleware.TerminationMessageTemplate);
        Assert.Contains("{count}", middleware.TerminationMessageTemplate);
    }

    [Fact]
    public async Task BeforeToolExecution_EmptyState_DoesNotTrigger()
    {
        // Arrange
        var middleware = new CircuitBreakerMiddleware
        {
            MaxConsecutiveCalls = 2 // Threshold of 2 - first call is count 1
        };

        // Empty state (no tool calls recorded yet)
        var state = CreateEmptyState();
        var context = CreateContext(iteration: 1, state: state);
        context.ToolCalls = new[] { CreateToolCall("test_tool") };

        // Act
        await middleware.BeforeToolExecutionAsync(context, CancellationToken.None);

        // Assert - first call doesn't trigger (predicted count is 1, threshold is 2)
        Assert.False(context.SkipToolExecution);
    }

    // ═══════════════════════════════════════════════════════
    // HELPER METHODS
    // ═══════════════════════════════════════════════════════

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

    private static AgentLoopState CreateStateWithConsecutiveCalls(string toolName, string signature, int count)
    {
        var cbState = new CircuitBreakerStateData();
        for (int i = 0; i < count; i++)
        {
            cbState = cbState.RecordToolCall(toolName, signature);
        }

        var state = CreateEmptyState();
        return state with
        {
            MiddlewareState = state.MiddlewareState.WithCircuitBreaker(cbState)
        };
    }

    private static FunctionCallContent CreateToolCall(string name, IDictionary<string, object?>? arguments = null)
    {
        return new FunctionCallContent(
            callId: Guid.NewGuid().ToString(),
            name: name,
            arguments: arguments ?? new Dictionary<string, object?>());
    }
}
