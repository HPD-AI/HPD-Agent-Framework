using HPD.Agent;
using HPD.Agent.Middleware;
using HPD.Agent.Collapsing;
using Microsoft.Extensions.AI;
using System.Collections.Immutable;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// Legacy tests for SkillInstructionMiddleware functionality (now merged into ContainerMiddleware).
/// Ported from SkillInstructionIterationFilterTests.cs with updates for new architecture.
/// </summary>
public class SkillInstructionMiddlewareTests
{
    [Fact]
    public async Task Middleware_DoesNotModifyInstructions_WhenNoActiveContainers()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var context = CreateContext(activeContainers: ImmutableDictionary<string, ContainerInstructionSet>.Empty);
        var originalInstructions = context.Options!.Instructions;

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(originalInstructions, context.Options.Instructions);
    }

    [Fact]
    public async Task Middleware_InjectsContainerInstructions_WhenActiveContainersExist()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var activeContainers = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("trading", new ContainerInstructionSet(null, "Trading skill instructions for buying and selling stocks"));
        var context = CreateContext(activeContainers: activeContainers);
        var originalInstructions = context.Options!.Instructions;

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains("🔧 ACTIVE CONTAINER PROTOCOLS", context.Options.Instructions!);
        Assert.Contains("trading", context.Options.Instructions);
        Assert.Contains("Trading skill instructions", context.Options.Instructions);
    }

    [Fact]
    public async Task Middleware_InjectsMultipleContainerInstructions_WhenMultipleActiveContainers()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var activeContainers = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("trading", new ContainerInstructionSet(null, "Trading skill instructions"))
            .Add("weather", new ContainerInstructionSet(null, "Weather skill instructions"));
        var context = CreateContext(activeContainers: activeContainers);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains("trading", context.Options!.Instructions!);
        Assert.Contains("weather", context.Options.Instructions);
        Assert.Contains("Trading skill instructions", context.Options.Instructions);
        Assert.Contains("Weather skill instructions", context.Options.Instructions);
    }

    [Fact]
    public async Task Middleware_ClearsContainers_AfterMessageTurn()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var activeContainers = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("trading", new ContainerInstructionSet(null, "Trading skill instructions"));
        var context = CreateContext(activeContainers: activeContainers);

        // Act - Cleanup happens at AfterMessageTurnAsync
        await middleware.AfterMessageTurnAsync(context, CancellationToken.None);

        // Assert - Check that middleware cleared active container instructions
        var pendingState = context.GetPendingState();
        Assert.NotNull(pendingState);
        var CollapsingState = pendingState!.MiddlewareState.Collapsing;
        Assert.NotNull(CollapsingState);
        Assert.Empty(CollapsingState!.ActiveContainerInstructions);
    }

    [Fact]
    public async Task Middleware_DoesNotSignalClearContainers_WhenNotFinalIteration()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var activeContainers = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("trading", new ContainerInstructionSet(null, "Trading skill instructions"));
        var context = CreateContext(activeContainers: activeContainers);

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
    public async Task Middleware_DoesNotSignalClearContainers_WhenNoActiveContainers()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var context = CreateContext(activeContainers: ImmutableDictionary<string, ContainerInstructionSet>.Empty);

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
        var middleware = CreateContainerMiddleware();
        var activeContainers = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("trading", new ContainerInstructionSet(null, "Trading skill instructions"));
        var context = CreateContext(activeContainers: activeContainers);
        context.Options = null; // Simulate null options

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Null(context.Options);
    }

    [Fact]
    public async Task Middleware_InjectsInBefore_ClearsAfterMessageTurn()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var activeContainers = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("trading", new ContainerInstructionSet(null, "Trading instructions"));
        var context = CreateContext(activeContainers: activeContainers);

        // Act - Before phase
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Instructions injected
        Assert.Contains("Trading instructions", context.Options!.Instructions!);

        // Act - Cleanup happens at AfterMessageTurnAsync
        await middleware.AfterMessageTurnAsync(context, CancellationToken.None);

        // Assert - State updated to clear containers
        var pendingState = context.GetPendingState();
        Assert.NotNull(pendingState);
        var CollapsingState = pendingState!.MiddlewareState.Collapsing;
        Assert.NotNull(CollapsingState);
        Assert.Empty(CollapsingState!.ActiveContainerInstructions);
    }

    [Fact]
    public async Task Middleware_PreservesOriginalInstructions_WhenInjecting()
    {
        // Arrange
        var middleware = CreateContainerMiddleware();
        var activeContainers = ImmutableDictionary<string, ContainerInstructionSet>.Empty
            .Add("trading", new ContainerInstructionSet(null, "Trading instructions"));
        var context = CreateContext(activeContainers: activeContainers);
        var originalInstructions = "Original system instructions";
        context.Options!.Instructions = originalInstructions;

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Original instructions should still be present
        Assert.Contains(originalInstructions, context.Options.Instructions!);
        Assert.Contains("Trading instructions", context.Options.Instructions);
    }

    private static ContainerMiddleware CreateContainerMiddleware()
    {
        // Create a dummy tool so ContainerMiddleware doesn't early-return
        var dummyFunction = AIFunctionFactory.Create(
            () => "test",
            name: "DummyFunction",
            description: "Dummy function for testing");

        var tools = new List<AITool> { dummyFunction };
        var emptyPlugins = ImmutableHashSet<string>.Empty;
        var config = new CollapsingConfig { Enabled = true };

        return new ContainerMiddleware(tools, emptyPlugins, config);
    }

    private static AgentMiddlewareContext CreateContext(ImmutableDictionary<string, ContainerInstructionSet> activeContainers)
    {
        // Create dummy tools for the context (required by ContainerMiddleware)
        var dummyTool = AIFunctionFactory.Create(
            () => "test",
            name: "DummyFunction",
            description: "Dummy");

        var state = AgentLoopState.Initial(
            messages: new List<ChatMessage>(),
            runId: "test-run-id",
            conversationId: "test-conv-id",
            agentName: "TestAgent")
            with
            {
                MiddlewareState = new MiddlewareState().WithCollapsing(
                    new CollapsingStateData { ActiveContainerInstructions = activeContainers })
            };

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
            ToolCalls = Array.Empty<FunctionCallContent>(), // Initialize to empty array
            Iteration = 0,
            CancellationToken = CancellationToken.None
        };
        context.SetOriginalState(state);
        return context;
    }
}
