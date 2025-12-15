using Xunit;
using FluentAssertions;
using Microsoft.Extensions.AI;
using HPD.Agent.Collapsing;
using System.Collections.Immutable;

namespace HPD.Agent.Tests.Collapsing;

/// <summary>
/// Comprehensive tests for ToolVisibilityManager to validate all Collapsing scenarios.
/// These tests cover explicit/implicit plugin registration, [Collapse] attribute behavior,
/// orphan function hiding, and skill parent Collapse detection.
/// </summary>
public class ToolVisibilityManagerTests
{
    #region Test Scenario 1: Both Plugin and Skills with [Collapse], Both Explicit

    [Fact]
    public void Scenario1_BothCollapsed_BothExplicit_ShowsOnlyContainers()
    {
        // Arrange: Plugin has [Collapse], Skills have [Collapse], both explicitly registered
        var tools = CreateTestTools(
            pluginHasCollapse: true,
            skillsHaveCollapse: true,
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
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisPlugin"); // Collapse container
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisSkills"); // Collapse container

        // Should NOT contain individual plugin functions
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");

        // Should NOT contain individual skills
        visibleTools.Should().NotContain(t => t.Name == "QuickLiquidityAnalysis");

        // Should NOT contain read_skill_document (no skills with documents expanded)
        visibleTools.Should().NotContain(t => t.Name == "ReadSkillDocument");
    }

    #endregion

    #region Test Scenario 2: Plugin Explicit WITHOUT [Collapse], Skills With [Collapse]

    [Fact]
    public void Scenario2_PluginNotCollapsed_SkillsCollapsed_ShowsAllPluginFunctions()
    {
        // Arrange: Plugin NO [Collapse] but explicit, Skills have [Collapse]
        var tools = CreateTestTools(
            pluginHasCollapse: false,
            skillsHaveCollapse: true,
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

        // Assert - Should show all plugin functions (explicit, no Collapse)
        visibleTools.Should().Contain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().Contain(t => t.Name == "CalculateQuickRatio");
        visibleTools.Should().Contain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");

        // Should show skills Collapse container
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisSkills");

        // Should NOT show individual skills (parent Collapse not expanded)
        visibleTools.Should().NotContain(t => t.Name == "QuickLiquidityAnalysis");

        // Should NOT contain read_skill_document (no skills with documents expanded)
        visibleTools.Should().NotContain(t => t.Name == "ReadSkillDocument");
    }

    #endregion

    #region Test Scenario 3: Plugin With [Collapse], Skills WITHOUT [Collapse], Both Explicit

    [Fact]
    public void Scenario3_PluginCollapsed_SkillsNotCollapsed_ShowsIndividualSkills()
    {
        // Arrange: Plugin has [Collapse], Skills NO [Collapse], both explicit
        var tools = CreateTestTools(
            pluginHasCollapse: true,
            skillsHaveCollapse: false,
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
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisPlugin"); // Collapse container
        visibleTools.Should().Contain(t => t.Name == "QuickLiquidityAnalysis"); // Individual skill
        visibleTools.Should().Contain(t => t.Name == "CapitalStructureAnalysis"); // Individual skill

        // Should NOT show plugin functions (Collapsed plugin not expanded)
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");

        // Should NOT contain read_skill_document (no skills expanded - only containers visible)
        visibleTools.Should().NotContain(t => t.Name == "ReadSkillDocument");

        // Total: 1 plugin container + 5 skills = 6
        visibleTools.Should().HaveCount(6);
    }

    #endregion

    #region Test Scenario 4: Only Skills Registered (No Explicit Plugin), Skills WITHOUT [Collapse]

    [Fact]
    public void Scenario4_OnlySkillsExplicit_NoCollapse_HidesOrphanFunctions()
    {
        // Arrange: Only skills registered (plugin auto-registered), skills NO [Collapse]
        var tools = CreateTestTools(
            pluginHasCollapse: false,
            skillsHaveCollapse: false,
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

    #region Test Scenario 5: Only Skills Registered, Skills WITH [Collapse]

    [Fact]
    public void Scenario5_OnlySkillsExplicit_WithCollapse_ShowsOnlyCollapseContainer()
    {
        // Arrange: Only skills registered, skills HAVE [Collapse]
        var tools = CreateTestTools(
            pluginHasCollapse: false,
            skillsHaveCollapse: true,
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
        visibleTools.Should().Contain(t => t.Name == "FinancialAnalysisSkills"); // Collapse container

        // Individual skills hidden (parent Collapse not expanded)
        visibleTools.Should().NotContain(t => t.Name == "QuickLiquidityAnalysis");

        // Should NOT contain read_skill_document (no skills expanded)
        visibleTools.Should().NotContain(t => t.Name == "ReadSkillDocument");

        // Plugin functions hidden (orphans)
        visibleTools.Should().NotContain(t => t.Name == "ComprehensiveBalanceSheetAnalysis");
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");

        visibleTools.Should().HaveCount(1); // Only Collapse container, no read_skill_document
    }

    #endregion

    #region Test Scenario 6: Collapsed Plugin Explicit, No Skills

    [Fact]
    public void Scenario6_CollapsedPluginExplicit_NoSkills_HidesFunctions()
    {
        // Arrange: Plugin has [Collapse], explicit, no skills
        var tools = CreateTestTools(
            pluginHasCollapse: true,
            skillsHaveCollapse: false,
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
    public void ExpandSkillCollapse_ShowsIndividualSkills()
    {
        // Arrange
        var tools = CreateTestTools(
            pluginHasCollapse: true,
            skillsHaveCollapse: true,
            includePluginFunctions: true,
            includeSkills: true);
        
        var explicitPlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "FinancialAnalysisPlugin",
            "FinancialAnalysisSkills");
        
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act - Expand FinancialAnalysisSkills Collapse
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
            pluginHasCollapse: true,
            skillsHaveCollapse: false,
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
        bool pluginHasCollapse,
        bool skillsHaveCollapse,
        bool includePluginFunctions,
        bool includeSkills)
    {
        var tools = new List<AIFunction>();

        // Add plugin container if Collapsed
        if (pluginHasCollapse)
        {
            tools.Add(CreatePluginContainer("FinancialAnalysisPlugin"));
        }

        // Add skills Collapse container if Collapsed
        if (skillsHaveCollapse)
        {
            tools.Add(CreateCollapseContainer("FinancialAnalysisSkills"));
        }

        // Add plugin functions
        if (includePluginFunctions)
        {
            tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));
        }

        // Add skills
        if (includeSkills)
        {
            tools.AddRange(CreateSkills(skillsHaveCollapse ? "FinancialAnalysisSkills" : null));
        }

        // Add non-Collapsed function
        tools.Add(CreateNonCollapsedFunction("ReadSkillDocument"));

        return tools;
    }

    private AIFunction CreatePluginContainer(string toolName)
    {
        return AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>($"{toolName} expanded"),
            new AIFunctionFactoryOptions
            {
                Name = toolName,
                Description = $"{toolName} Collapse container",
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["IsContainer"] = true,
                    ["PluginName"] = toolName,
                    ["FunctionNames"] = new[] { "CalculateCurrentRatio", "CalculateQuickRatio", "CalculateWorkingCapital", "CalculateDebtToEquityRatio", "CalculateDebtToAssetsRatio", "ComprehensiveBalanceSheetAnalysis" },
                    ["FunctionCount"] = 6
                }
            });
    }

    private AIFunction CreateCollapseContainer(string CollapseName)
    {
        return AIFunctionFactory.Create(
            (object? args, CancellationToken ct) => Task.FromResult<object?>($"{CollapseName} expanded"),
            new AIFunctionFactoryOptions
            {
                Name = CollapseName,
                Description = $"{CollapseName} Collapse container",
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["IsContainer"] = true,
                    ["IsCollapse"] = true
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

    private IEnumerable<AIFunction> CreateSkills(string? parentCollapse)
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

            if (parentCollapse != null)
            {
                props["ParentSkillContainer"] = parentCollapse;
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

    private AIFunction CreateNonCollapsedFunction(string name)
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

    private IEnumerable<AIFunction> CreateSkillsWithDocuments(string? parentCollapse, bool withDocuments)
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

            if (parentCollapse != null)
            {
                props["ParentSkillContainer"] = parentCollapse;
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
        tools.AddRange(CreateSkillsWithDocuments(parentCollapse: null, withDocuments: true));
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
        tools.AddRange(CreateSkillsWithDocuments(parentCollapse: null, withDocuments: true));
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
        tools.AddRange(CreateSkillsWithDocuments(parentCollapse: null, withDocuments: false));
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
        tools.AddRange(CreateSkillsWithDocuments(parentCollapse: null, withDocuments: true));
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
        var skillsWithDocs = CreateSkillsWithDocuments(parentCollapse: null, withDocuments: true).ToList();
        var skillsWithoutDocs = CreateSkillsWithDocuments(parentCollapse: null, withDocuments: false)
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

    #region Collapsed Plugin/Skill Expansion Tests

    [Fact]
    public void CollapsedPlugin_HidesAfterExpansion()
    {
        // Arrange: Create MathTools with [Collapse], containing functions and skills
        var tools = CreateMathToolsTools();
        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Initially, MathTools container should be visible
        var initialTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty);

        // Assert: Only container visible initially
        initialTools.Should().Contain(t => t.Name == "MathTools");
        initialTools.Should().NotContain(t => t.Name == "Add");
        initialTools.Should().NotContain(t => t.Name == "SolveQuadratic");

        // Act: After expansion, MathTools should hide and contents should show
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathTools");

        var expandedTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ExpandedSkillContainers,
            ImmutableHashSet<string>.Empty);

        // Assert: Container hidden, contents visible
        expandedTools.Should().NotContain(t => t.Name == "MathTools");
        expandedTools.Should().Contain(t => t.Name == "Add");
        expandedTools.Should().Contain(t => t.Name == "Multiply");
        expandedTools.Should().Contain(t => t.Name == "SolveQuadratic");
    }

    [Fact]
    public void CollapsedPlugin_ShowsFunctionsAfterExpansion()
    {
        // Arrange
        var tools = CreateMathToolsTools();
        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Expand MathTools
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathTools");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ExpandedSkillContainers,
            ImmutableHashSet<string>.Empty);

        // Assert: All AI functions from MathTools should be visible
        visibleTools.Should().Contain(t => t.Name == "Add");
        visibleTools.Should().Contain(t => t.Name == "Multiply");
        visibleTools.Should().Contain(t => t.Name == "Abs");
        visibleTools.Should().Contain(t => t.Name == "Square");
        visibleTools.Should().Contain(t => t.Name == "Subtract");
        visibleTools.Should().Contain(t => t.Name == "Min");
    }

    [Fact]
    public void CollapsedPlugin_ShowsSkillsAfterExpansion_ExpandedSkillContainers()
    {
        // Arrange
        var tools = CreateMathToolsTools();
        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Expand MathTools (goes into ExpandedSkillContainers)
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathTools");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ExpandedSkillContainers,
            ImmutableHashSet<string>.Empty);

        // Assert: SolveQuadratic skill should be visible when parent is in ExpandedSkillContainers
        visibleTools.Should().Contain(t => t.Name == "SolveQuadratic");
    }

    [Fact]
    public void CollapsedPlugin_ShowsSkillsAfterExpansion_ExpandedSkillsParameter()
    {
        // Arrange
        var tools = CreateMathToolsTools();
        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Expand MathTools via expandedSkills parameter (second parameter)
        var expandedSkills = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathTools");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet<string>.Empty,
            expandedSkills);

        // Assert: SolveQuadratic skill should be visible when parent is in expandedSkills
        visibleTools.Should().Contain(t => t.Name == "SolveQuadratic");
    }

    [Fact]
    public void CollapsedPlugin_OnlyHidesItself_NotOtherContainers()
    {
        // Arrange: Two separate Collapse containers
        var tools = new List<AIFunction>();
        tools.AddRange(CreateMathToolsTools());
        tools.Add(CreateCollapseContainer("OtherPlugin", "Other plugin for testing"));

        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Expand only MathTools
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathTools");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ExpandedSkillContainers,
            ImmutableHashSet<string>.Empty);

        // Assert: MathTools hidden, but OtherPlugin still visible
        visibleTools.Should().NotContain(t => t.Name == "MathTools");
        visibleTools.Should().Contain(t => t.Name == "OtherPlugin");
    }

    [Fact]
    public void SkillContainer_VisibleWhenParentCollapseExpandedInPlugins()
    {
        // Arrange: Skill with parent Collapse
        var tools = CreateMathToolsTools();
        var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

        // Act: Expand parent Collapse in ExpandedSkillContainers
        var ExpandedSkillContainers = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "MathTools");

        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ExpandedSkillContainers,
            ImmutableHashSet<string>.Empty);

        // Assert: Skill should be visible
        var solveQuadratic = visibleTools.FirstOrDefault(t => t.Name == "SolveQuadratic");
        solveQuadratic.Should().NotBeNull();
        solveQuadratic!.AdditionalProperties?["ParentSkillContainer"].Should().Be("MathTools");
    }

