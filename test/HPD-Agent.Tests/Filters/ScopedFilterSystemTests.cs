using Xunit;
using HPD.Agent.Internal.MiddleWare;
using HPD.Agent;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Tests.Middlewares;

public class ScopedMiddlewareSystemTests
{
    private class TestMiddleware : IAIFunctionMiddleware
    {
        public string Name { get; }
        public bool WasCalled { get; private set; }

        public TestMiddleware(string name) => Name = name;

        public Task InvokeAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            WasCalled = true;
            return next(context);
        }
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

    #region ScopedMiddleware AppliesTo Tests

    [Fact]
    public void GlobalMiddleware_AppliesToAllFunctions()
    {
        // Arrange
        var Middleware = new ScopedMiddleware(new TestMiddleware("global"), MiddlewareScope.Global, null);

        // Act & Assert
        Assert.True(Middleware.AppliesTo("AnyFunction"));
        Assert.True(Middleware.AppliesTo("AnyFunction", "AnyPlugin"));
        Assert.True(Middleware.AppliesTo("AnyFunction", "AnyPlugin", "AnySkill"));
        Assert.True(Middleware.AppliesTo("AnyFunction", null, null, true));
    }

    [Fact]
    public void PluginMiddleware_AppliesToPluginFunctions()
    {
        // Arrange
        var Middleware = new ScopedMiddleware(new TestMiddleware("plugin"), MiddlewareScope.Plugin, "FileSystemPlugin");

        // Act & Assert
        Assert.True(Middleware.AppliesTo("ReadFile", "FileSystemPlugin"));
        Assert.False(Middleware.AppliesTo("ReadFile", "DatabasePlugin"));
        Assert.False(Middleware.AppliesTo("ReadFile", null));
    }

    [Fact]
    public void SkillMiddleware_AppliesToSkillContainer()
    {
        // Arrange
        var Middleware = new ScopedMiddleware(new TestMiddleware("skill"), MiddlewareScope.Skill, "analyze_codebase");

        // Act - skill container itself
        var applies = Middleware.AppliesTo(
            functionName: "analyze_codebase",
            pluginTypeName: null,
            skillName: null,
            isSkillContainer: true
        );

        // Assert
        Assert.True(applies);
    }

    [Fact]
    public void SkillMiddleware_AppliesToReferencedFunctions()
    {
        // Arrange
        var Middleware = new ScopedMiddleware(new TestMiddleware("skill"), MiddlewareScope.Skill, "analyze_codebase");

        // Act - function called by skill
        var applies = Middleware.AppliesTo(
            functionName: "ReadFile",
            pluginTypeName: "FileSystemPlugin",
            skillName: "analyze_codebase",
            isSkillContainer: false
        );

        // Assert
        Assert.True(applies);
    }

    [Fact]
    public void SkillMiddleware_DoesNotApplyToOtherSkillContainers()
    {
        // Arrange
        var Middleware = new ScopedMiddleware(new TestMiddleware("skill"), MiddlewareScope.Skill, "analyze_codebase");

        // Act - different skill container
        var applies = Middleware.AppliesTo(
            functionName: "refactor_code",
            pluginTypeName: null,
            skillName: null,
            isSkillContainer: true
        );

        // Assert
        Assert.False(applies);
    }

    [Fact]
    public void SkillMiddleware_DoesNotApplyToFunctionsFromOtherSkills()
    {
        // Arrange
        var Middleware = new ScopedMiddleware(new TestMiddleware("skill"), MiddlewareScope.Skill, "analyze_codebase");

        // Act - function called by different skill
        var applies = Middleware.AppliesTo(
            functionName: "WriteFile",
            pluginTypeName: "FileSystemPlugin",
            skillName: "refactor_code",
            isSkillContainer: false
        );

        // Assert
        Assert.False(applies);
    }

    [Fact]
    public void FunctionMiddleware_AppliesToSpecificFunction()
    {
        // Arrange
        var Middleware = new ScopedMiddleware(new TestMiddleware("function"), MiddlewareScope.Function, "ReadFile");

        // Act & Assert
        Assert.True(Middleware.AppliesTo("ReadFile"));
        Assert.False(Middleware.AppliesTo("WriteFile"));
    }

    #endregion

    #region ScopedFunctionMiddlewareManager Tests

    [Fact]
    public void ScopedFunctionMiddlewareManager_RegisterFunctionSkill_StoresMapping()
    {
        // Arrange
        var manager = new ScopedFunctionMiddlewareManager();
        var Middleware = new TestMiddleware("skill");

        manager.AddMiddleware(Middleware, MiddlewareScope.Skill, "analyze_codebase");
        manager.RegisterFunctionSkill("ReadFile", "analyze_codebase");

        // Act - No skill name provided, should use fallback
        var applicable = manager.GetApplicableMiddlewares("ReadFile").ToList();

        // Assert
        Assert.Contains(Middleware, applicable);
    }

