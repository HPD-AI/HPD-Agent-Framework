using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using HPD.Agent.SourceGenerator.Capabilities;

/// <summary>
/// Source generator for HPD-Agent AI Toolkits. Generates AOT-compatible Toolkit registration code.
/// </summary>
[Generator]
public class HPDToolSourceGenerator : IIncrementalGenerator
{
    private static readonly System.Collections.Generic.List<string> _diagnosticMessages = new();

    // Phase 4: Feature flag removed - new polymorphic generation is now the only path

    /// <summary>
    /// Initializes the incremental generator with syntax providers and output callbacks.
    /// </summary>
    /// <param name="context">The generator initialization context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Toolkit detection (classes with [AIFunction], [Skill], or [SubAgent] methods)
        var toolClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => IsToolClass(node, ct),
                transform: static (ctx, ct) => GetToolDeclaration(ctx, ct))
            .Where(static Toolkit => Toolkit is not null)
            .Collect();

        context.RegisterSourceOutput(toolClasses, GenerateToolRegistrations);

        // Middleware detection (classes with [Middleware] attribute)
        var middlewareClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => IsMiddlewareClass(node),
                transform: static (ctx, ct) => GetMiddlewareDeclaration(ctx, ct))
            .Where(static middleware => middleware is not null)
            .Collect();

        context.RegisterSourceOutput(middlewareClasses, GenerateMiddlewareRegistry);
    }
    
    private static bool IsToolClass(SyntaxNode node, CancellationToken cancellationToken = default)
    {
        if (node is not ClassDeclarationSyntax classDecl)
            return false;

        var className = classDecl.Identifier.ValueText;
        System.Diagnostics.Debug.WriteLine($"[HPDToolSourceGenerator] Checking class: {className}");

        // Skip private classes - they cannot be accessed by generated Registration classes
        // This prevents compilation errors when private test classes have [Skill] or [AIFunction] attributes
        if (classDecl.Modifiers.Any(SyntaxKind.PrivateKeyword))
        {
            System.Diagnostics.Debug.WriteLine($"[HPDToolSourceGenerator]   Class {className} is private - SKIPPED");
            return false;
        }

        var methods = classDecl.Members.OfType<MethodDeclarationSyntax>().ToList();
        System.Diagnostics.Debug.WriteLine($"[HPDToolSourceGenerator]   Class {className} has {methods.Count} methods");

        // PHASE 2: Unified detection - check for ANY capability attribute
        // This replaces the 3 separate detection branches (AIFunction, Skill, SubAgent)
        var hasCapabilityMethods = methods.Any(method =>
        {
            var attrs = method.AttributeLists
                .SelectMany(attrList => attrList.Attributes)
                .Select(attr => attr.Name.ToString());

            // A Toolkit class has methods with any of these attributes
            return attrs.Any(name =>
                name.Contains("AIFunction") ||
                name.Contains("Skill") ||
                name.Contains("SubAgent"));
        });

        if (hasCapabilityMethods)
        {
            System.Diagnostics.Debug.WriteLine($"[HPDToolSourceGenerator]   Class {className} has capability methods - SELECTED");
        }

        return hasCapabilityMethods;
    }
    
    private static ToolkitInfo? GetToolDeclaration(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        // Get class info (needed for capability analysis)
        var className = classDecl.Identifier.ValueText;
        var namespaceName = GetNamespace(classDecl);

        // PHASE 5: Unified analysis for ALL capability types (Functions, Skills, SubAgents, MultiAgents)
        // Use CapabilityAnalyzer to discover all capabilities

        var capabilityDiagnostics = new List<Microsoft.CodeAnalysis.Diagnostic>();
        var capabilities = new List<HPD.Agent.SourceGenerator.Capabilities.ICapability>();

        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            var capability = HPD.Agent.SourceGenerator.Capabilities.CapabilityAnalyzer.AnalyzeMethod(
                method, semanticModel, context, className, namespaceName, out var methodDiagnostics);

            capabilityDiagnostics.AddRange(methodDiagnostics);

            if (capability != null)
            {
                capabilities.Add(capability);
            }
        }

        // Must have at least one capability
        if (!capabilities.Any())
            return null;

        // Check for [Toolkit] attribute and validate dual-context configuration
        var (isCollapsed, containerDescription, FunctionResult, FunctionResultExpression, FunctionResultIsStatic, SystemPrompt, SystemPromptExpression, SystemPromptIsStatic, diagnostics, customName) = GetToolkitAttribute(classDecl, semanticModel);

        // Merge capability diagnostics with toolkit diagnostics
        diagnostics.AddRange(capabilityDiagnostics);

        // Diagnostics will be stored in ToolkitInfo and reported in GenerateToolRegistrations

        // Check if the class has a parameterless constructor (either explicit or implicit)
        var hasParameterlessConstructor = HasParameterlessConstructor(classDecl);

        // Check if the class is publicly accessible (for ToolkitRegistry.All inclusion)
        // A class is publicly accessible if it's public and not nested inside a non-public class
        var isPubliclyAccessible = IsClassPubliclyAccessible(classDecl);

        // Build description from capabilities
        var functionCount = capabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.FunctionCapability>().Count();
        var skillCount = capabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.SkillCapability>().Count();
        var subAgentCount = capabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.SubAgentCapability>().Count();
        var description = BuildToolkitDescription(functionCount, skillCount, subAgentCount);

        // NEW: Extract function names for selective registration
        var functionNames = capabilities
            .OfType<HPD.Agent.SourceGenerator.Capabilities.FunctionCapability>()
            .Select(f => f.FunctionName)
            .ToList();

        // NEW: Extract config constructor type (single-parameter constructor with *Config type)
        var configConstructorTypeName = GetConfigConstructorTypeName(classDecl, semanticModel);

        // NEW: Extract metadata type from capabilities (first one wins, should all be same per proposal)
        var metadataTypeName = capabilities
            .OfType<HPD.Agent.SourceGenerator.Capabilities.BaseCapability>()
            .Where(c => !string.IsNullOrEmpty(c.ContextTypeName))
            .Select(c => c.ContextTypeName)
            .FirstOrDefault();

        return new ToolkitInfo
        {
            // ClassName is always the class identifier; CustomName comes from [Toolkit(Name = "...")]
            ClassName = classDecl.Identifier.ValueText,
            CustomName = customName,
            Description = description,
            Namespace = namespaceName,

            // PHASE 5: Unified Capabilities list (all capability types)
            Capabilities = capabilities!,

            IsCollapsed = isCollapsed,
            ContainerDescription = containerDescription,
            FunctionResult = FunctionResult,
            FunctionResultExpression = FunctionResultExpression,
            FunctionResultIsStatic = FunctionResultIsStatic,
            SystemPrompt = SystemPrompt,
            SystemPromptExpression = SystemPromptExpression,
            SystemPromptIsStatic = SystemPromptIsStatic,
            HasParameterlessConstructor = hasParameterlessConstructor,

            // Diagnostics from dual-context validation
            Diagnostics = diagnostics,
            IsPubliclyAccessible = isPubliclyAccessible,

            // NEW: Config serialization fields
            FunctionNames = functionNames,
            ConfigConstructorTypeName = configConstructorTypeName,
            MetadataTypeName = metadataTypeName
        };
    }

    /// <summary>
    /// Detects if the toolkit class has a constructor that accepts a single *Config parameter.
    /// This enables config-based instantiation from JSON.
    /// </summary>
    private static string? GetConfigConstructorTypeName(ClassDeclarationSyntax classDecl, SemanticModel semanticModel)
    {
        var constructors = classDecl.Members
            .OfType<ConstructorDeclarationSyntax>()
            .ToList();

        foreach (var ctor in constructors)
        {
            // Look for single-parameter constructor where parameter type ends with "Config"
            if (ctor.ParameterList.Parameters.Count == 1)
            {
                var param = ctor.ParameterList.Parameters[0];
                var paramTypeName = param.Type?.ToString() ?? "";

                // Check if parameter type ends with "Config" (convention for config classes)
                if (paramTypeName.EndsWith("Config"))
                {
                    // Get fully qualified type name via semantic model
                    var typeInfo = semanticModel.GetTypeInfo(param.Type!);
                    if (typeInfo.Type != null)
                    {
                        return typeInfo.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    }
                    return paramTypeName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a class has a parameterless constructor (either explicit or implicit).
    /// A class has an implicit parameterless constructor if it has NO explicit constructors.
    /// A class has an explicit parameterless constructor if it declares one.
    /// </summary>
    private static bool HasParameterlessConstructor(ClassDeclarationSyntax classDecl)
    {
        var constructors = classDecl.Members
            .OfType<ConstructorDeclarationSyntax>()
            .ToList();

        // If no explicit constructors, compiler generates implicit parameterless constructor
        if (!constructors.Any())
            return true;

        // Check if any explicit constructor is parameterless
        return constructors.Any(c => c.ParameterList.Parameters.Count == 0);
    }

    /// <summary>
    /// Checks if a class is publicly accessible from outside the assembly.
    /// A class must be:
    /// 1. Declared with 'public' modifier
    /// 2. Not nested inside a non-public class
    /// Private/internal classes (e.g., test fixtures) are excluded from ToolRegistry.All
    /// but are still processed for individual Registration files.
    /// </summary>
    private static bool IsClassPubliclyAccessible(ClassDeclarationSyntax classDecl)
    {
        // Check if this class has the public modifier
        if (!classDecl.Modifiers.Any(SyntaxKind.PublicKeyword))
            return false;

        // Check if nested inside another class that's not public
        var parent = classDecl.Parent;
        while (parent != null)
        {
            if (parent is ClassDeclarationSyntax parentClass)
            {
                // If parent class is not public, this class is not publicly accessible
                if (!parentClass.Modifiers.Any(SyntaxKind.PublicKeyword))
                    return false;
            }
            parent = parent.Parent;
        }

        return true;
    }

    private static string BuildToolkitDescription(int functionCount, int skillCount, int subAgentCount)
    {
        var parts = new List<string>();
        if (functionCount > 0) parts.Add($"{functionCount} AI functions");
        if (skillCount > 0) parts.Add($"{skillCount} skills");
        if (subAgentCount > 0) parts.Add($"{subAgentCount} sub-agents");

        if (parts.Count == 0)
            return "Empty Toolkit container.";
        else if (parts.Count == 1)
            return $"Toolkit containing {parts[0]}.";
        else if (parts.Count == 2)
            return $"Toolkit containing {parts[0]} and {parts[1]}.";
        else
            return $"Toolkit containing {parts[0]}, {parts[1]}, and {parts[2]}.";
    }
    
    private static void GenerateToolRegistrations(SourceProductionContext context, ImmutableArray<ToolkitInfo?> Toolkits)
    {
        // Group Toolkits by name+namespace to handle partial classes FIRST
        // This prevents duplicate generation by merging partial classes before validation
        var ToolkitGroups = Toolkits
            .Where(p => p != null)
            .GroupBy(p => $"{p!.Namespace}.{p.Name}")
            .Select(group =>
            {
                // Merge all partial class parts into one Toolkit
                var first = group.First()!;

                // PHASE 5: Merge unified Capabilities list (all capability types)
                var allCapabilities = group.SelectMany(p => p!.Capabilities).ToList();

                // Count capabilities by type for description
                var functionCount = allCapabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.FunctionCapability>().Count();
                var skillCount = allCapabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.SkillCapability>().Count();
                var subAgentCount = allCapabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.SubAgentCapability>().Count();

                // Preserve IsCollapsed and ContainerDescription from any partial class that has it
                var isCollapsed = group.Any(p => p!.IsCollapsed);
                var containerDescription = group.FirstOrDefault(p => p!.IsCollapsed)?.ContainerDescription;

                // All partial class parts must have parameterless constructor for the Toolkit to be AOT-instantiable
                // (If any part declares a constructor with parameters, no implicit parameterless constructor is generated)
                var hasParameterlessConstructor = group.All(p => p!.HasParameterlessConstructor);

                // All partial class parts must be publicly accessible for the Toolkit to be in the registry
                var isPubliclyAccessible = group.All(p => p!.IsPubliclyAccessible);

                // Merge diagnostics from all partial class parts
                var allDiagnostics = group.SelectMany(p => p!.Diagnostics).ToList();

                // Merge function names from all partial classes
                var allFunctionNames = group.SelectMany(p => p!.FunctionNames).Distinct().ToList();

                // Use first config constructor type found (should only be defined in one partial)
                var configConstructorTypeName = group.FirstOrDefault(p => !string.IsNullOrEmpty(p!.ConfigConstructorTypeName))?.ConfigConstructorTypeName;

                // Use first metadata type found (should be consistent across all capabilities per proposal)
                var metadataTypeName = group.FirstOrDefault(p => !string.IsNullOrEmpty(p!.MetadataTypeName))?.MetadataTypeName;

                return new ToolkitInfo
                {
                    Name = first.Name,
                    Description = BuildToolkitDescription(functionCount, skillCount, subAgentCount),
                    Namespace = first.Namespace,

                    // PHASE 5: Unified Capabilities list (all capability types)
                    Capabilities = allCapabilities,
                    IsCollapsed = isCollapsed,
                    ContainerDescription = containerDescription,
                    // NEW: Dual-context properties
                    FunctionResult = group.FirstOrDefault(p => p?.FunctionResult != null)?.FunctionResult,
                    FunctionResultExpression = group.FirstOrDefault(p => p?.FunctionResultExpression != null)?.FunctionResultExpression,
                    FunctionResultIsStatic = group.FirstOrDefault(p => p?.FunctionResultExpression != null)?.FunctionResultIsStatic ?? true,
                    SystemPrompt = group.FirstOrDefault(p => p?.SystemPrompt != null)?.SystemPrompt,
                    SystemPromptExpression = group.FirstOrDefault(p => p?.SystemPromptExpression != null)?.SystemPromptExpression,
                    SystemPromptIsStatic = group.FirstOrDefault(p => p?.SystemPromptExpression != null)?.SystemPromptIsStatic ?? true,
                    HasParameterlessConstructor = hasParameterlessConstructor,
                    IsPubliclyAccessible = isPubliclyAccessible,
                    // Diagnostics from dual-context validation
                    Diagnostics = allDiagnostics,
                    // NEW: Config serialization fields
                    FunctionNames = allFunctionNames,
                    ConfigConstructorTypeName = configConstructorTypeName,
                    MetadataTypeName = metadataTypeName
                };
            })
            .ToList();

        // Report diagnostics for all Toolkits
        foreach (var Toolkit in ToolkitGroups)
        {
            foreach (var diagnostic in Toolkit.Diagnostics)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        // DIAGNOSTIC: Generate detailed diagnostic report AFTER grouping
        var reportLines = string.Join("\\n", _diagnosticMessages.Select(m => m.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "")));
        var diagnosticCode = $@"
// HPD Source Generator Diagnostic Report
// Generated at: {DateTime.Now}
// Toolkits found: {Toolkits.Length} raw, {ToolkitGroups.Count} after merging
namespace HPD.Agent.Diagnostics {{
    public static class SourceGeneratorDiagnostic {{
        public const string Message = ""Source generator executed successfully"";
        public const int ToolkitsFound = {ToolkitGroups.Count};
        public const string DetailedReport = @""{reportLines}"";
    }}
}}";
        context.AddSource("HPD.Agent.Diagnostics.SourceGeneratorDiagnostic.g.cs", diagnosticCode);

        // Clear for next compilation
        _diagnosticMessages.Clear();

        var debugInfo = new StringBuilder();
        debugInfo.AppendLine($"// Found {Toolkits.Length} Toolkit parts total");
        debugInfo.AppendLine($"// Merged into {ToolkitGroups.Count} unique Toolkits");
        foreach (var Toolkit in ToolkitGroups)
        {
            debugInfo.AppendLine($"// Toolkit: {Toolkit.Namespace}.{Toolkit.Name} with {Toolkit.FunctionCapabilities.Count()} functions, {Toolkit.SkillCapabilities.Count()} skills, and {Toolkit.SubAgentCapabilities.Count()} sub-agents");
        }
        context.AddSource("HPD.Agent.Generated.SourceGeneratorDebug.g.cs", debugInfo.ToString());

        // Resolve skill references before validation and code generation
        // PHASE 5: Use unified SkillCapabilities from Capabilities list
        var allSkillCapabilities = ToolkitGroups
            .SelectMany(p => p.SkillCapabilities)
            .ToList();
        if (allSkillCapabilities.Any())
        {
            ResolveSkillCapabilities(allSkillCapabilities);
        }

        foreach (var Toolkit in ToolkitGroups)
        {
            if (Toolkit == null) continue;

            foreach (var function in Toolkit.FunctionCapabilities)
            {
                if (function.ValidationData?.NeedsValidation == true)
                {
                    var contextTypeName = function.ContextTypeName;
                    if (!string.IsNullOrEmpty(contextTypeName))
                    {
                        var contextType = function.ValidationData.SemanticModel.Compilation.GetTypeByMetadataName(contextTypeName!);
                        if (contextType != null)
                        {
                            ValidateTemplateProperties(context, function, contextType, function.ValidationData.Method);
                            if (!string.IsNullOrEmpty(function.ConditionalExpression))
                            {
                                ValidateConditionalExpression(context, function.ConditionalExpression!, contextType, function.ValidationData.Method, $"function {function.Name}");
                            }
                            ValidateFunctionContextUsage(context, function, function.ValidationData.Method);

                            foreach (var parameter in function.Parameters.Where(p => p.IsConditional))
                            {
                                if (!string.IsNullOrEmpty(parameter.ConditionalExpression))
                                {
                                    ValidateConditionalExpression(context, parameter.ConditionalExpression!, contextType, function.ValidationData.Method, $"parameter {parameter.Name} in function {function.Name}");
                                }
                            }
                        }
                    }
                }
            }

            var source = GenerateToolkitRegistration(Toolkit);
            // Use fully qualified name as hint to prevent duplicates
            var hintName = string.IsNullOrEmpty(Toolkit.Namespace)
                ? $"{Toolkit.Name}Registration.g.cs"
                : $"{Toolkit.Namespace}.{Toolkit.Name}Registration.g.cs";
            context.AddSource(hintName, source);
        }

        // NEW: Generate Toolkit registry catalog for AOT-compatible Toolkit discovery
        if (ToolkitGroups.Any())
        {
            var registrySource = GenerateToolkitRegistry(ToolkitGroups);
            context.AddSource("HPD.Agent.Generated.ToolkitRegistry.g.cs", registrySource);
        }
    }

    /// <summary>
    /// Generates the ToolkitRegistry.All array that serves as a catalog of all Toolkits in the assembly.
    /// This eliminates reflection in hot paths by providing direct delegate references.
    /// Only Toolkits with parameterless constructors and public accessibility are included.
    /// </summary>
    private static string GenerateToolkitRegistry(List<ToolkitInfo> Toolkits)
    {
        // Filter to only include Toolkits that can be instantiated via the registry:
        // 1. Must have parameterless constructor (Toolkits requiring DI use reflection fallback)
        // 2. Must be publicly accessible (private/internal test classes are excluded)
        var instantiableToolkits = Toolkits
            .Where(p => p.HasParameterlessConstructor && p.IsPubliclyAccessible)
            .OrderBy(p => p.Name)
            .ToList();

        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#pragma warning disable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using Microsoft.Extensions.AI;");
        sb.AppendLine("using HPD.Agent;  // For ToolkitFactory and IToolMetadata types");
        sb.AppendLine();
        sb.AppendLine("namespace HPD.Agent.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// AOT-compatible catalog of all Toolkits in this assembly.");
        sb.AppendLine("    /// Generated by HPDToolSourceGenerator.");
        sb.AppendLine("    /// Provides direct delegate references eliminating reflection in hot paths.");
        sb.AppendLine($"    /// Contains {instantiableToolkits.Count} Toolkits (Toolkits requiring DI are excluded).");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    [System.CodeDom.Compiler.GeneratedCodeAttribute(\"HPDToolSourceGenerator\", \"1.0.0.0\")]");
        sb.AppendLine("    public static class ToolkitRegistry");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Catalog of all Toolkits in this assembly that have parameterless constructors.");
        sb.AppendLine("        /// AgentBuilder automatically discovers and uses this at construction time.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static readonly ToolkitFactory[] All = new ToolkitFactory[]");
        sb.AppendLine("        {");

        foreach (var Toolkit in instantiableToolkits)
        {
            var ns = string.IsNullOrEmpty(Toolkit.Namespace) ? "" : $"{Toolkit.Namespace}.";
            var fullTypeName = $"{ns}{Toolkit.ClassName}";

            sb.AppendLine($"            new ToolkitFactory(");
            sb.AppendLine($"                // ========== EXISTING FIELDS ==========");
            // Use EffectiveName for registry lookup (supports [Toolkit(Name = "...")] override)
            sb.AppendLine($"                Name: \"{Toolkit.EffectiveName}\",");
            sb.AppendLine($"                ToolkitType: typeof({fullTypeName}),");
            sb.AppendLine($"                CreateInstance: () => new {fullTypeName}(),  // Direct instantiation (AOT-safe)");

            // Handle skill-only containers (no instance parameter)
            if (!Toolkit.RequiresInstance)
            {
                sb.AppendLine($"                CreateFunctions: (_, ctx) => {Toolkit.Name}Registration.CreateToolkit(ctx),");
            }
            else
            {
                sb.AppendLine($"                CreateFunctions: (instance, ctx) => {Toolkit.Name}Registration.CreateToolkit(({fullTypeName})instance, ctx),");
            }

            // Add GetReferencedToolkits if Toolkit has skills
            if (Toolkit.SkillCapabilities.Any())
            {
                sb.AppendLine($"                GetReferencedToolkits: {Toolkit.Name}Registration.GetReferencedToolkits,");
                sb.AppendLine($"                GetReferencedFunctions: {Toolkit.Name}Registration.GetReferencedFunctions,");
            }
            else
            {
                sb.AppendLine($"                GetReferencedToolkits: () => Array.Empty<string>(),");
                sb.AppendLine($"                GetReferencedFunctions: () => new Dictionary<string, string[]>(),");
            }

            // NEW: Collapsing metadata (from [Toolkit] attribute)
            sb.AppendLine($"                // ========== COLLAPSING METADATA ==========");
            sb.AppendLine($"                HasDescription: {Toolkit.IsCollapsed.ToString().ToLower()},");
            sb.AppendLine($"                Description: {(string.IsNullOrEmpty(Toolkit.ContainerDescription) ? "null" : $"@\"{EscapeForVerbatim(Toolkit.ContainerDescription)}\"")},");
            sb.AppendLine($"                FunctionResult: {(string.IsNullOrEmpty(Toolkit.FunctionResult) ? "null" : $"@\"{EscapeForVerbatim(Toolkit.FunctionResult)}\"")},");
            sb.AppendLine($"                SystemPrompt: {(string.IsNullOrEmpty(Toolkit.SystemPrompt) ? "null" : $"@\"{EscapeForVerbatim(Toolkit.SystemPrompt)}\"")},");

            // NEW: Config-based instantiation
            sb.AppendLine($"                // ========== CONFIG INSTANTIATION ==========");
            if (!string.IsNullOrEmpty(Toolkit.ConfigConstructorTypeName))
            {
                sb.AppendLine($"                ConfigType: typeof({Toolkit.ConfigConstructorTypeName}),");
                sb.AppendLine($"                CreateFromConfig: json => new {fullTypeName}(System.Text.Json.JsonSerializer.Deserialize<{Toolkit.ConfigConstructorTypeName}>(json.GetRawText())!),");
            }
            else
            {
                sb.AppendLine($"                ConfigType: null,");
                sb.AppendLine($"                CreateFromConfig: null,");
            }

            // NEW: Metadata type
            sb.AppendLine($"                // ========== METADATA ==========");
            if (!string.IsNullOrEmpty(Toolkit.MetadataTypeName))
            {
                sb.AppendLine($"                MetadataType: typeof({Toolkit.MetadataTypeName}),");
            }
            else
            {
                sb.AppendLine($"                MetadataType: null,");
            }

            // NEW: Function names for selective registration
            var functionNamesArray = Toolkit.FunctionNames.Any()
                ? $"new string[] {{ {string.Join(", ", Toolkit.FunctionNames.Select(n => $"\"{n}\""))} }}"
                : "Array.Empty<string>()";
            sb.AppendLine($"                FunctionNames: {functionNamesArray}");

            sb.AppendLine($"            ),");
        }

        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Escapes a string for use in a verbatim string literal (@"...").
    /// Only quotes need to be doubled.
    /// </summary>
    private static string EscapeForVerbatim(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return value.Replace("\"", "\"\"");
    }

    // ========== MIDDLEWARE SOURCE GENERATION ==========

    /// <summary>
    /// Checks if a class has the [Middleware] attribute.
    /// </summary>
    private static bool IsMiddlewareClass(SyntaxNode node)
    {
        if (node is not ClassDeclarationSyntax classDecl)
            return false;

        // Skip private classes
        if (classDecl.Modifiers.Any(SyntaxKind.PrivateKeyword))
            return false;

        // Check for [Middleware] attribute on the class
        var hasMiddlewareAttribute = classDecl.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Any(attr => attr.Name.ToString().Contains("Middleware"));

        return hasMiddlewareAttribute;
    }

    /// <summary>
    /// Extracts middleware information from a class with [Middleware] attribute.
    /// </summary>
    private static HPD.Agent.SourceGenerator.MiddlewareInfo? GetMiddlewareDeclaration(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        var className = classDecl.Identifier.ValueText;
        var namespaceName = GetNamespace(classDecl);

        // Get custom name from [Middleware(Name = "...")] or [Middleware("name")]
        var customName = GetMiddlewareCustomName(classDecl);

        // Check constructor patterns
        var hasParameterlessConstructor = HasParameterlessConstructor(classDecl);
        var configConstructorTypeName = GetConfigConstructorTypeName(classDecl, semanticModel);
        var isPubliclyAccessible = IsClassPubliclyAccessible(classDecl);

        return new HPD.Agent.SourceGenerator.MiddlewareInfo
        {
            ClassName = className,
            CustomName = customName,
            Namespace = namespaceName,
            HasParameterlessConstructor = hasParameterlessConstructor,
            ConfigConstructorTypeName = configConstructorTypeName,
            IsPubliclyAccessible = isPubliclyAccessible
        };
    }

    /// <summary>
    /// Gets the custom name from [Middleware] attribute if specified.
    /// </summary>
    private static string? GetMiddlewareCustomName(ClassDeclarationSyntax classDecl)
    {
        var middlewareAttr = classDecl.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .FirstOrDefault(attr => attr.Name.ToString().Contains("Middleware"));

        if (middlewareAttr?.ArgumentList?.Arguments.Count > 0)
        {
            var args = middlewareAttr.ArgumentList.Arguments;

            // Check for named argument: Name = "..."
            var namedArg = args.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Name");
            if (namedArg != null)
            {
                return ExtractStringLiteral(namedArg.Expression);
            }

            // Check for positional argument: [Middleware("name")]
            var firstArg = args.FirstOrDefault();
            if (firstArg != null && firstArg.NameEquals == null)
            {
                var value = ExtractStringLiteral(firstArg.Expression);
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Generates the MiddlewareRegistry.All array for AOT-compatible middleware resolution.
    /// Only middlewares with parameterless constructors OR config constructors are included.
    /// DI-only middlewares are marked with RequiresDI = true.
    /// </summary>
    private static void GenerateMiddlewareRegistry(SourceProductionContext context, ImmutableArray<HPD.Agent.SourceGenerator.MiddlewareInfo?> middlewares)
    {
        var validMiddlewares = middlewares
            .Where(m => m != null && m.IsPubliclyAccessible)
            .OrderBy(m => m!.EffectiveName)
            .ToList();

        if (!validMiddlewares.Any())
            return;

        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#pragma warning disable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using HPD.Agent.Middleware;  // For MiddlewareFactory and IAgentMiddleware");
        sb.AppendLine();
        sb.AppendLine("namespace HPD.Agent.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// AOT-compatible catalog of all middlewares in this assembly.");
        sb.AppendLine("    /// Generated by HPDToolSourceGenerator.");
        sb.AppendLine("    /// Provides direct delegate references eliminating reflection in hot paths.");
        sb.AppendLine($"    /// Contains {validMiddlewares.Count} middlewares.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    [System.CodeDom.Compiler.GeneratedCodeAttribute(\"HPDToolSourceGenerator\", \"1.0.0.0\")]");
        sb.AppendLine("    public static class MiddlewareRegistry");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Catalog of all middlewares in this assembly.");
        sb.AppendLine("        /// AgentBuilder automatically discovers and uses this at construction time.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static readonly MiddlewareFactory[] All = new MiddlewareFactory[]");
        sb.AppendLine("        {");

        foreach (var middleware in validMiddlewares)
        {
            var m = middleware!;
            var fullTypeName = m.FullTypeName;

            sb.AppendLine($"            new MiddlewareFactory(");
            sb.AppendLine($"                Name: \"{m.EffectiveName}\",");
            sb.AppendLine($"                MiddlewareType: typeof({fullTypeName}),");

            // CreateInstance: Only if has parameterless constructor
            if (m.HasParameterlessConstructor)
            {
                sb.AppendLine($"                CreateInstance: () => new {fullTypeName}(),");
            }
            else
            {
                sb.AppendLine($"                CreateInstance: null,");
            }

            // Config constructor support
            if (!string.IsNullOrEmpty(m.ConfigConstructorTypeName))
            {
                sb.AppendLine($"                ConfigType: typeof({m.ConfigConstructorTypeName}),");
                sb.AppendLine($"                CreateFromConfig: json => new {fullTypeName}(System.Text.Json.JsonSerializer.Deserialize<{m.ConfigConstructorTypeName}>(json.GetRawText())!),");
            }
            else
            {
                sb.AppendLine($"                ConfigType: null,");
                sb.AppendLine($"                CreateFromConfig: null,");
            }

            sb.AppendLine($"                RequiresDI: {m.RequiresDI.ToString().ToLower()}");
            sb.AppendLine($"            ),");
        }

        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("HPD.Agent.Generated.MiddlewareRegistry.g.cs", sb.ToString());
    }

    // ========== END MIDDLEWARE SOURCE GENERATION ==========

    /// <summary>
    /// Generates the CreateToolkit method using unified polymorphic ICapability iteration.
    /// Phase 4: Now the single unified generation path (old path removed).
    /// </summary>
    private static string GenerateCreateToolkitMethod(ToolkitInfo Toolkit)
    {
        var sb = new StringBuilder();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine($"    /// Creates an AIFunction list for the {Toolkit.Name} Toolkit.");
        sb.AppendLine("    /// </summary>");

        // Only include instance parameter if Toolkit has capabilities that need it
        if (!Toolkit.RequiresInstance)
        {
            sb.AppendLine($"    /// <param name=\"context\">The execution context (optional)</param>");
            sb.AppendLine($"    public static List<AIFunction> CreateToolkit(IToolMetadata? context = null)");
        }
        else
        {
            sb.AppendLine($"    /// <param name=\"instance\">The Toolkit instance</param>");
            sb.AppendLine($"    /// <param name=\"context\">The execution context (optional)</param>");
            sb.AppendLine($"    public static List<AIFunction> CreateToolkit({Toolkit.Name} instance, IToolMetadata? context = null)");
        }

        sb.AppendLine("    {");
        sb.AppendLine("        var functions = new List<AIFunction>();");
        sb.AppendLine();

        // Add collapse container registration if needed (BEFORE individual capabilities)
        var skillRegistrations = SkillCodeGenerator.GenerateSkillRegistrations(Toolkit);
        if (!string.IsNullOrEmpty(skillRegistrations))
        {
            sb.Append(skillRegistrations);
        }

        // PHASE 2A: POLYMORPHIC DISPATCH - All capabilities use the same pattern
        // All capabilities now return just the factory call, so we can treat them uniformly
        //
        // IMPORTANT: Skills are registered via GenerateSkillRegistrations() above,
        // so we exclude them here to avoid duplicate registration.
        // Skills use helper methods (CreateSkillNameSkill) while other capabilities use inline code.
        var nonSkillCapabilities = Toolkit.Capabilities.Where(c => c.Type != CapabilityType.Skill);

        if (nonSkillCapabilities.Any())
        {
            sb.AppendLine();
            sb.AppendLine("        // Register all non-skill capabilities (Functions, SubAgents)");
            foreach (var capability in nonSkillCapabilities)
            {
                // CRITICAL: Only generate conditional check if the evaluator method was generated
                // Conditional evaluators require a ContextTypeName to be set
                var hasConditionalEvaluator = capability.IsConditional &&
                                            capability is BaseCapability baseCapability &&
                                            baseCapability.HasTypedMetadata;

                if (hasConditionalEvaluator)
                {
                    sb.AppendLine($"        if (Evaluate{capability.Name}Condition(context))");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            functions.Add({capability.GenerateRegistrationCode(Toolkit)});");
                    sb.AppendLine("        }");
                }
                else
                {
                    sb.AppendLine($"        functions.Add({capability.GenerateRegistrationCode(Toolkit)});");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("        return functions;");
        sb.AppendLine("    }");
        return sb.ToString();
    }


    private static string GenerateToolkitRegistration(ToolkitInfo Toolkit)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#pragma warning disable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using System.Text.Json.Nodes;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine("using System.Text.Json.Serialization.Metadata;");
        sb.AppendLine("using Microsoft.Extensions.AI;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine("using HPD.Agent.Skills.DocumentStore;");

        // Add HPD.Agent namespace for AgentBuilder, ConversationThread, etc.
        sb.AppendLine("using HPD.Agent;");

        // Add HPD.Agent namespace for SubAgent types
        sb.AppendLine("using HPD.Agent;");

        // Add using directive for the Toolkit's namespace if it's not empty
        if (!string.IsNullOrEmpty(Toolkit.Namespace))
        {
            sb.AppendLine($"using {Toolkit.Namespace};");
        }

        sb.AppendLine();

        sb.AppendLine(GenerateArgumentsDtoAndContext(Toolkit));

        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// Generated registration code for {Toolkit.Name} Toolkit.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"[System.CodeDom.Compiler.GeneratedCodeAttribute(\"HPDToolSourceGenerator\", \"1.0.0.0\")]");
        sb.AppendLine($"public static partial class {Toolkit.Name}Registration");
        sb.AppendLine("    {");

        // Generate GetReferencedToolkits() and GetReferencedFunctions() if there are skills
        // PHASE 5: Use SkillCapabilities (fully populated with resolved references)
        if (Toolkit.SkillCapabilities.Any())
        {
            sb.AppendLine(SkillCodeGenerator.GenerateGetReferencedToolkitsMethod(Toolkit));
            sb.AppendLine();
            sb.AppendLine(SkillCodeGenerator.GenerateGetReferencedFunctionsMethod(Toolkit));
            sb.AppendLine();
        }

        // Generate Toolkit metadata accessor (always generated for consistency)
        // PHASE 5: Use SkillCapabilities instead of Skills
        if (Toolkit.SkillCapabilities.Any())
        {
            sb.AppendLine(SkillCodeGenerator.UpdateToolMetadataWithSkills(Toolkit, ""));
        }
        else
        {
            sb.AppendLine(GenerateToolMetadataMethod(Toolkit));
        }
        sb.AppendLine();

        // Generate empty schema helper if Toolkit is collapsed OR has skills
        // Note: Container function is generated in SkillCodeGenerator.GenerateAllSkillCode
        if (Toolkit.IsCollapsed || Toolkit.SkillCapabilities.Any())
        {
            sb.AppendLine(GenerateEmptySchemaMethod());
            sb.AppendLine();
        }

        sb.AppendLine(GenerateCreateToolkitMethod(Toolkit));

        foreach (var function in Toolkit.FunctionCapabilities)
        {
            sb.AppendLine();
            sb.AppendLine(GenerateSchemaValidator(function, Toolkit));
            
            // Generate manual JSON parser for AOT compatibility
            var relevantParams = function.Parameters
                .Where(p => p.Type != "CancellationToken" && p.Type != "AIFunctionArguments" && p.Type != "IServiceProvider").ToList();
            if (relevantParams.Any())
            {
                sb.AppendLine();
                sb.AppendLine(GenerateJsonParser(function, Toolkit));
            }
        }

        // PHASE 2B: Generate context resolvers for ALL capabilities (Functions, Skills, SubAgents)
        // This enables Skills and SubAgents to use dynamic descriptions and conditionals (feature parity!)
        // Replaces the old DSL-based GenerateContextResolutionMethods() which only worked for Functions
        foreach (var capability in Toolkit.Capabilities)
        {
            var resolvers = capability.GenerateContextResolvers();
            if (!string.IsNullOrEmpty(resolvers))
            {
                sb.AppendLine();
                sb.AppendLine(resolvers);
            }
        }

        // Generate skill code AND toolkit container (if Toolkit is collapsed)
        // NOTE: Container can exist even if there are no skills (e.g., collapsed Toolkit with only functions)
        if (Toolkit.SkillCapabilities.Any() || Toolkit.IsCollapsed)
        {
            sb.AppendLine(SkillCodeGenerator.GenerateAllSkillCode(Toolkit));
        }

        sb.AppendLine("    }");

        return sb.ToString();
    }

    private static string GenerateArgumentsDtoAndContext(ToolkitInfo Toolkit)
    {
        var sb = new StringBuilder();
        var contextSerializableTypes = new List<string>();

        // Generate SubAgentQueryArgs if there are sub-agents (Collapsed per Toolkit to avoid conflicts)
        if (Toolkit.SubAgentCapabilities.Any())
        {
            sb.AppendLine(
$@"    /// <summary>
    /// Represents the arguments for sub-agent invocations, generated at compile-time.
    /// </summary>
    [System.CodeDom.Compiler.GeneratedCodeAttribute(""HPDToolSourceGenerator"", ""1.0.0.0"")]
    public class {Toolkit.Name}SubAgentQueryArgs
    {{
        [System.Text.Json.Serialization.JsonPropertyName(""query"")]
        [System.ComponentModel.Description(""Query for the sub-agent"")]
        public string Query {{ get; set; }} = string.Empty;
    }}
");
        }

        // Generate MultiAgentInputArgs if there are multi-agents (Collapsed per Toolkit to avoid conflicts)
        if (Toolkit.MultiAgentCapabilities.Any())
        {
            sb.AppendLine(
$@"    /// <summary>
    /// Represents the arguments for multi-agent workflow invocations, generated at compile-time.
    /// </summary>
    [System.CodeDom.Compiler.GeneratedCodeAttribute(""HPDToolSourceGenerator"", ""1.0.0.0"")]
    public class {Toolkit.Name}MultiAgentInputArgs
    {{
        [System.Text.Json.Serialization.JsonPropertyName(""input"")]
        [System.ComponentModel.Description(""The user's question or task to process through the multi-agent workflow. Pass the full user message here."")]
        public string Input {{ get; set; }} = string.Empty;
    }}
");
        }

        foreach (var function in Toolkit.FunctionCapabilities)
        {
            if (!function.Parameters.Any(p => p.Type != "CancellationToken" && p.Type != "AIFunctionArguments" && p.Type != "IServiceProvider")) continue;

            var dtoName = $"{function.Name}Args";
            contextSerializableTypes.Add(dtoName);

            sb.AppendLine(
$@"    /// <summary>
    /// Represents the arguments for the {function.Name} function, generated at compile-time.
    /// </summary>
    [System.CodeDom.Compiler.GeneratedCodeAttribute(""HPDToolSourceGenerator"", ""1.0.0.0"")]
    public class {dtoName}
    {{");

            foreach (var param in function.Parameters.Where(p => p.Type != "CancellationToken" && p.Type != "AIFunctionArguments" && p.Type != "IServiceProvider"))
            {
                sb.AppendLine($"        [System.Text.Json.Serialization.JsonPropertyName(\"{param.Name}\")]");
                sb.AppendLine($"        public {param.Type} {param.Name} {{ get; set; }} = default!;");
            }

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // Note: We cannot generate JsonSerializerContext here because the System.Text.Json source generator
        // doesn't process attributes from other source generators in the same compilation.
        // Instead, we'll use JsonSerializerOptions with TypeInfoResolver for AOT compatibility.

        return sb.ToString();
    }

    private static string GenerateSchemaValidator(HPD.Agent.SourceGenerator.Capabilities.FunctionCapability function, ToolkitInfo Toolkit)
    {
        var relevantParams = function.Parameters
            .Where(p => p.Type != "CancellationToken" && p.Type != "AIFunctionArguments" && p.Type != "IServiceProvider").ToList();

        if (!relevantParams.Any())
        {
            return $"        private static Func<JsonElement, List<ValidationError>>? Create{function.Name}Validator() => (args) => new List<ValidationError>();";
        }

        var dtoName = $"{function.Name}Args";
        var sb = new StringBuilder();
        sb.AppendLine($"        private static Func<JsonElement, List<ValidationError>> Create{function.Name}Validator()");
        sb.AppendLine("        {");
        sb.AppendLine("            return (jsonArgs) =>");
        sb.AppendLine("            {");
        sb.AppendLine("                var errors = new List<ValidationError>();");
        sb.AppendLine("                try");
        sb.AppendLine("                {");
        sb.AppendLine($"                    var dto = Parse{dtoName}(jsonArgs);");
        
        // Add null checks for required properties
        foreach (var param in relevantParams.Where(p => !IsNullableParameter(p) && !p.HasDefaultValue))
        {
            sb.AppendLine($"                    if (dto.{param.Name} == null)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        errors.Add(new ValidationError {");
            sb.AppendLine($"                            Property = \"{param.Name}\",");
            sb.AppendLine($"                            ErrorMessage = \"Property '{param.Name}' is required and cannot be null.\",");
            sb.AppendLine("                            ErrorCode = \"missing_required_property\"");
            sb.AppendLine("                        });");
            sb.AppendLine("                    }");
        }

        sb.AppendLine("                }");
        sb.AppendLine("                catch (JsonException ex)");
        sb.AppendLine("                {");
        sb.AppendLine("                    string propertyName = ex.Path ?? \"Unknown\";");
        sb.AppendLine("                    errors.Add(new ValidationError { Property = propertyName, ErrorMessage = ex.Message, ErrorCode = \"type_conversion_error\" });");
        sb.AppendLine("                }");
        sb.AppendLine("                return errors;");
        sb.AppendLine("            };");
        sb.AppendLine("        }");
        return sb.ToString();
    }
    
    private static bool IsNullableParameter(ParameterInfo param)
    {
        // Simple heuristic - check if type ends with ?
        return param.Type.EndsWith("?");
    }

    private static List<ParameterInfo> AnalyzeParameters(ParameterListSyntax parameterList, SemanticModel semanticModel)
    {
        return parameterList.Parameters
            .Select(param => new ParameterInfo
            {
                Name = param.Identifier.ValueText,
                Type = GetParameterType(param, semanticModel),
                Description = GetParameterDescription(param),
                HasDefaultValue = param.Default != null,
                DefaultValue = GetDefaultValue(param),
                ConditionalExpression = GetParameterConditionalExpression(param)
            })
            .ToList();
    }
    
    // Additional helper methods would go here...
    // (GetCustomFunctionName, GetFunctionDescription, GetRequiredPermissions, etc.)
    
    private static string ExtractStringLiteral(ExpressionSyntax? expression)
    {
        if (expression is LiteralExpressionSyntax literal && literal.Token.IsKind(SyntaxKind.StringLiteralToken))
        {
            return literal.Token.ValueText;
        }
        return "";
    }
    
    private static string GetNamespace(SyntaxNode node)
    {
        var parent = node.Parent;
        while (parent != null)
        {
            if (parent is NamespaceDeclarationSyntax namespaceDecl)
                return namespaceDecl.Name.ToString();
            if (parent is FileScopedNamespaceDeclarationSyntax fileScopedNamespace)
                return fileScopedNamespace.Name.ToString();
            parent = parent.Parent;
        }
        return "";
    }
    
    private static string? GetCustomFunctionName(MethodDeclarationSyntax method)
    {
        // For Semantic Kernel style, function name is always the method name
        // No custom name override supported
        return null; // Use method name as default
    }
    
    private static string GetFunctionDescription(MethodDeclarationSyntax method)
    {
        // Check for [AIDescription] attribute first
        var aiDescriptionAttributes = method.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Where(attr => attr.Name.ToString().Contains("AIDescription"));
            
        foreach (var attr in aiDescriptionAttributes)
        {
            var arguments = attr.ArgumentList?.Arguments;
            if (arguments.HasValue && arguments.Value.Count >= 1)
            {
                return ExtractStringLiteral(arguments.Value[0].Expression);
            }
        }
        
        // Fallback to [Description] attribute for backward compatibility
        var descriptionAttributes = method.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Where(attr => attr.Name.ToString().Contains("Description") && !attr.Name.ToString().Contains("AIDescription"));
            
        foreach (var attr in descriptionAttributes)
        {
            var arguments = attr.ArgumentList?.Arguments;
            if (arguments.HasValue && arguments.Value.Count >= 1)
            {
                return ExtractStringLiteral(arguments.Value[0].Expression);
            }
        }
        
        return "";
    }

    private static List<string> GetRequiredPermissions(MethodDeclarationSyntax method)
    {
        // Implementation to extract required permissions
        return new List<string>(); // Placeholder
    }
    
    private static string GetParameterType(ParameterSyntax param, SemanticModel semanticModel)
    {
        return param.Type?.ToString() ?? "object";
    }
    
    private static string GetParameterDescription(ParameterSyntax param)
    {
        // Check for [AIDescription] attribute first
        var aiDescriptionAttributes = param.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Where(attr => attr.Name.ToString().Contains("AIDescription"));
            
        foreach (var attr in aiDescriptionAttributes)
        {
            var arguments = attr.ArgumentList?.Arguments;
            if (arguments.HasValue && arguments.Value.Count >= 1)
            {
                return ExtractStringLiteral(arguments.Value[0].Expression);
            }
        }
        
        // Fallback to [Description] attribute
        var descriptionAttributes = param.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Where(attr => attr.Name.ToString().Contains("Description") && !attr.Name.ToString().Contains("AIDescription"));
            
        foreach (var attr in descriptionAttributes)
        {
            var arguments = attr.ArgumentList?.Arguments;
            if (arguments.HasValue && arguments.Value.Count >= 1)
            {
                return ExtractStringLiteral(arguments.Value[0].Expression);
            }
        }
        
        return "";
    }

    private static string? GetDefaultValue(ParameterSyntax param)
    {
        return param.Default?.Value?.ToString();
    }
    
    private static string GetReturnType(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        return method.ReturnType.ToString();
    }
    
    private static bool IsAsyncMethod(MethodDeclarationSyntax method)
    {
        return method.Modifiers.Any(SyntaxKind.AsyncKeyword) ||
               method.ReturnType.ToString().StartsWith("Task");
    }

    // V3.0 New Helper Methods
    
    /// <summary>
    /// Extracts context type from AIFunction&lt;TMetadata&gt; attribute.
    /// </summary>
    private static (string? contextTypeName, bool isGeneric) GetAIFunctionContextType(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        var aiFunctionAttributes = method.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Where(attr => attr.Name.ToString().Contains("AIFunction"));
            
        foreach (var attr in aiFunctionAttributes)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(attr);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                var attributeType = methodSymbol.ContainingType;
                
                // Check if it's the generic AIFunction<TMetadata>
                if (attributeType.IsGenericType && attributeType.TypeArguments.Length == 1)
                {
                    var contextType = attributeType.TypeArguments[0];
                    return (contextType.Name, true);
                }
            }
        }
        
        return (null, false);
    }

    /// <summary>
    /// Gets conditional expression from ConditionalFunction attribute.
    /// </summary>
    private static string? GetConditionalExpression(MethodDeclarationSyntax method)
    {
        var conditionalAttributes = method.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Where(attr => attr.Name.ToString().Contains("ConditionalFunction"));
            
        foreach (var attr in conditionalAttributes)
        {
            var arguments = attr.ArgumentList?.Arguments;
            if (arguments.HasValue && arguments.Value.Count >= 1)
            {
                return ExtractStringLiteral(arguments.Value[0].Expression);
            }
        }
        
        return null;
    }

    /// <summary>
    /// Gets conditional expression from ConditionalParameter attribute.
    /// </summary>
    private static string? GetParameterConditionalExpression(ParameterSyntax param)
    {
        var conditionalAttributes = param.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Where(attr => attr.Name.ToString().Contains("ConditionalParameter"));
            
        foreach (var attr in conditionalAttributes)
        {
            var arguments = attr.ArgumentList?.Arguments;
            if (arguments.HasValue && arguments.Value.Count >= 1)
            {
                return ExtractStringLiteral(arguments.Value[0].Expression);
            }
        }
        
        return null;
    }

    /// <summary>
    /// Validates that template properties exist on the context type.
    /// </summary>
    private static void ValidateTemplateProperties(SourceProductionContext context, HPD.Agent.SourceGenerator.Capabilities.FunctionCapability function, ITypeSymbol contextType, SyntaxNode location)
    {
        // Validate function description templates
        if (function.HasDynamicDescription)
        {
            ValidateTemplateString(context, function.Description, contextType, location, $"function {function.Name} description");
        }
        
        // Validate parameter description templates
        foreach (var parameter in function.Parameters.Where(p => p.HasDynamicDescription))
        {
            ValidateTemplateString(context, parameter.Description, contextType, location, $"parameter {parameter.Name} description");
        }
    }

    /// <summary>
    /// Validates a single template string for property existence.
    /// </summary>
    private static void ValidateTemplateString(SourceProductionContext context, string template, ITypeSymbol contextType, SyntaxNode location, string locationDescription)
    {
        var regex = new Regex(@"\{context\.([a-zA-Z_][a-zA-Z0-9_]*)\}");
        var availableProperties = contextType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public)
            .Select(p => p.Name)
            .ToList();
            
        foreach (Match match in regex.Matches(template))
        {
            var propertyName = match.Groups[1].Value;
            if (!availableProperties.Contains(propertyName))
            {
                var diagnostic = Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "HPD001",
                        "Invalid template property",
                        $"Property '{propertyName}' not found in {contextType.Name} for {locationDescription}. Available properties: {string.Join(", ", availableProperties)}",
                        "HPD.Template",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true,
                        description: "Template properties must exist as public properties on the context type."),
                    location.GetLocation());
                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    /// <summary>
    /// Validates conditional expressions for property existence, syntax, and type compatibility.
    /// </summary>
    private static void ValidateConditionalExpression(SourceProductionContext context, string expression, ITypeSymbol contextType, SyntaxNode location, string locationDescription)
    {
        // First validate syntax
        ValidateExpressionSyntax(context, expression, location);
        
        // Then validate type compatibility
        ValidateTypeCompatibility(context, expression, contextType, location);
        
        // Finally validate property existence (existing logic)
        var propertyNames = ExtractPropertyNames(expression);
        var availableProperties = contextType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public)
            .Select(p => p.Name)
            .ToList();
            
        foreach (var propertyName in propertyNames)
        {
            if (!availableProperties.Contains(propertyName))
            {
                var diagnostic = Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "HPD002",
                        "Invalid conditional property",
                        $"Property '{propertyName}' not found in {contextType.Name} for {locationDescription}. Available properties: {string.Join(", ", availableProperties)}",
                        "HPD.Conditional",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true,
                        description: "Conditional expressions must reference properties that exist on the context type."),
                    location.GetLocation());
                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    /// <summary>
    /// Extracts property names from conditional expressions.
    /// </summary>
    private static HashSet<string> ExtractPropertyNames(string expression)
    {
        var propertyNames = new HashSet<string>();
        var identifierRegex = new Regex(@"\b[A-Za-z_][A-Za-z0-9_]*\b");
        var keywords = new HashSet<string> { "true", "false", "null", "&&", "||", "!", "==", "!=", "<", ">", "<=", ">=" };
        
        foreach (Match match in identifierRegex.Matches(expression))
        {
            var identifier = match.Value;
            if (!keywords.Contains(identifier.ToLower()) && !int.TryParse(identifier, out _))
            {
                propertyNames.Add(identifier);
            }
        }
        
        return propertyNames;
    }

    /// <summary>
    /// NEW: Check if function should use generic AIFunction attribute.
    /// </summary>
    private static void ValidateFunctionContextUsage(SourceProductionContext context, HPD.Agent.SourceGenerator.Capabilities.FunctionCapability function, MethodDeclarationSyntax method)
    {
        // Check if function uses dynamic features but no generic context
        bool usesDynamicFeatures = function.HasDynamicDescription || 
                                  function.IsConditional || 
                                  function.HasConditionalParameters;
        
        bool hasGenericContext = !string.IsNullOrEmpty(function.ContextTypeName);
        
        if (usesDynamicFeatures && !hasGenericContext)
        {
            var diagnostic = Diagnostic.Create(
                new DiagnosticDescriptor(
                    "HPD003",
                    "Missing context type",
                    $"Function '{function.Name}' uses dynamic features but lacks AIFunction<TMetadata> attribute. Use [AIFunction<YourContext>] instead of [AIFunction].",
                    "HPD.Context",
                    DiagnosticSeverity.Warning,
                    isEnabledByDefault: true,
                    description: "Functions with conditional logic or dynamic descriptions need a typed context."),
                method.GetLocation());
            context.ReportDiagnostic(diagnostic);
        }
    }

    /// <summary>
    /// NEW: Validates expression syntax for proper structure and balanced parentheses.
    /// </summary>
    private static void ValidateExpressionSyntax(SourceProductionContext context, string expression, SyntaxNode location)
    {
        try
        {
            // Simple syntax checks
            if (expression.Contains("&&&&") || expression.Contains("||||"))
            {
                ReportError(context, "Invalid operator sequence", location);
                return;
            }
            
            // Check balanced parentheses
            var openCount = expression.Count(c => c == '(');
            var closeCount = expression.Count(c => c == ')');
            if (openCount != closeCount)
            {
                ReportError(context, "Unbalanced parentheses", location);
                return;
            }
            
            // Check for empty expressions
            if (string.IsNullOrWhiteSpace(expression))
            {
                ReportError(context, "Empty expression", location);
                return;
            }
            
            // Check for invalid characters
            var invalidChars = expression.Where(c => !char.IsLetterOrDigit(c) && !"()&|!<>=. _".Contains(c)).ToArray();
            if (invalidChars.Any())
            {
                ReportError(context, $"Invalid characters in expression: {string.Join(", ", invalidChars.Distinct())}", location);
                return;
            }
        }
        catch
        {
            ReportError(context, "Invalid expression syntax", location);
        }
    }

    /// <summary>
    /// NEW: Validates type compatibility between operations and property types.
    /// </summary>
    private static void ValidateTypeCompatibility(SourceProductionContext context, string expression, 
        ITypeSymbol contextType, SyntaxNode location)
    {
        try
        {
            // Parse expression and check each operation
            var tokens = ParseExpressionTokens(expression);
            foreach (var token in tokens)
            {
                if (token.Type == TokenType.Comparison)
                {
                    var property = GetPropertyType(contextType, token.PropertyName);
                    if (property != null && !IsValidOperation(property, token.Operator))
                    {
                        ReportError(context, 
                            $"Cannot use operator '{token.Operator}' on property '{token.PropertyName}' of type {property.Name}", 
                            location);
                    }
                }
            }
        }
        catch
        {
            // If parsing fails, skip type compatibility check
            // Syntax validation will catch basic syntax errors
        }
    }

    /// <summary>
    /// Helper: Simple expression tokenizer for validation.
    /// </summary>
    private static List<ExpressionToken> ParseExpressionTokens(string expression)
    {
        var tokens = new List<ExpressionToken>();
        
        // Simple regex-based parsing for basic validation
        // This is a simplified version - could be enhanced with proper expression parsing
        var comparisonPattern = @"(\w+(?:\.\w+)*)\s*([<>=!]+)\s*(\w+|""[^""]*"")";
        var matches = System.Text.RegularExpressions.Regex.Matches(expression, comparisonPattern);
        
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            tokens.Add(new ExpressionToken
            {
                Type = TokenType.Comparison,
                PropertyName = match.Groups[1].Value,
                Operator = match.Groups[2].Value,
                Value = match.Groups[3].Value
            });
        }
        
        return tokens;
    }

    /// <summary>
    /// Helper: Gets the type of a property from the context type.
    /// </summary>
    private static ITypeSymbol? GetPropertyType(ITypeSymbol contextType, string propertyName)
    {
        // Handle nested property access (e.g., "context.User.Name")
        var parts = propertyName.Split('.');
        var currentType = contextType;
        
        foreach (var part in parts)
        {
            var property = currentType.GetMembers(part)
                .OfType<IPropertySymbol>()
                .FirstOrDefault();
                
            if (property == null)
                return null;
                
            currentType = property.Type;
        }
        
        return currentType;
    }

    /// <summary>
    /// Helper: Checks if an operator is valid for a given property type.
    /// </summary>
    private static bool IsValidOperation(ITypeSymbol propertyType, string operatorSymbol)
    {
        var typeName = propertyType.Name;
        
        return operatorSymbol switch
        {
            ">" or "<" or ">=" or "<=" => IsNumericType(typeName) || IsComparableType(typeName),
            "==" or "!=" => true, // All types support equality
            _ => false
        };
    }

    /// <summary>
    /// Helper: Checks if a type is numeric.
    /// </summary>
    private static bool IsNumericType(string typeName)
    {
        return typeName switch
        {
            "Int32" or "Int64" or "Double" or "Single" or "Decimal" or "Byte" or "SByte" or "Int16" or "UInt16" or "UInt32" or "UInt64" => true,
            _ => false
        };
    }

    /// <summary>
    /// Helper: Checks if a type implements IComparable.
    /// </summary>
    private static bool IsComparableType(string typeName)
    {
        return typeName switch
        {
            "String" or "DateTime" or "DateTimeOffset" or "TimeSpan" => true,
            _ => false
        };
    }

    /// <summary>
    /// Helper: Reports a diagnostic error.
    /// </summary>
    private static void ReportError(SourceProductionContext context, string message, SyntaxNode location)
    {
        var diagnostic = Diagnostic.Create(
            new DiagnosticDescriptor(
                "HPD004",
                "Expression validation error",
                message,
                "HPD.Validation",
                DiagnosticSeverity.Error,
                isEnabledByDefault: true),
            location.GetLocation());
        context.ReportDiagnostic(diagnostic);
    }

    // Helper to detect [RequiresPermission] attribute
    private static bool GetRequiresPermission(MethodDeclarationSyntax method)
    {
        return method.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Any(attr => attr.Name.ToString().Contains("RequiresPermission"));
    }

    /// <summary>
    /// Detects [Toolkit] attribute on a class and extracts its configuration.
    /// Supports dual-context (FunctionResult, SystemPrompt) and custom naming.
    /// Analyzes expressions to determine if they're static or instance methods/properties.
    /// </summary>
    private static (
        bool isCollapsed,
        string? containerDescription,
        string? FunctionResult,
        string? FunctionResultExpression,
        bool FunctionResultIsStatic,
        string? SystemPrompt,
        string? SystemPromptExpression,
        bool SystemPromptIsStatic,
        List<Diagnostic> diagnostics,
        string? customName
    ) GetToolkitAttribute(ClassDeclarationSyntax classDecl, SemanticModel semanticModel)
    {
        // Look for [Toolkit] attribute
        var allAttributes = classDecl.AttributeLists
            .SelectMany(attrList => attrList.Attributes);

        var attr = allAttributes.FirstOrDefault(attr =>
            attr.Name.ToString() == "Toolkit" || attr.Name.ToString() == "ToolkitAttribute");

        if (attr != null)
        {
            var arguments = attr.ArgumentList?.Arguments;

            string? description = null;
            string? funcResultCtx = null, funcResultExpr = null;
            bool funcResultIsStatic = true;
            string? sysPromptCtx = null, sysPromptExpr = null;
            bool sysPromptIsStatic = true;
            string? customName = null;
            bool hasDescription = false;

            // [Toolkit] attribute handling
            // Constructor forms:
            // - [Toolkit] - no args, not collapsed
            // - [Toolkit(Name = "...")] - named arg, not collapsed
            // - [Toolkit("description")] - collapsible (has description)
            // - [Toolkit("description", FunctionResult = "...")] - collapsible with contexts
            // Runtime override: CollapsingConfig.NeverCollapse to prevent collapsing at runtime

            if (arguments.HasValue)
            {
                foreach (var arg in arguments.Value)
                {
                    var argName = arg.NameEquals?.Name.Identifier.ValueText
                               ?? arg.NameColon?.Name.Identifier.ValueText;

                    if (argName == "Name")
                    {
                        customName = ExtractStringLiteral(arg.Expression);
                    }
                    else if (argName == "Description")
                    {
                        description = ExtractStringLiteral(arg.Expression);
                        hasDescription = true;
                    }
                    else if (argName == "FunctionResult")
                    {
                        if (arg.Expression is LiteralExpressionSyntax literal && literal.Token.IsKind(SyntaxKind.StringLiteralToken))
                        {
                            funcResultCtx = literal.Token.ValueText;
                        }
                        else
                        {
                            funcResultExpr = arg.Expression.ToString();
                            funcResultIsStatic = IsExpressionStatic(arg.Expression, semanticModel, classDecl);
                        }
                    }
                    else if (argName == "SystemPrompt")
                    {
                        if (arg.Expression is LiteralExpressionSyntax literal && literal.Token.IsKind(SyntaxKind.StringLiteralToken))
                        {
                            sysPromptCtx = literal.Token.ValueText;
                        }
                        else
                        {
                            sysPromptExpr = arg.Expression.ToString();
                            sysPromptIsStatic = IsExpressionStatic(arg.Expression, semanticModel, classDecl);
                        }
                    }
                    else if (argName == null && arg == arguments.Value[0])
                    {
                        // First positional argument is description (enables collapsing)
                        description = ExtractStringLiteral(arg.Expression);
                        hasDescription = true;
                    }
                }
            }

            // Toolkit is collapsed if it has a description
            // Runtime override available via CollapsingConfig.NeverCollapse
            bool isCollapsed = hasDescription;

            // If attribute is present but not collapsed, still return the customName
            if (!isCollapsed)
            {
                return (false, null, null, null, true, null, null, true, new List<Diagnostic>(), customName);
            }

            // If collapsed, validate and return
            var diagnostics = ValidateDualContextConfiguration(
                funcResultCtx, funcResultExpr,
                sysPromptCtx, sysPromptExpr,
                classDecl, semanticModel);

            return (true, description, funcResultCtx, funcResultExpr, funcResultIsStatic, sysPromptCtx, sysPromptExpr, sysPromptIsStatic, diagnostics, customName);
        }

        return (false, null, null, null, true, null, null, true, new List<Diagnostic>(), null);
    }

    /// <summary>
    /// Validates dual-context configuration to prevent conflicting settings.
    /// PHASE 2C: Compile-time validation for dual-context architecture.
    /// </summary>
    private static List<Diagnostic> ValidateDualContextConfiguration(
        string? funcResultCtx, string? funcResultExpr,
        string? sysPromptCtx, string? sysPromptExpr,
        ClassDeclarationSyntax classDecl, SemanticModel semanticModel)
    {
        var diagnostics = new List<Diagnostic>();
        var location = classDecl.GetLocation();

        // Validate: Can't have both literal AND expression for FunctionResult
        if (!string.IsNullOrEmpty(funcResultCtx) && !string.IsNullOrEmpty(funcResultExpr))
        {
            var diagnostic = Diagnostic.Create(
                new DiagnosticDescriptor(
                    "HPDAG0101",
                    "Conflicting FunctionResult configuration",
                    "Toolkit '{0}' specifies both FunctionResult literal and expression. Use one or the other, not both.",
                    "HPDAgent.SourceGenerator",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true,
                    description: "FunctionResult can be either a literal string or an expression, but not both. " +
                                "Literal: FunctionResult = \"text\". Expression: FunctionResult = MethodName."),
                location,
                classDecl.Identifier.ValueText);

            diagnostics.Add(diagnostic);
        }

        // Validate: Can't have both literal AND expression forSystemPrompt
        if (!string.IsNullOrEmpty(sysPromptCtx) && !string.IsNullOrEmpty(sysPromptExpr))
        {
            var diagnostic = Diagnostic.Create(
                new DiagnosticDescriptor(
                    "HPDAG0102",
                    "ConflictingSystemPrompt configuration",
                    "Toolkit '{0}' specifies bothSystemPrompt literal and expression. Use one or the other, not both.",
                    "HPDAgent.SourceGenerator",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true,
                    description: "SystemPrompt can be either a literal string or an expression, but not both. " +
                                "Literal:SystemPrompt = \"text\". Expression:SystemPrompt = MethodName."),
                location,
                classDecl.Identifier.ValueText);

            diagnostics.Add(diagnostic);
        }

        // Validate: Expression syntax (basic check)
        if (!string.IsNullOrEmpty(funcResultExpr))
        {
            var exprDiagnostics = ValidateContextExpression(funcResultExpr, "FunctionResult", classDecl);
            diagnostics.AddRange(exprDiagnostics);
        }

        if (!string.IsNullOrEmpty(sysPromptExpr))
        {
            var exprDiagnostics = ValidateContextExpression(sysPromptExpr, "SystemPrompt", classDecl);
            diagnostics.AddRange(exprDiagnostics);
        }

        return diagnostics;
    }

    /// <summary>
    /// Validates that a context expression has valid syntax.
    /// </summary>
    private static List<Diagnostic> ValidateContextExpression(
        string expression,
        string propertyName,
        ClassDeclarationSyntax classDecl)
    {
        var diagnostics = new List<Diagnostic>();

        if (string.IsNullOrWhiteSpace(expression))
        {
            // Empty expression - will be caught by other validation
            return diagnostics;
        }

        // Basic validation: Check for common mistakes
        // Valid examples: "MyMethod", "instance.GetInstructions", "MyClass.StaticMethod"
        // Invalid examples: Literals ("\"text\""), operators ("1 + 2"), empty strings

        // Check for string literals (user passed a string when they should use the literal parameter)
        if (expression.StartsWith("\"") || expression.StartsWith("@\""))
        {
            var diagnostic = Diagnostic.Create(
                new DiagnosticDescriptor(
                    "HPDAG0103",
                    $"Invalid {propertyName} expression syntax",
                    "Toolkit '{0}' uses a string literal for {1} expression. Use the literal parameter instead, or provide a method/property reference.",
                    "HPDAgent.SourceGenerator",
                    DiagnosticSeverity.Warning,
                    isEnabledByDefault: true,
                    description: $"Context expressions should reference methods or properties, not string literals. " +
                                $"For literal text, use the non-expression parameter."),
                classDecl.GetLocation(),
                classDecl.Identifier.ValueText,
                propertyName);

            diagnostics.Add(diagnostic);
        }

        return diagnostics;
    }

    /// <summary>
    /// Analyzes an expression to determine if it refers to a static member or requires instance access.
    /// Returns true if static, false if instance is required.
    /// </summary>
    private static bool IsExpressionStatic(ExpressionSyntax expression, SemanticModel semanticModel, ClassDeclarationSyntax classDecl)
    {
        // For member access expressions like ClassName.Method() or OtherClass.Property
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            var leftSymbol = semanticModel.GetSymbolInfo(memberAccess.Expression).Symbol;

            // If left side is a type (not an instance), it's static access
            if (leftSymbol is INamedTypeSymbol)
            {
                return true; // External static class or static member access
            }

            // Otherwise it's instance member access
            return false;
        }

        // For invocation expressions like Method() or Property
        if (expression is InvocationExpressionSyntax invocation)
        {
            // Get the identifier from the invocation
            if (invocation.Expression is IdentifierNameSyntax identifier)
            {
                // Check if this is a member of the current class
                var members = classDecl.Members;
                foreach (var member in members)
                {
                    if (member is MethodDeclarationSyntax method && method.Identifier.ValueText == identifier.Identifier.ValueText)
                    {
                        // Found the method in the class - check if it's static
                        return method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                    }
                    else if (member is PropertyDeclarationSyntax property && property.Identifier.ValueText == identifier.Identifier.ValueText)
                    {
                        // Found the property in the class - check if it's static
                        return property.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                    }
                }
            }
        }

        // For simple identifiers like Property or Method() without parentheses
        if (expression is IdentifierNameSyntax simpleIdentifier)
        {
            // Check if this is a member of the current class
            var members = classDecl.Members;
            foreach (var member in members)
            {
                if (member is MethodDeclarationSyntax method && method.Identifier.ValueText == simpleIdentifier.Identifier.ValueText)
                {
                    // Found the method in the class - check if it's static
                    return method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                }
                else if (member is PropertyDeclarationSyntax property && property.Identifier.ValueText == simpleIdentifier.Identifier.ValueText)
                {
                    // Found the property in the class - check if it's static
                    return property.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                }
            }
        }

        // Try semantic model as fallback
        var symbolInfo = semanticModel.GetSymbolInfo(expression);
        var symbol = symbolInfo.Symbol;

        if (symbol != null)
        {
            // Check if it's a method or property
            if (symbol is IMethodSymbol methodSymbol)
            {
                return methodSymbol.IsStatic;
            }
            else if (symbol is IPropertySymbol propertySymbol)
            {
                return propertySymbol.IsStatic;
            }
        }

        // Default to static if we can't determine (safer - won't add instance prefix)
        return true;
    }

    /// <summary>
    /// Generates a manual JSON parser for AOT compatibility - no reflection needed!
    /// </summary>
    private static string GenerateJsonParser(HPD.Agent.SourceGenerator.Capabilities.FunctionCapability function, ToolkitInfo Toolkit)
    {
        var dtoName = $"{function.Name}Args";
        var relevantParams = function.Parameters
            .Where(p => p.Type != "CancellationToken" && p.Type != "AIFunctionArguments" && p.Type != "IServiceProvider").ToList();
        
        var sb = new StringBuilder();
        sb.AppendLine($"        /// <summary>");
        sb.AppendLine($"        /// Manual JSON parser for {dtoName} - fully AOT compatible");
        sb.AppendLine($"        /// </summary>");
        sb.AppendLine($"        private static {dtoName} Parse{dtoName}(JsonElement json)");
        sb.AppendLine("        {");
        sb.AppendLine($"            var result = new {dtoName}();");
        sb.AppendLine();
        
        foreach (var param in relevantParams)
        {
            sb.AppendLine($"            // Parse {param.Name}");
            sb.AppendLine($"            if (json.TryGetProperty(\"{param.Name}\", out var {param.Name}Prop) || ");
            sb.AppendLine($"                json.TryGetProperty(\"{ToCamelCase(param.Name)}\", out {param.Name}Prop) ||");
            sb.AppendLine($"                json.TryGetProperty(\"{param.Name.ToLower()}\", out {param.Name}Prop))");
            sb.AppendLine("            {");
            
            // Generate parsing logic based on type
            sb.AppendLine(GeneratePropertyParser(param, $"{param.Name}Prop", $"result.{param.Name}"));
            
            sb.AppendLine("            }");
            
            // Add default value handling if needed
            if (!IsNullableParameter(param) && !param.HasDefaultValue)
            {
                sb.AppendLine("            else");
                sb.AppendLine("            {");
                sb.AppendLine($"                throw new JsonException($\"Required property '{param.Name}' not found\");");
                sb.AppendLine("            }");
            }
            sb.AppendLine();
        }
        
        sb.AppendLine("            return result;");
        sb.AppendLine("        }");
        
        return sb.ToString();
    }

    /// <summary>
    /// Generates property parsing code based on the parameter type
    /// </summary>
    private static string GeneratePropertyParser(ParameterInfo param, string jsonPropertyVar, string targetVar)
    {
        var sb = new StringBuilder();
        var type = param.Type.TrimEnd('?');
        var isNullable = param.Type.EndsWith("?");

        // Handle null values for nullable types
        if (isNullable)
        {
            sb.AppendLine($"                if ({jsonPropertyVar}.ValueKind == JsonValueKind.Null)");
            sb.AppendLine($"                {{");
            sb.AppendLine($"                    {targetVar} = null;");
            sb.AppendLine($"                }}");
            sb.AppendLine($"                else");
            sb.AppendLine($"                {{");
        }

        var indent = isNullable ? "    " : "";

        switch (type)
        {
            case "string":
                // String can come as String, Number, or Boolean
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.ValueKind switch");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    JsonValueKind.String => {jsonPropertyVar}.GetString(){(isNullable ? "" : " ?? string.Empty")},");
                sb.AppendLine($"{indent}                    JsonValueKind.Number => {jsonPropertyVar}.GetRawText(), // Convert number to string");
                sb.AppendLine($"{indent}                    JsonValueKind.True => \"true\",");
                sb.AppendLine($"{indent}                    JsonValueKind.False => \"false\",");
                sb.AppendLine($"{indent}                    _ => {jsonPropertyVar}.GetRawText()");
                sb.AppendLine($"{indent}                }};");
                break;

            case "int" or "Int32":
                // Handle both Number and String (for model compatibility)
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.ValueKind switch");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    JsonValueKind.Number => {jsonPropertyVar}.GetInt32(),");
                sb.AppendLine($"{indent}                    JsonValueKind.String => int.Parse({jsonPropertyVar}.GetString() ?? \"0\"),");
                sb.AppendLine($"{indent}                    _ => throw new JsonException($\"Cannot convert {{{{{jsonPropertyVar}.ValueKind}}}} to int\")");
                sb.AppendLine($"{indent}                }};");
                break;

            case "long" or "Int64":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.ValueKind switch");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    JsonValueKind.Number => {jsonPropertyVar}.GetInt64(),");
                sb.AppendLine($"{indent}                    JsonValueKind.String => long.Parse({jsonPropertyVar}.GetString() ?? \"0\"),");
                sb.AppendLine($"{indent}                    _ => throw new JsonException($\"Cannot convert {{{{{jsonPropertyVar}.ValueKind}}}} to long\")");
                sb.AppendLine($"{indent}                }};");
                break;

            case "double" or "Double":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.ValueKind switch");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    JsonValueKind.Number => {jsonPropertyVar}.GetDouble(),");
                sb.AppendLine($"{indent}                    JsonValueKind.String => double.Parse({jsonPropertyVar}.GetString() ?? \"0\"),");
                sb.AppendLine($"{indent}                    _ => throw new JsonException($\"Cannot convert {{{{{jsonPropertyVar}.ValueKind}}}} to double\")");
                sb.AppendLine($"{indent}                }};");
                break;

            case "float" or "Single":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.ValueKind switch");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    JsonValueKind.Number => (float){jsonPropertyVar}.GetDouble(),");
                sb.AppendLine($"{indent}                    JsonValueKind.String => float.Parse({jsonPropertyVar}.GetString() ?? \"0\"),");
                sb.AppendLine($"{indent}                    _ => throw new JsonException($\"Cannot convert {{{{{jsonPropertyVar}.ValueKind}}}} to float\")");
                sb.AppendLine($"{indent}                }};");
                break;

            case "bool" or "Boolean":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.ValueKind switch");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    JsonValueKind.True => true,");
                sb.AppendLine($"{indent}                    JsonValueKind.False => false,");
                sb.AppendLine($"{indent}                    JsonValueKind.String => bool.Parse({jsonPropertyVar}.GetString() ?? \"false\"),");
                sb.AppendLine($"{indent}                    _ => throw new JsonException($\"Cannot convert {{{{{jsonPropertyVar}.ValueKind}}}} to bool\")");
                sb.AppendLine($"{indent}                }};");
                break;

            case "decimal" or "Decimal":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.ValueKind switch");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    JsonValueKind.Number => {jsonPropertyVar}.GetDecimal(),");
                sb.AppendLine($"{indent}                    JsonValueKind.String => decimal.Parse({jsonPropertyVar}.GetString() ?? \"0\"),");
                sb.AppendLine($"{indent}                    _ => throw new JsonException($\"Cannot convert {{{{{jsonPropertyVar}.ValueKind}}}} to decimal\")");
                sb.AppendLine($"{indent}                }};");
                break;

            case "DateTime":
                // Handle both proper DateTime JSON and string representations
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.ValueKind switch");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    JsonValueKind.String => DateTime.Parse({jsonPropertyVar}.GetString()!),");
                sb.AppendLine($"{indent}                    _ => {jsonPropertyVar}.GetDateTime()");
                sb.AppendLine($"{indent}                }};");
                break;

            case "DateTimeOffset":
                // Handle both proper DateTimeOffset JSON and string representations
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.ValueKind switch");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    JsonValueKind.String => DateTimeOffset.Parse({jsonPropertyVar}.GetString()!),");
                sb.AppendLine($"{indent}                    _ => {jsonPropertyVar}.GetDateTimeOffset()");
                sb.AppendLine($"{indent}                }};");
                break;

            case "Guid":
                // Handle both proper Guid JSON and string representations
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.ValueKind switch");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    JsonValueKind.String => Guid.Parse({jsonPropertyVar}.GetString()!),");
                sb.AppendLine($"{indent}                    _ => {jsonPropertyVar}.GetGuid()");
                sb.AppendLine($"{indent}                }};");
                break;

            default:
                // For complex types, arrays, or unknown types, fall back to ToString or throw
                if (type.StartsWith("List<") || type.StartsWith("IList<") || type.EndsWith("[]"))
                {
                    // Handle arrays/lists using JsonSerializer
                    sb.AppendLine($"{indent}                {targetVar} = JsonSerializer.Deserialize<{type}>({jsonPropertyVar}.GetRawText());");
                }
                else
                {
                    // For other complex types
                    sb.AppendLine($"{indent}                // Complex type - needs custom parsing");
                    sb.AppendLine($"{indent}                {targetVar} = JsonSerializer.Deserialize<{type}>({jsonPropertyVar}.GetRawText());");
                }
                break;
        }

        if (isNullable)
        {
            sb.AppendLine($"                }}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts a string to camelCase
    /// </summary>
    private static string ToCamelCase(string str)
    {
        if (string.IsNullOrEmpty(str) || char.IsLower(str[0]))
            return str;

        return char.ToLower(str[0]) + str.Substring(1);
    }

    /// <summary>
    /// Generates the GetToolMetadata() method for Toolkit Collapsing support.
    /// </summary>
    private static string GenerateToolMetadataMethod(ToolkitInfo Toolkit)
    {
        var sb = new StringBuilder();

        var functionNamesArray = string.Join(", ", Toolkit.FunctionCapabilities.Select(f => $"\"{f.FunctionName}\""));
        var description = Toolkit.IsCollapsed && !string.IsNullOrEmpty(Toolkit.ContainerDescription)
            ? Toolkit.ContainerDescription
            : Toolkit.Description;

        sb.AppendLine("        private static ToolMetadata? _cachedMetadata;");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Gets metadata for the {Toolkit.ClassName} Toolkit (used for Collapsing).");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static ToolMetadata GetToolMetadata()");
        sb.AppendLine("        {");
        sb.AppendLine("            return _cachedMetadata ??= new ToolMetadata");
        sb.AppendLine("            {");
        // Use EffectiveName for LLM-visible name (supports [Toolkit(Name = "...")] override)
        sb.AppendLine($"                Name = \"{Toolkit.EffectiveName}\",");
        sb.AppendLine($"                Description = \"{description}\",");
        sb.AppendLine($"                FunctionNames = new string[] {{ {functionNamesArray} }},");
        sb.AppendLine($"                FunctionCount = {Toolkit.FunctionCapabilities.Count()},");
        sb.AppendLine($"                IsCollapsed = {Toolkit.IsCollapsed.ToString().ToLower()}");
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        return sb.ToString();
    }

    /// <summary>
    /// Generates the container function for a Collapsed Toolkit.
    /// </summary>
    private static string GenerateContainerFunction(ToolkitInfo Toolkit)
    {
        var sb = new StringBuilder();

        // Combine both AI functions and skills
        var allCapabilities = Toolkit.FunctionCapabilities.Select(f => f.FunctionName)
            .Concat(Toolkit.SkillCapabilities.Select(s => s.Name))
            .ToList();
        var capabilitiesList = string.Join(", ", allCapabilities);
        var totalCount = Toolkit.FunctionCapabilities.Count() + Toolkit.SkillCapabilities.Count();

        var description = !string.IsNullOrEmpty(Toolkit.ContainerDescription)
            ? Toolkit.ContainerDescription
            : Toolkit.Description ?? string.Empty;

        // Use shared helper to generate description and return message
        // Use EffectiveName for LLM-visible container name
        var fullDescription = ToolkitContainerHelper.GenerateContainerDescription(description, Toolkit.EffectiveName, allCapabilities);

        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Container function for {Toolkit.ClassName} Toolkit.");
        sb.AppendLine("        /// </summary>");
        // Method signature uses ClassName for type reference
        sb.AppendLine($"        private static AIFunction Create{Toolkit.ClassName}Container({Toolkit.ClassName} instance)");
        sb.AppendLine("        {");
        sb.AppendLine("            return HPDAIFunctionFactory.Create(");
        sb.AppendLine("                async (arguments, cancellationToken) =>");
        sb.AppendLine("                {");

        // Use the ContainerDescription (or Toolkit description as fallback) in the return message
        var returnMessage = ToolkitContainerHelper.GenerateReturnMessage(description, allCapabilities, Toolkit.FunctionResult);

        if (!string.IsNullOrEmpty(Toolkit.FunctionResultExpression))
        {
            // Using an interpolated string to combine the base message and the dynamic instructions
            var baseMessage = ToolkitContainerHelper.GenerateReturnMessage(description, allCapabilities, null);
            // Escape special characters for the interpolated string - we need to convert \n\n to \\n\\n in source code
            baseMessage = baseMessage.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\"", "\\\"");
            // Add separator between capabilities list and dynamic instructions
            var separator = "\\n\\n";  // This will be two backslash-n sequences in the source code

            // Use instance. prefix for instance methods, nothing for static
            var expressionCall = Toolkit.FunctionResultIsStatic
                ? Toolkit.FunctionResultExpression
                : $"instance.{Toolkit.FunctionResultExpression}";

            sb.AppendLine($"                    var dynamicInstructions = {expressionCall};");
            sb.AppendLine($"                    return $\"{baseMessage}{separator}{{dynamicInstructions}}\";");
        }
        else
        {
            // Using a verbatim string literal for static content
            // In a verbatim string, actual newlines are allowed but we need to represent them as \n
            var escapedReturnMessage = returnMessage
                .Replace("\\", "\\\\")  // Escape backslashes first
                .Replace("\"", "\"\"")  // Escape quotes (double them in verbatim strings)
                .Replace("\n", "\\n"); // Convert actual newlines to backslash-n
            sb.AppendLine($"                    return @\"{escapedReturnMessage}\";");
        }

        sb.AppendLine("                },");
        sb.AppendLine("                new HPDAIFunctionFactoryOptions");
        sb.AppendLine("                {");
        // Use EffectiveName for LLM-visible container function name
        sb.AppendLine($"                    Name = \"{Toolkit.EffectiveName}\",");
        sb.AppendLine($"                    Description = \"{fullDescription}\",");
        sb.AppendLine("                    SchemaProvider = () => CreateEmptyContainerSchema(),");
        sb.AppendLine("                    AdditionalProperties = new Dictionary<string, object>");
        sb.AppendLine("                    {");
        sb.AppendLine("                        [\"IsContainer\"] = true,");
        // Use EffectiveName for ToolkitName metadata (supports custom naming)
        sb.AppendLine($"                        [\"ToolkitName\"] = \"{Toolkit.EffectiveName}\",");
        sb.AppendLine($"                        [\"FunctionNames\"] = new string[] {{ {string.Join(", ", allCapabilities.Select(c => $"\"{c}\""))} }},");
        sb.AppendLine($"                        [\"FunctionCount\"] = {totalCount},");

        // AddSystemPrompt to metadata (for middleware injection)
        if (!string.IsNullOrEmpty(Toolkit.SystemPrompt))
        {
            // Use verbatim string literal - only escape quotes (double them), NOT newlines
            var escapedSysPrompt = Toolkit.SystemPrompt.Replace("\"", "\"\"");
            sb.AppendLine($"                        [\"SystemPrompt\"] = @\"{escapedSysPrompt}\",");
        }
        else if (!string.IsNullOrEmpty(Toolkit.SystemPromptExpression))
        {
            // Expression - evaluate at container creation time
            // Use instance. prefix for instance methods, nothing for static
            var expressionCall = Toolkit.SystemPromptIsStatic
                ? Toolkit.SystemPromptExpression
                : $"instance.{Toolkit.SystemPromptExpression}";

            sb.AppendLine($"                        [\"SystemPrompt\"] = {expressionCall},");
        }

        // Optionally store FunctionResult for introspection
        if (!string.IsNullOrEmpty(Toolkit.FunctionResult))
        {
            // Use verbatim string literal - only escape quotes (double them), NOT newlines
            var escapedFuncResult = Toolkit.FunctionResult.Replace("\"", "\"\"");
            sb.AppendLine($"                        [\"FunctionResult\"] = @\"{escapedFuncResult}\"");
        }
        else if (!string.IsNullOrEmpty(Toolkit.FunctionResultExpression))
        {
            // Don't store expression in metadata (it's already executed in return statement)
            sb.AppendLine($"                        // FunctionResult is dynamic: {Toolkit.FunctionResultExpression}");
        }
        else
        {
            // Remove trailing comma from FunctionCount if no context properties
            // This is handled by checking if we added anything after FunctionCount
        }

        sb.AppendLine("                    }");
        sb.AppendLine("                });");
        sb.AppendLine("        }");

        return sb.ToString();
    }

    /// <summary>
    /// Generates the CreateEmptyContainerSchema() helper method.
    /// </summary>
    private static string GenerateEmptySchemaMethod()
    {
        var sb = new StringBuilder();

        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Creates an empty JSON schema for container functions (no parameters).");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        private static JsonElement CreateEmptyContainerSchema()");
        sb.AppendLine("        {");
        sb.AppendLine("            var options = new global::Microsoft.Extensions.AI.AIJsonSchemaCreateOptions { IncludeSchemaKeyword = false };");
        sb.AppendLine("            return global::Microsoft.Extensions.AI.AIJsonUtilities.CreateJsonSchema(");
        sb.AppendLine("                null,");
        sb.AppendLine("                serializerOptions: HPDJsonContext.Default.Options,");
        sb.AppendLine("                inferenceOptions: options");
        sb.AppendLine("            );");
        sb.AppendLine("        }");

        return sb.ToString();
    }

    /// <summary>
    /// Resolves SkillCapability references recursively (Phase 5 migration).
    /// Populates ResolvedFunctionReferences and ResolvedToolkitTypes from UnresolvedReferences.
    /// </summary>
    private static void ResolveSkillCapabilities(List<HPD.Agent.SourceGenerator.Capabilities.SkillCapability> skills)
    {
        // Build lookup dictionary: FullName -> SkillCapability
        var skillLookup = skills.ToDictionary(s => s.FullQualifiedName);

        // Resolve each skill
        var visited = new HashSet<string>();
        var stack = new Stack<string>();

        foreach (var skill in skills)
        {
            ResolveSkillCapability(skill, skillLookup, visited, stack);
        }
    }

    /// <summary>
    /// Recursively resolves a single SkillCapability, handling nested skills and circular dependencies.
    /// </summary>
    private static void ResolveSkillCapability(
        HPD.Agent.SourceGenerator.Capabilities.SkillCapability skill,
        Dictionary<string, HPD.Agent.SourceGenerator.Capabilities.SkillCapability> skillLookup,
        HashSet<string> visited,
        Stack<string> stack,
        int maxDepth = 50)
    {
        // Already resolved
        if (visited.Contains(skill.FullQualifiedName))
            return;

        // Circular reference detected
        if (stack.Contains(skill.FullQualifiedName))
            return;

        // Depth limit exceeded
        if (stack.Count >= maxDepth)
            return;

        stack.Push(skill.FullQualifiedName);
        visited.Add(skill.FullQualifiedName);

        var functionRefs = new List<string>();
        var toolTypes = new HashSet<string>();

        foreach (var reference in skill.UnresolvedReferences)
        {
            if (reference.ReferenceType == HPD.Agent.SourceGenerator.Capabilities.ReferenceType.Skill)
            {
                // It's a skill reference - resolve it recursively
                var referencedSkillName = $"{reference.ToolkitType}.{reference.MethodName}";
                if (skillLookup.TryGetValue(referencedSkillName, out var referencedSkill))
                {
                    // Recursively resolve the referenced skill first
                    ResolveSkillCapability(referencedSkill, skillLookup, visited, stack, maxDepth);

                    // Add all its function references to our list
                    functionRefs.AddRange(referencedSkill.ResolvedFunctionReferences);
                    foreach (var pt in referencedSkill.ResolvedToolkitTypes)
                    {
                        toolTypes.Add(pt);
                    }
                }
            }
            else
            {
                // It's a function reference - add directly
                functionRefs.Add(reference.FullName);
                toolTypes.Add(reference.ToolkitType);
            }
        }

        // Update the skill with resolved references
        skill.ResolvedFunctionReferences = functionRefs.Distinct().OrderBy(f => f).ToList();
        skill.ResolvedToolkitTypes = toolTypes.OrderBy(p => p).ToList();

        stack.Pop();
    }

}

/// <summary>
/// Token types for expression parsing.
/// </summary>
internal enum TokenType
{
    Property,
    Comparison,
    Logical,
    Value
}

/// <summary>
/// Represents a token in a conditional expression.
/// </summary>
internal class ExpressionToken
{
    public TokenType Type { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

