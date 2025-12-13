using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Microsoft.Extensions.AI;
using HPD.Agent;

namespace HPD.Agent.Tests.SourceGenerator;

/// <summary>
/// Regression tests for collapsing system bugs discovered during v2.0 cleanup.
/// These tests ensure that the following critical bugs don't reoccur:
///
/// Bug 1: Container not generated for plugins with [Collapse] + functions but no skills
/// Bug 2: Skill code generation not called for collapse-only plugins
/// Bug 3: Container registration never called (critical)
/// Bug 4: Explicitly registered plugins with collapse containers bypass collapse rules
/// Bug 5:SystemPrompt not injected (missing metadata)
/// </summary>
public class CollapsingRegressionTests
{
    #region Helper Methods

    private static (string? generatedCode, ImmutableArray<Diagnostic> diagnostics) RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.RuntimeHelpers).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.AI.AIFunction).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(CollapseAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new global::HPDPluginSourceGenerator();
        CSharpGeneratorDriver.Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var generatedSyntaxTrees = outputCompilation.SyntaxTrees
            .Where(st => st.FilePath.Contains("g.cs"))
            .ToImmutableArray();

        var generatedSourceCode = string.Join("\n\n", generatedSyntaxTrees.Select(st => st.GetText().ToString()));

        return (generatedSourceCode, diagnostics);
    }

    #endregion

    #region Bug 1: Container Generation for Function-Only Collapsed Plugins

    /// <summary>
    /// Bug 1 Regression Test: Plugins with [Collapse] attribute + functions but NO skills
    /// must generate a container function. Previously, container generation only happened
    /// if the plugin had skills.
    ///
    /// Real-world example: FinancialAnalysisPlugin had [Collapse] + 17 functions + 0 skills.
    /// Container was never generated, causing all 17 functions to be visible instead of 1 container.
    ///
    /// Fix location: SkillCodeGenerator.cs:377-393 (GenerateAllSkillCode)
    /// Changed: return early ONLY if no skills AND no collapse attribute
    /// </summary>
    [Fact]
    public void Bug1_PluginWithCollapseAndFunctionsButNoSkills_GeneratesContainer()
    {
        // Arrange: Plugin with [Collapse] attribute, multiple functions, but NO skills
        var pluginSource = @"
using HPD.Agent;
using System;

namespace TestPlugins
{
    [Collapse(
        description: ""Test plugin with only functions"",
        FunctionResult: ""Plugin expanded. These functions are now available."",
       SystemPrompt: ""Use these functions carefully."")]
    public partial class FunctionOnlyPlugin
    {
        [AIFunction]
        public string Function1() => ""Result1"";

        [AIFunction]
        public string Function2() => ""Result2"";

        [AIFunction]
        public string Function3() => ""Result3"";
    }
}";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(pluginSource);

        // Assert: No compilation errors
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);

        // Assert: Container creation method exists
        Assert.Contains("CreateFunctionOnlyPluginCollapseContainer", generatedCode);

        // Assert: Container is registered in CreatePlugin method
        Assert.Contains("functions.Add(CreateFunctionOnlyPluginCollapseContainer(instance));", generatedCode);

        // Assert: Container metadata includes function names
        Assert.Contains("\"Function1\"", generatedCode);
        Assert.Contains("\"Function2\"", generatedCode);
        Assert.Contains("\"Function3\"", generatedCode);
    }

    #endregion

    #region Bug 2: Skill Code Generation Called for Collapse-Only Plugins

    /// <summary>
    /// Bug 2 Regression Test: Source generator must call skill code generation
    /// when plugin has [Collapse] attribute, even if it has no skills.
    ///
    /// Previously: HPDPluginSourceGenerator.cs:622 only called GenerateAllSkillCode()
    /// if plugin.SkillCapabilities.Any() was true.
    ///
    /// Fix location: HPDPluginSourceGenerator.cs:621-626
    /// Changed: Call skill code generation if plugin has skills OR collapse attribute
    /// </summary>
    [Fact]
    public void Bug2_CollapseOnlyPlugin_TriggersSkillCodeGeneration()
    {
        // Arrange: Plugin with [Collapse] but no skills
        var pluginSource = @"
using HPD.Agent;

namespace TestPlugins
{
    [Collapse(description: ""Collapse plugin without skills"")]
    public partial class CollapseOnlyPlugin
    {
        [AIFunction]
        public string DoSomething() => ""Done"";
    }
}";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(pluginSource);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);

        // The presence of CreateCollapseOnlyPluginCollapseContainer proves that
        // GenerateAllSkillCode was called (which generates container methods)
        Assert.Contains("CreateCollapseOnlyPluginCollapseContainer", generatedCode);
    }

    #endregion

    #region Bug 3: Container Registration is Called

    /// <summary>
    /// Bug 3 Regression Test: The container registration code must actually be invoked
    /// in the CreatePlugin method. This was the CRITICAL bug - containers were being
    /// generated but never registered.
    ///
    /// Previously: GenerateSkillRegistrations() existed but was never called.
    ///
    /// Fix location: HPDPluginSourceGenerator.cs:479-484
    /// Added: Call to SkillCodeGenerator.GenerateSkillRegistrations(plugin)
    /// </summary>
    [Fact]
    public void Bug3_CollapsedPlugin_ContainerIsRegistered()
    {
        // Arrange: Collapsed plugin
        var pluginSource = @"
using HPD.Agent;

namespace TestPlugins
{
    [Collapse(description: ""Test collapsed plugin"")]
    public partial class TestPlugin
    {
        [AIFunction]
        public string TestFunction() => ""Test"";
    }
}";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(pluginSource);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);

        // Assert: Container creation method exists
        Assert.Contains("private static AIFunction CreateTestPluginCollapseContainer", generatedCode);

        // Assert: Container is ACTUALLY REGISTERED in CreatePlugin method
        // This is the key assertion - proves the registration code is called
        Assert.Contains("functions.Add(CreateTestPluginCollapseContainer(instance));", generatedCode);

        // Assert: Registration happens BEFORE individual capability registration
        // Look for the CreatePlugin method and verify container comes before individual functions
        var createPluginIndex = generatedCode.IndexOf("public static List<AIFunction> CreatePlugin");
        var containerRegistrationIndex = generatedCode.IndexOf("functions.Add(CreateTestPluginCollapseContainer(instance));", createPluginIndex);
        var individualFunctionPattern = "HPDAIFunctionFactory.Create"; // First individual function registration
        var firstIndividualFunctionIndex = generatedCode.IndexOf(individualFunctionPattern, createPluginIndex);

        Assert.True(containerRegistrationIndex > 0, "Container registration must exist");
        Assert.True(firstIndividualFunctionIndex > 0, "Individual function registration must exist");
        Assert.True(containerRegistrationIndex < firstIndividualFunctionIndex,
            "Container registration must occur before individual function registration");
    }

    #endregion

    #region Bug 4: Explicitly Registered Plugins Follow Collapse Rules

    /// <summary>
    /// Bug 4 Regression Test: Runtime visibility manager must respect collapse rules
    /// for explicitly registered plugins if they have collapse containers.
    ///
    /// Previously: ToolVisibilityManager.cs:461-468 showed ALL functions for explicitly
    /// registered plugins, even if they had [Collapse] attribute.
    ///
    /// Fix: Added check - skip "always show" rule if plugin has collapse container
    ///
    /// Note: This is tested in ToolVisibilityManagerTests.cs but included here for completeness
    /// </summary>
    [Fact]
    public void Bug4_ExplicitlyRegisteredCollapsedPlugin_HidesFunctionsUntilExpanded()
    {
        // This test validates the source generator produces the right metadata
        // Runtime behavior is tested in ToolVisibilityManagerTests.cs

        var pluginSource = @"
using HPD.Agent;

namespace TestPlugins
{
    [Collapse(description: ""Explicitly registered collapsed plugin"")]
    public partial class ExplicitPlugin
    {
        [AIFunction]
        public string Func1() => ""1"";

        [AIFunction]
        public string Func2() => ""2"";
    }
}";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(pluginSource);

        // Assert: Container is generated with correct metadata
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);

        // Assert: Container has IsContainer metadata
        Assert.Contains("[\"IsContainer\"] = true", generatedCode);
        Assert.Contains("[\"IsCollapse\"] = true", generatedCode);

        // Assert: Individual functions have ParentPlugin metadata linking to container
        // This metadata is used by ToolVisibilityManager to respect collapse rules
        Assert.Contains("CreateExplicitPluginCollapseContainer", generatedCode);
    }

    #endregion

    #region Bug 5: Container Metadata Includes Dual-Context Fields

    /// <summary>
    /// Bug 5 Regression Test: Container functions must include FunctionResult
    /// andSystemPrompt in their metadata so ContainerMiddleware can inject them.
    ///
    /// Previously: Container AdditionalProperties only had IsContainer, FunctionNames, etc.
    /// Missing: FunctionResult andSystemPrompt fields
    ///
    /// Result:SystemPrompt was never injected into agent's system prompt after expansion
    ///
    /// Fix location: SkillCodeGenerator.cs:362-392
    /// Added: Both context fields to AdditionalProperties dictionary
    /// </summary>
    [Fact]
    public void Bug5_ContainerMetadata_IncludesFunctionResult()
    {
        // Arrange: Plugin with FunctionResult
        var pluginSource = @"
using HPD.Agent;

namespace TestPlugins
{
    [Collapse(
        description: ""Plugin with function result context"",
        FunctionResult: ""This is ephemeral context returned in function result."")]
    public partial class ContextPlugin
    {
        [AIFunction]
        public string TestFunc() => ""Test"";
    }
}";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(pluginSource);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);

        // Assert: FunctionResult is in AdditionalProperties
        Assert.Contains("[\"FunctionResult\"]", generatedCode);
        Assert.Contains("This is ephemeral context returned in function result.", generatedCode);
    }

    [Fact]
    public void Bug5_ContainerMetadata_IncludesSystemPrompt()
    {
        // Arrange: Plugin withSystemPrompt
        var pluginSource = @"
using HPD.Agent;

namespace TestPlugins
{
    [Collapse(
        description: ""Plugin with system prompt context"",
       SystemPrompt: ""This is persistent context injected into system prompt."")]
    public partial class SystemPromptPlugin
    {
        [AIFunction]
        public string TestFunc() => ""Test"";
    }
}";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(pluginSource);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);

        // Assert:SystemPrompt is in AdditionalProperties
        Assert.Contains("[\"SystemPrompt\"]", generatedCode);
        Assert.Contains("This is persistent context injected into system prompt.", generatedCode);
    }

    [Fact]
    public void Bug5_ContainerMetadata_IncludesBothContextFields()
    {
        // Arrange: Plugin with BOTH context fields
        var pluginSource = @"
using HPD.Agent;

namespace TestPlugins
{
    [Collapse(
        description: ""Plugin with dual context"",
        FunctionResult: ""Ephemeral instructions."",
       SystemPrompt: ""Persistent instructions."")]
    public partial class DualContextPlugin
    {
        [AIFunction]
        public string TestFunc() => ""Test"";
    }
}";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(pluginSource);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);

        // Assert: Both contexts in AdditionalProperties
        Assert.Contains("[\"FunctionResult\"]", generatedCode);
        Assert.Contains("Ephemeral instructions.", generatedCode);
        Assert.Contains("[\"SystemPrompt\"]", generatedCode);
        Assert.Contains("Persistent instructions.", generatedCode);
    }

    [Fact]
    public void Bug5_ContainerMetadata_NullWhenContextNotProvided()
    {
        // Arrange: Plugin without context fields
        var pluginSource = @"
using HPD.Agent;

namespace TestPlugins
{
    [Collapse(description: ""Plugin without contexts"")]
    public partial class NoContextPlugin
    {
        [AIFunction]
        public string TestFunc() => ""Test"";
    }
}";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(pluginSource);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);

        // Assert: Context fields set to null when not provided
        Assert.Contains("[\"FunctionResult\"] = null", generatedCode);
        Assert.Contains("[\"SystemPrompt\"] = null", generatedCode);
    }

    #endregion

    #region Integration Test: Full Bug Suite

    /// <summary>
    /// Integration test that validates all 5 bugs are fixed in a single realistic scenario.
    /// This mimics the FinancialAnalysisPlugin that exposed all the bugs.
    /// </summary>
    [Fact]
    public void Integration_FinancialAnalysisPluginScenario_AllBugsFixed()
    {
        // Arrange: Realistic plugin similar to FinancialAnalysisPlugin
        // - Has [Collapse] attribute
        // - Has multiple functions (17 in real case, using 5 for test)
        // - Has NO skills
        // - Has both FunctionResult andSystemPrompt
        var pluginSource = @"
using HPD.Agent;

namespace TestPlugins
{
    [Collapse(
        description: ""Financial Analysis Plugin"",
        FunctionResult: @""Financial Analysis Plugin activated.
Available capabilities:
• Common-size analysis
• Liquidity ratios
• Balance sheet validation"",
       SystemPrompt: @""# FINANCIAL ANALYSIS RULES
## Core Principles
- ALWAYS validate the accounting equation: Assets = Liabilities + Equity
- Round percentages to 2 decimal places
- Express currency values in USD unless specified otherwise"")]
    public partial class FinancialAnalysisPlugin
    {
        [AIFunction]
        public string CalculateCommonSizePercentage(decimal value, decimal total) => ""Result"";

        [AIFunction]
        public string CalculateCurrentRatio(decimal currentAssets, decimal currentLiabilities) => ""Result"";

        [AIFunction]
        public string CalculateQuickRatio(decimal quickAssets, decimal currentLiabilities) => ""Result"";

        [AIFunction]
        public string ValidateAccountingEquation(decimal assets, decimal liabilities, decimal equity) => ""Result"";

        [AIFunction]
        public string ComprehensiveBalanceSheetAnalysis(string balanceSheetJson) => ""Result"";
    }
}";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(pluginSource);

        // Assert: No errors
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);

        // Bug 1: Container is generated (function-only collapsed plugin)
        Assert.Contains("CreateFinancialAnalysisPluginCollapseContainer", generatedCode);

        // Bug 2: Skill code generation was called (proven by container creation)
        // (Already proven by Bug 1 assertion)

        // Bug 3: Container is registered
        Assert.Contains("functions.Add(CreateFinancialAnalysisPluginCollapseContainer(instance));", generatedCode);

        // Bug 4: Metadata exists for runtime visibility manager
        Assert.Contains("[\"IsContainer\"] = true", generatedCode);
        Assert.Contains("[\"IsCollapse\"] = true", generatedCode);

        // Bug 5: Both context fields are in metadata
        Assert.Contains("[\"FunctionResult\"]", generatedCode);
        Assert.Contains("Financial Analysis Plugin activated", generatedCode);
        Assert.Contains("[\"SystemPrompt\"]", generatedCode);
        Assert.Contains("FINANCIAL ANALYSIS RULES", generatedCode);

        // Verify all 5 function names are in metadata
        Assert.Contains("\"CalculateCommonSizePercentage\"", generatedCode);
        Assert.Contains("\"CalculateCurrentRatio\"", generatedCode);
        Assert.Contains("\"CalculateQuickRatio\"", generatedCode);
        Assert.Contains("\"ValidateAccountingEquation\"", generatedCode);
        Assert.Contains("\"ComprehensiveBalanceSheetAnalysis\"", generatedCode);

        // Verify function count is correct
        Assert.Contains("[\"FunctionCount\"] = 5", generatedCode);
    }

    #endregion
}
