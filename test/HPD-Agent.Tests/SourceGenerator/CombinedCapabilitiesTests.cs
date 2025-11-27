using Xunit;
using HPD.Agent;
using HPD_Agent.Tests.TestPlugins;

namespace HPD_Agent.Tests.SourceGenerator;

/// <summary>
/// Tests for plugins that combine multiple capability types:
/// - AIFunctions + Skills
/// - AIFunctions + SubAgents
/// - Skills + SubAgents
/// - AIFunctions + Skills + SubAgents (all three)
///
/// These tests verify the source generator correctly processes and generates
/// registration code for plugins with mixed capabilities.
/// </summary>
public class CombinedCapabilitiesTests
{
    // ═══════════════════════════════════════════════════════════════
    // ALL THREE: AIFunctions + Skills + SubAgents
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CombinedPlugin_WithAllThreeTypes_GeneratesRegistration()
    {
        // Arrange
        var manager = new PluginManager();

        // Act
        manager.RegisterPlugin<CombinedCapabilitiesPlugin>();
        var functions = manager.CreateAllFunctions();

        // Assert - Should have generated registration and created functions
        Assert.NotEmpty(functions);
    }

    [Fact]
    public void CombinedPlugin_AIFunctions_AreRegistered()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<CombinedCapabilitiesPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();

