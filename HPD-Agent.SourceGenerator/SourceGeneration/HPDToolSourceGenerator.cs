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
/// Source generator for HPD-Agent AI plugins. Generates AOT-compatible plugin registration code.
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
        var toolClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => IsToolClass(node, ct),
                transform: static (ctx, ct) => GetToolDeclaration(ctx, ct))
            .Where(static plugin => plugin is not null)
            .Collect();

        context.RegisterSourceOutput(toolClasses, GenerateToolRegistrations);
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

            // A plugin class has methods with any of these attributes
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
    
    private static ToolInfo? GetToolDeclaration(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        // Get class info (needed for capability analysis)
        var className = classDecl.Identifier.ValueText;
        var namespaceName = GetNamespace(classDecl);

        // PHASE 5: Unified analysis for ALL capability types (Functions, Skills, SubAgents)
        // Use CapabilityAnalyzer to discover all capabilities

        var capabilities = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Select(method => HPD.Agent.SourceGenerator.Capabilities.CapabilityAnalyzer.AnalyzeMethod(
                method, semanticModel, context, className, namespaceName))
            .Where(cap => cap != null)
            .ToList();

        // Must have at least one capability
        if (!capabilities.Any())
            return null;

        // Check for [Collapse] attribute and validate dual-context configuration
        var (hasCollapseAttribute, CollapseDescription, FunctionResult, FunctionResultExpression, FunctionResultIsStatic, SystemPrompt, SystemPromptExpression, SystemPromptIsStatic, diagnostics) = GetCollapseAttribute(classDecl, semanticModel);

        // Diagnostics will be stored in ToolInfo and reported in GenerateToolRegistrations

        // Check if the class has a parameterless constructor (either explicit or implicit)
        var hasParameterlessConstructor = HasParameterlessConstructor(classDecl);

        // Check if the class is publicly accessible (for ToolRegistry.All inclusion)
        // A class is publicly accessible if it's public and not nested inside a non-public class
        var isPubliclyAccessible = IsClassPubliclyAccessible(classDecl);

        // Build description from capabilities
        var functionCount = capabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.FunctionCapability>().Count();
        var skillCount = capabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.SkillCapability>().Count();
        var subAgentCount = capabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.SubAgentCapability>().Count();
        var description = BuildPluginDescription(functionCount, skillCount, subAgentCount);

        return new ToolInfo
        {
            Name = classDecl.Identifier.ValueText,
            Description = description,
            Namespace = namespaceName,

            // PHASE 5: Unified Capabilities list (all capability types)
            Capabilities = capabilities!,

            HasCollapseAttribute = hasCollapseAttribute,
            CollapseDescription = CollapseDescription,
            FunctionResult = FunctionResult,
            FunctionResultExpression = FunctionResultExpression,
            FunctionResultIsStatic = FunctionResultIsStatic,
                    SystemPrompt = SystemPrompt,
                    SystemPromptExpression = SystemPromptExpression,
            SystemPromptIsStatic = SystemPromptIsStatic,
            HasParameterlessConstructor = hasParameterlessConstructor,

            // Diagnostics from dual-context validation
            Diagnostics = diagnostics,
            IsPubliclyAccessible = isPubliclyAccessible
        };
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

    private static string BuildPluginDescription(int functionCount, int skillCount, int subAgentCount)
    {
        var parts = new List<string>();
        if (functionCount > 0) parts.Add($"{functionCount} AI functions");
        if (skillCount > 0) parts.Add($"{skillCount} skills");
        if (subAgentCount > 0) parts.Add($"{subAgentCount} sub-agents");

        if (parts.Count == 0)
            return "Empty plugin container.";
        else if (parts.Count == 1)
            return $"Plugin containing {parts[0]}.";
        else if (parts.Count == 2)
            return $"Plugin containing {parts[0]} and {parts[1]}.";
        else
            return $"Plugin containing {parts[0]}, {parts[1]}, and {parts[2]}.";
    }
    
    private static void GenerateToolRegistrations(SourceProductionContext context, ImmutableArray<ToolInfo?> plugins)
    {
        // Group plugins by name+namespace to handle partial classes FIRST
        // This prevents duplicate generation by merging partial classes before validation
        var pluginGroups = plugins
            .Where(p => p != null)
            .GroupBy(p => $"{p!.Namespace}.{p.Name}")
            .Select(group =>
            {
                // Merge all partial class parts into one plugin
                var first = group.First()!;

                // PHASE 5: Merge unified Capabilities list (all capability types)
                var allCapabilities = group.SelectMany(p => p!.Capabilities).ToList();

                // Count capabilities by type for description
                var functionCount = allCapabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.FunctionCapability>().Count();
                var skillCount = allCapabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.SkillCapability>().Count();
                var subAgentCount = allCapabilities.OfType<HPD.Agent.SourceGenerator.Capabilities.SubAgentCapability>().Count();

                // Preserve HasCollapseAttribute and CollapseDescription from any partial class that has it
                var hasCollapseAttribute = group.Any(p => p!.HasCollapseAttribute);
                var CollapseDescription = group.FirstOrDefault(p => p!.HasCollapseAttribute)?.CollapseDescription;

                // All partial class parts must have parameterless constructor for the plugin to be AOT-instantiable
                // (If any part declares a constructor with parameters, no implicit parameterless constructor is generated)
                var hasParameterlessConstructor = group.All(p => p!.HasParameterlessConstructor);

                // All partial class parts must be publicly accessible for the plugin to be in the registry
                var isPubliclyAccessible = group.All(p => p!.IsPubliclyAccessible);

                // Merge diagnostics from all partial class parts
                var allDiagnostics = group.SelectMany(p => p!.Diagnostics).ToList();

                return new ToolInfo
                {
                    Name = first.Name,
                    Description = BuildPluginDescription(functionCount, skillCount, subAgentCount),
                    Namespace = first.Namespace,

                    // PHASE 5: Unified Capabilities list (all capability types)
                    Capabilities = allCapabilities,
                    HasCollapseAttribute = hasCollapseAttribute,
                    CollapseDescription = CollapseDescription,
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
                    Diagnostics = allDiagnostics
                };
            })
            .ToList();

        // Report diagnostics for all plugins
        foreach (var plugin in pluginGroups)
        {
            foreach (var diagnostic in plugin.Diagnostics)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        // DIAGNOSTIC: Generate detailed diagnostic report AFTER grouping
        var reportLines = string.Join("\\n", _diagnosticMessages.Select(m => m.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "")));
        var diagnosticCode = $@"
// HPD Source Generator Diagnostic Report
// Generated at: {DateTime.Now}
// Plugins found: {plugins.Length} raw, {pluginGroups.Count} after merging
namespace HPD.Agent.Diagnostics {{
    public static class SourceGeneratorDiagnostic {{
        public const string Message = ""Source generator executed successfully"";
        public const int PluginsFound = {pluginGroups.Count};
        public const string DetailedReport = @""{reportLines}"";
    }}
}}";
        context.AddSource("HPD.Agent.Diagnostics.SourceGeneratorDiagnostic.g.cs", diagnosticCode);

        // Clear for next compilation
        _diagnosticMessages.Clear();

        var debugInfo = new StringBuilder();
        debugInfo.AppendLine($"// Found {plugins.Length} plugin parts total");
        debugInfo.AppendLine($"// Merged into {pluginGroups.Count} unique plugins");
        foreach (var plugin in pluginGroups)
        {
            debugInfo.AppendLine($"// Plugin: {plugin.Namespace}.{plugin.Name} with {plugin.FunctionCapabilities.Count()} functions, {plugin.SkillCapabilities.Count()} skills, and {plugin.SubAgentCapabilities.Count()} sub-agents");
        }
        context.AddSource("HPD.Agent.Generated.SourceGeneratorDebug.g.cs", debugInfo.ToString());

        // Resolve skill references before validation and code generation
        // PHASE 5: Use unified SkillCapabilities from Capabilities list
        var allSkillCapabilities = pluginGroups
            .SelectMany(p => p.SkillCapabilities)
            .ToList();
        if (allSkillCapabilities.Any())
        {
            ResolveSkillCapabilities(allSkillCapabilities);
        }

        foreach (var plugin in pluginGroups)
        {
            if (plugin == null) continue;

            foreach (var function in plugin.FunctionCapabilities)
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

            var source = GeneratePluginRegistration(plugin);
            // Use fully qualified name as hint to prevent duplicates
            var hintName = string.IsNullOrEmpty(plugin.Namespace)
                ? $"{plugin.Name}Registration.g.cs"
                : $"{plugin.Namespace}.{plugin.Name}Registration.g.cs";
            context.AddSource(hintName, source);
        }

        // NEW: Generate plugin registry catalog for AOT-compatible plugin discovery
        if (pluginGroups.Any())
        {
            var registrySource = GenerateToolRegistry(pluginGroups);
            context.AddSource("HPD.Agent.Generated.ToolRegistry.g.cs", registrySource);
        }
    }

    /// <summary>
    /// Generates the ToolRegistry.All array that serves as a catalog of all plugins in the assembly.
    /// This eliminates reflection in hot paths by providing direct delegate references.
    /// Only plugins with parameterless constructors and public accessibility are included.
    /// </summary>
    private static string GenerateToolRegistry(List<ToolInfo> plugins)
    {
        // Filter to only include plugins that can be instantiated via the registry:
        // 1. Must have parameterless constructor (plugins requiring DI use reflection fallback)
        // 2. Must be publicly accessible (private/internal test classes are excluded)
        var instantiablePlugins = plugins
            .Where(p => p.HasParameterlessConstructor && p.IsPubliclyAccessible)
            .OrderBy(p => p.Name)
            .ToList();

        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#pragma warning disable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using Microsoft.Extensions.AI;");
        sb.AppendLine("using HPD.Agent;  // For ToolFactory and IToolMetadata types");
        sb.AppendLine();
        sb.AppendLine("namespace HPD.Agent.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// AOT-compatible catalog of all plugins in this assembly.");
        sb.AppendLine("    /// Generated by HPDToolSourceGenerator.");
        sb.AppendLine("    /// Provides direct delegate references eliminating reflection in hot paths.");
        sb.AppendLine($"    /// Contains {instantiablePlugins.Count} plugins (plugins requiring DI are excluded).");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    [System.CodeDom.Compiler.GeneratedCodeAttribute(\"HPDToolSourceGenerator\", \"1.0.0.0\")]");
        sb.AppendLine("    public static class ToolRegistry");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Catalog of all plugins in this assembly that have parameterless constructors.");
        sb.AppendLine("        /// AgentBuilder automatically discovers and uses this at construction time.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static readonly ToolFactory[] All = new ToolFactory[]");
        sb.AppendLine("        {");

        foreach (var plugin in instantiablePlugins)
        {
            var ns = string.IsNullOrEmpty(plugin.Namespace) ? "" : $"{plugin.Namespace}.";
            var fullTypeName = $"{ns}{plugin.Name}";

            sb.AppendLine($"            new ToolFactory(");
            sb.AppendLine($"                \"{plugin.Name}\",");
            sb.AppendLine($"                typeof({fullTypeName}),");
            sb.AppendLine($"                () => new {fullTypeName}(),  // Direct instantiation (AOT-safe)");

            // Handle skill-only containers (no instance parameter)
            if (!plugin.RequiresInstance)
            {
                sb.AppendLine($"                (_, ctx) => {plugin.Name}Registration.CreatePlugin(ctx),");
            }
            else
            {
                sb.AppendLine($"                (instance, ctx) => {plugin.Name}Registration.CreatePlugin(({fullTypeName})instance, ctx),");
            }

            // Add GetReferencedPlugins if plugin has skills
            if (plugin.SkillCapabilities.Any())
            {
                sb.AppendLine($"                {plugin.Name}Registration.GetReferencedPlugins,");
                sb.AppendLine($"                {plugin.Name}Registration.GetReferencedFunctions");
            }
            else
            {
                sb.AppendLine($"                () => Array.Empty<string>(),");
                sb.AppendLine($"                () => new Dictionary<string, string[]>()");
            }

            sb.AppendLine($"            ),");
        }

        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Generates the CreatePlugin method using unified polymorphic ICapability iteration.
    /// Phase 4: Now the single unified generation path (old path removed).
    /// </summary>
    private static string GenerateCreatePluginMethod(ToolInfo plugin)
    {
        var sb = new StringBuilder();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine($"    /// Creates an AIFunction list for the {plugin.Name} plugin.");
        sb.AppendLine("    /// </summary>");

        // Only include instance parameter if plugin has capabilities that need it
        if (!plugin.RequiresInstance)
        {
            sb.AppendLine($"    /// <param name=\"context\">The execution context (optional)</param>");
            sb.AppendLine($"    public static List<AIFunction> CreatePlugin(IToolMetadata? context = null)");
        }
        else
        {
            sb.AppendLine($"    /// <param name=\"instance\">The plugin instance</param>");
            sb.AppendLine($"    /// <param name=\"context\">The execution context (optional)</param>");
            sb.AppendLine($"    public static List<AIFunction> CreatePlugin({plugin.Name} instance, IToolMetadata? context = null)");
        }

        sb.AppendLine("    {");
        sb.AppendLine("        var functions = new List<AIFunction>();");
        sb.AppendLine();

        // Add collapse container registration if needed (BEFORE individual capabilities)
        var skillRegistrations = SkillCodeGenerator.GenerateSkillRegistrations(plugin);
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
        var nonSkillCapabilities = plugin.Capabilities.Where(c => c.Type != CapabilityType.Skill);

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
                    sb.AppendLine($"            functions.Add({capability.GenerateRegistrationCode(plugin)});");
                    sb.AppendLine("        }");
                }
                else
                {
                    sb.AppendLine($"        functions.Add({capability.GenerateRegistrationCode(plugin)});");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("        return functions;");
        sb.AppendLine("    }");
        return sb.ToString();
    }


    private static string GeneratePluginRegistration(ToolInfo plugin)
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

        // Add using directive for the plugin's namespace if it's not empty
        if (!string.IsNullOrEmpty(plugin.Namespace))
        {
            sb.AppendLine($"using {plugin.Namespace};");
        }

        sb.AppendLine();

        sb.AppendLine(GenerateArgumentsDtoAndContext(plugin));

        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// Generated registration code for {plugin.Name} plugin.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"[System.CodeDom.Compiler.GeneratedCodeAttribute(\"HPDToolSourceGenerator\", \"1.0.0.0\")]");
        sb.AppendLine($"public static partial class {plugin.Name}Registration");
        sb.AppendLine("    {");

        // Generate GetReferencedPlugins() and GetReferencedFunctions() if there are skills
        // PHASE 5: Use SkillCapabilities (fully populated with resolved references)
        if (plugin.SkillCapabilities.Any())
        {
            sb.AppendLine(SkillCodeGenerator.GenerateGetReferencedPluginsMethod(plugin));
            sb.AppendLine();
            sb.AppendLine(SkillCodeGenerator.GenerateGetReferencedFunctionsMethod(plugin));
            sb.AppendLine();
        }

        // Generate plugin metadata accessor (always generated for consistency)
        // PHASE 5: Use SkillCapabilities instead of Skills
        if (plugin.SkillCapabilities.Any())
        {
            sb.AppendLine(SkillCodeGenerator.UpdateToolMetadataWithSkills(plugin, ""));
        }
        else
        {
            sb.AppendLine(GenerateToolMetadataMethod(plugin));
        }
        sb.AppendLine();

        // Generate container function and helper if plugin is Collapsed OR has skills
        if (plugin.HasCollapseAttribute || plugin.SkillCapabilities.Any())
        {
            if (plugin.HasCollapseAttribute)
            {
                sb.AppendLine(GenerateContainerFunction(plugin));
                sb.AppendLine();
            }
            sb.AppendLine(GenerateEmptySchemaMethod());
            sb.AppendLine();
        }

        sb.AppendLine(GenerateCreatePluginMethod(plugin));

        foreach (var function in plugin.FunctionCapabilities)
        {
            sb.AppendLine();
            sb.AppendLine(GenerateSchemaValidator(function, plugin));
            
            // Generate manual JSON parser for AOT compatibility
            var relevantParams = function.Parameters
                .Where(p => p.Type != "CancellationToken" && p.Type != "AIFunctionArguments" && p.Type != "IServiceProvider").ToList();
            if (relevantParams.Any())
            {
                sb.AppendLine();
                sb.AppendLine(GenerateJsonParser(function, plugin));
            }
        }

        // PHASE 2B: Generate context resolvers for ALL capabilities (Functions, Skills, SubAgents)
        // This enables Skills and SubAgents to use dynamic descriptions and conditionals (feature parity!)
        // Replaces the old DSL-based GenerateContextResolutionMethods() which only worked for Functions
        foreach (var capability in plugin.Capabilities)
        {
            var resolvers = capability.GenerateContextResolvers();
            if (!string.IsNullOrEmpty(resolvers))
            {
                sb.AppendLine();
                sb.AppendLine(resolvers);
            }
        }

        // Generate skill code AND collapse container (if plugin has [Collapse] attribute)
        // NOTE: Collapse container can exist even if there are no skills (e.g., collapsed plugin with only functions)
        if (plugin.SkillCapabilities.Any() || plugin.HasCollapseAttribute)
        {
            sb.AppendLine(SkillCodeGenerator.GenerateAllSkillCode(plugin));
        }

        sb.AppendLine("    }");

        return sb.ToString();
    }

    private static string GenerateArgumentsDtoAndContext(ToolInfo plugin)
    {
        var sb = new StringBuilder();
        var contextSerializableTypes = new List<string>();

        // Generate SubAgentQueryArgs if there are sub-agents (Collapsed per plugin to avoid conflicts)
        if (plugin.SubAgentCapabilities.Any())
        {
            sb.AppendLine(
$@"    /// <summary>
    /// Represents the arguments for sub-agent invocations, generated at compile-time.
    /// </summary>
    [System.CodeDom.Compiler.GeneratedCodeAttribute(""HPDToolSourceGenerator"", ""1.0.0.0"")]
    public class {plugin.Name}SubAgentQueryArgs
    {{
        [System.Text.Json.Serialization.JsonPropertyName(""query"")]
        [System.ComponentModel.Description(""Query for the sub-agent"")]
        public string Query {{ get; set; }} = string.Empty;
    }}
");
        }

        foreach (var function in plugin.FunctionCapabilities)
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

    private static string GenerateSchemaValidator(HPD.Agent.SourceGenerator.Capabilities.FunctionCapability function, ToolInfo plugin)
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
    /// Detects [Collapse] attribute on a class and extracts its description and instruction contexts.
    /// Supports both legacy postExpansionInstructions and new dual-context (FunctionResult,SystemPrompt).
    /// Analyzes expressions to determine if they're static or instance methods/properties.
    /// </summary>
    private static (
        bool hasCollapseAttribute,
        string? collapseDescription,
        string? FunctionResult,
        string? FunctionResultExpression,
        bool FunctionResultIsStatic,
        string? SystemPrompt,
        string? SystemPromptExpression,
        bool SystemPromptIsStatic,
        List<Diagnostic> diagnostics
    ) GetCollapseAttribute(ClassDeclarationSyntax classDecl, SemanticModel semanticModel)
    {
        var collapseAttributes = classDecl.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Where(attr => attr.Name.ToString() == "Collapse");

        foreach (var attr in collapseAttributes)
        {
            var arguments = attr.ArgumentList?.Arguments;
            if (!arguments.HasValue || arguments.Value.Count < 1)
            {
                // Attribute present but no description
                return (true, null, null, null, true, null, null, true, new List<Diagnostic>());
            }

            // First argument is always the description
            var description = ExtractStringLiteral(arguments.Value[0].Expression);

            string? funcResultCtx = null, funcResultExpr = null;
            bool funcResultIsStatic = true;
            string? sysPromptCtx = null, sysPromptExpr = null;
            bool sysPromptIsStatic = true;

            // Parse remaining arguments (positional or named)
            for (int i = 1; i < arguments.Value.Count; i++)
            {
                var arg = arguments.Value[i];
                var argName = arg.NameColon?.Name.Identifier.ValueText;

                // Check if argument is named or positional
                if (argName == "FunctionResult" || (argName == null && i == 1))
                {
                    // FunctionResult (or 2nd positional argument)
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
                else if (argName == "SystemPrompt" || (argName == null && i == 2))
                {
                    // SystemPrompt (or 3rd positional argument)
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
                else if (argName == "postExpansionInstructions")
                {
                    // Legacy parameter - maps to FunctionResult
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
            }

            // PHASE 2C: Validate dual-context configuration before returning
            var diagnostics = ValidateDualContextConfiguration(
                funcResultCtx, funcResultExpr,
                sysPromptCtx, sysPromptExpr,
                classDecl, semanticModel);

            return (true, description, funcResultCtx, funcResultExpr, funcResultIsStatic, sysPromptCtx, sysPromptExpr, sysPromptIsStatic, diagnostics);
        }

        return (false, null, null, null, true, null, null, true, new List<Diagnostic>());
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
                    "Plugin '{0}' specifies both FunctionResult literal and expression. Use one or the other, not both.",
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
                    "Plugin '{0}' specifies bothSystemPrompt literal and expression. Use one or the other, not both.",
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
                    "Plugin '{0}' uses a string literal for {1} expression. Use the literal parameter instead, or provide a method/property reference.",
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
    private static string GenerateJsonParser(HPD.Agent.SourceGenerator.Capabilities.FunctionCapability function, ToolInfo plugin)
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
    /// Generates the GetToolMetadata() method for plugin Collapsing support.
    /// </summary>
    private static string GenerateToolMetadataMethod(ToolInfo plugin)
    {
        var sb = new StringBuilder();

        var functionNamesArray = string.Join(", ", plugin.FunctionCapabilities.Select(f => $"\"{f.FunctionName}\""));
        var description = plugin.HasCollapseAttribute && !string.IsNullOrEmpty(plugin.CollapseDescription)
            ? plugin.CollapseDescription
            : plugin.Description;

        sb.AppendLine("        private static ToolMetadata? _cachedMetadata;");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Gets metadata for the {plugin.Name} plugin (used for Collapsing).");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static ToolMetadata GetToolMetadata()");
        sb.AppendLine("        {");
        sb.AppendLine("            return _cachedMetadata ??= new ToolMetadata");
        sb.AppendLine("            {");
        sb.AppendLine($"                Name = \"{plugin.Name}\",");
        sb.AppendLine($"                Description = \"{description}\",");
        sb.AppendLine($"                FunctionNames = new string[] {{ {functionNamesArray} }},");
        sb.AppendLine($"                FunctionCount = {plugin.FunctionCapabilities.Count()},");
        sb.AppendLine($"                HasCollapseAttribute = {plugin.HasCollapseAttribute.ToString().ToLower()}");
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        return sb.ToString();
    }

    /// <summary>
    /// Generates the container function for a Collapsed plugin.
    /// </summary>
    private static string GenerateContainerFunction(ToolInfo plugin)
    {
        var sb = new StringBuilder();

        // Combine both AI functions and skills
        var allCapabilities = plugin.FunctionCapabilities.Select(f => f.FunctionName)
            .Concat(plugin.SkillCapabilities.Select(s => s.Name))
            .ToList();
        var capabilitiesList = string.Join(", ", allCapabilities);
        var totalCount = plugin.FunctionCapabilities.Count() + plugin.SkillCapabilities.Count();

        var description = !string.IsNullOrEmpty(plugin.CollapseDescription)
            ? plugin.CollapseDescription
            : plugin.Description ?? string.Empty;

        // Use shared helper to generate description and return message
        var fullDescription = CollapseContainerHelper.GenerateContainerDescription(description, plugin.Name, allCapabilities);

        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Container function for {plugin.Name} plugin Collapsing.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine($"        private static AIFunction Create{plugin.Name}Container({plugin.Name} instance)");
        sb.AppendLine("        {");
        sb.AppendLine("            return HPDAIFunctionFactory.Create(");
        sb.AppendLine("                async (arguments, cancellationToken) =>");
        sb.AppendLine("                {");

        // Use the CollapseDescription (or plugin description as fallback) in the return message
        var returnMessage = CollapseContainerHelper.GenerateReturnMessage(description, allCapabilities, plugin.FunctionResult);

        if (!string.IsNullOrEmpty(plugin.FunctionResultExpression))
        {
            // Using an interpolated string to combine the base message and the dynamic instructions
            var baseMessage = CollapseContainerHelper.GenerateReturnMessage(description, allCapabilities, null);
            // Escape special characters for the interpolated string - we need to convert \n\n to \\n\\n in source code
            baseMessage = baseMessage.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\"", "\\\"");
            // Add separator between capabilities list and dynamic instructions
            var separator = "\\n\\n";  // This will be two backslash-n sequences in the source code

            // Use instance. prefix for instance methods, nothing for static
            var expressionCall = plugin.FunctionResultIsStatic
                ? plugin.FunctionResultExpression
                : $"instance.{plugin.FunctionResultExpression}";

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
        sb.AppendLine($"                    Name = \"{plugin.Name}\",");
        sb.AppendLine($"                    Description = \"{fullDescription}\",");
        sb.AppendLine("                    SchemaProvider = () => CreateEmptyContainerSchema(),");
        sb.AppendLine("                    AdditionalProperties = new Dictionary<string, object>");
        sb.AppendLine("                    {");
        sb.AppendLine("                        [\"IsContainer\"] = true,");
        sb.AppendLine($"                        [\"PluginName\"] = \"{plugin.Name}\",");
        sb.AppendLine($"                        [\"FunctionNames\"] = new string[] {{ {string.Join(", ", allCapabilities.Select(c => $"\"{c}\""))} }},");
        sb.AppendLine($"                        [\"FunctionCount\"] = {totalCount},");

        // AddSystemPrompt to metadata (for middleware injection)
        if (!string.IsNullOrEmpty(plugin.SystemPrompt))
        {
            // Use verbatim string literal - only escape quotes (double them), NOT newlines
            var escapedSysPrompt = plugin.SystemPrompt.Replace("\"", "\"\"");
            sb.AppendLine($"                        [\"SystemPrompt\"] = @\"{escapedSysPrompt}\",");
        }
        else if (!string.IsNullOrEmpty(plugin.SystemPromptExpression))
        {
            // Expression - evaluate at container creation time
            // Use instance. prefix for instance methods, nothing for static
            var expressionCall = plugin.SystemPromptIsStatic
                ? plugin.SystemPromptExpression
                : $"instance.{plugin.SystemPromptExpression}";

            sb.AppendLine($"                        [\"SystemPrompt\"] = {expressionCall},");
        }

        // Optionally store FunctionResult for introspection
        if (!string.IsNullOrEmpty(plugin.FunctionResult))
        {
            // Use verbatim string literal - only escape quotes (double them), NOT newlines
            var escapedFuncResult = plugin.FunctionResult.Replace("\"", "\"\"");
            sb.AppendLine($"                        [\"FunctionResult\"] = @\"{escapedFuncResult}\"");
        }
        else if (!string.IsNullOrEmpty(plugin.FunctionResultExpression))
        {
            // Don't store expression in metadata (it's already executed in return statement)
            sb.AppendLine($"                        // FunctionResult is dynamic: {plugin.FunctionResultExpression}");
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
    /// Populates ResolvedFunctionReferences and ResolvedPluginTypes from UnresolvedReferences.
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
                var referencedSkillName = $"{reference.PluginType}.{reference.MethodName}";
                if (skillLookup.TryGetValue(referencedSkillName, out var referencedSkill))
                {
                    // Recursively resolve the referenced skill first
                    ResolveSkillCapability(referencedSkill, skillLookup, visited, stack, maxDepth);

                    // Add all its function references to our list
                    functionRefs.AddRange(referencedSkill.ResolvedFunctionReferences);
                    foreach (var pt in referencedSkill.ResolvedPluginTypes)
                    {
                        toolTypes.Add(pt);
                    }
                }
            }
            else
            {
                // It's a function reference - add directly
                functionRefs.Add(reference.FullName);
                toolTypes.Add(reference.PluginType);
            }
        }

        // Update the skill with resolved references
        skill.ResolvedFunctionReferences = functionRefs.Distinct().OrderBy(f => f).ToList();
        skill.ResolvedPluginTypes = toolTypes.OrderBy(p => p).ToList();

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

