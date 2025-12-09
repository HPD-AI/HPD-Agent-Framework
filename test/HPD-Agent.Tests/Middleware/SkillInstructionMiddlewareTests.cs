using HPD.Agent;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using System.Collections.Immutable;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// Tests for SkillInstructionIterationMiddleware functionality.
/// Ported from SkillInstructionIterationFilterTests.cs with updates for new architecture.
/// </summary>
public class SkillInstructionMiddlewareTests
{
    [Fact]
    public async Task Middleware_DoesNotModifyInstructions_WhenNoActiveSkills()
    {
        // Arrange
        var middleware = new SkillInstructionMiddleware();
        var context = CreateContext(activeSkills: ImmutableDictionary<string, string>.Empty);
        var originalInstructions = context.Options!.Instructions;

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(originalInstructions, context.Options.Instructions);
    }

    [Fact]
    public async Task Middleware_InjectsSkillInstructions_WhenActiveSkillsExist()
    {
        // Arrange
        var middleware = new SkillInstructionMiddleware();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading skill instructions for buying and selling stocks");
        var context = CreateContext(activeSkills: activeSkills);
        var originalInstructions = context.Options!.Instructions;

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains("🔧 ACTIVE SKILL PROTOCOLS", context.Options.Instructions!);
        Assert.Contains("trading", context.Options.Instructions);
        Assert.Contains("Trading skill instructions", context.Options.Instructions);
    }

    [Fact]
    public async Task Middleware_InjectsMultipleSkillInstructions_WhenMultipleActiveSkills()
    {
        // Arrange
        var middleware = new SkillInstructionMiddleware();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading skill instructions")
            .Add("weather", "Weather skill instructions");
        var context = CreateContext(activeSkills: activeSkills);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains("trading", context.Options!.Instructions!);
        Assert.Contains("weather", context.Options.Instructions);
        Assert.Contains("Trading skill instructions", context.Options.Instructions);
        Assert.Contains("Weather skill instructions", context.Options.Instructions);
    }

    [Fact]
    public async Task Middleware_SignalsClearSkills_OnFinalIteration()
    {
        // Arrange
        var middleware = new SkillInstructionMiddleware();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading skill instructions");
        var context = CreateContext(activeSkills: activeSkills);

        // Simulate LLM response with no tool calls (final iteration)
        context.Response = new ChatMessage(ChatRole.Assistant, "Final response");
        context.ToolCalls = Array.Empty<FunctionCallContent>();

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - Check that middleware updated state to clear active skills
        Assert.True(context.IsFinalIteration);
        var pendingState = context.GetPendingState();
        Assert.NotNull(pendingState);
        var CollapsingState = pendingState!.MiddlewareState.Collapsing;
        Assert.NotNull(CollapsingState);
        Assert.Empty(CollapsingState!.ActiveSkillInstructions);
    }

    [Fact]
    public async Task Middleware_DoesNotSignalClearSkills_WhenNotFinalIteration()
    {
        // Arrange
        var middleware = new SkillInstructionMiddleware();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading skill instructions");
        var context = CreateContext(activeSkills: activeSkills);

        // Simulate LLM response with tool calls (NOT final iteration)
        context.Response = new ChatMessage(ChatRole.Assistant, "Response with tools");
        context.ToolCalls = new[] { new FunctionCallContent("call_123", "test_function") };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.False(context.IsFinalIteration);
        Assert.False(context.Properties.ContainsKey("ShouldClearActiveSkills"));
    }

    [Fact]
    public async Task Middleware_DoesNotSignalClearSkills_WhenNoActiveSkills()
    {
        // Arrange
        var middleware = new SkillInstructionMiddleware();
        var context = CreateContext(activeSkills: ImmutableDictionary<string, string>.Empty);

        // Simulate final iteration
        context.Response = new ChatMessage(ChatRole.Assistant, "Final response");
        context.ToolCalls = Array.Empty<FunctionCallContent>();

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.IsFinalIteration);
        Assert.False(context.Properties.ContainsKey("ShouldClearActiveSkills"));
    }

    [Fact]
    public async Task Middleware_HandlesNullOptions_Gracefully()
    {
        // Arrange
        var middleware = new SkillInstructionMiddleware();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading skill instructions");
        var context = CreateContext(activeSkills: activeSkills);
        context.Options = null; // Simulate null options

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Null(context.Options);
    }

    [Fact]
    public async Task Middleware_InjectsInBefore_DetectsFinalInAfter()
    {
        // Arrange
        var middleware = new SkillInstructionMiddleware();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading instructions");
        var context = CreateContext(activeSkills: activeSkills);

        // Act - Before phase
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Instructions injected
        Assert.Contains("Trading instructions", context.Options!.Instructions!);

        // Simulate final LLM response
        context.Response = new ChatMessage(ChatRole.Assistant, "Done");
        context.ToolCalls = Array.Empty<FunctionCallContent>();

        // Act - After phase
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - State updated to clear skills
        var pendingState = context.GetPendingState();
        Assert.NotNull(pendingState);
        var CollapsingState = pendingState!.MiddlewareState.Collapsing;
        Assert.NotNull(CollapsingState);
        Assert.Empty(CollapsingState!.ActiveSkillInstructions);
    }

    [Fact]
    public async Task Middleware_PreservesOriginalInstructions_WhenInjecting()
    {
        // Arrange
        var middleware = new SkillInstructionMiddleware();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading instructions");
        var context = CreateContext(activeSkills: activeSkills);
        var originalInstructions = "Original system instructions";
        context.Options!.Instructions = originalInstructions;

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Original instructions should still be present
        Assert.Contains(originalInstructions, context.Options.Instructions!);
        Assert.Contains("Trading instructions", context.Options.Instructions);
    }

    private static AgentMiddlewareContext CreateContext(ImmutableDictionary<string, string> activeSkills)
    {
        var state = AgentLoopState.Initial(
            messages: new List<ChatMessage>(),
            runId: "test-run-id",
            conversationId: "test-conv-id",
            agentName: "TestAgent")
            with
            {
                MiddlewareState = new MiddlewareState().WithCollapsing(
                    new CollapsingStateData { ActiveSkillInstructions = activeSkills })
            };

        var context = new AgentMiddlewareContext
        {
            AgentName = "TestAgent",
            ConversationId = "test-conv-id",
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions { Instructions = "Base instructions" },
            ToolCalls = Array.Empty<FunctionCallContent>(), // Initialize to empty array
            Iteration = 0,
            CancellationToken = CancellationToken.None
        };
        context.SetOriginalState(state);
        return context;
    }
}
