using HPD.Agent;
using HPD.Agent.Middleware;
using HPD_Agent.Scoping;
using HPD_Agent.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using System.Collections.Immutable;
using Xunit;

namespace HPD_Agent.Tests.Middleware;

/// <summary>
/// Comprehensive tests for ToolScopingMiddleware.
/// Tests the full lifecycle: visibility filtering (BeforeIteration) and container detection (AfterIteration).
/// </summary>
public class ToolScopingMiddlewareTests
{
    // ═══════════════════════════════════════════════════════
    // BEFORE ITERATION TESTS - Tool Visibility Filtering
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task BeforeIteration_WhenDisabled_DoesNotFilterTools()
    {
        // Arrange
        var (container, members) = CreateScopedPlugin("TestPlugin", "Test plugin", "Add", "Subtract");
        var allTools = new List<AITool> { container, members[0], members[1] };

        var middleware = new ToolScopingMiddleware(
            allTools,
            ImmutableHashSet<string>.Empty,
            new ScopingConfig { Enabled = false });

        var context = CreateContext();
        context.Options = new ChatOptions { Tools = allTools };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - tools should remain unchanged when disabled
        Assert.Equal(3, context.Options.Tools.Count);
    }

    [Fact]
    public async Task BeforeIteration_WhenEnabled_FiltersToVisibleTools()
    {
        // Arrange
        var (container, members) = CreateScopedPlugin("TestPlugin", "Test plugin", "Add", "Subtract");
        var nonScoped = CreateNonScopedFunction("Echo");
        var allTools = new List<AITool> { container, members[0], members[1], nonScoped };

        var middleware = new ToolScopingMiddleware(
            allTools,
            ImmutableHashSet<string>.Empty,
            new ScopingConfig { Enabled = true });

        var context = CreateContext();
        context.Options = new ChatOptions { Tools = allTools };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - only container and non-scoped functions visible (members hidden)
        var toolNames = context.Options.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.Contains("TestPlugin", toolNames);
        Assert.Contains("Echo", toolNames);
        Assert.DoesNotContain("Add", toolNames);
        Assert.DoesNotContain("Subtract", toolNames);
    }

    [Fact]
    public async Task BeforeIteration_WhenPluginExpanded_ShowsMemberFunctions()
    {
        // Arrange
        var (container, members) = CreateScopedPlugin("TestPlugin", "Test plugin", "Add", "Subtract");
        var allTools = new List<AITool> { container, members[0], members[1] };

        var middleware = new ToolScopingMiddleware(
            allTools,
            ImmutableHashSet<string>.Empty,
            new ScopingConfig { Enabled = true });

        // State with expanded plugin
        var state = CreateEmptyState();
        var scopingState = new ScopingStateData().WithExpandedPlugin("TestPlugin");
        state = state with { MiddlewareState = state.MiddlewareState.WithScoping(scopingState) };

        var context = CreateContext(state: state);
        context.Options = new ChatOptions { Tools = allTools };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - all functions visible when plugin is expanded
        var toolNames = context.Options.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.Contains("Add", toolNames);
        Assert.Contains("Subtract", toolNames);
    }

    [Fact]
    public async Task BeforeIteration_WhenNoTools_DoesNothing()
    {
        // Arrange
        var middleware = new ToolScopingMiddleware(
            Array.Empty<AITool>(),
            ImmutableHashSet<string>.Empty);

        var context = CreateContext();
        context.Options = new ChatOptions { Tools = null };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - no exception, options unchanged
        Assert.Null(context.Options.Tools);
    }

    [Fact]
    public async Task BeforeIteration_PreservesOtherChatOptionsProperties()
    {
        // Arrange
        var function = CreateNonScopedFunction("TestFunc");
        var allTools = new List<AITool> { function };

        var middleware = new ToolScopingMiddleware(
            allTools,
            ImmutableHashSet<string>.Empty);

        var context = CreateContext();
        context.Options = new ChatOptions
        {
            Tools = allTools,
            ModelId = "test-model",
            Temperature = 0.5f,
            MaxOutputTokens = 1000,
            ConversationId = "test-conversation"
        };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - all other properties preserved
        Assert.Equal("test-model", context.Options.ModelId);
        Assert.Equal(0.5f, context.Options.Temperature);
        Assert.Equal(1000, context.Options.MaxOutputTokens);
        Assert.Equal("test-conversation", context.Options.ConversationId);
    }

