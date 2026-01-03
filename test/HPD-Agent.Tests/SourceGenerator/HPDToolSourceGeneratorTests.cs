using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Microsoft.Extensions.AI; // For AIFunction
using HPD.Agent; // For ToolkitAttribute


namespace HPD.Agent.Tests.SourceGenerator;

public class HPDToolSourceGeneratorTests
{
    private static (string? generatedCode, ImmutableArray<Diagnostic> diagnostics) RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[]
            {
                // Add your assembly references here
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.RuntimeHelpers).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.AI.AIFunction).Assembly.Location), // Corrected AIFunction reference
                MetadataReference.CreateFromFile(typeof(ToolkitAttribute).Assembly.Location), // ToolkitAttribute is in HPD-Agent assembly
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new global::HPDToolSourceGenerator(); // HPDToolSourceGenerator is in the global namespace
        CSharpGeneratorDriver.Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var generatedSyntaxTrees = outputCompilation.SyntaxTrees
            .Where(st => st.FilePath.Contains("g.cs")) // Filter for generated files
            .ToImmutableArray();

        // Join all generated source code into a single string for easier assertion
        var generatedSourceCode = string.Join("\n\n", generatedSyntaxTrees.Select(st => st.GetText().ToString()));

        return (generatedSourceCode, diagnostics);
    }

    [Fact]
    public void GeneratedToolkit_WithDynamicCollapseInstructions_ContainsCorrectCode()
    {
        // Arrange - Using an expression (method call) as attribute value
        // The source generator detects this as an expression rather than a literal string
        var ToolkitSource = @$"
using HPD.Agent;
using System;

namespace TestToolkits
{{
    public static class DynamicInstructionsProvider
    {{
        public static string GetInstructions()
        {{
            return ""Dynamic instructions for the collapsed Toolkit."";
        }}
    }}

    [Toolkit(""Test collapsed Toolkit"",   FunctionResult = DynamicInstructionsProvider.GetInstructions())]
    public partial class CollapsedTestToolkit
    {{
        [AIFunction]
        public string HelloWorld() => ""Hello!"";
    }}
}}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(ToolkitSource);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);
        Assert.Contains("var dynamicInstructions = DynamicInstructionsProvider.GetInstructions();", generatedCode);
        Assert.Contains("dynamicInstructions", generatedCode);
    }
    
    [Fact]
    public void GeneratedToolkit_WithStaticCollapseInstructions_ContainsCorrectCode()
    {
        // Arrange
        var ToolkitSource = @$"
using HPD.Agent;
using System;

namespace TestToolkits
{{
    [Toolkit(""Test static collapsed Toolkit"",   FunctionResult = ""Static instructions here."")]
    public partial class StaticCollapsedTestToolkit
    {{
        [AIFunction]
        public string HelloStatic() => ""Hello Static!"";
    }}
}}
";
        // Act
        var (generatedCode, diagnostics) = RunGenerator(ToolkitSource);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);
        Assert.Contains("Static instructions here.", generatedCode);
    }
}
