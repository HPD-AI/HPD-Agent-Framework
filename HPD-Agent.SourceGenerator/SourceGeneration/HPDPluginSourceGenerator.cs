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
        var pluginClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => IsPluginClass(node, ct),
                transform: static (ctx, ct) => GetPluginDeclaration(ctx, ct))
            .Where(static plugin => plugin is not null)
            .Collect();
        
        context.RegisterSourceOutput(pluginClasses, GeneratePluginRegistrations);
    }
    
    private static bool IsPluginClass(SyntaxNode node, CancellationToken cancellationToken = default)
    {
        return node is ClassDeclarationSyntax classDecl &&
               classDecl.Modifiers.Any(SyntaxKind.PublicKeyword) &&
               classDecl.Members.OfType<MethodDeclarationSyntax>()
                   .Any(method => method.AttributeLists
                       .SelectMany(attrList => attrList.Attributes)
                       .Any(attr => attr.Name.ToString().Contains("AIFunction")));
    }
    
    private static PluginInfo? GetPluginDeclaration(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;
        
        var functions = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(method => HasAIFunctionAttribute(method, semanticModel))
            .Select(method => AnalyzeFunction(method, semanticModel, context))
            .Where(func => func != null)
            .ToList();
        
        if (!functions.Any()) return null;
        
        var namespaceName = GetNamespace(classDecl);
        
        return new PluginInfo
        {
            Name = classDecl.Identifier.ValueText,
            Description = $"Plugin containing {functions.Count} AI functions.",
            Namespace = namespaceName,
            Functions = functions!
        };
    }
    
    private static void GeneratePluginRegistrations(SourceProductionContext context, ImmutableArray<PluginInfo?> plugins)
    {
        context.AddSource("_SourceGeneratorTest.g.cs", "// Source generator is running!");
        
        var debugInfo = new StringBuilder();
        debugInfo.AppendLine($"// Found {plugins.Length} plugins total");
        debugInfo.AppendLine($"// Non-null plugins: {plugins.Count(p => p != null)}");
        for (int i = 0; i < plugins.Length; i++)
        {
            var plugin = plugins[i];
            if (plugin != null)
            {
                debugInfo.AppendLine($"// Plugin {i}: {plugin.Name} with {plugin.Functions.Count} functions");
            }
            else
            {
                debugInfo.AppendLine($"// Plugin {i}: null");
            }
        }
        context.AddSource("_SourceGeneratorDebug.g.cs", debugInfo.ToString());

        foreach (var plugin in plugins.Where(p => p != null))
        {
            if (plugin == null) continue;

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
    
    private static string GenerateCreatePluginMethod(PluginInfo plugin)
    {
        var unconditionalFunctions = plugin.Functions.Where(f => !f.IsConditional).ToList();
        var conditionalFunctions = plugin.Functions.Where(f => f.IsConditional).ToList();
        
        var sb = new StringBuilder();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine($"    /// Creates an AIFunction list for the {plugin.Name} plugin.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    /// <param name=\"instance\">The plugin instance</param>");
        sb.AppendLine($"    /// <param name=\"context\">The execution context (optional)</param>");
        sb.AppendLine($"    public static List<AIFunction> CreatePlugin({plugin.Name} instance, IPluginMetadataContext? context = null)");
        sb.AppendLine("    {");
        sb.AppendLine("        var functions = new List<AIFunction>();");
        
        if (unconditionalFunctions.Any())
        {
            sb.AppendLine();
            sb.AppendLine("        // Always included functions");
            foreach (var function in unconditionalFunctions)
            {
                sb.AppendLine($"        functions.Add({GenerateFunctionRegistration(function, plugin)});");
            }
        }
        
        if (conditionalFunctions.Any())
        {
            sb.AppendLine();
            sb.AppendLine("        // Conditionally included functions");
            foreach (var function in conditionalFunctions)
            {
                sb.AppendLine($"        if (Evaluate{function.Name}Condition(context))");
                sb.AppendLine("        {");
                sb.AppendLine($"            functions.Add({GenerateFunctionRegistration(function, plugin)});");
                sb.AppendLine("        }");
            }
        }
        
        sb.AppendLine();
        sb.AppendLine("        return functions;");
        sb.AppendLine("    }");
        return sb.ToString();
    }

    private static string GeneratePluginRegistration(PluginInfo plugin)
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
        sb.AppendLine("using Json.Schema;");
        sb.AppendLine("using Json.Schema.Generation;");
        
        if (!string.IsNullOrEmpty(plugin.Namespace))
        {
            sb.AppendLine();
            sb.AppendLine($"namespace {plugin.Namespace}");
            sb.AppendLine("{");
        }

        sb.AppendLine(GenerateArgumentsDtoAndContext(plugin));
        
        sb.AppendLine("    /// <summary>");
        sb.AppendLine($"    /// Generated registration code for {plugin.Name} plugin.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    [System.CodeDom.Compiler.GeneratedCodeAttribute(\"HPDPluginSourceGenerator\", \"1.0.0.0\")]");
        sb.AppendLine($"    public static partial class {plugin.Name}Registration");
        sb.AppendLine("    {");
        
        sb.AppendLine(GenerateCreatePluginMethod(plugin));
        
        foreach (var function in plugin.Functions)
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
        
        if (plugin.RequiresContext || plugin.Functions.Any(f => f.IsConditional))
        {
            sb.AppendLine();
            sb.AppendLine(GenerateContextResolutionMethods(plugin));
        }
        
        sb.AppendLine("    }");
        
        if (!string.IsNullOrEmpty(plugin.Namespace))
        {
            sb.AppendLine("}");
        }
        
        return sb.ToString();
    }

    private static string GenerateArgumentsDtoAndContext(PluginInfo plugin)
    {
        var sb = new StringBuilder();
        var contextSerializableTypes = new List<string>();

        foreach (var function in plugin.Functions)
        {
            if (!function.Parameters.Any(p => p.Type != "CancellationToken" && p.Type != "AIFunctionArguments" && p.Type != "IServiceProvider")) continue;

            var dtoName = $"{function.Name}Args";
            contextSerializableTypes.Add(dtoName);

            sb.AppendLine(
$@"    /// <summary>
    /// Represents the arguments for the {function.Name} function, generated at compile-time.
    /// </summary>
    [System.CodeDom.Compiler.GeneratedCodeAttribute(""HPDPluginSourceGenerator"", ""1.0.0.0"")]
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

    private static string GenerateSchemaValidator(FunctionInfo function, PluginInfo plugin)
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

    private static string GenerateFunctionRegistration(FunctionInfo function, PluginInfo plugin)
    {
        var nameCode = $"\"{function.FunctionName}\"";
        var descriptionCode = function.HasDynamicDescription
            ? $"Resolve{function.Name}Description(context)"
            : $"\"{function.Description}\"";
        
        var relevantParams = function.Parameters
            .Where(p => p.Type != "CancellationToken" && p.Type != "AIFunctionArguments" && p.Type != "IServiceProvider").ToList();
        
        var dtoName = relevantParams.Any() ? $"{function.Name}Args" : "object";
        
        var invocationArgs = string.Join(", ", function.Parameters.Select(p =>
        {
            if (p.Type == "CancellationToken") return "cancellationToken";
            if (p.Type == "AIFunctionArguments") return "arguments";
            if (p.Type == "IServiceProvider") return "arguments.Services";
            return $"args.{p.Name}";
        }));

        string asyncKeyword = function.IsAsync ? "async" : "";
        string awaitKeyword = function.IsAsync ? "await" : "";
        string returnType = "Task<object?>";
        string returnWrapper = function.IsAsync ? "" : "Task.FromResult";
        
        string schemaProviderCode = "() => { ";
        if (relevantParams.Any())
        {
            schemaProviderCode += $@"
    var schema = new Json.Schema.JsonSchemaBuilder().FromType<{dtoName}>().Build();
    var schemaJson = JsonSerializer.Serialize(schema, HPDJsonContext.Default.JsonSchema);
    var node = JsonNode.Parse(schemaJson);
    if (node is JsonObject root && root[""properties""] is JsonObject properties)
    {{
";
            foreach (var param in relevantParams.Where(p => !string.IsNullOrEmpty(p.Description)))
            {
                var escapedDescription = SymbolDisplay.FormatLiteral(param.Description, true);
                schemaProviderCode += $@"
        if (properties[""{param.Name}""] is JsonObject {param.Name}Obj)
        {{
            {param.Name}Obj[""description""] = {escapedDescription};
        }}
";
            }
            schemaProviderCode += "    }\n";
            schemaProviderCode += "    return JsonSerializer.SerializeToElement(node ?? JsonNode.Parse(\"{}\"), HPDJsonContext.Default.JsonNode);\n";
        }
        else
        {
            schemaProviderCode += "return JsonSerializer.SerializeToElement(new Json.Schema.JsonSchemaBuilder().Type(Json.Schema.SchemaValueType.Object).Build(), HPDJsonContext.Default.JsonSchema);";
        }
        schemaProviderCode += " }";

        string invocationLogic;
        if (relevantParams.Any())
        {
            string returnStatement = function.IsAsync 
                ? $"return ({awaitKeyword} instance.{function.Name}({invocationArgs})) as object;"
                : $"return {returnWrapper}(({awaitKeyword} instance.{function.Name}({invocationArgs})) as object);";
                
            invocationLogic = 
$@"({asyncKeyword} (arguments, cancellationToken) =>
            {{
                var jsonArgs = arguments.GetJson();
                var args = Parse{dtoName}(jsonArgs);
                {returnStatement}
            }})";
        }
        else
        {
            string returnStatement = function.IsAsync 
                ? $"return ({awaitKeyword} instance.{function.Name}({invocationArgs})) as object;"
                : $"return {returnWrapper}(({awaitKeyword} instance.{function.Name}({invocationArgs})) as object);";
                
            invocationLogic = 
$@"({asyncKeyword} (arguments, cancellationToken) =>
            {{
                {returnStatement}
            }})";
        }

        var options = new StringBuilder();
        options.AppendLine($"                Name = {nameCode},");
        options.AppendLine($"                Description = {descriptionCode},");
    options.AppendLine($"                RequiresPermission = {function.RequiresPermission.ToString().ToLower()},");
        options.AppendLine($"                Validator = Create{function.Name}Validator(),");
        options.AppendLine($"                SchemaProvider = {schemaProviderCode},");
        options.Append($"                ParameterDescriptions = {GenerateParameterDescriptions(function)}");
        
        return 
$@"HPDAIFunctionFactory.Create(
            new Func<AIFunctionArguments, CancellationToken, {returnType}>{invocationLogic},
            new HPDAIFunctionFactoryOptions
            {{ 
{options}
            }}
        )";
    }

    private static string GenerateParameterDescriptions(FunctionInfo function)
    {
        var paramsWithDesc = function.Parameters.Where(p => !string.IsNullOrEmpty(p.Description)).ToList();
        if (!paramsWithDesc.Any())
            return "null";
            
        var descriptions = new StringBuilder();
        descriptions.AppendLine("new Dictionary<string, string> {");
        
        for (int i = 0; i < paramsWithDesc.Count; i++)
        {
            var param = paramsWithDesc[i];
            var comma = i < paramsWithDesc.Count - 1 ? "," : "";
            var descCode = param.HasDynamicDescription 
                ? $"Resolve{function.Name}Parameter{param.Name}Description(context)"
                : $"\"{param.Description}\"";
            descriptions.AppendLine($"                    {{ \"{param.Name}\", {descCode} }}{comma}");
        }
        
        descriptions.Append("                }");
        return descriptions.ToString();
    }

    // Helper Methods

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
        
        var requiresPermission = GetRequiresPermission(method);
        var functionInfo = new FunctionInfo
        {
            Name = method.Identifier.ValueText,
            CustomName = customName,
            Description = description,
            Parameters = AnalyzeParameters(method.ParameterList, semanticModel),
            ReturnType = GetReturnType(method, semanticModel),
            IsAsync = IsAsyncMethod(method),
            RequiredPermissions = permissions,
            RequiresPermission = requiresPermission,
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

    // Helper to detect [RequiresPermission] attribute
    private static bool GetRequiresPermission(MethodDeclarationSyntax method)
    {
        return method.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Any(attr => attr.Name.ToString().Contains("RequiresPermission"));
    }

    /// <summary>
    /// Generates a manual JSON parser for AOT compatibility - no reflection needed!
    /// </summary>
    private static string GenerateJsonParser(FunctionInfo function, PluginInfo plugin)
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
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.GetString(){(isNullable ? "" : " ?? string.Empty")};");
                break;
                
            case "int" or "Int32":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.GetInt32();");
                break;
                
            case "long" or "Int64":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.GetInt64();");
                break;
                
            case "double" or "Double":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.GetDouble();");
                break;
                
            case "float" or "Single":
                sb.AppendLine($"{indent}                {targetVar} = (float){jsonPropertyVar}.GetDouble();");
                break;
                
            case "bool" or "Boolean":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.GetBoolean();");
                break;
                
            case "decimal" or "Decimal":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.GetDecimal();");
                break;
                
            case "DateTime":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.GetDateTime();");
                break;
                
            case "DateTimeOffset":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.GetDateTimeOffset();");
                break;
                
            case "Guid":
                sb.AppendLine($"{indent}                {targetVar} = {jsonPropertyVar}.GetGuid();");
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