    // ═══════════════════════════════════════════════════════
    // AFTER ITERATION TESTS - Container Detection
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task AfterIteration_WhenDisabled_DoesNotDetectContainers()
    {
        // Arrange
        var (container, _) = CreateScopedPlugin("TestPlugin", "Test plugin", "Add");
        var allTools = new List<AITool> { container };

        var middleware = new ToolScopingMiddleware(
            allTools,
            ImmutableHashSet<string>.Empty,
            new ScopingConfig { Enabled = false });

        var context = CreateContext();
        context.Options = new ChatOptions { Tools = allTools };
        context.ToolCalls = new[] { CreateToolCall("TestPlugin") };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - no state updates
        Assert.False(context.HasPendingStateUpdates);
    }

    [Fact]
    public async Task AfterIteration_WhenNoToolCalls_DoesNothing()
    {
        // Arrange
        var middleware = new ToolScopingMiddleware(
            Array.Empty<AITool>(),
            ImmutableHashSet<string>.Empty);

        var context = CreateContext();
        context.ToolCalls = Array.Empty<FunctionCallContent>();

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - no state updates
        Assert.False(context.HasPendingStateUpdates);
    }

    [Fact]
    public async Task AfterIteration_DetectsPluginContainer_UpdatesState()
    {
        // Arrange
        var (container, members) = CreateScopedPlugin("FinancialPlugin", "Financial tools", "Add", "Subtract");
        var allTools = new List<AITool> { container, members[0], members[1] };

        var middleware = new ToolScopingMiddleware(
            allTools,
            ImmutableHashSet<string>.Empty);

        var context = CreateContext();
        context.Options = new ChatOptions { Tools = allTools };
        context.ToolCalls = new[] { CreateToolCall("FinancialPlugin") };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - state updated with expanded plugin
        Assert.True(context.HasPendingStateUpdates);
        var pendingState = context.GetPendingState();
        Assert.NotNull(pendingState);

        // Check scoping state
        Assert.Contains("FinancialPlugin", pendingState.MiddlewareState.Scoping?.ExpandedPlugins ?? ImmutableHashSet<string>.Empty);
    }

    [Fact]
    public async Task AfterIteration_DetectsSkillContainer_UpdatesState()
    {
        // Arrange
        var skill = CreateSkillContainer("TestSkill", "Test skill description", "func1", "func2");
        var func1 = CreateNonScopedFunction("func1");
        var func2 = CreateNonScopedFunction("func2");
        var allTools = new List<AITool> { skill, func1, func2 };

        var middleware = new ToolScopingMiddleware(
            allTools,
            ImmutableHashSet<string>.Empty);

        var context = CreateContext();
        context.Options = new ChatOptions { Tools = allTools };
        context.ToolCalls = new[] { CreateToolCall("TestSkill") };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.True(context.HasPendingStateUpdates);
        var pendingState = context.GetPendingState();
        Assert.NotNull(pendingState);

        // Check scoping state
        Assert.Contains("TestSkill", pendingState.MiddlewareState.Scoping?.ExpandedSkills ?? ImmutableHashSet<string>.Empty);
    }

    [Fact]
    public async Task AfterIteration_SkillWithInstructions_StoresInstructions()
    {
        // Arrange
        var instructions = "Always use metric units when performing calculations.";
        var skill = CreateSkillContainerWithInstructions("MetricSkill", "Metric calculations", instructions);
        var allTools = new List<AITool> { skill };

        var middleware = new ToolScopingMiddleware(
            allTools,
            ImmutableHashSet<string>.Empty);

        var context = CreateContext();
        context.Options = new ChatOptions { Tools = allTools };
        context.ToolCalls = new[] { CreateToolCall("MetricSkill") };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        var pendingState = context.GetPendingState();
        Assert.NotNull(pendingState);

        // Check instructions in scoping state
        var scopingState = pendingState.MiddlewareState.Scoping;
        Assert.NotNull(scopingState);
        Assert.True(scopingState!.ActiveSkillInstructions.ContainsKey("MetricSkill"));
        Assert.Equal(instructions, scopingState.ActiveSkillInstructions["MetricSkill"]);
    }