    [Fact]
    public void ScopedFunctionMiddlewareManager_GetApplicableMiddlewares_ReturnsCorrectMiddlewares()
    {
        // Arrange
        var manager = new ScopedFunctionMiddlewareManager();
        var globalMiddleware = new TestMiddleware("Global");
        var pluginMiddleware = new TestMiddleware("Plugin");
        var skillMiddleware = new TestMiddleware("Skill");
        var functionMiddleware = new TestMiddleware("Function");

        manager.AddMiddleware(globalMiddleware, MiddlewareScope.Global, null);
        manager.AddMiddleware(pluginMiddleware, MiddlewareScope.Plugin, "FileSystemPlugin");
        manager.AddMiddleware(skillMiddleware, MiddlewareScope.Skill, "analyze_codebase");
        manager.AddMiddleware(functionMiddleware, MiddlewareScope.Function, "ReadFile");

        manager.RegisterFunctionPlugin("ReadFile", "FileSystemPlugin");
        manager.RegisterFunctionSkill("ReadFile", "analyze_codebase");

        // Act
        var Middlewares = manager.GetApplicableMiddlewares(
            "ReadFile",
            "FileSystemPlugin",
            "analyze_codebase",
            false
        ).ToList();

        // Assert - all 4 Middlewares should apply
        Assert.Equal(4, Middlewares.Count);
        Assert.Contains(globalMiddleware, Middlewares);
        Assert.Contains(pluginMiddleware, Middlewares);
        Assert.Contains(skillMiddleware, Middlewares);
        Assert.Contains(functionMiddleware, Middlewares);
    }

    [Fact]
    public void ScopedFunctionMiddlewareManager_GetApplicableMiddlewares_OrdersByScope()
    {
        // Arrange
        var manager = new ScopedFunctionMiddlewareManager();
        var globalMiddleware = new TestMiddleware("Global");
        var pluginMiddleware = new TestMiddleware("Plugin");
        var skillMiddleware = new TestMiddleware("Skill");
        var functionMiddleware = new TestMiddleware("Function");

        manager.AddMiddleware(functionMiddleware, MiddlewareScope.Function, "ReadFile");
        manager.AddMiddleware(globalMiddleware, MiddlewareScope.Global, null);
        manager.AddMiddleware(skillMiddleware, MiddlewareScope.Skill, "analyze_codebase");
        manager.AddMiddleware(pluginMiddleware, MiddlewareScope.Plugin, "FileSystemPlugin");

        manager.RegisterFunctionPlugin("ReadFile", "FileSystemPlugin");
        manager.RegisterFunctionSkill("ReadFile", "analyze_codebase");

        // Act
        var Middlewares = manager.GetApplicableMiddlewares(
            "ReadFile",
            "FileSystemPlugin",
            "analyze_codebase",
            false
        ).ToList();

        // Assert - OrderBy(scope) returns: Global(0) → Plugin(1) → Skill(2) → Function(3)
        Assert.Equal(4, Middlewares.Count);
        Assert.Same(globalMiddleware, Middlewares[0]);
        Assert.Same(pluginMiddleware, Middlewares[1]);
        Assert.Same(skillMiddleware, Middlewares[2]);
        Assert.Same(functionMiddleware, Middlewares[3]);
    }

    [Fact]
    public void ScopedFunctionMiddlewareManager_FallbackLookup_FindsPluginFromMapping()
    {
        // Arrange
        var manager = new ScopedFunctionMiddlewareManager();
        var Middleware = new TestMiddleware("plugin");

        manager.AddMiddleware(Middleware, MiddlewareScope.Plugin, "FileSystemPlugin");
        manager.RegisterFunctionPlugin("ReadFile", "FileSystemPlugin");

        // Act - No pluginTypeName provided, should use fallback
        var applicable = manager.GetApplicableMiddlewares("ReadFile").ToList();

        // Assert
        Assert.Contains(Middleware, applicable);
    }

    [Fact]
    public void ScopedFunctionMiddlewareManager_FallbackLookup_FindsSkillFromMapping()
    {
        // Arrange
        var manager = new ScopedFunctionMiddlewareManager();
        var Middleware = new TestMiddleware("skill");

        manager.AddMiddleware(Middleware, MiddlewareScope.Skill, "analyze_codebase");
        manager.RegisterFunctionSkill("ReadFile", "analyze_codebase");

        // Act - No skillName provided, should use fallback
        var applicable = manager.GetApplicableMiddlewares("ReadFile").ToList();

        // Assert
        Assert.Contains(Middleware, applicable);
    }

    [Fact]
    public void ScopedFunctionMiddlewareManager_SkillContainerInvocation_DoesNotRequireSkillName()
    {
        // Arrange
        var manager = new ScopedFunctionMiddlewareManager();
        var Middleware = new TestMiddleware("skill");

        manager.AddMiddleware(Middleware, MiddlewareScope.Skill, "analyze_codebase");

        // Act - Skill container invocation (isSkillContainer=true, skillName=null)
        var applicable = manager.GetApplicableMiddlewares(
            "analyze_codebase",
            pluginTypeName: null,
            skillName: null,
            isSkillContainer: true
        ).ToList();

        // Assert - Middleware should apply because container name matches
        Assert.Contains(Middleware, applicable);
    }

