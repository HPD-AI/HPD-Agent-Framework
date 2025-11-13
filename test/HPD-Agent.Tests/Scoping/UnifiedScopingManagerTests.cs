using Xunit;
using FluentAssertions;
using Microsoft.Extensions.AI;
using HPD_Agent.Scoping;
using System.Collections.Immutable;

namespace HPD_Agent.Tests.Scoping;

/// <summary>
/// Comprehensive tests for UnifiedScopingManager to validate all scoping scenarios.
/// These tests cover explicit/implicit plugin registration, [Scope] attribute behavior,
/// orphan function hiding, and skill parent scope detection.
/// </summary>
public class UnifiedScopingManagerTests
{
    #region Test Scenario 1: Both Plugin and Skills with [Scope], Both Explicit

    [Fact]
    public void Scenario1_BothScoped_BothExplicit_ShowsOnlyContainers()
    {
        // Arrange: Plugin has [Scope], Skills have [Scope], both explicitly registered
        var tools = CreateTestTools(
            pluginHasScope: true,
            skillsHaveScope: true,
            includePluginFunctions: true,
            includeSkills: true);
        
        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin",
            "FinancialAnalysisSkills");
        
        var manager = new UnifiedScopingManager(tools, explicitPlugins);

        // Act
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty, // No expanded plugins
            ImmutableHashSet<string>.Empty); // No expanded skills

        // Assert
        visibleTools.Should().HaveCount(3);
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisPlugin"); // Scope container
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisSkills"); // Scope container
        visibleTools.Should().Contain(t => t.Name == "ReadSkillDocument"); // Non-scoped
        
        // Should NOT contain individual plugin functions
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");
        
        // Should NOT contain individual skills
        visibleTools.Should().NotContain(t => t.Name == "QuickLiquidityAnalysis");
    }

    #endregion

    #region Test Scenario 2: Plugin Explicit WITHOUT [Scope], Skills With [Scope]

    [Fact]
    public void Scenario2_PluginNotScoped_SkillsScoped_ShowsAllPluginFunctions()
    {
        // Arrange: Plugin NO [Scope] but explicit, Skills have [Scope]
        var tools = CreateTestTools(
            pluginHasScope: false,
            skillsHaveScope: true,
            includePluginFunctions: true,
            includeSkills: true);
        
        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin",
            "FinancialAnalysisSkills");
        
        var manager = new UnifiedScopingManager(tools, explicitPlugins);

        // Act
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert - Should show all plugin functions (explicit, no scope)
        visibleTools.Should().Contain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().Contain(t => t.Name == "CalculateQuickRatio");
        visibleTools.Should().Contain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");
        
        // Should show skills scope container
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisSkills");
        
        // Should NOT show individual skills (parent scope not expanded)
        visibleTools.Should().NotContain(t => t.Name == "QuickLiquidityAnalysis");
        
        visibleTools.Should().Contain(t => t.Name == "ReadSkillDocument");
    }

    #endregion

    #region Test Scenario 3: Plugin With [Scope], Skills WITHOUT [Scope], Both Explicit

    [Fact]
    public void Scenario3_PluginScoped_SkillsNotScoped_ShowsIndividualSkills()
    {
        // Arrange: Plugin has [Scope], Skills NO [Scope], both explicit
        var tools = CreateTestTools(
            pluginHasScope: true,
            skillsHaveScope: false,
            includePluginFunctions: true,
            includeSkills: true);
        
        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin",
            "FinancialAnalysisSkills");
        
        var manager = new UnifiedScopingManager(tools, explicitPlugins);

        // Act
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisPlugin"); // Scope container
        visibleTools.Should().Contain(t => t.Name == "QuickLiquidityAnalysis"); // Individual skill
        visibleTools.Should().Contain(t => t.Name == "CapitalStructureAnalysis"); // Individual skill
        visibleTools.Should().Contain(t => t.Name == "ReadSkillDocument");
        
        // Should NOT show plugin functions (scoped plugin not expanded)
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");
        
        // Total: 1 plugin container + 5 skills + 1 ReadSkillDocument = 7
        visibleTools.Should().HaveCount(7);
    }

    #endregion

    #region Test Scenario 4: Only Skills Registered (No Explicit Plugin), Skills WITHOUT [Scope]

    [Fact]
    public void Scenario4_OnlySkillsExplicit_NoScope_HidesOrphanFunctions()
    {
        // Arrange: Only skills registered (plugin auto-registered), skills NO [Scope]
        var tools = CreateTestTools(
            pluginHasScope: false,
            skillsHaveScope: false,
            includePluginFunctions: true,
            includeSkills: true);
        
        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisSkills"); // Only skills explicit
        
        var manager = new UnifiedScopingManager(tools, explicitPlugins);

        // Act
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert - Skills visible
        visibleTools.Should().Contain(t => t.Name == "QuickLiquidityAnalysis");
        visibleTools.Should().Contain(t => t.Name == "CapitalStructureAnalysis");
        visibleTools.Should().Contain(t => t.Name == "ReadSkillDocument");
        
        // Orphan functions should be hidden (plugin auto-registered, not explicit)
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");
        
        // Referenced functions hidden until skill expanded
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
    }

    #endregion

    #region Test Scenario 5: Only Skills Registered, Skills WITH [Scope]

    [Fact]
    public void Scenario5_OnlySkillsExplicit_WithScope_ShowsOnlyScopeContainer()
    {
        // Arrange: Only skills registered, skills HAVE [Scope]
        var tools = CreateTestTools(
            pluginHasScope: false,
            skillsHaveScope: true,
            includePluginFunctions: true,
            includeSkills: true);
        
        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisSkills");
        
        var manager = new UnifiedScopingManager(tools, explicitPlugins);

        // Act
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisSkills"); // Scope container
        visibleTools.Should().Contain(t => t.Name == "ReadSkillDocument");
        
        // Individual skills hidden (parent scope not expanded)
        visibleTools.Should().NotContain(t => t.Name == "QuickLiquidityAnalysis");
        
        // Plugin functions hidden (orphans)
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
        
        visibleTools.Should().HaveCount(2);
    }

    #endregion

    #region Test Scenario 6: Scoped Plugin Explicit, No Skills

    [Fact]
    public void Scenario6_ScopedPluginExplicit_NoSkills_HidesFunctions()
    {
        // Arrange: Plugin has [Scope], explicit, no skills
        var tools = CreateTestTools(
            pluginHasScope: true,
            skillsHaveScope: false,
            includePluginFunctions: true,
            includeSkills: false);
        
        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin");
        
        var manager = new UnifiedScopingManager(tools, explicitPlugins);

        // Act - Not expanded
        var visibleToolsBeforeExpansion = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert - Before expansion
        visibleToolsBeforeExpansion.Should().Contain(t => t.Name == "FinancialAnalysisPlugin");
        visibleToolsBeforeExpansion.Should().Contain(t => t.Name == "ReadSkillDocument");
        visibleToolsBeforeExpansion.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
        visibleToolsBeforeExpansion.Should().HaveCount(2);

        // Act - After expansion
        var visibleToolsAfterExpansion = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "FinancialAnalysisPlugin"), // Expanded
            ImmutableHashSet<string>.Empty);

        // Assert - After expansion, all functions visible
        visibleToolsAfterExpansion.Should().Contain(t => t.Name == "CalculateCurrentRatio");
        visibleToolsAfterExpansion.Should().Contain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");
        visibleToolsAfterExpansion.Should().Contain(t => t.Name == "ReadSkillDocument");
    }

    #endregion

    #region Test Expansion Behavior

    [Fact]
    public void ExpandSkillScope_ShowsIndividualSkills()
    {
        // Arrange
        var tools = CreateTestTools(
            pluginHasScope: true,
            skillsHaveScope: true,
            includePluginFunctions: true,
            includeSkills: true);
        
        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin",
            "FinancialAnalysisSkills");
        
        var manager = new UnifiedScopingManager(tools, explicitPlugins);

        // Act - Expand FinancialAnalysisSkills scope
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "FinancialAnalysisSkills"));

        // Assert - Individual skills now visible
        visibleTools.Should().Contain(t => t.Name == "QuickLiquidityAnalysis");
        visibleTools.Should().Contain(t => t.Name == "CapitalStructureAnalysis");
        visibleTools.Should().Contain(t => t.Name == "PeriodChangeAnalysis");
        visibleTools.Should().Contain(t => t.Name == "CommonSizeBalanceSheet");
        visibleTools.Should().Contain(t => t.Name == "FinancialHealthDashboard");
    }

    [Fact]
    public void ExpandSkill_ShowsReferencedFunctions()
    {
        // Arrange
        var tools = CreateTestTools(
            pluginHasScope: true,
            skillsHaveScope: false,
            includePluginFunctions: true,
            includeSkills: true);
        
        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin",
            "FinancialAnalysisSkills");
        
        var manager = new UnifiedScopingManager(tools, explicitPlugins);

        // Act - Expand both the plugin (so functions are available) AND the skill (so it references them)
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "FinancialAnalysisPlugin"), // Expand plugin
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "QuickLiquidityAnalysis")); // Expand skill

        // Assert - Functions referenced by QuickLiquidityAnalysis now visible
        visibleTools.Should().Contain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().Contain(t => t.Name == "CalculateQuickRatio");
        visibleTools.Should().Contain(t => t.Name == "CalculateWorkingCapital");
    }

    #endregion

    #region Helper Methods

    private IEnumerable<AIFunction> CreateTestTools(
        bool pluginHasScope,
        bool skillsHaveScope,
        bool includePluginFunctions,
        bool includeSkills)
    {
        var tools = new List<AIFunction>();

        // Add plugin container if scoped
        if (pluginHasScope)
        {
            tools.Add(CreatePluginContainer("FinancialAnalysisPlugin"));
        }

        // Add skills scope container if scoped
        if (skillsHaveScope)
        {
            tools.Add(CreateScopeContainer("FinancialAnalysisSkills"));
        }

        // Add plugin functions
        if (includePluginFunctions)
        {
            tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));
        }

        // Add skills
        if (includeSkills)
        {
            tools.AddRange(CreateSkills(skillsHaveScope ? "FinancialAnalysisSkills" : null));
        }

        // Add non-scoped function
        tools.Add(CreateNonScopedFunction("ReadSkillDocument"));

        return tools;
    }

    private AIFunction CreatePluginContainer(string pluginName)
    {
        return AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>($"{pluginName} expanded"),
            new AIFunctionFactoryOptions
            {
                Name = pluginName,
                Description = $"{pluginName} scope container",
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["IsContainer"] = true,
                    ["PluginName"] = pluginName,
                    ["FunctionNames"] = new[] { "CalculateCurrentRatio", "CalculateQuickRatio", "CalculateWorkingCapital", "CalculateDebtToEquityRatio", "CalculateDebtToAssetsRatio", "ComprehensiveBalanceSheetAnalysis" },
                    ["FunctionCount"] = 6
                }
            });
    }

    private AIFunction CreateScopeContainer(string scopeName)
    {
        return AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>($"{scopeName} expanded"),
            new AIFunctionFactoryOptions
            {
                Name = scopeName,
                Description = $"{scopeName} scope container",
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["IsContainer"] = true,
                    ["IsScope"] = true
                }
            });
    }

    private IEnumerable<AIFunction> CreatePluginFunctions(string parentPlugin)
    {
        var functionNames = new[]
        {
            "CalculateCurrentRatio",
            "CalculateQuickRatio",
            "CalculateWorkingCapital",
            "CalculateDebtToEquityRatio",
            "CalculateDebtToAssetsRatio",
            "ComprehensiveBalanceSheetAnalysis"
        };

        return functionNames.Select(name => AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>($"{name} result"),
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = $"{name} function",
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["ParentPlugin"] = parentPlugin
                }
            }));
    }

    private IEnumerable<AIFunction> CreateSkills(string? parentScope)
    {
        var skillNames = new[]
        {
            "QuickLiquidityAnalysis",
            "CapitalStructureAnalysis",
            "PeriodChangeAnalysis",
            "CommonSizeBalanceSheet",
            "FinancialHealthDashboard"
        };

        return skillNames.Select(name =>
        {
            var props = new Dictionary<string, object>
            {
                ["IsContainer"] = true,
                ["IsSkill"] = true,
                ["ReferencedFunctions"] = GetReferencedFunctionsForSkill(name),
                ["ReferencedPlugins"] = new[] { "FinancialAnalysisPlugin" }
            };

            if (parentScope != null)
            {
                props["ParentSkillContainer"] = parentScope;
            }

            return AIFunctionFactory.Create(
                (object? args, CancellationToken ct) => Task.FromResult<object?>($"{name} executed"),
                new AIFunctionFactoryOptions
                {
                    Name = name,
                    Description = $"{name} skill",
                    AdditionalProperties = props
                });
        });
    }

    private string[] GetReferencedFunctionsForSkill(string skillName)
    {
        return skillName switch
        {
            "QuickLiquidityAnalysis" => new[]
            {
                "FinancialAnalysisPlugin.CalculateCurrentRatio",
                "FinancialAnalysisPlugin.CalculateQuickRatio",
                "FinancialAnalysisPlugin.CalculateWorkingCapital"
            },
            "CapitalStructureAnalysis" => new[]
            {
                "FinancialAnalysisPlugin.CalculateDebtToEquityRatio",
                "FinancialAnalysisPlugin.CalculateDebtToAssetsRatio"
            },
            _ => Array.Empty<string>()
        };
    }

    private AIFunction CreateNonScopedFunction(string name)
    {
        return AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>($"{name} result"),
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = $"{name} function"
            });
    }

    #endregion
}