    [Fact]
    public async Task AfterIteration_NonContainerToolCall_DoesNotUpdateState()
    {
        // Arrange
        var regularFunc = CreateNonScopedFunction("RegularFunction");
        var allTools = new List<AITool> { regularFunc };

        var middleware = new ToolScopingMiddleware(
            allTools,
            ImmutableHashSet<string>.Empty);

        var context = CreateContext();
        context.Options = new ChatOptions { Tools = allTools };
        context.ToolCalls = new[] { CreateToolCall("RegularFunction") };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert - no state updates for non-container calls
        Assert.False(context.HasPendingStateUpdates);
    }

    [Fact]
    public async Task AfterIteration_MultipleContainers_ExpandsAll()
    {
        // Arrange
        var (plugin1, _) = CreateScopedPlugin("Plugin1", "First plugin", "A");
        var (plugin2, _) = CreateScopedPlugin("Plugin2", "Second plugin", "B");
        var skill = CreateSkillContainer("Skill1", "First skill");
        var allTools = new List<AITool> { plugin1, plugin2, skill };

        var middleware = new ToolScopingMiddleware(
            allTools,
            ImmutableHashSet<string>.Empty);

        var context = CreateContext();
        context.Options = new ChatOptions { Tools = allTools };
        context.ToolCalls = new[]
        {
            CreateToolCall("Plugin1"),
            CreateToolCall("Plugin2"),
            CreateToolCall("Skill1")
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        var pendingState = context.GetPendingState();
        Assert.NotNull(pendingState);

        var scopingState = pendingState.MiddlewareState.Scoping;
        Assert.NotNull(scopingState);
        Assert.Contains("Plugin1", scopingState!.ExpandedPlugins);
        Assert.Contains("Plugin2", scopingState.ExpandedPlugins);
        Assert.Contains("Skill1", scopingState.ExpandedSkills);
    }

    // ═══════════════════════════════════════════════════════
    // INTEGRATION TESTS - Full Middleware Lifecycle
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task FullLifecycle_ExpandPlugin_NextIterationShowsFunctions()
    {
        // Arrange
        var (container, members) = CreateScopedPlugin("TestPlugin", "Test plugin", "Add", "Subtract");
        var allTools = new List<AITool> { container, members[0], members[1] };

        var middleware = new ToolScopingMiddleware(
            allTools,
            ImmutableHashSet<string>.Empty);

        // Iteration 1: Plugin not expanded
        var context1 = CreateContext();
        context1.Options = new ChatOptions { Tools = allTools };

        await middleware.BeforeIterationAsync(context1, CancellationToken.None);

        var visibleIter1 = context1.Options.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.Contains("TestPlugin", visibleIter1);
        Assert.DoesNotContain("Add", visibleIter1);

        // Simulate LLM calling the container
        context1.ToolCalls = new[] { CreateToolCall("TestPlugin") };
        await middleware.AfterIterationAsync(context1, CancellationToken.None);

        // Iteration 2: Plugin now expanded
        var expandedState = context1.GetPendingState()!;
        var context2 = CreateContext(state: expandedState);
        context2.Options = new ChatOptions { Tools = allTools };

        await middleware.BeforeIterationAsync(context2, CancellationToken.None);

        // Assert - member functions now visible
        var visibleIter2 = context2.Options.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.Contains("Add", visibleIter2);
        Assert.Contains("Subtract", visibleIter2);
    }

    // ═══════════════════════════════════════════════════════
    // SCOPING STATE DATA TESTS
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void ScopingStateData_WithExpandedPlugin_AddsToSet()
    {
        var state = new ScopingStateData();

        var updated = state.WithExpandedPlugin("Plugin1");

        Assert.Contains("Plugin1", updated.ExpandedPlugins);
        Assert.DoesNotContain("Plugin1", state.ExpandedPlugins); // Original unchanged
    }

    [Fact]
    public void ScopingStateData_WithExpandedSkill_AddsToSet()
    {
        var state = new ScopingStateData();

        var updated = state.WithExpandedSkill("Skill1");

        Assert.Contains("Skill1", updated.ExpandedSkills);
    }

    [Fact]
    public void ScopingStateData_WithSkillInstructions_AddsToDict()
    {
        var state = new ScopingStateData();

        var updated = state.WithSkillInstructions("Skill1", "Some instructions");

        Assert.True(updated.ActiveSkillInstructions.ContainsKey("Skill1"));
        Assert.Equal("Some instructions", updated.ActiveSkillInstructions["Skill1"]);
    }

    [Fact]
    public void ScopingStateData_ClearSkillInstructions_EmptiesDict()
    {
        var state = new ScopingStateData()
            .WithSkillInstructions("Skill1", "Instructions 1")
            .WithSkillInstructions("Skill2", "Instructions 2");

        var cleared = state.ClearSkillInstructions();

        Assert.Empty(cleared.ActiveSkillInstructions);
        Assert.Equal(2, state.ActiveSkillInstructions.Count); // Original unchanged
    }

    // ═══════════════════════════════════════════════════════
    // CHAT OPTIONS EXTENSIONS TESTS
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void ChatOptionsExtensions_WithTools_CopiesAllProperties()
    {
        // Arrange
        var originalTools = new List<AITool> { CreateNonScopedFunction("Original") };
        var newTools = new List<AITool> { CreateNonScopedFunction("New") };

        var original = new ChatOptions
        {
            ModelId = "test-model",
            Tools = originalTools,
            ToolMode = ChatToolMode.Auto,
            Temperature = 0.7f,
            MaxOutputTokens = 2000,
            TopP = 0.9f,
            TopK = 40,
            FrequencyPenalty = 0.1f,
            PresencePenalty = 0.2f,
            StopSequences = new[] { "STOP" },
            Seed = 42,
            ConversationId = "conv-123"
        };

        // Act
        var updated = original.Clone();
        updated.Tools = newTools;

        // Assert - new tools applied
        Assert.Single(updated.Tools);
        Assert.Equal("New", ((AIFunction)updated.Tools[0]).Name);

        // Assert - all other properties preserved
        Assert.Equal("test-model", updated.ModelId);
        Assert.Equal(ChatToolMode.Auto, updated.ToolMode);
        Assert.Equal(0.7f, updated.Temperature);
        Assert.Equal(2000, updated.MaxOutputTokens);
        Assert.Equal(0.9f, updated.TopP);
        Assert.Equal(40, updated.TopK);
        Assert.Equal(0.1f, updated.FrequencyPenalty);
        Assert.Equal(0.2f, updated.PresencePenalty);
        Assert.Equal(new[] { "STOP" }, updated.StopSequences);
        Assert.Equal(42, updated.Seed);
        Assert.Equal("conv-123", updated.ConversationId);

        // Assert - original unchanged
        Assert.Equal(originalTools, original.Tools);
    }

    // ═══════════════════════════════════════════════════════
    // AFTER MESSAGE TURN TESTS - Ephemeral Result Filtering
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task AfterMessageTurn_RemovesContainerExpansions()
    {
        // Arrange
        var containerFunc = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("expanded"),
            new AIFunctionFactoryOptions
            {
                Name = "ExpandPlugin",
                Description = "Expands plugin",
                AdditionalProperties = new Dictionary<string, object?> { ["IsContainer"] = true }
            });

        var regularFunc1 = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>(42),
            new AIFunctionFactoryOptions { Name = "Calculate", Description = "Calculates" });

