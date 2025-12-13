using HPD.Agent;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using System.Collections.Immutable;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// Tests for Container Instruction Injection Unification V2 (dual-context architecture).
/// Tests both FunctionResult (ephemeral) andSystemPrompt (persistent) injection.
/// </summary>
public class ContainerInstructionUnificationTests
{
    #region SystemPrompt Injection Tests

    [Fact]
    public async Task SystemPrompt_InjectedIntoSystemPrompt_WhenContainerActive()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("FinancialPlugin", new ContainerInstructionSet(
                FunctionResult: "Plugin activated",
               SystemPrompt: "Always validate calculations"));
        var context = CreateContext(containerInstructions);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains("🔧 ACTIVE CONTAINER PROTOCOLS", context.Options!.Instructions!);
        Assert.Contains("FinancialPlugin", context.Options.Instructions);
        Assert.Contains("Always validate calculations", context.Options.Instructions);
    }

    [Fact]
    public async Task SystemPrompt_OnlyHeaderInjected_WhenOnlyFunctionResultPresent()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: "Plugin activated",
               SystemPrompt: null)); // Only FunctionResult
        var context = CreateContext(containerInstructions);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Header is emitted but container content is skipped sinceSystemPrompt is null
        Assert.Contains("🔧 ACTIVE CONTAINER PROTOCOLS", context.Options!.Instructions!);

        // But the plugin name and FunctionResult should NOT be in system prompt
        Assert.DoesNotContain("TestPlugin", context.Options.Instructions!);
        Assert.DoesNotContain("Plugin activated", context.Options.Instructions);
    }

    [Fact]
    public async Task SystemPrompt_InjectedForMultipleContainers()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("FinancialPlugin", new ContainerInstructionSet(
                FunctionResult: "Financial plugin activated",
               SystemPrompt: "Financial rules: Always validate equations"))
            .Add("WeatherPlugin", new ContainerInstructionSet(
                FunctionResult: "Weather plugin activated",
               SystemPrompt: "Weather rules: Use metric units"));
        var context = CreateContext(containerInstructions);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains("FinancialPlugin", context.Options!.Instructions!);
        Assert.Contains("Financial rules: Always validate equations", context.Options.Instructions);
        Assert.Contains("WeatherPlugin", context.Options.Instructions);
        Assert.Contains("Weather rules: Use metric units", context.Options.Instructions);
    }

    [Fact]
    public async Task SystemPrompt_PreservesOriginalInstructions()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: null,
               SystemPrompt: "Plugin-specific rules"));
        var context = CreateContext(containerInstructions);
        var originalInstructions = "You are a helpful AI assistant.";
        context.Options!.Instructions = originalInstructions;

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Original instructions should be preserved
        Assert.StartsWith(originalInstructions, context.Options.Instructions!);
        Assert.Contains("Plugin-specific rules", context.Options.Instructions);
    }

    [Fact]
    public async Task SystemPrompt_NotDuplicatedOnMultipleInvocations()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: null,
               SystemPrompt: "Test rules"));
        var context = CreateContext(containerInstructions);

        // Act - Call twice
        await middleware.BeforeIterationAsync(context, CancellationToken.None);
        var firstInstructions = context.Options!.Instructions!;

        await middleware.BeforeIterationAsync(context, CancellationToken.None);
        var secondInstructions = context.Options.Instructions!;

        // Assert - Should not duplicate (contains check prevents re-injection)
        Assert.Equal(firstInstructions, secondInstructions);
    }

    #endregion

    #region FunctionResult Tests (Legacy Behavior)

    [Fact]
    public async Task FunctionResult_NotInjectedByMiddleware_OnlyStoredInMetadata()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: "This should appear in function result",
               SystemPrompt: null));
        var context = CreateContext(containerInstructions);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - FunctionResult is NOT injected into system prompt
        Assert.DoesNotContain("This should appear in function result", context.Options!.Instructions!);
    }

    #endregion

    #region Dual-Context Scenarios

    [Fact]
    public async Task DualContext_BothContextsWork_Independently()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("FinancialPlugin", new ContainerInstructionSet(
                FunctionResult: "Plugin activated with capabilities X, Y, Z",
               SystemPrompt: "# FINANCIAL RULES\n- Always validate\n- Show work"));
        var context = CreateContext(containerInstructions);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        //SystemPrompt should be in system instructions
        Assert.Contains("# FINANCIAL RULES", context.Options!.Instructions!);
        Assert.Contains("Always validate", context.Options.Instructions);

        // FunctionResult should NOT be in system instructions (it goes in function result)
        Assert.DoesNotContain("Plugin activated with capabilities", context.Options.Instructions);
    }

    [Fact]
    public async Task DualContext_EmptyContexts_InjectHeader_ButSkipContainer()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: "",
               SystemPrompt: ""));
        var context = CreateContext(containerInstructions);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Header is emitted but container is skipped (empty string is IsNullOrEmpty)
        Assert.Contains("🔧 ACTIVE CONTAINER PROTOCOLS", context.Options!.Instructions!);
        Assert.DoesNotContain("TestPlugin", context.Options.Instructions); // Container is skipped
    }

    [Fact]
    public async Task DualContext_WhitespaceOnlyContexts_InjectsHeaderButNoContent()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: "   ",
               SystemPrompt: "\n\t  ")); // Whitespace-only (not null or empty)
        var context = CreateContext(containerInstructions);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Header is injected, but whitespace-only content is still added
        // (IsNullOrEmpty returns false for whitespace)
        Assert.Contains("🔧 ACTIVE CONTAINER PROTOCOLS", context.Options!.Instructions!);
        Assert.Contains("TestPlugin", context.Options.Instructions); // Container name is added
        // The whitespace itself will be in the output but isn't meaningful
    }

    #endregion

    #region Special Characters and Formatting Tests

    [Fact]
    public async Task SystemPrompt_HandlesMultilineText()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var multilineRules = @"# RULES
