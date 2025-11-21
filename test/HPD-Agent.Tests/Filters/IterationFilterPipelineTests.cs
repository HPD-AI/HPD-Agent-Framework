using HPD.Agent;
using HPD.Agent.Internal.Filters;
using Microsoft.Extensions.AI;
using Xunit;

namespace HPD.Agent.Tests.Filters;

/// <summary>
/// Tests for iteration filter before/after lifecycle pattern.
/// </summary>
public class IterationFilterPipelineTests
{
    [Fact]
    public async Task Filters_ExecuteInOrder_BeforeAndAfter()
    {
        // Arrange
        var executionLog = new List<string>();
        var filter1 = new TestIterationFilter("Filter1", executionLog);
        var filter2 = new TestIterationFilter("Filter2", executionLog);
        var filter3 = new TestIterationFilter("Filter3", executionLog);

        var context = CreateContext();
        var filters = new[] { filter1, filter2, filter3 };

        // Act
        // Before phase
        foreach (var filter in filters)
        {
            await filter.BeforeIterationAsync(context, CancellationToken.None);
        }

        // Simulate LLM call
        executionLog.Add("LLM");
        context.Response = new ChatMessage(ChatRole.Assistant, "Response");

        // After phase
        foreach (var filter in filters)
        {
            await filter.AfterIterationAsync(context, CancellationToken.None);
        }

        // Assert
        Assert.Equal(new[]
        {
            "Filter1-before",
            "Filter2-before",
            "Filter3-before",
            "LLM",
            "Filter1-after",
            "Filter2-after",
            "Filter3-after"
        }, executionLog);
    }

    [Fact]
    public async Task BeforeFilters_ModifyContext()
    {
        // Arrange
        var context = CreateContext();
        context.Options = new ChatOptions { Instructions = "Base" };

        var filter1 = new InstructionAppendingFilter(" + F1");
        var filter2 = new InstructionAppendingFilter(" + F2");

        var filters = new IIterationFilter[] { filter1, filter2 };

        // Act
        foreach (var filter in filters)
        {
            await filter.BeforeIterationAsync(context, CancellationToken.None);
        }

        // Assert
        Assert.Equal("Base + F1 + F2", context.Options.Instructions);
    }