    #endregion

    #region BuilderScopeContext Tests

    [Fact]
    public void BuilderScopeContext_DefaultScope_IsGlobal()
    {
        // Arrange & Act
        var context = new BuilderScopeContext();

        // Assert
        Assert.Equal(MiddlewareScope.Global, context.CurrentScope);
        Assert.Null(context.CurrentTarget);
    }

    [Fact]
    public void BuilderScopeContext_SetSkillScope_UpdatesScope()
    {
        // Arrange
        var context = new BuilderScopeContext();

        // Act
        context.SetSkillScope("analyze_codebase");

        // Assert
        Assert.Equal(MiddlewareScope.Skill, context.CurrentScope);
        Assert.Equal("analyze_codebase", context.CurrentTarget);
    }

    [Fact]
    public void BuilderScopeContext_SetGlobalScope_ResetsScope()
    {
        // Arrange
        var context = new BuilderScopeContext();
        context.SetSkillScope("analyze_codebase");

        // Act
        context.SetGlobalScope();

        // Assert
        Assert.Equal(MiddlewareScope.Global, context.CurrentScope);
        Assert.Null(context.CurrentTarget);
    }

    [Fact]
    public void BuilderScopeContext_SetPluginScope_UpdatesScope()
    {
        // Arrange
        var context = new BuilderScopeContext();

        // Act
        context.SetPluginScope("FileSystemPlugin");

        // Assert
        Assert.Equal(MiddlewareScope.Plugin, context.CurrentScope);
        Assert.Equal("FileSystemPlugin", context.CurrentTarget);
    }

    [Fact]
    public void BuilderScopeContext_SetFunctionScope_UpdatesScope()
    {
        // Arrange
        var context = new BuilderScopeContext();

        // Act
        context.SetFunctionScope("ReadFile");

        // Assert
        Assert.Equal(MiddlewareScope.Function, context.CurrentScope);
        Assert.Equal("ReadFile", context.CurrentTarget);
    }

    #endregion

    #region Integration-style Tests

    [Fact]
    public void SkillMiddleware_AppliesToBothContainerAndReferencedFunctions()
    {
        // Arrange
        var manager = new ScopedFunctionMiddlewareManager();
        var Middleware = new TestMiddleware("skill");

        manager.AddMiddleware(Middleware, MiddlewareScope.Skill, "analyze_codebase");
        manager.RegisterFunctionSkill("ReadFile", "analyze_codebase");
        manager.RegisterFunctionSkill("ListDirectory", "analyze_codebase");

        // Act & Assert - skill container
        var containerApplicable = manager.GetApplicableMiddlewares(
            "analyze_codebase",
            null,
            null,
            isSkillContainer: true
        ).ToList();
        Assert.Contains(Middleware, containerApplicable);

        // Act & Assert - referenced function 1
        var func1Applicable = manager.GetApplicableMiddlewares("ReadFile").ToList();
        Assert.Contains(Middleware, func1Applicable);

        // Act & Assert - referenced function 2
        var func2Applicable = manager.GetApplicableMiddlewares("ListDirectory").ToList();
        Assert.Contains(Middleware, func2Applicable);

        // Act & Assert - non-referenced function
        var otherApplicable = manager.GetApplicableMiddlewares("WriteFile").ToList();
        Assert.DoesNotContain(Middleware, otherApplicable);
    }

    [Fact]
    public void MultipleSkills_CanReferenceeSameFunction()
    {
        // Arrange
        var manager = new ScopedFunctionMiddlewareManager();
        var skill1Middleware = new TestMiddleware("skill1");
        var skill2Middleware = new TestMiddleware("skill2");

        manager.AddMiddleware(skill1Middleware, MiddlewareScope.Skill, "analyze_codebase");
        manager.AddMiddleware(skill2Middleware, MiddlewareScope.Skill, "refactor_code");

        // Both skills reference ReadFile
        manager.RegisterFunctionSkill("ReadFile", "analyze_codebase");
        // Note: In real scenario, mapping is 1:1, last one wins
        // But we're testing that different skills can have different Middlewares

        // Act
        var skill1Applicable = manager.GetApplicableMiddlewares(
            "ReadFile",
            skillName: "analyze_codebase"
        ).ToList();

        var skill2Applicable = manager.GetApplicableMiddlewares(
            "ReadFile",
            skillName: "refactor_code"
        ).ToList();

        // Assert
        Assert.Contains(skill1Middleware, skill1Applicable);
        Assert.DoesNotContain(skill2Middleware, skill1Applicable);

        Assert.Contains(skill2Middleware, skill2Applicable);
        Assert.DoesNotContain(skill1Middleware, skill2Applicable);
    }

    #endregion
}
