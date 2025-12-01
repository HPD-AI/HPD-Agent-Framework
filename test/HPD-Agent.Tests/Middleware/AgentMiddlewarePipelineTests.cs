using HPD.Agent;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// Tests for AgentMiddlewarePipeline execution order and lifecycle behavior.
/// Ported from IterationFilterPipelineTests.cs with updates for new architecture.
/// </summary>
public class AgentMiddlewarePipelineTests
{
    [Fact]
    public async Task Middlewares_ExecuteInOrder_BeforeAndAfterIteration()
    {
        // Arrange
        var executionLog = new List<string>();
        var middleware1 = new TestMiddleware("Middleware1", executionLog);
        var middleware2 = new TestMiddleware("Middleware2", executionLog);
        var middleware3 = new TestMiddleware("Middleware3", executionLog);

        var pipeline = new AgentMiddlewarePipeline(new[] { middleware1, middleware2, middleware3 });
        var context = CreateContext();

        // Act - Before phase
        await pipeline.ExecuteBeforeIterationAsync(context, CancellationToken.None);

        // Simulate LLM call
        executionLog.Add("LLM");
        context.Response = new ChatMessage(ChatRole.Assistant, "Response");

        // Act - After phase
        await pipeline.ExecuteAfterIterationAsync(context, CancellationToken.None);

        // Assert - Before executes in order, After executes in REVERSE order
        Assert.Equal(new[]
        {
            "Middleware1-before",
            "Middleware2-before",
            "Middleware3-before",
            "LLM",
            "Middleware3-after",  // Reversed!
            "Middleware2-after",
            "Middleware1-after"
        }, executionLog);
    }

    [Fact]
    public async Task BeforeIterationMiddlewares_CanModifyContext()
    {
        // Arrange
        var context = CreateContext();
        context.Options = new ChatOptions { Instructions = "Base" };

        var middleware1 = new InstructionAppendingMiddleware(" + M1");
        var middleware2 = new InstructionAppendingMiddleware(" + M2");

        var pipeline = new AgentMiddlewarePipeline(new[] { middleware1, middleware2 });

        // Act
        await pipeline.ExecuteBeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal("Base + M1 + M2", context.Options.Instructions);
    }