- Rule 1
- Rule 2
- Rule 3";
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: null,
               SystemPrompt: multilineRules));
        var context = CreateContext(containerInstructions);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains("# RULES", context.Options!.Instructions!);
        Assert.Contains("- Rule 1", context.Options.Instructions);
        Assert.Contains("- Rule 2", context.Options.Instructions);
        Assert.Contains("- Rule 3", context.Options.Instructions);
    }

    [Fact]
    public async Task SystemPrompt_HandlesMarkdownFormatting()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var markdownRules = @"# Financial Analysis Rules

## Core Principles
- **ALWAYS** validate equations
- *Never* mix periods

## Formatting
Use `decimal` type for precision.";

        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("FinancialPlugin", new ContainerInstructionSet(
                FunctionResult: null,
               SystemPrompt: markdownRules));
        var context = CreateContext(containerInstructions);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - All markdown should be preserved
        Assert.Contains("**ALWAYS**", context.Options!.Instructions!);
        Assert.Contains("*Never*", context.Options.Instructions);
        Assert.Contains("`decimal`", context.Options.Instructions);
    }

    [Fact]
    public async Task SystemPrompt_HandlesSpecialCharacters()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var rulesWithSpecialChars = @"Rules: Use $, €, ¥ symbols. Math: 2 + 2 = 4. Comparison: x > y.";

        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: null,
               SystemPrompt: rulesWithSpecialChars));
        var context = CreateContext(containerInstructions);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains("$", context.Options!.Instructions!);
        Assert.Contains("€", context.Options.Instructions);
        Assert.Contains("x > y", context.Options.Instructions);
    }

    #endregion

    #region State Clearing Tests

    [Fact]
    public async Task ActiveContainerInstructions_ClearedAfterMessageTurn()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: "Activated",
               SystemPrompt: "Rules"));
        var context = CreateContext(containerInstructions);

        // Simulate message turn ending
        context.Response = new ChatMessage(ChatRole.Assistant, "Final response");
        context.TurnHistory = new List<ChatMessage>();

        // Act - Call AfterMessageTurnAsync (cleanup happens here now, not AfterIterationAsync)
        await middleware.AfterMessageTurnAsync(context, CancellationToken.None);

        // Assert - Instructions should be cleared at end of message turn
        var pendingState = context.GetPendingState();
        Assert.NotNull(pendingState);
        var collapsingState = pendingState!.MiddlewareState.Collapsing;
        Assert.NotNull(collapsingState);
        Assert.Empty(collapsingState!.ActiveContainerInstructions);
    }

    [Fact]
    public async Task ActiveContainerInstructions_NotClearedOnNonFinalIteration()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: "Activated",
               SystemPrompt: "Rules"));
        var context = CreateContext(containerInstructions);

        // Simulate non-final iteration (has tool calls)
        context.Response = new ChatMessage(ChatRole.Assistant, "Response with tools");
        context.ToolCalls = new[] { new FunctionCallContent("call_123", "SomeFunction") };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.False(context.IsFinalIteration);
        var pendingState = context.GetPendingState();

        // State should not be modified (no clearing)
        if (pendingState != null)
        {
            var collapsingState = pendingState.MiddlewareState.Collapsing;
            // Either state is null (not modified) or still has the container
            Assert.True(collapsingState == null || collapsingState.ActiveContainerInstructions.Count > 0);
        }
    }

    #endregion

    #region Backward Compatibility Tests

    [Fact]
    public async Task LegacySkillInstructions_StillWork()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("trading", "Legacy trading skill instructions");
        var context = CreateContextWithLegacySkills(activeSkills);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Legacy skill instructions should still be injected
        Assert.Contains("🔧 ACTIVE SKILL PROTOCOLS", context.Options!.Instructions!);
        Assert.Contains("trading", context.Options.Instructions);
        Assert.Contains("Legacy trading skill instructions", context.Options.Instructions);
    }

    [Fact]
    public async Task UnifiedContainers_TakePrecedenceOverLegacySkills()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();

        // Mix of new unified containers and legacy skills
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("FinancialPlugin", new ContainerInstructionSet(
                FunctionResult: null,
               SystemPrompt: "Financial plugin rules"));

        var activeSkills = ImmutableDictionary<string, string>.Empty
            .Add("weather", "Legacy weather skill instructions");

        var context = CreateContextWithBoth(containerInstructions, activeSkills);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Unified containers take precedence (fallback logic)
        Assert.Contains("ACTIVE CONTAINER PROTOCOLS", context.Options!.Instructions!);
        Assert.Contains("Financial plugin rules", context.Options.Instructions);

        // Legacy skills are NOT injected when unified containers are present (OR logic, not AND)
        Assert.DoesNotContain("ACTIVE SKILL PROTOCOLS", context.Options.Instructions);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task NullOptions_DoesNotThrowException()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: null,
               SystemPrompt: "Rules"));
        var context = CreateContext(containerInstructions);
        context.Options = null;

        // Act & Assert - Should not throw
        await middleware.BeforeIterationAsync(context, CancellationToken.None);
        Assert.Null(context.Options);
    }

    [Fact]
    public async Task EmptyContainerInstructions_DoesNotInject()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty;
        var context = CreateContext(containerInstructions);
        var originalInstructions = context.Options!.Instructions!;

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(originalInstructions, context.Options.Instructions);
    }

    [Fact]
    public async Task VeryLongSystemPrompt_IsInjectedCompletely()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var longRules = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Rule {i}: This is rule number {i}"));

        var containerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("TestPlugin", new ContainerInstructionSet(
                FunctionResult: null,
               SystemPrompt: longRules));
        var context = CreateContext(containerInstructions);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - All rules should be present
        Assert.Contains("Rule 1:", context.Options!.Instructions!);
        Assert.Contains("Rule 50:", context.Options.Instructions);
        Assert.Contains("Rule 100:", context.Options.Instructions);
    }

    #endregion

    #region Helper Methods

    private static AgentMiddlewareContext CreateContext(
        ImmutableDictionary<string, ContainerInstructionSet> containerInstructions)
    {
        var state = AgentLoopState.Initial(
            messages: new List<ChatMessage>(),
            runId: "test-run-id",
            conversationId: "test-conv-id",
            agentName: "TestAgent")
            with
            {
                MiddlewareState = new MiddlewareState().WithCollapsing(
                    new CollapsingStateData { ActiveContainerInstructions = containerInstructions })
            };

        // Create dummy tools for the context (required by ContainerMiddleware)
        var dummyTool = AIFunctionFactory.Create(
            () => "test",
            name: "DummyFunction",
            description: "Dummy");

        var context = new AgentMiddlewareContext
        {
            AgentName = "TestAgent",
            ConversationId = "test-conv-id",
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions
            {
                Instructions = "Base instructions",
                Tools = new List<AITool> { dummyTool }
            },
            ToolCalls = Array.Empty<FunctionCallContent>(),
            Iteration = 0,
            CancellationToken = CancellationToken.None
        };
        context.SetOriginalState(state);
        return context;
    }

    private static AgentMiddlewareContext CreateContextWithLegacySkills(
        ImmutableDictionary<string, string> activeSkills)
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

        // Create dummy tools for the context (required by ContainerMiddleware)
        var dummyTool = AIFunctionFactory.Create(
            () => "test",
            name: "DummyFunction",
            description: "Dummy");

        var context = new AgentMiddlewareContext
        {
            AgentName = "TestAgent",
            ConversationId = "test-conv-id",
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions
            {
                Instructions = "Base instructions",
                Tools = new List<AITool> { dummyTool }
            },
            ToolCalls = Array.Empty<FunctionCallContent>(),
            Iteration = 0,
            CancellationToken = CancellationToken.None
        };
        context.SetOriginalState(state);
        return context;
    }

    private static AgentMiddlewareContext CreateContextWithBoth(
        ImmutableDictionary<string, ContainerInstructionSet> containerInstructions,
        ImmutableDictionary<string, string> activeSkills)
    {
        var state = AgentLoopState.Initial(
            messages: new List<ChatMessage>(),
            runId: "test-run-id",
            conversationId: "test-conv-id",
            agentName: "TestAgent")
            with
            {
                MiddlewareState = new MiddlewareState().WithCollapsing(
                    new CollapsingStateData
                    {
                        ActiveContainerInstructions = containerInstructions,
                        ActiveSkillInstructions = activeSkills
                    })
            };

        // Create dummy tools for the context (required by ContainerMiddleware)
        var dummyTool = AIFunctionFactory.Create(
            () => "test",
            name: "DummyFunction",
            description: "Dummy");

        var context = new AgentMiddlewareContext
        {
            AgentName = "TestAgent",
            ConversationId = "test-conv-id",
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions
            {
                Instructions = "Base instructions",
                Tools = new List<AITool> { dummyTool }
            },
            ToolCalls = Array.Empty<FunctionCallContent>(),
            Iteration = 0,
            CancellationToken = CancellationToken.None
        };
        context.SetOriginalState(state);
        return context;
    }

    private static ContainerMiddleware CreateContainerMiddleware()
    {
        // Create a minimal ContainerMiddleware for testing
        // We need at least one dummy tool to pass the "Tools != null && Tools.Count > 0" check
        var dummyFunction = AIFunctionFactory.Create(
            () => "test",
            name: "DummyFunction",
            description: "Dummy function for testing");

        var tools = new List<AITool> { dummyFunction };
        var emptyPlugins = ImmutableHashSet<string>.Empty;
        var config = new CollapsingConfig { Enabled = true };

        return new ContainerMiddleware(tools, emptyPlugins, config);
    }

    #endregion
}
