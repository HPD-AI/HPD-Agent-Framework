using HPD.Agent;
using HPD.Agent.Internal.Filters;
using Microsoft.Extensions.AI;
using System.Collections.Immutable;
using Xunit;

namespace HPD.Agent.Tests.Filters;

/// <summary>
/// Tests for SkillInstructionIterationFilter functionality.
/// </summary>
public class SkillInstructionIterationFilterTests
{
    [Fact]
    public async Task Filter_DoesNotModifyInstructions_WhenNoActiveSkills()
    {
        // Arrange
        var filter = new SkillInstructionIterationFilter();
        var context = CreateContext(activeSkills: ImmutableDictionary<string, string>.Empty);
        var originalInstructions = context.Options!.Instructions;

        // Act
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(originalInstructions, context.Options.Instructions);
    }

    [Fact]
    public async Task Filter_InjectsSkillInstructions_WhenActiveSkillsExist()
    {
        // Arrange
        var filter = new SkillInstructionIterationFilter();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading skill instructions for buying and selling stocks");
        var context = CreateContext(activeSkills: activeSkills);
        var originalInstructions = context.Options!.Instructions;

        // Act
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains("🔧 ACTIVE SKILL PROTOCOLS", context.Options.Instructions!);
        Assert.Contains("trading", context.Options.Instructions);
        Assert.Contains("Trading skill instructions", context.Options.Instructions);
    }

    [Fact]
    public async Task Filter_InjectsMultipleSkillInstructions_WhenMultipleActiveSkills()
    {
        // Arrange
        var filter = new SkillInstructionIterationFilter();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading skill instructions")
            .Add("weather", "Weather skill instructions");
        var context = CreateContext(activeSkills: activeSkills);

        // Act
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains("trading", context.Options!.Instructions!);
        Assert.Contains("weather", context.Options.Instructions);
        Assert.Contains("Trading skill instructions", context.Options.Instructions);
        Assert.Contains("Weather skill instructions", context.Options.Instructions);
    }

    [Fact]
    public async Task Filter_SignalsClearSkills_OnFinalIteration()
    {
        // Arrange
        var filter = new SkillInstructionIterationFilter();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading skill instructions");
        var context = CreateContext(activeSkills: activeSkills);

        // Simulate LLM response with no tool calls (final iteration)
        context.Response = new ChatMessage(ChatRole.Assistant, "Final response");
        context.ToolCalls = Array.Empty<FunctionCallContent>();
        context.Exception = null;

        // Act
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.IsFinalIteration);
        Assert.True(context.Properties.ContainsKey("ShouldClearActiveSkills"));
        Assert.Equal(true, context.Properties["ShouldClearActiveSkills"]);
    }

    [Fact]
    public async Task Filter_DoesNotSignalClearSkills_WhenNotFinalIteration()
    {
        // Arrange
        var filter = new SkillInstructionIterationFilter();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading skill instructions");
        var context = CreateContext(activeSkills: activeSkills);

        // Simulate LLM response with tool calls (NOT final iteration)
        context.Response = new ChatMessage(ChatRole.Assistant, "Response with tools");
        context.ToolCalls = new[] { new FunctionCallContent("call_123", "test_function") };
        context.Exception = null;

        // Act
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.False(context.IsFinalIteration);
        Assert.False(context.Properties.ContainsKey("ShouldClearActiveSkills"));
    }

    [Fact]
    public async Task Filter_DoesNotSignalClearSkills_WhenNoActiveSkills()
    {
        // Arrange
        var filter = new SkillInstructionIterationFilter();
        var context = CreateContext(activeSkills: ImmutableDictionary<string, string>.Empty);

        // Simulate final iteration
        context.Response = new ChatMessage(ChatRole.Assistant, "Final response");
        context.ToolCalls = Array.Empty<FunctionCallContent>();
        context.Exception = null;

        // Act
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.IsFinalIteration);
        Assert.False(context.Properties.ContainsKey("ShouldClearActiveSkills"));
    }

    [Fact]
    public async Task Filter_HandlesNullOptions_Gracefully()
    {
        // Arrange
        var filter = new SkillInstructionIterationFilter();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading skill instructions");
        var context = CreateContext(activeSkills: activeSkills);
        context.Options = null; // Simulate null options

        // Act
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Null(context.Options);
    }

    [Fact]
    public async Task Filter_InjectsInBefore_DetectsFinalInAfter()
    {
        // Arrange
        var filter = new SkillInstructionIterationFilter();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Trading instructions");
        var context = CreateContext(activeSkills: activeSkills);

        // Act - Before phase
        await filter.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Instructions injected
        Assert.Contains("Trading instructions", context.Options!.Instructions!);

        // Simulate final LLM response
        context.Response = new ChatMessage(ChatRole.Assistant, "Done");
        context.ToolCalls = Array.Empty<FunctionCallContent>();
        context.Exception = null;

        // Act - After phase
        await filter.AfterIterationAsync(context, CancellationToken.None);

        // Assert - Signal set
        Assert.True(context.Properties.ContainsKey("ShouldClearActiveSkills"));
    }

    private static IterationFilterContext CreateContext(ImmutableDictionary<string, string> activeSkills)
    {
        var state = AgentLoopState.Initial(
            messages: new List<ChatMessage>(),
            runId: "test-run-id",
            conversationId: "test-conv-id",
            agentName: "TestAgent")
            with
            {
                ActiveSkillInstructions = activeSkills
            };

        return new IterationFilterContext
        {
            Iteration = 0,
            AgentName = "TestAgent",
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions { Instructions = "Base instructions" },
            State = state,
            CancellationToken = CancellationToken.None
        };
    }
}