    [Fact]
    public async Task Filter_CanSetSkipLLMCall()
    {
        // Arrange
        var context = CreateContext();
        var filter = new SkipLLMCallFilter();

        // Act
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.SkipLLMCall);
        Assert.NotNull(context.Response); // Filter should provide cached response
    }

    [Fact]
    public async Task AfterFilters_CanInspectResponse()
    {
        // Arrange
        var context = CreateContext();
        var modifications = new List<string>();

        var filter = new ContextModificationTrackingFilter(modifications);

        // Act - Before phase
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        // Simulate LLM call
        modifications.Add("LLM-executed");
        context.Response = new ChatMessage(ChatRole.Assistant, "Response");
        context.ToolCalls = new[] { new FunctionCallContent("call_1", "func1") };

        // Act - After phase
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(new[]
        {
            "pre-invoke-messages-modified",
            "LLM-executed",
            "post-invoke-response-checked"
        }, modifications);
    }

    [Fact]
    public async Task AfterFilter_DetectsFinalIteration()
    {
        // Arrange
        var context = CreateContext();
        var signalSet = false;

        var filter = new FinalIterationDetectingFilter(() => signalSet = true);

        // Simulate LLM response with NO tool calls (final iteration)
        context.Response = new ChatMessage(ChatRole.Assistant, "Final response");
        context.ToolCalls = Array.Empty<FunctionCallContent>();
        context.Exception = null;

        // Act
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.IsFinalIteration);
        Assert.True(signalSet, "Filter should have detected final iteration");
    }

    [Fact]
    public async Task AfterFilter_DoesNotSignalWhenToolCallsExist()
    {
        // Arrange
        var context = CreateContext();
        var signalSet = false;

        var filter = new FinalIterationDetectingFilter(() => signalSet = true);

        // Simulate LLM response WITH tool calls (not final)
        context.Response = new ChatMessage(ChatRole.Assistant, "Response with tools");
        context.ToolCalls = new[] { new FunctionCallContent("call_1", "func1") };
        context.Exception = null;

        // Act
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.False(context.IsFinalIteration);
        Assert.False(signalSet, "Filter should NOT signal on non-final iteration");
    }

    [Fact]
    public async Task Filter_SkipsWorkWhen_NoOpportunity()
    {
        // Arrange
        var context = CreateContext();
        context.Options = null; // Test with null options
        var filter = new InstructionAppendingFilter(" + Modified");

        // Act
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - No options, so no modification should occur (no exception)
        Assert.Null(context.Options);
    }

    private static IterationFilterContext CreateContext()
    {
        var state = AgentLoopState.Initial(
            messages: new List<ChatMessage>(),
            runId: "test-run-id",
            conversationId: "test-conv-id",
            agentName: "TestAgent");

        return new IterationFilterContext
        {
            Iteration = 0,
            AgentName = "TestAgent",
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions(),
            State = state,
            CancellationToken = CancellationToken.None
        };
    }

    // Test helper filters

    private class TestIterationFilter : IIterationFilter
    {
        private readonly string _name;
        private readonly List<string> _log;

        public TestIterationFilter(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public Task BeforeIterationAsync(
            IterationFilterContext context,
            CancellationToken cancellationToken)
        {
            _log.Add($"{_name}-before");
            return Task.CompletedTask;
        }

        public Task AfterIterationAsync(
            IterationFilterContext context,
            CancellationToken cancellationToken)
        {
            _log.Add($"{_name}-after");
            return Task.CompletedTask;
        }
    }

    private class InstructionAppendingFilter : IIterationFilter
    {
        private readonly string _textToAppend;

        public InstructionAppendingFilter(string textToAppend)
        {
            _textToAppend = textToAppend;
        }

        public Task BeforeIterationAsync(
            IterationFilterContext context,
            CancellationToken cancellationToken)
        {
            if (context.Options != null)
            {
                context.Options.Instructions += _textToAppend;
            }
            return Task.CompletedTask;
        }

        public Task AfterIterationAsync(
            IterationFilterContext context,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private class SkipLLMCallFilter : IIterationFilter
    {
        public Task BeforeIterationAsync(
            IterationFilterContext context,
            CancellationToken cancellationToken)
        {
            // Skip LLM call and provide cached response
            context.SkipLLMCall = true;
            context.Response = new ChatMessage(ChatRole.Assistant, "Cached response");
            context.ToolCalls = Array.Empty<FunctionCallContent>();
            return Task.CompletedTask;
        }

        public Task AfterIterationAsync(
            IterationFilterContext context,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private class ContextModificationTrackingFilter : IIterationFilter
    {
        private readonly List<string> _modifications;

        public ContextModificationTrackingFilter(List<string> modifications)
        {
            _modifications = modifications;
        }

        public Task BeforeIterationAsync(
            IterationFilterContext context,
            CancellationToken cancellationToken)
        {
            // Pre-invoke: Modify context
            context.Messages.Add(new ChatMessage(ChatRole.User, "Modified message"));
            _modifications.Add("pre-invoke-messages-modified");
            return Task.CompletedTask;
        }

        public Task AfterIterationAsync(
            IterationFilterContext context,
            CancellationToken cancellationToken)
        {
            // Post-invoke: Check response
            if (context.Response != null)
            {
                _modifications.Add("post-invoke-response-checked");
            }
            return Task.CompletedTask;
        }
    }

    private class FinalIterationDetectingFilter : IIterationFilter
    {
        private readonly Action _onFinalIteration;

        public FinalIterationDetectingFilter(Action onFinalIteration)
        {
            _onFinalIteration = onFinalIteration;
        }

        public Task BeforeIterationAsync(
            IterationFilterContext context,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task AfterIterationAsync(
            IterationFilterContext context,
            CancellationToken cancellationToken)
        {
            if (context.IsFinalIteration)
            {
                _onFinalIteration();
            }
            return Task.CompletedTask;
        }
    }
}
