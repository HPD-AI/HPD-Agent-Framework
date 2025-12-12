using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Microsoft.Extensions.AI; // For AIFunction
using HPD.Agent; // For CollapseAttribute
using System.IO; // For Path.Combine
using System; // For AppDomain.CurrentDomain.BaseDirectory


namespace HPD.Agent.Tests.SourceGenerator;

public class HPDPluginSourceGeneratorTests
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
                MetadataReference.CreateFromFile(typeof(CollapseAttribute).Assembly.Location), // CollapseAttribute is in HPD-Agent assembly
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                
                // Reference the source generator's assembly directly to get the HPDPluginSourceGenerator type
                MetadataReference.CreateFromFile("/Users/einsteinessibu/Documents/HPD-Agent/HPD-Agent.SourceGenerator/bin/Debug/netstandard2.0/HPD-Agent.SourceGenerator.dll")
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new global::HPDPluginSourceGenerator(); // HPDPluginSourceGenerator is in the global namespace
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
    public void GeneratedPlugin_WithDynamicCollapseInstructions_ContainsCorrectCode()
    {
        // Arrange
        var pluginSource = @$"
using HPD.Agent;
// Removed: using HPD.Agent.Plugins.Attributes;
using System;

namespace TestPlugins
{{ 
    public static class DynamicInstructionsProvider
    {{ 
        public static string GetInstructions()
        {{ 
            return ""Dynamic instructions for the collapsed plugin."";
        }} 
    }} 

    [Collapse(""Test collapsed plugin"", postExpansionInstructions: DynamicInstructionsProvider.GetInstructions())]
    public partial class CollapsedTestPlugin
    {{ 
        [AIFunction]
        public string HelloWorld() => ""Hello!"";
    }} 
}}
";

        // Act
        var (generatedCode, diagnostics) = RunGenerator(pluginSource);

        // Assert
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(generatedCode);
        Assert.Contains("var dynamicInstructions = DynamicInstructionsProvider.GetInstructions();", generatedCode);
        Assert.Contains("return $\"Test collapsed plugin expanded. Available functions: HelloWorld\\n\\n{dynamicInstructions}\";", generatedCode);
    }
    
        [Fact]
        public void GeneratedPlugin_WithStaticCollapseInstructions_ContainsCorrectCode()
        {
            // Arrange
            var pluginSource = @$"
            using HPD.Agent;
            // Removed: using HPD.Agent.Plugins.Attributes;
            using System;
            
            namespace TestPlugins
            {{ 
                [Collapse(""Test static collapsed plugin"", postExpansionInstructions: ""Static instructions here."")]
                public partial class StaticCollapsedTestPlugin
                {{ 
                    [AIFunction]
                    public string HelloStatic() => ""Hello Static!"";
                }} 
            }}
            ";    
            // Act
            var (generatedCode, diagnostics) = RunGenerator(pluginSource);
    
            // Assert
            Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.NotNull(generatedCode);
            Assert.Contains(@"return @""Test static collapsed plugin expanded. Available functions: HelloStatic\n\nStatic instructions here."";", generatedCode);
        }}