    #endregion

    #region Helper Methods for New Tests

    private List<AIFunction> CreateMathToolsTools()
    {
        var tools = new List<AIFunction>();

        // 1. Collapse container for MathTools
        tools.Add(CreateCollapseContainer(
            "MathTools",
            "Math Operations. Contains 7 functions: Add, Multiply, Abs, Square, Subtract, Min, SolveQuadratic"));

        // 2. AI Functions in MathTools
        tools.Add(CreatePluginFunction("Add", "MathTools", "Adds two numbers"));
        tools.Add(CreatePluginFunction("Multiply", "MathTools", "Multiplies two numbers"));
        tools.Add(CreatePluginFunction("Abs", "MathTools", "Returns absolute value"));
        tools.Add(CreatePluginFunction("Square", "MathTools", "Squares a number"));
        tools.Add(CreatePluginFunction("Subtract", "MathTools", "Subtracts b from a"));
        tools.Add(CreatePluginFunction("Min", "MathTools", "Returns minimum of two numbers"));

        // 3. Skill container in MathTools
        tools.Add(CreateSkillContainer(
            "SolveQuadratic",
            "Solves quadratic equations",
            "MathTools",
            new[] { "MathTools.Multiply", "MathTools.Add", "MathTools.Subtract" },
            new[] { "MathTools" }));

        return tools;
    }