    [Fact]
    public async Task BeforeIterationMiddleware_CanSetSkipLLMCall()
    {
        // Arrange
        var context = CreateContext();
        var middleware = new SkipLLMCallMiddleware();
        var pipeline = new AgentMiddlewarePipeline(new[] { middleware });

        // Act
        await pipeline.ExecuteBeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.SkipLLMCall);
        Assert.NotNull(context.Response); // Middleware should provide cached response
    }

    [Fact]
    public async Task AfterIterationMiddlewares_CanInspectResponse()
    {
        // Arrange
        var context = CreateContext();
        var modifications = new List<string>();

        var middleware = new ContextModificationTrackingMiddleware(modifications);
        var pipeline = new AgentMiddlewarePipeline(new[] { middleware });

        // Act - Before phase
        await pipeline.ExecuteBeforeIterationAsync(context, CancellationToken.None);

        // Simulate LLM call
        modifications.Add("LLM-executed");
        context.Response = new ChatMessage(ChatRole.Assistant, "Response");
        context.ToolCalls = new[] { new FunctionCallContent("call_1", "func1") };

        // Act - After phase
        await pipeline.ExecuteAfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(new[]
        {
            "pre-invoke-messages-modified",
            "LLM-executed",
            "post-invoke-response-checked"
        }, modifications);
    }

    [Fact]
    public async Task AfterIterationMiddleware_DetectsFinalIteration()
    {
        // Arrange
        var context = CreateContext();
        var signalSet = false;

        var middleware = new FinalIterationDetectingMiddleware(() => signalSet = true);
        var pipeline = new AgentMiddlewarePipeline(new[] { middleware });

        // Simulate LLM response with NO tool calls (final iteration)
        context.Response = new ChatMessage(ChatRole.Assistant, "Final response");
        context.ToolCalls = Array.Empty<FunctionCallContent>();

        // Act
        await pipeline.ExecuteAfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.IsFinalIteration);
        Assert.True(signalSet, "Middleware should have detected final iteration");
    }

    [Fact]
    public async Task AfterIterationMiddleware_DoesNotSignalWhenToolCallsExist()
    {
        // Arrange
        var context = CreateContext();
        var signalSet = false;

        var middleware = new FinalIterationDetectingMiddleware(() => signalSet = true);
        var pipeline = new AgentMiddlewarePipeline(new[] { middleware });

        // Simulate LLM response WITH tool calls (not final)
        context.Response = new ChatMessage(ChatRole.Assistant, "Response with tools");
        context.ToolCalls = new[] { new FunctionCallContent("call_1", "func1") };

        // Act
        await pipeline.ExecuteAfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.False(context.IsFinalIteration);
        Assert.False(signalSet, "Middleware should NOT signal on non-final iteration");
    }

    [Fact]
    public async Task Middleware_HandlesNullOptions_Gracefully()
    {
        // Arrange
        var context = CreateContext();
        context.Options = null; // Test with null options
        var middleware = new InstructionAppendingMiddleware(" + Modified");
        var pipeline = new AgentMiddlewarePipeline(new[] { middleware });

        // Act
        await pipeline.ExecuteBeforeIterationAsync(context, CancellationToken.None);

        // Assert - No options, so no modification should occur (no exception)
        Assert.Null(context.Options);
    }

    [Fact]
    public async Task Pipeline_PropagatesMiddlewareExceptions()
    {
        // Arrange
        var executionLog = new List<string>();
        var middleware1 = new TestMiddleware("M1", executionLog);
        var throwingMiddleware = new ThrowingMiddleware();
        var middleware3 = new TestMiddleware("M3", executionLog);

        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { middleware1, throwingMiddleware, middleware3 });
        var context = CreateContext();

        // Act & Assert - Should throw and stop executing remaining middlewares
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.ExecuteBeforeIterationAsync(context, CancellationToken.None));

        // M1 should have executed before the exception
        Assert.Contains("M1-before", executionLog);

        // M3 should NOT execute because exception stopped the pipeline
        Assert.DoesNotContain("M3-before", executionLog);
    }

    [Fact]
    public async Task BeforeToolExecution_ExecutesInOrder()
    {
        // Arrange
        var executionLog = new List<string>();
        var middleware1 = new TestMiddleware("M1", executionLog);
        var middleware2 = new TestMiddleware("M2", executionLog);

        var pipeline = new AgentMiddlewarePipeline(new[] { middleware1, middleware2 });
        var context = CreateContext();
        context.ToolCalls = new[] { new FunctionCallContent("call_1", "func1") };

        // Act
        await pipeline.ExecuteBeforeToolExecutionAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(new[] { "M1-beforeTool", "M2-beforeTool" }, executionLog);
    }

    private static AgentMiddlewareContext CreateContext()
    {
        var state = AgentLoopState.Initial(
            messages: new List<ChatMessage>(),
            runId: "test-run-id",
            conversationId: "test-conv-id",
            agentName: "TestAgent");

        var context = new AgentMiddlewareContext
        {
            AgentName = "TestAgent",
            ConversationId = "test-conv-id",
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions(),
            Iteration = 0,
            CancellationToken = CancellationToken.None
        };
        context.SetOriginalState(state);
        return context;
    }

    //     
    // TEST HELPER MIDDLEWARES
    //     

    private class TestMiddleware : IAgentMiddleware
    {
        private readonly string _name;
        private readonly List<string> _log;

        public TestMiddleware(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public Task BeforeIterationAsync(
            AgentMiddlewareContext context,
            CancellationToken cancellationToken)
        {
            _log.Add($"{_name}-before");
            return Task.CompletedTask;
        }

        public Task BeforeToolExecutionAsync(
            AgentMiddlewareContext context,
            CancellationToken cancellationToken)
        {
            _log.Add($"{_name}-beforeTool");
            return Task.CompletedTask;
        }

        public Task AfterIterationAsync(
            AgentMiddlewareContext context,
            CancellationToken cancellationToken)
        {
            _log.Add($"{_name}-after");
            return Task.CompletedTask;
        }

        public Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private class InstructionAppendingMiddleware : IAgentMiddleware
    {
        private readonly string _textToAppend;

        public InstructionAppendingMiddleware(string textToAppend)
        {
            _textToAppend = textToAppend;
        }

        public Task BeforeIterationAsync(
            AgentMiddlewareContext context,
            CancellationToken cancellationToken)
        {
            if (context.Options != null)
            {
                context.Options.Instructions += _textToAppend;
            }
            return Task.CompletedTask;
        }

        public Task BeforeToolExecutionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterIterationAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private class SkipLLMCallMiddleware : IAgentMiddleware
    {
        public Task BeforeIterationAsync(
            AgentMiddlewareContext context,
            CancellationToken cancellationToken)
        {
            // Skip LLM call and provide cached response
            context.SkipLLMCall = true;
            context.Response = new ChatMessage(ChatRole.Assistant, "Cached response");
            context.ToolCalls = Array.Empty<FunctionCallContent>();
            return Task.CompletedTask;
        }

        public Task BeforeToolExecutionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterIterationAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private class ContextModificationTrackingMiddleware : IAgentMiddleware
    {
        private readonly List<string> _modifications;

        public ContextModificationTrackingMiddleware(List<string> modifications)
        {
            _modifications = modifications;
        }

        public Task BeforeIterationAsync(
            AgentMiddlewareContext context,
            CancellationToken cancellationToken)
        {
            // Pre-invoke: Modify context
            context.Messages?.Add(new ChatMessage(ChatRole.User, "Modified message"));
            _modifications.Add("pre-invoke-messages-modified");
            return Task.CompletedTask;
        }

        public Task BeforeToolExecutionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterIterationAsync(
            AgentMiddlewareContext context,
            CancellationToken cancellationToken)
        {
            // Post-invoke: Check response
            if (context.Response != null)
            {
                _modifications.Add("post-invoke-response-checked");
            }
            return Task.CompletedTask;
        }

        public Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private class FinalIterationDetectingMiddleware : IAgentMiddleware
    {
        private readonly Action _onFinalIteration;

        public FinalIterationDetectingMiddleware(Action onFinalIteration)
        {
            _onFinalIteration = onFinalIteration;
        }

        public Task BeforeIterationAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeToolExecutionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterIterationAsync(
            AgentMiddlewareContext context,
            CancellationToken cancellationToken)
        {
            if (context.IsFinalIteration)
            {
                _onFinalIteration();
            }
            return Task.CompletedTask;
        }

        public Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private class ThrowingMiddleware : IAgentMiddleware
    {
        public Task BeforeIterationAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Test exception");
        }

        public Task BeforeToolExecutionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterIterationAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
