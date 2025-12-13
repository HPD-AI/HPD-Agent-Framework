using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace HPD.Agent.Tests.SourceGenerator;

/// <summary>
/// Tests for instance method and property support in dual-context instruction injection.
/// Verifies that the source generator correctly detects static vs instance members
/// and generates appropriate code (instance.Method() vs StaticClass.Method()).
/// </summary>
public class InstanceMethodContextTests
{
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
                MetadataReference.CreateFromFile("/Users/einsteinessibu/Documents/HPD-Agent/HPD-Agent.SourceGenerator/bin/Debug/netstandard2.0/HPD-Agent.SourceGenerator.dll")
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new global::HPDPluginSourceGenerator();
        var driver = CSharpGeneratorDriver.Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var generatedSyntaxTrees = outputCompilation.SyntaxTrees
            .Where(st => st.FilePath.Contains("g.cs"))
            .ToImmutableArray();

        var generatedSourceCode = string.Join("\n\n", generatedSyntaxTrees.Select(st => st.GetText().ToString()));

        return (generatedSourceCode, diagnostics);
    }

    [Fact]
    public void Generator_SupportsInstanceMethod_InFunctionResult()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;

[Collapse(
    description: ""Dynamic Plugin"",
    FunctionResult: GetActivationMessage()
)]
public class DynamicPlugin
{
    private int _version = 1;

    public string GetActivationMessage()
    {
        return $""Plugin v{_version} activated"";
    }

    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // Should generate instance.GetActivationMessage() since it's an instance method
        Assert.Contains("instance.GetActivationMessage()", generatedCode!);
        Assert.DoesNotContain("DynamicPlugin.GetActivationMessage()", generatedCode);
    }

    [Fact]
    public void Generator_SupportsStaticMethod_InFunctionResult()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;

[Collapse(
    description: ""Static Plugin"",
    FunctionResult: GetStaticMessage()
)]
public class StaticPlugin
{
    public static string GetStaticMessage()
    {
        return ""Static activation message"";
    }

    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // Should NOT prepend instance. for static methods
        Assert.Contains("GetStaticMessage()", generatedCode!);
        Assert.DoesNotContain("instance.GetStaticMessage()", generatedCode);
    }

    [Fact]
    public void Generator_SupportsInstanceProperty_InSystemPrompt()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;

[Collapse(
    description: ""Property Plugin"",
   SystemPrompt: Rules
)]
public class PropertyPlugin
{
    public string Rules => ""Instance-specific rules"";

    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // Should generate instance.Rules since it's an instance property
        Assert.Contains("instance.Rules", generatedCode!);
    }

    [Fact]
    public void Generator_SupportsStaticProperty_InSystemPrompt()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;

[Collapse(
    description: ""Static Property Plugin"",
   SystemPrompt: StaticRules
)]
public class StaticPropertyPlugin
{
    public static string StaticRules => ""Static rules for all instances"";

    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // Should NOT prepend instance. for static properties
        Assert.Contains("StaticRules", generatedCode!);
        Assert.DoesNotContain("instance.StaticRules", generatedCode);
    }

    [Fact]
    public void Generator_SupportsMixedStaticAndInstance_InBothContexts()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;

[Collapse(
    description: ""Mixed Plugin"",
    FunctionResult: GetInstanceMessage(),
   SystemPrompt: StaticRules
)]
public class MixedPlugin
{
    private string _name = ""MixedPlugin"";

    public string GetInstanceMessage()
    {
        return $""{_name} activated"";
    }

    public static string StaticRules => ""Global rules"";

    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // FunctionResult should use instance method
        Assert.Contains("instance.GetInstanceMessage()", generatedCode!);

        //SystemPrompt should use static property
        Assert.Contains("[\"SystemPrompt\"] = StaticRules", generatedCode);
        Assert.DoesNotContain("instance.StaticRules", generatedCode);
    }

    [Fact]
    public void Generator_SupportsExternalStaticClass_InFunctionResult()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;

public static class MessageBuilder
{
    public static string BuildMessage() => ""External static message"";
}