    private AIFunction CreateCollapseContainer(string name, string description)
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
                    ["IsCollapse"] = true,
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
        string? parentCollapse,
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

        if (parentCollapse != null)
        {
            additionalProps["ParentSkillContainer"] = parentCollapse;
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

    #region Collapsed Plugin Referenced by Skill Tests

    [Fact]
    public void CollapsedPluginReferencedBySkill_HidesPluginContainer_ShowsOnlySkill()
    {
        // Arrange: Collapsed plugin referenced by a skill (NOT explicitly registered)
        var tools = new List<AIFunction>();

        // Add Collapsed plugin container
        tools.Add(CreatePluginContainer("FinancialAnalysisPlugin"));

        // Add plugin functions
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        // Add skill that references the Collapsed plugin
        tools.Add(CreateSkillWithReferences(
            "QuickLiquidityAnalysis",
            "Quick liquidity analysis skill",
            parentCollapse: null,
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

        // Assert: Should show ONLY the skill container, NOT the plugin Collapse container
        visibleTools.Should().Contain(t => t.Name == "QuickLiquidityAnalysis");
        visibleTools.Should().NotContain(t => t.Name == "FinancialAnalysisPlugin");

        // Functions should be hidden (skill not expanded yet)
        visibleTools.Should().NotContain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().NotContain(t => t.Name == "CalculateQuickRatio");
    }

    [Fact]
    public void CollapsedPluginReferencedBySkill_ExpandSkill_ShowsReferencedFunctions()
    {
        // Arrange: Collapsed plugin referenced by a skill
        var tools = new List<AIFunction>();

        tools.Add(CreatePluginContainer("FinancialAnalysisPlugin"));
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        tools.Add(CreateSkillWithReferences(
            "QuickLiquidityAnalysis",
            "Quick liquidity analysis skill",
            parentCollapse: null,
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
            ImmutableHashSet<string>.Empty, // Plugin Collapse NOT expanded
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "QuickLiquidityAnalysis")); // Skill expanded

        // Assert: Skill bypass should make referenced functions visible
        visibleTools.Should().Contain(t => t.Name == "CalculateCurrentRatio");
        visibleTools.Should().Contain(t => t.Name == "CalculateQuickRatio");

        // Skill container should be hidden (it's expanded)
        visibleTools.Should().NotContain(t => t.Name == "QuickLiquidityAnalysis");

        // Plugin Collapse container should still be hidden (implicitly registered)
        visibleTools.Should().NotContain(t => t.Name == "FinancialAnalysisPlugin");
    }

    [Fact]
    public void CollapsedPluginReferencedBySkill_OrphanFunctions_StayHidden()
    {
        // Arrange: Collapsed plugin with some functions referenced by skill, others are orphans
        var tools = new List<AIFunction>();

        tools.Add(CreatePluginContainer("FinancialAnalysisPlugin"));
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        // Skill only references 2 functions out of 6
        tools.Add(CreateSkillWithReferences(
            "QuickLiquidityAnalysis",
            "Quick liquidity analysis skill",
            parentCollapse: null,
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
    public void CollapsedPluginReferencedBySkill_ExpandPlugin_ShowsAllFunctions()
    {
        // Arrange: Collapsed plugin referenced by skill
        var tools = new List<AIFunction>();

        tools.Add(CreatePluginContainer("FinancialAnalysisPlugin"));
        tools.AddRange(CreatePluginFunctions("FinancialAnalysisPlugin"));

        tools.Add(CreateSkillWithReferences(
            "QuickLiquidityAnalysis",
            "Quick liquidity analysis skill",
            parentCollapse: null,
            referencedFunctions: new[]
            {
                "FinancialAnalysisPlugin.CalculateCurrentRatio",
                "FinancialAnalysisPlugin.CalculateQuickRatio"
            },
            referencedPlugins: new[] { "FinancialAnalysisPlugin" }));

        var explicitPlugins = ImmutableHashSet<string>.Empty;
        var manager = new ToolVisibilityManager(tools, explicitPlugins);

        // Act: Expand the PLUGIN Collapse (not the skill)
        // This is an edge case - user manually expands the plugin even though it was implicitly registered
        var visibleTools = manager.GetToolsForAgentTurn(
            tools.ToList(),
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "FinancialAnalysisPlugin"), // Plugin expanded
            ImmutableHashSet<string>.Empty);

        // Assert: ALL plugin functions should be visible (plugin Collapse expanded)
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