        var regularFunc2 = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("Hello"),
            new AIFunctionFactoryOptions { Name = "GetGreeting", Description = "Gets greeting" });

        var allTools = new List<AITool> { containerFunc, regularFunc1, regularFunc2 };
        var middleware = new ToolScopingMiddleware(allTools, ImmutableHashSet<string>.Empty);

        var turnHistory = new List<ChatMessage>
        {
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call1", result: "PluginExpanded"),  // Container
                new FunctionResultContent("call2", result: "42"),              // Regular
                new FunctionResultContent("call3", result: "Hello")            // Regular
            })
        };

        var context = CreateContext();
        context.TurnHistory = turnHistory;
        context.Properties["EphemeralCallIds"] = new HashSet<string> { "call1" };

        // Act
        await middleware.AfterMessageTurnAsync(context, CancellationToken.None);

        // Assert
        Assert.Single(turnHistory);
        var message = turnHistory[0];
        Assert.Equal(2, message.Contents.Count);
        Assert.DoesNotContain(message.Contents, c => c is FunctionResultContent frc && frc.CallId == "call1");
        Assert.Contains(message.Contents, c => c is FunctionResultContent frc && frc.CallId == "call2");
        Assert.Contains(message.Contents, c => c is FunctionResultContent frc && frc.CallId == "call3");
    }

    [Fact]
    public async Task AfterMessageTurn_PreservesNonFunctionContent()
    {
        // Arrange
        var regularFunc = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("Result"),
            new AIFunctionFactoryOptions { Name = "RegularFunction", Description = "Regular" });

        var allTools = new List<AITool> { regularFunc };
        var middleware = new ToolScopingMiddleware(allTools, ImmutableHashSet<string>.Empty);

        var turnHistory = new List<ChatMessage>
        {
            new(ChatRole.Assistant, new List<AIContent> { new TextContent("Some text") }),
            new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call1", "Result") })
        };

        var context = CreateContext();
        context.TurnHistory = turnHistory;

        // Act
        await middleware.AfterMessageTurnAsync(context, CancellationToken.None);

        // Assert: Text content should remain unchanged
        Assert.Equal(2, turnHistory.Count);
        Assert.Contains(turnHistory, m => m.Role == ChatRole.Assistant);
        Assert.Contains(turnHistory, m => m.Role == ChatRole.Tool);
    }

    [Fact]
    public async Task AfterMessageTurn_HandlesEmptyTurnHistory()
    {
        // Arrange
        var middleware = new ToolScopingMiddleware(
            Array.Empty<AITool>(),
            ImmutableHashSet<string>.Empty);

        var turnHistory = new List<ChatMessage>();
        var context = CreateContext();
        context.TurnHistory = turnHistory;

        // Act & Assert - should not throw
        await middleware.AfterMessageTurnAsync(context, CancellationToken.None);
        Assert.Empty(turnHistory);
    }

    [Fact]
    public async Task AfterMessageTurn_MultipleContainersRemoved()
    {
        // Arrange
        var containerA = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("expanded"),
            new AIFunctionFactoryOptions
            {
                Name = "ExpandPluginA",
                Description = "Expands A",
                AdditionalProperties = new Dictionary<string, object?> { ["IsContainer"] = true }
            });

        var containerB = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("expanded"),
            new AIFunctionFactoryOptions
            {
                Name = "ExpandPluginB",
                Description = "Expands B",
                AdditionalProperties = new Dictionary<string, object?> { ["IsContainer"] = true }
            });

        var regularFunc = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("result"),
            new AIFunctionFactoryOptions { Name = "DoWork", Description = "Does work" });

        var allTools = new List<AITool> { containerA, containerB, regularFunc };
        var middleware = new ToolScopingMiddleware(allTools, ImmutableHashSet<string>.Empty);

        var turnHistory = new List<ChatMessage>
        {
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call1", "PluginA expanded"),
                new FunctionResultContent("call2", "PluginB expanded"),
                new FunctionResultContent("call3", "Actual result")
            })
        };

        var context = CreateContext();
        context.TurnHistory = turnHistory;
        context.Properties["EphemeralCallIds"] = new HashSet<string> { "call1", "call2" };

        // Act
        await middleware.AfterMessageTurnAsync(context, CancellationToken.None);

        // Assert: Only the regular function result should remain
        Assert.Single(turnHistory);
        var contents = turnHistory[0].Contents;
        Assert.Single(contents);
        var result = Assert.IsType<FunctionResultContent>(contents[0]);
        Assert.Equal("call3", result.CallId);
    }

    [Fact]
    public async Task AfterMessageTurn_RemovesEntireMessageIfAllEphemeral()
    {
        // Arrange
        var containerFunc = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("expanded"),
            new AIFunctionFactoryOptions
            {
                Name = "ExpandPlugin",
                Description = "Expands",
                AdditionalProperties = new Dictionary<string, object?> { ["IsContainer"] = true }
            });

        var allTools = new List<AITool> { containerFunc };
        var middleware = new ToolScopingMiddleware(allTools, ImmutableHashSet<string>.Empty);

        var turnHistory = new List<ChatMessage>
        {
            new(ChatRole.User, new List<AIContent> { new TextContent("Hello") }),
            new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call1", "Container expanded") }), // All ephemeral
            new(ChatRole.Assistant, new List<AIContent> { new TextContent("Done") })
        };

        var context = CreateContext();
        context.TurnHistory = turnHistory;
        context.Properties["EphemeralCallIds"] = new HashSet<string> { "call1" };

        // Act
        await middleware.AfterMessageTurnAsync(context, CancellationToken.None);

        // Assert: Tool message with only ephemeral results should be removed
        Assert.Equal(2, turnHistory.Count);
        Assert.DoesNotContain(turnHistory, m => m.Role == ChatRole.Tool);
        Assert.Contains(turnHistory, m => m.Role == ChatRole.User);
        Assert.Contains(turnHistory, m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task AfterMessageTurn_MixedSkillAndPluginContainers()
    {
        // Arrange
        var scopedPluginContainer = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("Plugin expanded"),
            new AIFunctionFactoryOptions
            {
                Name = "MathPlugin",
                Description = "Math plugin",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true
                }
            });

        var skillContainer = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("Skill expanded"),
            new AIFunctionFactoryOptions
            {
                Name = "QuickAnalysis",
                Description = "Analysis skill",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["IsSkill"] = true
                }
            });

        var regularFunc = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("Result"),
            new AIFunctionFactoryOptions { Name = "Calculate", Description = "Calculates" });

        var allTools = new List<AITool> { scopedPluginContainer, skillContainer, regularFunc };
        var middleware = new ToolScopingMiddleware(allTools, ImmutableHashSet<string>.Empty);

        var turnHistory = new List<ChatMessage>
        {
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call1", "Plugin expanded"),
                new FunctionResultContent("call2", "Skill expanded"),
                new FunctionResultContent("call3", "Result")
            })
        };

        var context = CreateContext();
        context.TurnHistory = turnHistory;
        context.Properties["EphemeralCallIds"] = new HashSet<string> { "call1", "call2" };

        // Act
        await middleware.AfterMessageTurnAsync(context, CancellationToken.None);

        // Assert: Both containers filtered, only regular function remains
        Assert.Single(turnHistory);
        var contents = turnHistory[0].Contents;
        Assert.Single(contents);
        Assert.DoesNotContain(contents, c => c is FunctionResultContent frc && frc.CallId == "call1");
        Assert.DoesNotContain(contents, c => c is FunctionResultContent frc && frc.CallId == "call2");
        Assert.Contains(contents, c => c is FunctionResultContent frc && frc.CallId == "call3");
    }

    [Fact]
    public async Task AfterMessageTurn_NoEphemeralCallIds_NoFiltering()
    {
        // Arrange
        var regularFunc = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("Result"),
            new AIFunctionFactoryOptions { Name = "Calculate", Description = "Calculates" });

        var allTools = new List<AITool> { regularFunc };
        var middleware = new ToolScopingMiddleware(allTools, ImmutableHashSet<string>.Empty);

        var turnHistory = new List<ChatMessage>
        {
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call1", "Result 1"),
                new FunctionResultContent("call2", "Result 2")
            })
        };

        var context = CreateContext();
        context.TurnHistory = turnHistory;
        // No EphemeralCallIds in Properties

        // Act
        await middleware.AfterMessageTurnAsync(context, CancellationToken.None);

        // Assert: No filtering should occur
        Assert.Single(turnHistory);
        Assert.Equal(2, turnHistory[0].Contents.Count);
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

    private static AgentMiddlewareContext CreateContext(AgentLoopState? state = null)
    {
        var context = new AgentMiddlewareContext
        {
            AgentName = "TestAgent",
            CancellationToken = CancellationToken.None,
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions(),
            ConversationId = "test-conversation",
            Iteration = 0
        };
        context.SetOriginalState(state ?? CreateEmptyState());
        return context;
    }

    private static (AIFunction Container, AIFunction[] Members) CreateScopedPlugin(
        string pluginName,
        string description,
        params string[] memberNames)
    {
        var members = memberNames.Select(name =>
            ScopedPluginTestHelper.CreatePluginMemberFunction(
                name,
                $"{name} function",
                (args, ct) => Task.FromResult<object?>($"{name} result"),
                pluginName)
        ).ToArray();

        var container = ScopedPluginTestHelper.CreateContainerFunction(pluginName, description, members);

        return (container, members);
    }

    private static AIFunction CreateNonScopedFunction(string name)
    {
        return ScopedPluginTestHelper.CreateNonScopedFunction(
            name,
            $"{name} function",
            (args, ct) => Task.FromResult<object?>($"{name} result"));
    }

    private static AIFunction CreateSkillContainer(
        string name,
        string description,
        params string[] referencedFunctions)
    {
        return AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>($"{name} activated"),
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["IsSkill"] = true,
                    ["ReferencedFunctions"] = referencedFunctions
                }
            });
    }

    private static AIFunction CreateSkillContainerWithInstructions(
        string name,
        string description,
        string instructions)
    {
        return AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>($"{name} activated"),
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["IsSkill"] = true,
                    ["Instructions"] = instructions
                }
            });
    }

    private static FunctionCallContent CreateToolCall(string name)
    {
        return new FunctionCallContent(
            callId: Guid.NewGuid().ToString(),
            name: name,
            arguments: new Dictionary<string, object?>());
    }
}