[Collapse(
    description: ""External Plugin"",
    FunctionResult: MessageBuilder.BuildMessage()
)]
public class ExternalPlugin
{
    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // Should keep MessageBuilder.BuildMessage() as-is (external static class)
        Assert.Contains("MessageBuilder.BuildMessage()", generatedCode!);
        Assert.DoesNotContain("instance.MessageBuilder", generatedCode);
    }

    [Fact]
    public void Generator_SupportsComplexInstanceMethod_WithMultipleParameters()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;

[Collapse(
    description: ""Complex Plugin"",
    FunctionResult: BuildActivationMessage()
)]
public class ComplexPlugin
{
    private readonly string _environment;
    private readonly int _version;

    public ComplexPlugin()
    {
        _environment = ""Production"";
        _version = 2;
    }

    public string BuildActivationMessage()
    {
        return $""Plugin v{_version} in {_environment} activated"";
    }

    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // Should generate instance.BuildActivationMessage()
        Assert.Contains("instance.BuildActivationMessage()", generatedCode!);
    }

    [Fact]
    public void Generator_SupportsInstanceMethodWithChaining()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;

[Collapse(
    description: ""Chained Plugin"",
   SystemPrompt: GetRules().Trim()
)]
public class ChainedPlugin
{
    public string GetRules()
    {
        return ""  Rules with spaces  "";
    }

    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // Should detect GetRules() as instance method and prepend instance.
        Assert.Contains("instance.GetRules().Trim()", generatedCode!);
    }

    [Fact]
    public void Generator_HandlesBackwardCompatibility_WithDeprecatedPostExpansionInstructions()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;

#pragma warning disable CS0618 // Suppress obsolete warning for test
[Collapse(
    description: ""Legacy Plugin"",
    postExpansionInstructions: GetLegacyInstructions()
)]
#pragma warning restore CS0618
public class LegacyPlugin
{
    public string GetLegacyInstructions()
    {
        return ""Legacy instructions"";
    }

    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // postExpansionInstructions maps to FunctionResult, so should use instance method
        Assert.Contains("instance.GetLegacyInstructions()", generatedCode!);
    }

    [Fact]
    public void Generator_DoesNotPrependInstance_ForLiteralStrings()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;

[Collapse(
    description: ""Literal Plugin"",
    FunctionResult: ""Literal activation message"",
   SystemPrompt: ""Literal rules""
)]
public class LiteralPlugin
{
    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // Should NOT have any instance. prefix for literal strings
        Assert.DoesNotContain("instance.\"", generatedCode!);

        // Should have verbatim string literals
        Assert.Contains("@\"Literal activation message\"", generatedCode);
        Assert.Contains("@\"Literal rules\"", generatedCode);
    }

    [Fact]
    public void Generator_SupportsInstanceMethod_ReturningDynamicContent()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;
using System;

[Collapse(
    description: ""Time-based Plugin"",
    FunctionResult: GetTimeBasedMessage()
)]
public class TimeBasedPlugin
{
    private DateTime _createdAt = DateTime.UtcNow;

    public string GetTimeBasedMessage()
    {
        return $""Plugin created at {_createdAt:yyyy-MM-dd HH:mm:ss} UTC"";
    }

    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // Should use instance method
        Assert.Contains("instance.GetTimeBasedMessage()", generatedCode!);
    }

    [Fact]
    public void Generator_GeneratesCorrectMetadata_ForInstanceContexts()
    {
        // Arrange
        var source = @"
using Microsoft.Extensions.AI;
using HPD.Agent;

[Collapse(
    description: ""Metadata Plugin"",
   SystemPrompt: GetDynamicRules()
)]
public class MetadataPlugin
{
    public string GetDynamicRules() => ""Dynamic rules"";

    [AIFunction]
    public string TestFunction() => ""result"";
}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(source);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // Should storeSystemPrompt in AdditionalProperties with instance. prefix
        Assert.Contains("[\"SystemPrompt\"] = instance.GetDynamicRules()", generatedCode!);
    }
}
