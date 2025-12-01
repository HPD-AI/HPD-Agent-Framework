using Xunit;
using HPD.Agent.Middleware;
using HPD.Agent;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// Tests for the scoped middleware system using the new ConditionalWeakTable-based architecture.
/// Ported from ScopedFilterSystemTests.cs with updates for middleware pattern.
/// </summary>
public class ScopedMiddlewareSystemTests
{
    private class TestMiddleware : IAgentMiddleware
    {
        public string Name { get; }
        public bool WasCalled { get; set; }

        public TestMiddleware(string name) => Name = name;

        public Task BeforeIterationAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }

        public Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }

        public Task AfterIterationAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeToolExecutionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task BeforeMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AfterMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    #region MiddlewareScope Enum Tests

    [Fact]
    public void MiddlewareScope_HasCorrectValues()
    {
        // Ensure scope values are in correct order for priority
        Assert.Equal(0, (int)MiddlewareScope.Global);
        Assert.Equal(1, (int)MiddlewareScope.Plugin);
        Assert.Equal(2, (int)MiddlewareScope.Skill);
        Assert.Equal(3, (int)MiddlewareScope.Function);
    }

    #endregion

    #region Scoped Middleware ShouldExecute Tests

    [Fact]
    public void GlobalMiddleware_AppliesToAllFunctions()
    {
        // Arrange
        var middleware = new TestMiddleware("global").AsGlobal();

        // Act & Assert - should apply to any context
        Assert.True(middleware.ShouldExecute(CreateContext("AnyFunction")));
        Assert.True(middleware.ShouldExecute(CreateContext("AnyFunction", pluginName: "AnyPlugin")));
        Assert.True(middleware.ShouldExecute(CreateContext("AnyFunction", pluginName: "AnyPlugin", skillName: "AnySkill")));
        Assert.True(middleware.ShouldExecute(CreateContext("AnyFunction", isSkillContainer: true)));
    }

    [Fact]
    public void PluginMiddleware_AppliesToPluginFunctions()
    {
        // Arrange
        var middleware = new TestMiddleware("plugin").ForPlugin("FileSystemPlugin");

        // Act & Assert
        Assert.True(middleware.ShouldExecute(CreateContext("ReadFile", pluginName: "FileSystemPlugin")));
        Assert.False(middleware.ShouldExecute(CreateContext("ReadFile", pluginName: "DatabasePlugin")));
        Assert.False(middleware.ShouldExecute(CreateContext("ReadFile", pluginName: null)));
    }

    [Fact]
    public void SkillMiddleware_AppliesToSkillContainer()
    {
        // Arrange
        var middleware = new TestMiddleware("skill").ForSkill("analyze_codebase");

        // Act - skill container itself
        var context = CreateContext(
            functionName: "analyze_codebase",
            pluginName: null,
            skillName: null,
            isSkillContainer: true
        );

        // Assert
        Assert.True(middleware.ShouldExecute(context));
    }

    [Fact]
    public void SkillMiddleware_AppliesToReferencedFunctions()
    {
        // Arrange
        var middleware = new TestMiddleware("skill").ForSkill("analyze_codebase");

        // Act - function called by skill
        var context = CreateContext(
            functionName: "ReadFile",
            pluginName: "FileSystemPlugin",
            skillName: "analyze_codebase",
            isSkillContainer: false
        );

        // Assert
        Assert.True(middleware.ShouldExecute(context));
    }

    [Fact]
    public void SkillMiddleware_DoesNotApplyToOtherSkillContainers()
    {
        // Arrange
        var middleware = new TestMiddleware("skill").ForSkill("analyze_codebase");

        // Act - different skill container
        var context = CreateContext(
            functionName: "refactor_code",
            pluginName: null,
            skillName: null,
            isSkillContainer: true
        );

        // Assert
        Assert.False(middleware.ShouldExecute(context));
    }

    [Fact]
    public void SkillMiddleware_DoesNotApplyToFunctionsFromOtherSkills()
    {
        // Arrange
        var middleware = new TestMiddleware("skill").ForSkill("analyze_codebase");

        // Act - function called by different skill
        var context = CreateContext(
            functionName: "WriteFile",
            pluginName: "FileSystemPlugin",
            skillName: "refactor_code",
            isSkillContainer: false
        );

        // Assert
        Assert.False(middleware.ShouldExecute(context));
    }

    [Fact]
    public void FunctionMiddleware_AppliesToSpecificFunction()
    {
        // Arrange
        var middleware = new TestMiddleware("function").ForFunction("ReadFile");

        // Act & Assert
        Assert.True(middleware.ShouldExecute(CreateContext("ReadFile")));
        Assert.False(middleware.ShouldExecute(CreateContext("WriteFile")));
    }

    #endregion

    #region Pipeline Integration Tests

    [Fact]
    public async Task Pipeline_FiltersMiddlewaresByScope()
    {
        // Arrange
        var globalMiddleware = new TestMiddleware("Global");
        var pluginMiddleware = new TestMiddleware("Plugin");
        var skillMiddleware = new TestMiddleware("Skill");
        var functionMiddleware = new TestMiddleware("Function");

        globalMiddleware.AsGlobal();
        pluginMiddleware.ForPlugin("FileSystemPlugin");
        skillMiddleware.ForSkill("analyze_codebase");
        functionMiddleware.ForFunction("ReadFile");

        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[]
        {
            globalMiddleware,
            pluginMiddleware,
            skillMiddleware,
            functionMiddleware
        });

        var context = CreateContext(
            "ReadFile",
            pluginName: "FileSystemPlugin",
            skillName: "analyze_codebase",
            isSkillContainer: false
        );

        // Act
        await pipeline.ExecuteBeforeSequentialFunctionAsync(context, CancellationToken.None);

        // Assert - all 4 middlewares should have executed
        Assert.True(globalMiddleware.WasCalled);
        Assert.True(pluginMiddleware.WasCalled);
        Assert.True(skillMiddleware.WasCalled);
        Assert.True(functionMiddleware.WasCalled);
    }

    [Fact]
    public async Task Pipeline_SkipsNonApplicableMiddlewares()
    {
        // Arrange
        var globalMiddleware = new TestMiddleware("Global");
        var pluginMiddleware = new TestMiddleware("Plugin");
        var skillMiddleware = new TestMiddleware("Skill");
        var functionMiddleware = new TestMiddleware("Function");

        globalMiddleware.AsGlobal();
        pluginMiddleware.ForPlugin("DatabasePlugin"); // Wrong plugin
        skillMiddleware.ForSkill("refactor_code"); // Wrong skill
        functionMiddleware.ForFunction("WriteFile"); // Wrong function

        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[]
        {
            globalMiddleware,
            pluginMiddleware,
            skillMiddleware,
            functionMiddleware
        });

        var context = CreateContext(
            "ReadFile",
            pluginName: "FileSystemPlugin",
            skillName: "analyze_codebase",
            isSkillContainer: false
        );

        // Act
        await pipeline.ExecuteBeforeSequentialFunctionAsync(context, CancellationToken.None);

        // Assert - only global middleware should execute
        Assert.True(globalMiddleware.WasCalled);
        Assert.False(pluginMiddleware.WasCalled);
        Assert.False(skillMiddleware.WasCalled);
        Assert.False(functionMiddleware.WasCalled);
    }

    #endregion

    #region Skill Container Tests

    [Fact]
    public void SkillMiddleware_AppliesToBothContainerAndReferencedFunctions()
    {
        // Arrange
        var middleware = new TestMiddleware("skill").ForSkill("analyze_codebase");

        // Act & Assert - skill container
        var containerContext = CreateContext(
            "analyze_codebase",
            pluginName: null,
            skillName: null,
            isSkillContainer: true
        );
        Assert.True(middleware.ShouldExecute(containerContext));

        // Act & Assert - function called by skill
        var funcContext = CreateContext(
            "ReadFile",
            pluginName: "FileSystemPlugin",
            skillName: "analyze_codebase"
        );
        Assert.True(middleware.ShouldExecute(funcContext));

        // Act & Assert - different skill's function
        var otherContext = CreateContext(
            "WriteFile",
            skillName: "refactor_code"
        );
        Assert.False(middleware.ShouldExecute(otherContext));
    }

    [Fact]
    public void MultipleSkills_CanHaveDifferentMiddlewares()
    {
        // Arrange
        var skill1Middleware = new TestMiddleware("skill1").ForSkill("analyze_codebase");
        var skill2Middleware = new TestMiddleware("skill2").ForSkill("refactor_code");

        // Act - function called from skill1
        var skill1Context = CreateContext(
            "ReadFile",
            skillName: "analyze_codebase"
        );

        // Act - function called from skill2
        var skill2Context = CreateContext(
            "ReadFile",
            skillName: "refactor_code"
        );

        // Assert
        Assert.True(skill1Middleware.ShouldExecute(skill1Context));
        Assert.False(skill1Middleware.ShouldExecute(skill2Context));

        Assert.True(skill2Middleware.ShouldExecute(skill2Context));
        Assert.False(skill2Middleware.ShouldExecute(skill1Context));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Middleware_WithoutScope_DefaultsToGlobal()
    {
        // Arrange - middleware without explicit scope
        var middleware = new TestMiddleware("default");

        // Act & Assert - should behave as global
        Assert.True(middleware.ShouldExecute(CreateContext("AnyFunction")));
        Assert.True(middleware.ShouldExecute(CreateContext("AnyFunction", pluginName: "AnyPlugin")));
    }

    [Fact]
    public void ForPlugin_ThrowsOnNullOrEmpty()
    {
        // Arrange
        var middleware = new TestMiddleware("test");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => middleware.ForPlugin(null!));
        Assert.Throws<ArgumentException>(() => middleware.ForPlugin(""));
        Assert.Throws<ArgumentException>(() => middleware.ForPlugin("   "));
    }

    [Fact]
    public void ForSkill_ThrowsOnNullOrEmpty()
    {
        // Arrange
        var middleware = new TestMiddleware("test");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => middleware.ForSkill(null!));
        Assert.Throws<ArgumentException>(() => middleware.ForSkill(""));
        Assert.Throws<ArgumentException>(() => middleware.ForSkill("   "));
    }

    [Fact]
    public void ForFunction_ThrowsOnNullOrEmpty()
    {
        // Arrange
        var middleware = new TestMiddleware("test");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => middleware.ForFunction(null!));
        Assert.Throws<ArgumentException>(() => middleware.ForFunction(""));
        Assert.Throws<ArgumentException>(() => middleware.ForFunction("   "));
    }

    #endregion

    //     
    // HELPER METHODS
    //     

    private static AgentMiddlewareContext CreateContext(
        string functionName,
        string? pluginName = null,
        string? skillName = null,
        bool isSkillContainer = false)
    {
        var function = AIFunctionFactory.Create(() => "test", functionName);
        var state = AgentLoopState.Initial(
            messages: Array.Empty<ChatMessage>(),
            runId: "test-run",
            conversationId: "test-conv",
            agentName: "TestAgent");

        var context = new AgentMiddlewareContext
        {
            AgentName = "TestAgent",
            ConversationId = "test-conv",
            Function = function,
            PluginName = pluginName,
            SkillName = skillName,
            IsSkillContainer = isSkillContainer,
            CancellationToken = CancellationToken.None
        };
        context.SetOriginalState(state);
        return context;
    }
}