        // Assert - All 3 AIFunctions should be present
        Assert.Contains(functions, f => f.Name == "AnalyzeData");
        Assert.Contains(functions, f => f.Name == "TransformData");
        Assert.Contains(functions, f => f.Name == "ValidateData");
    }

    [Fact]
    public void CombinedPlugin_Skills_AreRegistered()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<CombinedCapabilitiesPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();

        // Assert - Both skills should be present as container functions
        Assert.Contains(functions, f => f.Name == "DataAnalysis");
        Assert.Contains(functions, f => f.Name == "DataTransformation");
    }

    [Fact]
    public void CombinedPlugin_SubAgents_AreRegistered()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<CombinedCapabilitiesPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();

        // Assert - Both sub-agents should be present as AIFunctions
        Assert.Contains(functions, f => f.Name == "DataExpert");
        Assert.Contains(functions, f => f.Name == "DataProcessor");
    }

    [Fact]
    public void CombinedPlugin_TotalFunctionCount_IsCorrect()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<CombinedCapabilitiesPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();

        // Assert
        // Expected: 3 AIFunctions + 2 Skills + 2 SubAgents = 7 total
        Assert.Equal(7, functions.Count);
    }

    [Fact]
    public void CombinedPlugin_SubAgents_HaveCorrectMetadata()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<CombinedCapabilitiesPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();
        var dataExpert = functions.FirstOrDefault(f => f.Name == "DataExpert");
        var dataProcessor = functions.FirstOrDefault(f => f.Name == "DataProcessor");

        // Assert
        Assert.NotNull(dataExpert);
        Assert.NotNull(dataProcessor);

        // Check IsSubAgent metadata
        Assert.True(dataExpert.AdditionalProperties?.ContainsKey("IsSubAgent") ?? false);
        Assert.True((bool)dataExpert.AdditionalProperties!["IsSubAgent"]);

        Assert.True(dataProcessor.AdditionalProperties?.ContainsKey("IsSubAgent") ?? false);
        Assert.True((bool)dataProcessor.AdditionalProperties!["IsSubAgent"]);

        // Check Category metadata
        Assert.Equal("Analysis", dataExpert.AdditionalProperties["SubAgentCategory"]);
        Assert.Equal("Processing", dataProcessor.AdditionalProperties["SubAgentCategory"]);

        // Check Priority metadata
        Assert.Equal(50, dataProcessor.AdditionalProperties["SubAgentPriority"]);
    }

    [Fact]
    public void CombinedPlugin_Skills_HaveCorrectMetadata()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<CombinedCapabilitiesPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();
        var dataAnalysis = functions.FirstOrDefault(f => f.Name == "DataAnalysis");
        var dataTransform = functions.FirstOrDefault(f => f.Name == "DataTransformation");

        // Assert
        Assert.NotNull(dataAnalysis);
        Assert.NotNull(dataTransform);

        // Check IsSkill and IsContainer metadata
        Assert.True(dataAnalysis.AdditionalProperties?.ContainsKey("IsSkill") ?? false);
        Assert.True((bool)dataAnalysis.AdditionalProperties!["IsSkill"]);
        Assert.True((bool)dataAnalysis.AdditionalProperties["IsContainer"]);

        // Check referenced functions
        var referencedFunctions = dataAnalysis.AdditionalProperties["ReferencedFunctions"] as string[];
        Assert.NotNull(referencedFunctions);
        Assert.Contains("CombinedCapabilitiesPlugin.AnalyzeData", referencedFunctions);
        Assert.Contains("CombinedCapabilitiesPlugin.ValidateData", referencedFunctions);
    }

    // ═══════════════════════════════════════════════════════════════
    // AIFunctions + SubAgents (no Skills)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FunctionsAndSubAgents_BothTypesRegistered()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<FunctionsAndSubAgentsPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();

        // Assert - 2 AIFunctions + 1 SubAgent = 3 total
        Assert.Equal(3, functions.Count);

        // AIFunctions
        Assert.Contains(functions, f => f.Name == "Search");
        Assert.Contains(functions, f => f.Name == "Filter");

        // SubAgent
        Assert.Contains(functions, f => f.Name == "SearchExpert");
    }

    [Fact]
    public void FunctionsAndSubAgents_SubAgent_HasCorrectMetadata()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<FunctionsAndSubAgentsPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();
        var searchExpert = functions.FirstOrDefault(f => f.Name == "SearchExpert");

        // Assert
        Assert.NotNull(searchExpert);
        Assert.True((bool)searchExpert.AdditionalProperties!["IsSubAgent"]);
        Assert.Equal("Search", searchExpert.AdditionalProperties["SubAgentCategory"]);
        Assert.Equal("FunctionsAndSubAgentsPlugin", searchExpert.AdditionalProperties["PluginName"]);
    }

    [Fact]
    public void FunctionsAndSubAgents_AIFunctions_DoNotHaveSubAgentMetadata()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<FunctionsAndSubAgentsPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();
        var search = functions.FirstOrDefault(f => f.Name == "Search");
        var filter = functions.FirstOrDefault(f => f.Name == "Filter");

        // Assert - AIFunctions should not have SubAgent metadata
        Assert.NotNull(search);
        Assert.NotNull(filter);
        Assert.False(search.AdditionalProperties?.ContainsKey("IsSubAgent") ?? false);
        Assert.False(filter.AdditionalProperties?.ContainsKey("IsSubAgent") ?? false);
    }

    // ═══════════════════════════════════════════════════════════════
    // Skills + SubAgents (no direct AIFunctions in plugin)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SkillsAndSubAgents_BothTypesRegistered()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<SkillsAndSubAgentsPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();

        // Assert - 1 Skill + 1 SubAgent = 2 total
        Assert.Equal(2, functions.Count);

        // Skill
        Assert.Contains(functions, f => f.Name == "FileOps");

        // SubAgent
        Assert.Contains(functions, f => f.Name == "FileAssistant");
    }

    [Fact]
    public void SkillsAndSubAgents_Skill_ReferencesExternalPlugin()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<SkillsAndSubAgentsPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();
        var fileOps = functions.FirstOrDefault(f => f.Name == "FileOps");

        // Assert
        Assert.NotNull(fileOps);
        Assert.True((bool)fileOps.AdditionalProperties!["IsSkill"]);

        var referencedFunctions = fileOps.AdditionalProperties["ReferencedFunctions"] as string[];
        Assert.NotNull(referencedFunctions);
        Assert.Contains("MockFileSystemPlugin.ReadFile", referencedFunctions);
        Assert.Contains("MockFileSystemPlugin.WriteFile", referencedFunctions);
    }

    [Fact]
    public void SkillsAndSubAgents_SubAgent_HasCorrectMetadata()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<SkillsAndSubAgentsPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();
        var fileAssistant = functions.FirstOrDefault(f => f.Name == "FileAssistant");

        // Assert
        Assert.NotNull(fileAssistant);
        Assert.True((bool)fileAssistant.AdditionalProperties!["IsSubAgent"]);
        Assert.Equal("Files", fileAssistant.AdditionalProperties["SubAgentCategory"]);
    }

    // ═══════════════════════════════════════════════════════════════
    // Multiple Mixed Plugins Together
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MultiplePlugins_WithDifferentCombinations_AllRegisterCorrectly()
    {
        // Arrange
        var manager = new PluginManager();

        // Act - Register all three plugin types
        manager.RegisterPlugin<CombinedCapabilitiesPlugin>();      // All 3 types
        manager.RegisterPlugin<FunctionsAndSubAgentsPlugin>();     // Functions + SubAgents
        manager.RegisterPlugin<SkillsAndSubAgentsPlugin>();        // Skills + SubAgents

        var functions = manager.CreateAllFunctions();

        // Assert
        // CombinedCapabilitiesPlugin: 3 functions + 2 skills + 2 subagents = 7
        // FunctionsAndSubAgentsPlugin: 2 functions + 1 subagent = 3
        // SkillsAndSubAgentsPlugin: 1 skill + 1 subagent = 2
        // Total = 12
        Assert.Equal(12, functions.Count);
    }

    [Fact]
    public void MultiplePlugins_SubAgents_AllHaveUniqueNames()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<CombinedCapabilitiesPlugin>();
        manager.RegisterPlugin<FunctionsAndSubAgentsPlugin>();
        manager.RegisterPlugin<SkillsAndSubAgentsPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();
        var subAgents = functions.Where(f =>
            (f.AdditionalProperties?.ContainsKey("IsSubAgent") ?? false) &&
            (bool)f.AdditionalProperties["IsSubAgent"]).ToList();

        // Assert - All 4 sub-agents should be present with unique names
        Assert.Equal(4, subAgents.Count);
        Assert.Contains(subAgents, f => f.Name == "DataExpert");
        Assert.Contains(subAgents, f => f.Name == "DataProcessor");
        Assert.Contains(subAgents, f => f.Name == "SearchExpert");
        Assert.Contains(subAgents, f => f.Name == "FileAssistant");
    }

    [Fact]
    public void MultiplePlugins_Skills_AllHaveUniqueNames()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPlugin<CombinedCapabilitiesPlugin>();
        manager.RegisterPlugin<SkillsAndSubAgentsPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();
        var skills = functions.Where(f =>
            (f.AdditionalProperties?.ContainsKey("IsSkill") ?? false) &&
            (bool)f.AdditionalProperties["IsSkill"]).ToList();

        // Assert - All 3 skills should be present
        Assert.Equal(3, skills.Count);
        Assert.Contains(skills, f => f.Name == "DataAnalysis");
        Assert.Contains(skills, f => f.Name == "DataTransformation");
        Assert.Contains(skills, f => f.Name == "FileOps");
    }

    // ═══════════════════════════════════════════════════════════════
    // SubAgentQueryArgs Uniqueness (Issue #3 Regression Test)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MultiplePluginsWithSubAgents_DoNotCauseNameConflicts()
    {
        // This test verifies the fix for Issue #3 where SubAgentQueryArgs
        // was generated with the same name for all plugins, causing conflicts.
        // Now it should be {PluginName}SubAgentQueryArgs

        // Arrange
        var manager = new PluginManager();

        // Act - Register multiple plugins with sub-agents
        // If the name conflict still exists, this would fail at compile time
        // or throw at runtime
        manager.RegisterPlugin<CombinedCapabilitiesPlugin>();
        manager.RegisterPlugin<FunctionsAndSubAgentsPlugin>();
        manager.RegisterPlugin<SkillsAndSubAgentsPlugin>();
        manager.RegisterPlugin<TestSubAgentPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();

        // Assert - All should register without conflicts
        Assert.NotEmpty(functions);

        // Count all sub-agents from all plugins
        var subAgents = functions.Where(f =>
            (f.AdditionalProperties?.ContainsKey("IsSubAgent") ?? false) &&
            (bool)f.AdditionalProperties["IsSubAgent"]).ToList();

        // CombinedCapabilitiesPlugin: 2
        // FunctionsAndSubAgentsPlugin: 1
        // SkillsAndSubAgentsPlugin: 1
        // TestSubAgentPlugin: 10 (from TestSubAgentPlugin.cs)
        Assert.True(subAgents.Count >= 4, $"Expected at least 4 sub-agents, got {subAgents.Count}");
    }
}
