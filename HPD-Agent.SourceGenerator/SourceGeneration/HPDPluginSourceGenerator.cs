using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

/// <summary>
/// Source generator for HPD-Agent AI plugins. Generates AOT-compatible plugin registration code.
/// </summary>
[Generator]
public class HPDPluginSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all classes with [AIPlugin] attribute
        var pluginClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => IsPluginClass(node, ct),
                transform: static (ctx, ct) => GetPluginDeclaration(ctx, ct))
            .Where(static plugin => plugin is not null)
            .Collect();
        
        // Generate registration code for each plugin
        context.RegisterSourceOutput(pluginClasses, GeneratePluginRegistrations);
    }
    
    /// <summary>
    /// Determines if a syntax node represents a plugin class.
    /// </summary>
    private static bool IsPluginClass(SyntaxNode node, CancellationToken cancellationToken = default)
    {
        return node is ClassDeclarationSyntax classDecl &&
               classDecl.Modifiers.Any(SyntaxKind.PublicKeyword) &&
               classDecl.Members.OfType<MethodDeclarationSyntax>()
                   .Any(method => method.AttributeLists
                       .SelectMany(attrList => attrList.Attributes)
                       .Any(attr => attr.Name.ToString().Contains("AIFunction")));
    }
    
    /// <summary>
    /// Extracts plugin information from a class declaration.
    /// </summary>
    private static PluginInfo? GetPluginDeclaration(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;
        
        // Find all methods with [AIFunction] attribute
        var functions = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(method => HasAIFunctionAttribute(method, semanticModel))
            .Select(method => AnalyzeFunction(method, semanticModel, context))
            .Where(func => func != null)
            .ToList();
        
        // Skip classes with no AI functions
        if (!functions.Any()) return null;
        
        // Get namespace
        var namespaceName = GetNamespace(classDecl);
        
        return new PluginInfo
        {
            Name = classDecl.Identifier.ValueText,
            PluginName = classDecl.Identifier.ValueText, // Use class name as plugin name
            Description = $"Plugin containing {functions.Count} AI functions.", // Auto-generated description
            Namespace = namespaceName,
            Functions = functions!
        };
    }
    
    /// <summary>
    /// Generates plugin registration code for all discovered plugins.
    /// </summary>
    private static void GeneratePluginRegistrations(SourceProductionContext context, ImmutableArray<PluginInfo?> plugins)
    {
        // Always generate a test file to confirm the generator is running
        context.AddSource("_SourceGeneratorTest.g.cs", "// Source generator is running!");
        
        // Add debug info about what we found
        var debugInfo = $"// Found {plugins.Length} plugins total\n";
        debugInfo += $"// Non-null plugins: {plugins.Count(p => p != null)}\n";
        for (int i = 0; i < plugins.Length; i++)
        {
            var plugin = plugins[i];
            if (plugin != null)
            {
                debugInfo += $"// Plugin {i}: {plugin.Name} with {plugin.Functions.Count} functions\n";
            }
            else
            {
                debugInfo += $"// Plugin {i}: null\n";
            }
        }
        context.AddSource("_SourceGeneratorDebug.g.cs", debugInfo);
        
        foreach (var plugin in plugins)
        {
            if (plugin != null)
            {
                // ✅ Execute validation for all functions with validation data
                foreach (var function in plugin.Functions)
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
                                
                                // Validate conditional parameters
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
                context.AddSource($"{plugin.Name}Registration.g.cs", source);
            }
        }
    }
    
    /// <summary>
    /// Generates the CreatePlugin method with context support.
    /// </summary>
    private static string GenerateCreatePluginMethod(PluginInfo plugin)
    {
        var unconditionalFunctions = plugin.Functions.Where(f => !f.IsConditional).ToList();
        var conditionalFunctions = plugin.Functions.Where(f => f.IsConditional).ToList();
        
        var sb = new StringBuilder();
        sb.AppendLine($"    /// <summary>");
        sb.AppendLine($"    /// Creates an AIFunction list for the {plugin.Name} plugin.");
        sb.AppendLine($"    /// </summary>");
        sb.AppendLine($"    /// <param name=\"instance\">The plugin instance</param>");
        sb.AppendLine($"    /// <param name=\"context\">The execution context (optional)</param>");
        sb.AppendLine($"    public static List<AIFunction> CreatePlugin({plugin.Name} instance, IPluginMetadataContext? context = null)");
        sb.AppendLine($"    {{");
        sb.AppendLine($"        var functions = new List<AIFunction>();");
        sb.AppendLine();
        
        // Always included functions
        if (unconditionalFunctions.Any())
        {
            sb.AppendLine($"        // Always included functions");
            foreach (var function in unconditionalFunctions)
            {
                var functionCode = GenerateFunctionRegistrationWithParameterFiltering(function);
                sb.AppendLine($"        functions.Add({functionCode});");
            }
            sb.AppendLine();
        }
        
        // Conditionally included functions
        if (conditionalFunctions.Any())
        {
            sb.AppendLine($"        // Conditionally included functions");
            foreach (var function in conditionalFunctions)
            {
                sb.AppendLine($"        if (Evaluate{function.Name}Condition(context))");
                sb.AppendLine($"        {{");
                var functionCode = GenerateFunctionRegistrationWithParameterFiltering(function);
                sb.AppendLine($"            functions.Add({functionCode});");
                sb.AppendLine($"        }}");
                sb.AppendLine();
            }
        }
        
        sb.AppendLine($"        return functions;");
        sb.AppendLine($"    }}");
        return sb.ToString();
    }

    /// <summary>
    /// Generates the registration code for a single plugin.
    /// </summary>
    private static string GeneratePluginRegistration(PluginInfo plugin)
    {
        var sb = new StringBuilder();
        
        // File header
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// This code was generated by HPDPluginSourceGenerator");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();
        // disable null and type conflict warnings in generated code
        sb.AppendLine("#pragma warning disable CS8601");
        sb.AppendLine("#pragma warning disable CS0436");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Extensions.AI;");
        sb.AppendLine();
        
        // Namespace
        if (!string.IsNullOrEmpty(plugin.Namespace))
        {
            sb.AppendLine($"namespace {plugin.Namespace};");
            sb.AppendLine();
        }
        
        // Registration class
        sb.AppendLine($"/// <summary>");
        sb.AppendLine($"/// Generated registration code for {plugin.Name} plugin with generic context support.");
        sb.AppendLine($"/// </summary>");
        sb.AppendLine($"public static partial class {plugin.Name}Registration");
        sb.AppendLine("{");
        
        // CreatePlugin method
        sb.AppendLine(GenerateCreatePluginMethod(plugin));
        
        // Context resolution methods (always generate conditional evaluators if needed)
        bool hasContextMethods = plugin.RequiresContext;
        bool hasConditionalEvaluators = plugin.Functions.Any(f => f.IsConditional);
        if (hasContextMethods || hasConditionalEvaluators)
        {
            sb.AppendLine();
            sb.AppendLine(GenerateContextResolutionMethods(plugin));
        }
        
        sb.AppendLine("}");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Generates the registration code for a single function.
    /// </summary>
    /// <summary>
    /// V3.0: Generate function registration with conditional parameter filtering.
    /// </summary>
    private static string GenerateFunctionRegistrationWithParameterFiltering(FunctionInfo function)
    {
        var parameterParts = function.Parameters.Select(p => 
        {
            var paramDecl = $"{p.Type} {p.Name}";
            if (p.HasDefaultValue && !string.IsNullOrEmpty(p.DefaultValue))
            {
                paramDecl += $" = {p.DefaultValue}";
            }
            return paramDecl;
        });
        var parameterList = string.Join(", ", parameterParts);
        var argumentList = string.Join(", ", function.Parameters.Select(p => p.Name));
        
        var nameCode = $"\"{function.FunctionName}\"";
        var descriptionCode = function.HasDynamicDescription 
            ? $"Resolve{function.Name}Description(context)"
            : $"\"{function.Description}\"";

        // Generate conditional parameter filtering
        string optionsCode;
        if (function.HasConditionalParameters)
        {
            optionsCode = GenerateOptionsWithConditionalParameters(function);
        }
        else
        {
            optionsCode = GenerateStaticOptions(function, nameCode, descriptionCode);
        }
        
        if (function.IsAsync)
        {
            return $$"""
                HPDAIFunctionFactory.Create(
                            async ({{parameterList}}) => await instance.{{function.Name}}({{argumentList}}),
                            {{optionsCode}}
                        )
                """;
        }
        else
        {
            return $$"""
                HPDAIFunctionFactory.Create(
                            ({{parameterList}}) => instance.{{function.Name}}({{argumentList}}),
                            {{optionsCode}}
                        )
                """;
        }
    }

    /// <summary>
    /// V3.0: Generate options with conditional parameter filtering logic.
    /// </summary>
    /// <summary>
    /// BETTER: Generate inline conditional checks for each parameter.
    /// </summary>
    private static string GenerateOptionsWithConditionalParameters(FunctionInfo function)
    {
        var nameCode = $"\"{function.FunctionName}\"";
        var descriptionCode = function.HasDynamicDescription 
            ? $"Resolve{function.Name}Description(context)"
            : $"\"{function.Description}\"";
        
        var sb = new StringBuilder();
        sb.AppendLine("new HPDAIFunctionFactoryOptions");
        sb.AppendLine("                        {");
        sb.AppendLine($"                            Name = {nameCode},");
        sb.AppendLine($"                            Description = {descriptionCode},");
        sb.AppendLine("                            ParameterDescriptions = CreateParameterDescriptions(context)");
        sb.AppendLine("                        }");
        
        // Generate local helper method for this specific function
        sb.AppendLine();
        sb.AppendLine("                        Dictionary<string, string> CreateParameterDescriptions(IPluginMetadataContext? ctx)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            var result = new Dictionary<string, string>();");
        
        var parametersWithDescriptions = function.Parameters.Where(p => !string.IsNullOrEmpty(p.Description)).ToList();
        foreach (var param in parametersWithDescriptions)
        {
            var paramDescCode = param.HasDynamicDescription 
                ? $"Resolve{function.Name}Parameter{param.Name}Description(ctx)"
                : $"\"{param.Description}\"";
            
            if (param.IsConditional)
            {
                sb.AppendLine($"                            if (Evaluate{function.Name}Parameter{param.Name}Condition(ctx))");
                sb.AppendLine($"                                result[\"{param.Name}\"] = {paramDescCode};");
            }
            else
            {
                sb.AppendLine($"                            result[\"{param.Name}\"] = {paramDescCode};");
            }
        }
        
        sb.AppendLine("                            return result;");
        sb.AppendLine("                        }");
        
        return sb.ToString();
    }

    /// <summary>
    /// V3.0: Generate static options (no conditional parameters).
    /// </summary>
    private static string GenerateStaticOptions(FunctionInfo function, string nameCode, string descriptionCode)
    {
        var hasParameterDescriptions = function.Parameters.Any(p => !string.IsNullOrEmpty(p.Description));
        
        var sb = new StringBuilder();
        sb.AppendLine("new HPDAIFunctionFactoryOptions");
        sb.AppendLine("                            {");
        sb.AppendLine($"                                Name = {nameCode},");
        sb.AppendLine($"                                Description = {descriptionCode}");
        
        if (hasParameterDescriptions)
        {
            sb.AppendLine(",");
            sb.AppendLine("                                ParameterDescriptions = new Dictionary<string, string>");
            sb.AppendLine("                                {");
            var descriptionsWithValues = function.Parameters.Where(p => !string.IsNullOrEmpty(p.Description)).ToList();
            for (int i = 0; i < descriptionsWithValues.Count; i++)
            {
                var param = descriptionsWithValues[i];
                var comma = i < descriptionsWithValues.Count - 1 ? "," : "";
                
                // ✅ FIX: Resolve dynamic parameter descriptions
                var paramDescCode = param.HasDynamicDescription 
                    ? $"Resolve{function.Name}Parameter{param.Name}Description(context)"
                    : $"\"{param.Description}\"";
                sb.AppendLine($"                                    {{ \"{param.Name}\", {paramDescCode} }}{comma}");
            }
            sb.AppendLine("                                }");
        }
        
        sb.AppendLine("                            }");
        return sb.ToString();
    }

    /// <summary>
    /// V3.0: Generate context resolution methods for dynamic descriptions and conditional logic.
    /// </summary>
    private static string GenerateContextResolutionMethods(PluginInfo plugin)
    {
        var sb = new StringBuilder();
        
        // Function-level description resolvers
        foreach (var function in plugin.Functions.Where(f => f.HasDynamicDescription))
        {
            if (sb.Length > 0) sb.AppendLine();
            
            if (!string.IsNullOrEmpty(function.ContextTypeName))
            {
                sb.AppendLine(DSLCodeGenerator.GenerateDescriptionResolver(
                    function.Name,
                    function.Description,
                    function.ContextTypeName!));
            }
        }
        
        // Parameter description resolvers  
        foreach (var function in plugin.Functions)
        {
            foreach (var parameter in function.Parameters.Where(p => p.HasDynamicDescription))
            {
                if (sb.Length > 0) sb.AppendLine();
                
                if (!string.IsNullOrEmpty(function.ContextTypeName))
                {
                    sb.AppendLine(DSLCodeGenerator.GenerateParameterDescriptionResolver(
                        function.Name,
                        parameter.Name,
                        parameter.Description,
                        function.ContextTypeName!));
                }
            }
        }
        
        // Function conditional evaluators
        foreach (var function in plugin.Functions.Where(f => f.IsConditional))
        {
            if (sb.Length > 0) sb.AppendLine();
            
            if (!string.IsNullOrEmpty(function.ConditionalExpression) && !string.IsNullOrEmpty(function.ContextTypeName))
            {
                sb.AppendLine(DSLCodeGenerator.GenerateConditionalEvaluator(
                    function.Name, 
                    function.ConditionalExpression!, 
                    function.ContextTypeName!));
            }
        }
        
        // Parameter conditional evaluators
        foreach (var function in plugin.Functions.Where(f => f.HasConditionalParameters))
        {
            foreach (var parameter in function.Parameters.Where(p => p.IsConditional))
            {
                if (sb.Length > 0) sb.AppendLine();
                
                if (!string.IsNullOrEmpty(parameter.ConditionalExpression) && !string.IsNullOrEmpty(function.ContextTypeName))
                {
                    sb.AppendLine(DSLCodeGenerator.GenerateParameterConditionalEvaluator(
                        function.Name,
                        parameter.Name,
                        parameter.ConditionalExpression!,
                        function.ContextTypeName!));
                }
            }
        }
        
        return sb.ToString();
    }

    // Helper methods for analysis...
    
    private static bool HasAIFunctionAttribute(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        return method.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Any(attr => attr.Name.ToString().Contains("AIFunction"));
    }
    
    private static FunctionInfo? AnalyzeFunction(MethodDeclarationSyntax method, SemanticModel semanticModel, GeneratorSyntaxContext context)
    {
        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword))
            return null;
        
        var symbol = semanticModel.GetDeclaredSymbol(method);
        if (symbol == null) return null;
        
        // Get function attributes
        var customName = GetCustomFunctionName(method);
        var description = GetFunctionDescription(method);
        var permissions = GetRequiredPermissions(method);
        
        // Get context type from AIFunction<TContext> (V3.0)
        var (contextTypeName, isGenericAIFunction) = GetAIFunctionContextType(method, semanticModel);
        
        // Get function metadata
        var conditionalExpression = GetConditionalExpression(method);
        
        var functionInfo = new FunctionInfo
        {
            Name = method.Identifier.ValueText,
            CustomName = customName,
            Description = description,
            Parameters = AnalyzeParameters(method.ParameterList, semanticModel),
            ReturnType = GetReturnType(method, semanticModel),
            IsAsync = IsAsyncMethod(method),
            RequiredPermissions = permissions,
            ContextTypeName = contextTypeName,
            ConditionalExpression = conditionalExpression
        };
        
        // Store validation data for later processing in the main source generator method
        functionInfo.ValidationData = new ValidationData
        {
            Method = method,
            SemanticModel = semanticModel,
            NeedsValidation = !string.IsNullOrEmpty(contextTypeName)
        };
        
        return functionInfo;
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
    
    private static string ExtractStringLiteral(ExpressionSyntax expression)
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
    /// Extracts context type from AIFunction<TContext> attribute.
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
                
                // Check if it's the generic AIFunction<TContext>
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
    private static void ValidateTemplateProperties(SourceProductionContext context, FunctionInfo function, ITypeSymbol contextType, SyntaxNode location)
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
    private static void ValidateFunctionContextUsage(SourceProductionContext context, FunctionInfo function, MethodDeclarationSyntax method)
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
                    $"Function '{function.Name}' uses dynamic features but lacks AIFunction<TContext> attribute. Use [AIFunction<YourContext>] instead of [AIFunction].",
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
