using Microsoft.Extensions.AI;
using System.Collections.Immutable;
using HPD_Agent.Scoping;
using Xunit;

namespace HPD_Agent.Tests.Scoping;

/// <summary>
/// Tests for skill scoping functionality in UnifiedScopingManager.
/// Ensures skills work like plugin containers - hiding referenced functions until expanded.
/// </summary>
public class SkillScopingTests
{
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
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["IsContainer"] = true,
                    ["IsSkill"] = true,
                    ["ReferencedFunctions"] = referencedFunctions
                }
            });
    }

    private static AIFunction CreateFunction(string name, string? parentPlugin = null)
    {
        var options = new AIFunctionFactoryOptions
        {
            Name = name,
            Description = $"{name} function"
        };

        if (parentPlugin != null)
        {
            options.AdditionalProperties = new Dictionary<string, object>
            {
                ["ParentPlugin"] = parentPlugin
            };
        }

        return AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("result"),
            options);
    }

    [Fact]
    public void SkillContainer_NotExpanded_HidesReferencedFunctions()
    {
        // Arrange
        var skill = CreateSkillContainer("TestSkill", "Test skill", "Function1", "Function2");
        var func1 = CreateFunction("Function1");
        var func2 = CreateFunction("Function2");
        var nonScoped = CreateFunction("AlwaysVisible");

        var allTools = new List<AIFunction> { skill, func1, func2, nonScoped };
        var manager = new UnifiedScopingManager(allTools);

        // Act
        var visible = manager.GetToolsForAgentTurn(
            allTools,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert
        Assert.Contains(visible, f => f.Name == "TestSkill"); // Skill container visible
        Assert.DoesNotContain(visible, f => f.Name == "Function1"); // Referenced function hidden
        Assert.DoesNotContain(visible, f => f.Name == "Function2"); // Referenced function hidden
        Assert.Contains(visible, f => f.Name == "AlwaysVisible"); // Non-scoped function visible
    }

    [Fact]
    public void SkillContainer_Expanded_ShowsReferencedFunctions()
    {
        // Arrange
        var skill = CreateSkillContainer("TestSkill", "Test skill", "Function1", "Function2");
        var func1 = CreateFunction("Function1");
        var func2 = CreateFunction("Function2");
        var nonScoped = CreateFunction("AlwaysVisible");

        var allTools = new List<AIFunction> { skill, func1, func2, nonScoped };
        var manager = new UnifiedScopingManager(allTools);

        // Act - expand the skill
        var visible = manager.GetToolsForAgentTurn(
            allTools,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create("TestSkill"));

        // Assert
        Assert.DoesNotContain(visible, f => f.Name == "TestSkill"); // Skill container hidden when expanded
        Assert.Contains(visible, f => f.Name == "Function1"); // Referenced function now visible
        Assert.Contains(visible, f => f.Name == "Function2"); // Referenced function now visible
        Assert.Contains(visible, f => f.Name == "AlwaysVisible"); // Non-scoped function still visible
    }

    [Fact]
    public void MultipleSkills_ReferenceSameFunction_DeduplicationWorks()
    {
        // Arrange
        var skill1 = CreateSkillContainer("Skill1", "First skill", "SharedFunction");
        var skill2 = CreateSkillContainer("Skill2", "Second skill", "SharedFunction");
        var sharedFunc = CreateFunction("SharedFunction");

        var allTools = new List<AIFunction> { skill1, skill2, sharedFunc };
        var manager = new UnifiedScopingManager(allTools);

        // Act - expand only Skill1
        var visible = manager.GetToolsForAgentTurn(
            allTools,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create("Skill1"));

        // Assert
        Assert.DoesNotContain(visible, f => f.Name == "Skill1"); // Skill1 hidden (expanded)
        Assert.Contains(visible, f => f.Name == "Skill2"); // Skill2 visible (not expanded)
        Assert.Contains(visible, f => f.Name == "SharedFunction"); // Shared function visible (Skill1 expanded)
        Assert.Single(visible.Where(f => f.Name == "SharedFunction")); // Only one instance
    }

    [Fact]
    public void MultipleSkills_BothExpanded_FunctionVisibleOnce()
    {
        // Arrange
        var skill1 = CreateSkillContainer("Skill1", "First skill", "SharedFunction");
        var skill2 = CreateSkillContainer("Skill2", "Second skill", "SharedFunction");
        var sharedFunc = CreateFunction("SharedFunction");

        var allTools = new List<AIFunction> { skill1, skill2, sharedFunc };
        var manager = new UnifiedScopingManager(allTools);

        // Act - expand both skills
        var visible = manager.GetToolsForAgentTurn(
            allTools,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create("Skill1", "Skill2"));

        // Assert
        Assert.DoesNotContain(visible, f => f.Name == "Skill1"); // Skill1 hidden (expanded)
        Assert.DoesNotContain(visible, f => f.Name == "Skill2"); // Skill2 hidden (expanded)
        Assert.Contains(visible, f => f.Name == "SharedFunction"); // Shared function visible
        Assert.Single(visible.Where(f => f.Name == "SharedFunction")); // Still only one instance
    }

    [Fact]
    public void SkillReferences_QualifiedNames_ExtractsCorrectly()
    {
        // Arrange - skill references qualified names like "PluginName.FunctionName"
        var skill = CreateSkillContainer(
            "TestSkill", 
            "Test skill", 
            "MyPlugin.Function1", 
            "MyPlugin.Function2");
        var func1 = CreateFunction("Function1", "MyPlugin");
        var func2 = CreateFunction("Function2", "MyPlugin");

        var allTools = new List<AIFunction> { skill, func1, func2 };
        var manager = new UnifiedScopingManager(allTools);

        // Act - skill not expanded
        var visibleCollapsed = manager.GetToolsForAgentTurn(
            allTools,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert - functions hidden when skill collapsed
        Assert.Contains(visibleCollapsed, f => f.Name == "TestSkill");
        Assert.DoesNotContain(visibleCollapsed, f => f.Name == "Function1");
        Assert.DoesNotContain(visibleCollapsed, f => f.Name == "Function2");

        // Act - skill expanded
        var visibleExpanded = manager.GetToolsForAgentTurn(
            allTools,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create("TestSkill"));

        // Assert - functions visible when skill expanded
        Assert.DoesNotContain(visibleExpanded, f => f.Name == "TestSkill");
        Assert.Contains(visibleExpanded, f => f.Name == "Function1");
        Assert.Contains(visibleExpanded, f => f.Name == "Function2");
    }

    [Fact]
    public void MixedSkillsAndPlugins_BothWorkCorrectly()
    {
        // Arrange
        var skill = CreateSkillContainer("TestSkill", "Test skill", "SkillFunc");
        var skillFunc = CreateFunction("SkillFunc");
        
        var pluginContainer = AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("plugin activated"),
            new AIFunctionFactoryOptions
            {
                Name = "PluginContainer",
                Description = "Plugin container",
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["IsContainer"] = true,
                    ["PluginName"] = "TestPlugin"
                }
            });
        var pluginFunc = CreateFunction("PluginFunc", "TestPlugin");

        var allTools = new List<AIFunction> { skill, skillFunc, pluginContainer, pluginFunc };
        var manager = new UnifiedScopingManager(allTools);

        // Act - nothing expanded
        var visibleInitial = manager.GetToolsForAgentTurn(
            allTools,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert - containers visible, functions hidden
        Assert.Contains(visibleInitial, f => f.Name == "TestSkill");
        Assert.Contains(visibleInitial, f => f.Name == "PluginContainer");
        Assert.DoesNotContain(visibleInitial, f => f.Name == "SkillFunc");
        Assert.DoesNotContain(visibleInitial, f => f.Name == "PluginFunc");

        // Act - expand both
        var visibleExpanded = manager.GetToolsForAgentTurn(
            allTools,
            ImmutableHashSet.Create("TestPlugin"),
            ImmutableHashSet.Create("TestSkill"));

        // Assert - containers hidden, functions visible
        Assert.DoesNotContain(visibleExpanded, f => f.Name == "TestSkill");
        Assert.DoesNotContain(visibleExpanded, f => f.Name == "PluginContainer");
        Assert.Contains(visibleExpanded, f => f.Name == "SkillFunc");
        Assert.Contains(visibleExpanded, f => f.Name == "PluginFunc");
    }
}
