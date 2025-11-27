using Xunit;
using FluentAssertions;
using Microsoft.Extensions.AI;
using HPD_Agent.Scoping;
using System.Collections.Immutable;

namespace HPD_Agent.Tests.Scoping;

/// <summary>
/// Comprehensive tests for ToolVisibilityManager to validate all scoping scenarios.
/// These tests cover explicit/implicit plugin registration, [Collapse] attribute behavior,
/// orphan function hiding, and skill parent scope detection.
/// </summary>
public class ToolVisibilityManagerTests
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
        
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty, // No expanded plugins
            ImmutableHashSet<string>.Empty); // No expanded skills

        // Assert
        visibleTools.Should().HaveCount(2); // Only containers, no read_skill_document (no skills expanded)
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisPlugin"); // Scope container
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisSkills"); // Scope container

        // Should NOT contain individual plugin functions
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");

        // Should NOT contain individual skills
        visibleTools.Should().NotContain(t => t.Name == "QuickLiquidityAnalysis");

        // Should NOT contain read_skill_document (no skills with documents expanded)
        visibleTools.Should().NotContain(t => t.Name == "ReadSkillDocument");
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
        
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

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

        // Should NOT contain read_skill_document (no skills with documents expanded)
        visibleTools.Should().NotContain(t => t.Name == "ReadSkillDocument");
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
        
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisPlugin"); // Scope container
        visibleTools.Should().Contain(t => t.Name == "QuickLiquidityAnalysis"); // Individual skill
        visibleTools.Should().Contain(t => t.Name == "CapitalStructureAnalysis"); // Individual skill

        // Should NOT show plugin functions (scoped plugin not expanded)
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");

        // Should NOT contain read_skill_document (no skills expanded - only containers visible)
        visibleTools.Should().NotContain(t => t.Name == "ReadSkillDocument");

        // Total: 1 plugin container + 5 skills = 6
        visibleTools.Should().HaveCount(6);
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
        
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert - Skills visible
        visibleTools.Should().Contain(t => t.Name == "QuickLiquidityAnalysis");
        visibleTools.Should().Contain(t => t.Name == "CapitalStructureAnalysis");

        // Orphan functions should be hidden (plugin auto-registered, not explicit)
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");

        // Should NOT contain read_skill_document (no skills expanded - only skill containers visible)
        visibleTools.Should().NotContain(t => t.Name == "ReadSkillDocument");
        
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
        
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisSkills"); // Scope container

        // Individual skills hidden (parent scope not expanded)
        visibleTools.Should().NotContain(t => t.Name == "QuickLiquidityAnalysis");

        // Should NOT contain read_skill_document (no skills expanded)
        visibleTools.Should().NotContain(t => t.Name == "ReadSkillDocument");

        // Plugin functions hidden (orphans)
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");

        visibleTools.Should().HaveCount(1); // Only scope container, no read_skill_document
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
        
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act - Not expanded
        var visibleToolsBeforeExpansion = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert - Before expansion
        visibleToolsBeforeExpansion.Should().Contain(t => t.Name == "FinancialAnalysisPlugin");
        visibleToolsBeforeExpansion.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
        visibleToolsBeforeExpansion.Should().NotContain(t => t.Name == "ReadSkillDocument"); // No skills expanded
        visibleToolsBeforeExpansion.Should().HaveCount(1); // Only plugin container

        // Act - After expansion
        var visibleToolsAfterExpansion = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "FinancialAnalysisPlugin"), // Expanded
            ImmutableHashSet<string>.Empty);

        // Assert - After expansion, all functions visible
        visibleToolsAfterExpansion.Should().Contain(t => t.Name == "CalculateCurrentRatio");
        visibleToolsAfterExpansion.Should().Contain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");
        // Still no read_skill_document (no skills expanded, only plugin expanded)
        visibleToolsAfterExpansion.Should().NotContain(t => t.Name == "ReadSkillDocument");
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
        
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

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
        
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

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

    private AIFunction CreateReadSkillDocumentFunction()
    {
        return AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>("Document content"),
            new AIFunctionFactoryOptions
            {
                Name = "read_skill_document",
                Description = "Read a skill document by ID",
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["ParentPlugin"] = "DocumentRetrievalPlugin"
                }
            });
    }

    private IEnumerable<AIFunction> CreateSkillsWithDocuments(string? parentScope, bool withDocuments)
    {
        var skillNames = new[]
        {
            "QuickLiquidityAnalysis",
            "CapitalStructureAnalysis"
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

            // Add documents metadata if requested
            if (withDocuments)
            {
                props["DocumentUploads"] = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["FilePath"] = $"./Skills/SOPs/{name}-SOP.md",
                        ["DocumentId"] = $"{name.ToLower()}-sop",
                        ["Description"] = $"SOP for {name}"
                    }
                };
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

    #endregion

    #region read_skill_document Conditional Visibility Tests

    [Fact]
    public void ReadSkillDocument_NotVisible_WhenNoSkillsExpanded()
    {
        // Arrange: Skills with documents exist, but none expanded
        var tools = new List<AIFunction>();
        tools.AddRange(CreateSkillsWithDocuments(parentScope: null, withDocuments: true));
        tools.Add(CreateReadSkillDocumentFunction());
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin");

        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act: No skills expanded
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert: read_skill_document should NOT be visible
        visibleTools.Should().NotContain(t =>
            t.Name.Equals("read_skill_document", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadSkillDocument_Visible_WhenSkillWithDocumentsExpanded()
    {
        // Arrange: Skill with documents
        var tools = new List<AIFunction>();
        tools.AddRange(CreateSkillsWithDocuments(parentScope: null, withDocuments: true));
        tools.Add(CreateReadSkillDocumentFunction());
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin");

        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act: Expand skill that has documents
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "QuickLiquidityAnalysis");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ExpandedSkillContainers);

        // Assert: read_skill_document SHOULD be visible
        visibleTools.Should().Contain(t =>
            t.Name.Equals("read_skill_document", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadSkillDocument_NotVisible_WhenSkillWithoutDocumentsExpanded()
    {
        // Arrange: Skills WITHOUT documents
        var tools = new List<AIFunction>();
        tools.AddRange(CreateSkillsWithDocuments(parentScope: null, withDocuments: false));
        tools.Add(CreateReadSkillDocumentFunction());
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin");

        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act: Expand skill that has NO documents
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "QuickLiquidityAnalysis");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ExpandedSkillContainers);

        // Assert: read_skill_document should NOT be visible (skill has no documents)
        visibleTools.Should().NotContain(t =>
            t.Name.Equals("read_skill_document", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadSkillDocument_VisibleOnce_WhenMultipleSkillsWithDocumentsExpanded()
    {
        // Arrange: Multiple skills with documents
        var tools = new List<AIFunction>();
        tools.AddRange(CreateSkillsWithDocuments(parentScope: null, withDocuments: true));
        tools.Add(CreateReadSkillDocumentFunction());
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin");

        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act: Expand BOTH skills that have documents
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "QuickLiquidityAnalysis",
            "CapitalStructureAnalysis");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ExpandedSkillContainers);

        // Assert: read_skill_document appears exactly once (deduplicated)
        var readDocCount = visibleTools.Count(t =>
            t.Name.Equals("read_skill_document", StringComparison.OrdinalIgnoreCase));
        readDocCount.Should().Be(1);
    }

    [Fact]
    public void ReadSkillDocument_Visible_WhenMixedSkillsExpandedButOneHasDocuments()
    {
        // Arrange: Mix of skills with and without documents
        var tools = new List<AIFunction>();
        var skillsWithDocs = CreateSkillsWithDocuments(parentScope: null, withDocuments: true).ToList();
        var skillsWithoutDocs = CreateSkillsWithDocuments(parentScope: null, withDocuments: false)
            .Select(s =>
            {
                // Rename to avoid conflicts
                var name = s.Name + "_NoDoc";
                return AIFunctionFactory.Create(
                    (object? args, CancellationToken ct) => Task.FromResult<object?>($"{name} executed"),
                    new AIFunctionFactoryOptions
                    {
                        Name = name,
                        Description = $"{name} skill",
                        AdditionalProperties = s.AdditionalProperties
                    });
            }).ToList();

        tools.AddRange(skillsWithDocs);
        tools.AddRange(skillsWithoutDocs);
        tools.Add(CreateReadSkillDocumentFunction());
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin");

        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act: Expand one skill WITH documents and one WITHOUT
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "QuickLiquidityAnalysis", // Has documents
            "QuickLiquidityAnalysis_NoDoc"); // No documents

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ExpandedSkillContainers);

        // Assert: read_skill_document SHOULD be visible (at least one skill has documents)
        visibleTools.Should().Contain(t =>
            t.Name.Equals("read_skill_document", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Scoped Plugin/Skill Expansion Tests

    [Fact]
    public void ScopedPlugin_HidesAfterExpansion()
    {
        // Arrange: Create MathPlugin with [Scope], containing functions and skills
        var tools = CreateMathPluginTools();
        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Initially, MathPlugin container should be visible
        var initialTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert: Only container visible initially
        initialTools.Should().Contain(t => t.Name == "MathPlugin");
        initialTools.Should().NotContain(t => t.Name == "Add");
        initialTools.Should().NotContain(t => t.Name == "SolveQuadratic");

        // Act: After expansion, MathPlugin should hide and contents should show
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathPlugin");

        var expandedTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ExpandedSkillContainers,
            ImmutableHashSet<string>.Empty);

        // Assert: Container hidden, contents visible
        expandedTools.Should().NotContain(t => t.Name == "MathPlugin");
        expandedTools.Should().Contain(t => t.Name == "Add");
        expandedTools.Should().Contain(t => t.Name == "Multiply");
        expandedTools.Should().Contain(t => t.Name == "SolveQuadratic");
    }

    [Fact]
    public void ScopedPlugin_ShowsFunctionsAfterExpansion()
    {
        // Arrange
        var tools = CreateMathPluginTools();
        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Expand MathPlugin
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathPlugin");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ExpandedSkillContainers,
            ImmutableHashSet<string>.Empty);

        // Assert: All AI functions from MathPlugin should be visible
        visibleTools.Should().Contain(t => t.Name == "Add");
        visibleTools.Should().Contain(t => t.Name == "Multiply");
        visibleTools.Should().Contain(t => t.Name == "Abs");
        visibleTools.Should().Contain(t => t.Name == "Square");
        visibleTools.Should().Contain(t => t.Name == "Subtract");
        visibleTools.Should().Contain(t => t.Name == "Min");
    }

    [Fact]
    public void ScopedPlugin_ShowsSkillsAfterExpansion_ExpandedSkillContainers()
    {
        // Arrange
        var tools = CreateMathPluginTools();
        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Expand MathPlugin (goes into ExpandedSkillContainers)
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathPlugin");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ExpandedSkillContainers,
            ImmutableHashSet<string>.Empty);

        // Assert: SolveQuadratic skill should be visible when parent is in ExpandedSkillContainers
        visibleTools.Should().Contain(t => t.Name == "SolveQuadratic");
    }

    [Fact]
    public void ScopedPlugin_ShowsSkillsAfterExpansion_ExpandedSkillsParameter()
    {
        // Arrange
        var tools = CreateMathPluginTools();
        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Expand MathPlugin via expandedSkills parameter (second parameter)
        var expandedSkills = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathPlugin");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            expandedSkills);

        // Assert: SolveQuadratic skill should be visible when parent is in expandedSkills
        visibleTools.Should().Contain(t => t.Name == "SolveQuadratic");
    }

    [Fact]
    public void ScopedPlugin_OnlyHidesItself_NotOtherContainers()
    {
        // Arrange: Two separate scope containers
        var tools = new List<AIFunction>();
        tools.AddRange(CreateMathPluginTools());
        tools.Add(CreateScopeContainer("OtherPlugin", "Other plugin for testing"));

        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Expand only MathPlugin
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathPlugin");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ExpandedSkillContainers,
            ImmutableHashSet<string>.Empty);

        // Assert: MathPlugin hidden, but OtherPlugin still visible
        visibleTools.Should().NotContain(t => t.Name == "MathPlugin");
        visibleTools.Should().Contain(t => t.Name == "OtherPlugin");
    }

    [Fact]
    public void SkillContainer_VisibleWhenParentScopeExpandedInPlugins()
    {
        // Arrange: Skill with parent scope
        var tools = CreateMathPluginTools();
        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Expand parent scope in ExpandedSkillContainers
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathPlugin");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ExpandedSkillContainers,
            ImmutableHashSet<string>.Empty);

        // Assert: Skill should be visible
        var solveQuadratic = visibleTools.FirstOrDefault(t => t.Name == "SolveQuadratic");
        solveQuadratic.Should().NotBeNull();
        solveQuadratic!.AdditionalProperties?["ParentSkillContainer"].Should().Be("MathPlugin");
    }

    #endregion

    #region Helper Methods for New Tests

    private List<AIFunction> CreateMathPluginTools()
    {
        var tools = new List<AIFunction>();

        // 1. Scope container for MathPlugin
        tools.Add(CreateScopeContainer(
            "MathPlugin",
            "Math Operations. Contains 7 functions: Add, Multiply, Abs, Square, Subtract, Min, SolveQuadratic"));

        // 2. AI Functions in MathPlugin
        tools.Add(CreatePluginFunction("Add", "MathPlugin", "Adds two numbers"));
        tools.Add(CreatePluginFunction("Multiply", "MathPlugin", "Multiplies two numbers"));
        tools.Add(CreatePluginFunction("Abs", "MathPlugin", "Returns absolute value"));
        tools.Add(CreatePluginFunction("Square", "MathPlugin", "Squares a number"));
        tools.Add(CreatePluginFunction("Subtract", "MathPlugin", "Subtracts b from a"));
        tools.Add(CreatePluginFunction("Min", "MathPlugin", "Returns minimum of two numbers"));

        // 3. Skill container in MathPlugin
        tools.Add(CreateSkillContainer(
            "SolveQuadratic",
            "Solves quadratic equations",
            "MathPlugin",
            new[] { "MathPlugin.Multiply", "MathPlugin.Add", "MathPlugin.Subtract" },
            new[] { "MathPlugin" }));

        return tools;
    }

    private AIFunction CreateScopeContainer(string name, string description)
    {
        return AIFunctionFactory.Create(
            async (AIFunctionArguments args, CancellationToken ct) => name + " expanded",
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["IsContainer"] = true,
                    ["IsScope"] = true,
                    ["FunctionNames"] = new string[] { },
                    ["FunctionCount"] = 0
                }
            });
    }

    private AIFunction CreatePluginFunction(string name, string parentPlugin, string description)
    {
        return AIFunctionFactory.Create(
            async (AIFunctionArguments args, CancellationToken ct) => "Result",
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["ParentPlugin"] = parentPlugin,
                    ["IsContainer"] = false
                }
            });
    }

    private AIFunction CreateSkillContainer(
        string name,
        string description,
        string parentSkillContainer,
        string[] referencedFunctions,
        string[] referencedPlugins)
    {
        return AIFunctionFactory.Create(
            async (AIFunctionArguments args, CancellationToken ct) => name + " activated",
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["IsContainer"] = true,
                    ["IsSkill"] = true,
                    ["ParentSkillContainer"] = parentSkillContainer,
                    ["ReferencedFunctions"] = referencedFunctions,
                    ["ReferencedPlugins"] = referencedPlugins
                }
            });
    }

    private AIFunction CreateSkillWithReferences(
        string name,
        string description,
        string? parentScope,
        string[] referencedFunctions,
        string[] referencedPlugins)
    {
        var additionalProps = new Dictionary<string, object>
        {
            ["IsContainer"] = true,
            ["IsSkill"] = true,
            ["ReferencedFunctions"] = referencedFunctions,
            ["ReferencedPlugins"] = referencedPlugins
        };

        if (parentScope != null)
        {
            additionalProps["ParentSkillContainer"] = parentScope;
        }

        return AIFunctionFactory.Create(
            async (AIFunctionArguments args, CancellationToken ct) => name + " activated",
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                AdditionalProperties = additionalProps
            });
    }

    #endregion

    #region Scoped Plugin Referenced by Skill Tests

    [Fact]
    public void ScopedPluginReferencedBySkill_HidesPluginContainer_ShowsOnlySkill()
    {
        // Arrange: Scoped plugin referenced by a skill (NOT explicitly registered)
        var tools = new List<AIFunction>();

        // Add scoped plugin container
        tools.Add(CreatePluginContainer("FinancialAnalysisPlugin"));

        // Add plugin functions
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        // Add skill that references the scoped plugin
        tools.Add(CreateSkillWithReferences(
            "QuickLiquidityAnalysis",
            "Quick liquidity analysis skill",
            parentScope: null,
            referencedFunctions: new[]
            {
                "FinancialAnalysisPlugin.CalculateCurrentRatio",
                "FinancialAnalysisPlugin.CalculateQuickRatio"
            },
            referencedPlugins: new[] { "FinancialAnalysisPlugin" }));

        // Plugin is NOT explicitly registered - only implicitly via skill reference
        var explicitPlugins = ImmutableHashSet<string>.Empty;

        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act: No expansions
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert: Should show ONLY the skill container, NOT the plugin scope container
        visibleTools.Should().Contain(t => t.Name == "QuickLiquidityAnalysis");
        visibleTools.Should().NotContain(t => t.Name == "FinancialAnalysisPlugin");

        // Functions should be hidden (skill not expanded yet)
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().NotContain(t => t.Name == "CalculateQuickRatio");
    }

    [Fact]
    public void ScopedPluginReferencedBySkill_ExpandSkill_ShowsReferencedFunctions()
    {
        // Arrange: Scoped plugin referenced by a skill
        var tools = new List<AIFunction>();

        tools.Add(CreatePluginContainer("FinancialAnalysisPlugin"));
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        tools.Add(CreateSkillWithReferences(
            "QuickLiquidityAnalysis",
            "Quick liquidity analysis skill",
            parentScope: null,
            referencedFunctions: new[]
            {
                "FinancialAnalysisPlugin.CalculateCurrentRatio",
                "FinancialAnalysisPlugin.CalculateQuickRatio"
            },
            referencedPlugins: new[] { "FinancialAnalysisPlugin" }));

        var explicitPlugins = ImmutableHashSet<string>.Empty;
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act: Expand the skill (NOT the plugin)
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty, // Plugin scope NOT expanded
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "QuickLiquidityAnalysis")); // Skill expanded

        // Assert: Skill bypass should make referenced functions visible
        visibleTools.Should().Contain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().Contain(t => t.Name == "CalculateQuickRatio");

        // Skill container should be hidden (it's expanded)
        visibleTools.Should().NotContain(t => t.Name == "QuickLiquidityAnalysis");

        // Plugin scope container should still be hidden (implicitly registered)
        visibleTools.Should().NotContain(t => t.Name == "FinancialAnalysisPlugin");
    }

    [Fact]
    public void ScopedPluginReferencedBySkill_OrphanFunctions_StayHidden()
    {
        // Arrange: Scoped plugin with some functions referenced by skill, others are orphans
        var tools = new List<AIFunction>();

        tools.Add(CreatePluginContainer("FinancialAnalysisPlugin"));
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        // Skill only references 2 functions out of 6
        tools.Add(CreateSkillWithReferences(
            "QuickLiquidityAnalysis",
            "Quick liquidity analysis skill",
            parentScope: null,
            referencedFunctions: new[]
            {
                "FinancialAnalysisPlugin.CalculateCurrentRatio",
                "FinancialAnalysisPlugin.CalculateQuickRatio"
            },
            referencedPlugins: new[] { "FinancialAnalysisPlugin" }));

        var explicitPlugins = ImmutableHashSet<string>.Empty;
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act: Expand the skill
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "QuickLiquidityAnalysis"));

        // Assert: Referenced functions visible
        visibleTools.Should().Contain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().Contain(t => t.Name == "CalculateQuickRatio");

        // Orphan functions (not referenced by any skill) should remain HIDDEN
        visibleTools.Should().NotContain(t => t.Name == "CalculateWorkingCapital");
        visibleTools.Should().NotContain(t => t.Name == "CalculateDebtToEquityRatio");
        visibleTools.Should().NotContain(t => t.Name == "CalculateDebtToAssetsRatio");
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");
    }

    [Fact]
    public void ScopedPluginReferencedBySkill_ExpandPlugin_ShowsAllFunctions()
    {
        // Arrange: Scoped plugin referenced by skill
        var tools = new List<AIFunction>();

        tools.Add(CreatePluginContainer("FinancialAnalysisPlugin"));
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        tools.Add(CreateSkillWithReferences(
            "QuickLiquidityAnalysis",
            "Quick liquidity analysis skill",
            parentScope: null,
            referencedFunctions: new[]
            {
                "FinancialAnalysisPlugin.CalculateCurrentRatio",
                "FinancialAnalysisPlugin.CalculateQuickRatio"
            },
            referencedPlugins: new[] { "FinancialAnalysisPlugin" }));

        var explicitPlugins = ImmutableHashSet<string>.Empty;
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act: Expand the PLUGIN scope (not the skill)
        // This is an edge case - user manually expands the plugin even though it was implicitly registered
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "FinancialAnalysisPlugin"), // Plugin expanded
            ImmutableHashSet<string>.Empty);

        // Assert: ALL plugin functions should be visible (plugin scope expanded)
        visibleTools.Should().Contain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().Contain(t => t.Name == "CalculateQuickRatio");
        visibleTools.Should().Contain(t => t.Name == "CalculateWorkingCapital");
        visibleTools.Should().Contain(t => t.Name == "CalculateDebtToEquityRatio");
        visibleTools.Should().Contain(t => t.Name == "CalculateDebtToAssetsRatio");
        visibleTools.Should().Contain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");

        // Plugin container should be hidden (expanded)
        visibleTools.Should().NotContain(t => t.Name == "FinancialAnalysisPlugin");

        // Skill container should still be visible
        visibleTools.Should().Contain(t => t.Name == "QuickLiquidityAnalysis");
    }

    #endregion
}
