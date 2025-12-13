using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace HPD.Agent.SourceGenerator.Capabilities;

/// <summary>
/// Unified analyzer for all capability types (Functions, Skills, SubAgents).
/// Provides attribute-based dispatch to the appropriate capability analyzer.
/// This replaces the fragmented analysis approach (inline functions, SkillAnalyzer, SubAgentAnalyzer).
/// </summary>
internal static class CapabilityAnalyzer
{
    /// <summary>
    /// Analyzes a method and returns the appropriate ICapability implementation
    /// based on which attribute it has ([AIFunction], [Skill], or [SubAgent]).
    ///
    /// This is the single entry point for all capability analysis, enabling polymorphic processing.
    /// </summary>
    /// <param name="method">The method declaration to analyze</param>
    /// <param name="semanticModel">The semantic model for symbol resolution</param>
    /// <param name="context">The generator syntax context</param>
    /// <param name="className">The name of the containing class</param>
    /// <param name="namespaceName">The namespace of the containing class</param>
    /// <returns>An ICapability instance, or null if the method is not a valid capability</returns>
    public static ICapability? AnalyzeMethod(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel,
        GeneratorSyntaxContext context,
        string className,
        string namespaceName)
    {
        // Must be public
        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword))
            return null;

        var attrs = method.AttributeLists
            .SelectMany(al => al.Attributes)
            .ToList();

        // Dispatch based on attribute type (priority order: Skill > SubAgent > AIFunction)
        // This order matters because a method could theoretically have multiple attributes

        // 1. Check for [Skill] attribute
        if (HasAttribute(attrs, "Skill"))
        {
            return AnalyzeSkillCapability(method, attrs, semanticModel, context, className, namespaceName);
        }

        // 2. Check for [SubAgent] attribute
        if (HasAttribute(attrs, "SubAgent"))
        {
            return AnalyzeSubAgentCapability(method, attrs, semanticModel, context, className, namespaceName);
        }

        // 3. Check for [AIFunction] attribute
        if (HasAttribute(attrs, "AIFunction"))
        {
            return AnalyzeFunctionCapability(method, attrs, semanticModel, context, className, namespaceName);
        }

        // Not a capability
        return null;
    }

    // ========== Skill Analysis ==========

    /// <summary>
    /// Analyzes a method with [Skill] attribute and creates a SkillCapability.
    /// Phase 5: Full implementation migrated from SkillAnalyzer.AnalyzeSkill().
    /// </summary>
    private static SkillCapability? AnalyzeSkillCapability(
        MethodDeclarationSyntax method,
        List<AttributeSyntax> attrs,
        SemanticModel semanticModel,
        GeneratorSyntaxContext context,
        string className,
        string namespaceName)
    {
        // Validate return type is Skill
        var returnType = semanticModel.GetTypeInfo(method.ReturnType).Type;
        if (returnType == null || returnType.Name != "Skill")
        {
            // Invalid skill method - skip
            return null;
        }

        var methodName = method.Identifier.ValueText;
        System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer] Analyzing Skill method: {methodName}");

        // Find SkillFactory.Create() invocation in method body
        var invocation = method.Body?.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(inv => IsSkillFactoryCreate(inv, semanticModel));

        if (invocation == null)
        {
            // Check arrow expression body: => SkillFactory.Create(...)
            invocation = method.ExpressionBody?.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(inv => IsSkillFactoryCreate(inv, semanticModel));
        }

        if (invocation == null)
        {
            System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer] No SkillFactory.Create() found in {methodName}");
            return null;
        }

        System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer] Found SkillFactory.Create() in {methodName}");

        var arguments = invocation.ArgumentList.Arguments;

        // Minimum 2 arguments: name, description (functionResult/systemPrompt are optional but one is required)
        if (arguments.Count < 2)
        {
            return null;
        }

        // Extract required positional arguments (name, description)
        var name = ExtractStringLiteral(arguments[0].Expression, semanticModel);
        var description = ExtractStringLiteral(arguments[1].Expression, semanticModel);

        // Extract dual-context instructions (functionResult and systemPrompt)
        // These can be positional (args 2,3) or named
        string? functionResult = null;
        string? systemPrompt = null;
        SkillOptionsInfo? options = null;
        int referencesStartIndex = 2;

        // Build a dictionary of named arguments for easy lookup
        var namedArgs = arguments
            .Where(a => a.NameColon != null)
            .ToDictionary(
                a => a.NameColon!.Name.Identifier.ValueText,
                a => a);

        // Extract functionResult (named or positional at index 2)
        if (namedArgs.TryGetValue("functionResult", out var funcResultArg))
        {
            functionResult = ExtractStringLiteral(funcResultArg.Expression, semanticModel);
        }
        else if (arguments.Count > 2 && arguments[2].NameColon == null)
        {
            // Legacy: positional argument at index 2 (was "instructions")
            functionResult = ExtractStringLiteral(arguments[2].Expression, semanticModel);
            referencesStartIndex = 3;
        }

        // Extract systemPrompt (named or positional at index 3)
        if (namedArgs.TryGetValue("systemPrompt", out var sysPromptArg))
        {
            systemPrompt = ExtractStringLiteral(sysPromptArg.Expression, semanticModel);
        }
        else if (arguments.Count > 3 && arguments[3].NameColon == null && referencesStartIndex == 3)
        {
            // Legacy: positional argument at index 3
            systemPrompt = ExtractStringLiteral(arguments[3].Expression, semanticModel);
            referencesStartIndex = 4;
        }

        // For backward compatibility: if only old "instructions" arg provided, use it as both contexts
        // The old API was: Create(name, description, instructions, options?, refs...)
        // Check if this looks like the old signature (no named params, index 2 is a string, not options)
        if (functionResult != null && systemPrompt == null && !namedArgs.ContainsKey("functionResult"))
        {
            // Old signature: use instructions as systemPrompt (persistent context)
            systemPrompt = functionResult;
            functionResult = null;
        }

        // Extract options (named parameter)
        if (namedArgs.TryGetValue("options", out var optionsArg))
        {
            System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer] Found named 'options' parameter in {methodName}");
            options = ExtractSkillOptions(optionsArg.Expression, semanticModel);
        }
        else
        {
            // Check remaining positional args for SkillOptions
            for (int i = referencesStartIndex; i < arguments.Count; i++)
            {
                if (arguments[i].NameColon != null) continue; // Skip named args

                var argType = semanticModel.GetTypeInfo(arguments[i].Expression).Type;
                if (argType?.Name == "SkillOptions")
                {
                    System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer] Found SkillOptions at position {i} in {methodName}");
                    options = ExtractSkillOptions(arguments[i].Expression, semanticModel);
                    referencesStartIndex = i + 1;
                    break;
                }
            }
        }

        // Check for [AIDescription] attribute override (supports dynamic descriptions like Functions)
        var attributeDescription = GetDescription(attrs);
        if (!string.IsNullOrWhiteSpace(attributeDescription))
        {
            description = attributeDescription;
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        // DIAGNOSTIC: Log document uploads found
        if (options != null && options.DocumentUploads.Any())
        {
            System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer] Found {options.DocumentUploads.Count} document uploads in {methodName}:");
            foreach (var upload in options.DocumentUploads)
            {
                System.Diagnostics.Debug.WriteLine($"  - {upload.FilePath} -> {upload.DocumentId}");
            }
        }

        // Extract function/skill references (remaining arguments)
        var references = new List<ReferenceInfo>();
        for (int i = referencesStartIndex; i < arguments.Count; i++)
        {
            var argExpr = arguments[i].Expression;

            // Skip if this is a named "options" parameter
            if (arguments[i].NameColon?.Name.Identifier.ValueText == "options")
                continue;

            var reference = AnalyzeSkillReference(argExpr, semanticModel);
            if (reference != null)
                references.Add(reference);
        }

        // Extract conditional and context metadata (same as Functions)
        var conditionalExpression = GetConditionalExpression(attrs);
        var contextTypeName = GetMetadataTypeName(method, semanticModel);
        var requiresPermission = HasAttribute(attrs, "RequiresPermission");

        System.Diagnostics.Debug.WriteLine($"[AnalyzeSkillCapability] Skill={name}, Description={description}");
        System.Diagnostics.Debug.WriteLine($"[AnalyzeSkillCapability] ContextTypeName={contextTypeName ?? "NULL"}");
        System.Diagnostics.Debug.WriteLine($"[AnalyzeSkillCapability] HasDynamicDescription={description.Contains("{metadata.")}");

        return new SkillCapability
        {
            Name = name,
            MethodName = methodName,
            Description = description,
            FunctionResult = functionResult,
            SystemPrompt = systemPrompt,
            ParentPluginName = className,
            ParentNamespace = namespaceName,
            Options = options ?? new SkillOptionsInfo(),
            UnresolvedReferences = references,
            RequiresPermission = requiresPermission,

            // Context and conditionals (feature parity with Functions!)
            ContextTypeName = contextTypeName,
            ConditionalExpression = conditionalExpression,

            // Note: ResolvedFunctionReferences and ResolvedPluginTypes will be populated
            // during the resolution phase in HPDPluginSourceGenerator
        };
    }

    // ========== SubAgent Analysis ==========

    /// <summary>
    /// Analyzes a method with [SubAgent] attribute and creates a SubAgentCapability.
    /// For Phase 1, delegates to existing SubAgentAnalyzer for full analysis.
    /// Full migration will happen in Phase 2-3.
    /// </summary>
    private static SubAgentCapability? AnalyzeSubAgentCapability(
        MethodDeclarationSyntax method,
        List<AttributeSyntax> attrs,
        SemanticModel semanticModel,
        GeneratorSyntaxContext context,
        string className,
        string namespaceName)
    {
        // Validate return type is SubAgent
        var returnType = semanticModel.GetTypeInfo(method.ReturnType).Type;
        if (returnType == null || returnType.Name != "SubAgent")
        {
            // Invalid sub-agent method - skip
            return null;
        }

        var methodName = method.Identifier.ValueText;
        var isStatic = method.Modifiers.Any(SyntaxKind.StaticKeyword);

        // Extract metadata from SubAgentFactory calls in method body
        var (name, description, threadMode) = ExtractSubAgentMetadata(method, semanticModel);

        if (string.IsNullOrWhiteSpace(name))
        {
            System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer] Could not extract sub-agent name from {methodName}");
            return null;
        }

        // Extract conditional and context metadata (same as Functions and Skills)
        var conditionalExpression = GetConditionalExpression(attrs);
        var contextTypeName = GetMetadataTypeName(method, semanticModel);

        // SubAgents default to requiring permission (true), but can be overridden
        // If [RequiresPermission] is NOT present, default is true (for safety)
        // If [RequiresPermission] IS present, it's explicitly true
        // To disable permission requirement, we'd need a different mechanism (future enhancement)
        var requiresPermission = true;  // SubAgents always require permission by default

        var subAgentCapability = new SubAgentCapability
        {
            Name = name,  // Actual sub-agent name from SubAgentFactory.Create()
            MethodName = methodName,
            SubAgentName = name,  // Actual sub-agent name
            Description = description ?? GetDescription(attrs) ?? $"Sub-agent: {name}",
            ParentPluginName = className,
            ParentNamespace = namespaceName,
            IsStatic = isStatic,
            ThreadMode = threadMode,  // Extracted from factory method (Create vs CreateStateful vs CreatePerSession)
            RequiresPermission = requiresPermission,

            // Context and conditionals (feature parity with Functions and Skills!)
            ContextTypeName = contextTypeName,
            ConditionalExpression = conditionalExpression,
        };

        return subAgentCapability;
    }

    /// <summary>
    /// Extracts sub-agent name, description, and thread mode from method body.
    /// Looks for SubAgentFactory.Create(), CreateStateful(), or CreatePerSession() calls.
    /// </summary>
    private static (string? name, string? description, string threadMode) ExtractSubAgentMetadata(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel)
    {
        string? name = null;
        string? description = null;
        string threadMode = "Stateless";

        // Find all invocation expressions in the method body
        var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            // Check if this is a SubAgentFactory.Create() call
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodSymbol = semanticModel.GetSymbolInfo(memberAccess).Symbol as IMethodSymbol;

                if (methodSymbol?.ContainingType?.Name == "SubAgentFactory")
                {
                    var methodNameStr = methodSymbol.Name;

                    // Determine thread mode from factory method name
                    if (methodNameStr == "CreateStateful")
                        threadMode = "SharedThread";
                    else if (methodNameStr == "CreatePerSession")
                        threadMode = "PerSession";
                    else
                        threadMode = "Stateless";

                    // Extract arguments (name and description)
                    if (invocation.ArgumentList?.Arguments.Count >= 2)
                    {
                        // First argument is name
                        var nameArg = invocation.ArgumentList.Arguments[0];
                        if (nameArg.Expression is LiteralExpressionSyntax nameLiteral)
                        {
                            name = nameLiteral.Token.ValueText;
                        }

                        // Second argument is description
                        var descArg = invocation.ArgumentList.Arguments[1];
                        if (descArg.Expression is LiteralExpressionSyntax descLiteral)
                        {
                            description = descLiteral.Token.ValueText;
                        }
                    }

                    // Found the factory call, we're done
                    break;
                }
            }
        }

        return (name, description, threadMode);
    }

    // ========== Function Analysis ==========

    /// <summary>
    /// Analyzes a method with [AIFunction] attribute and creates a FunctionCapability.
    /// For Phase 1, performs basic analysis similar to existing inline analysis.
    /// Full migration from HPDPluginSourceGenerator will happen in Phase 2-3.
    /// </summary>
    private static FunctionCapability? AnalyzeFunctionCapability(
        MethodDeclarationSyntax method,
        List<AttributeSyntax> attrs,
        SemanticModel semanticModel,
        GeneratorSyntaxContext context,
        string className,
        string namespaceName)
    {
        var symbol = semanticModel.GetDeclaredSymbol(method);
        if (symbol == null) return null;

        var methodName = method.Identifier.ValueText;

        // Extract function metadata
        var description = GetDescription(attrs);
        var customName = GetCustomName(attrs);
        var conditionalExpression = GetConditionalExpression(attrs);
        var contextTypeName = GetMetadataTypeName(method, semanticModel);

        // Analyze parameters
        var parameters = method.ParameterList.Parameters
            .Select(param => new ParameterInfo
            {
                Name = param.Identifier.ValueText,
                Type = param.Type?.ToString() ?? "object",
                Description = GetParameterDescription(param),
                HasDefaultValue = param.Default != null,
                DefaultValue = param.Default?.Value?.ToString(),
                ConditionalExpression = GetParameterConditionalExpression(param)
            })
            .ToList();

        // Get return type
        var returnType = method.ReturnType.ToString();
        var isAsync = returnType.Contains("Task");

        // Check for permissions
        var requiresPermission = HasAttribute(attrs, "RequiresPermission");
        var requiredPermissions = GetRequiredPermissions(attrs);

        var functionCapability = new FunctionCapability
        {
            Name = methodName,
            CustomName = customName,
            Description = description ?? $"Function: {methodName}",
            ParentPluginName = className,
            ParentNamespace = namespaceName,

            // Context and conditionals (feature parity!)
            ContextTypeName = contextTypeName,
            ConditionalExpression = conditionalExpression,

            // Function-specific properties
            Parameters = parameters,
            ReturnType = returnType,
            IsAsync = isAsync,
            RequiresPermission = requiresPermission,
            RequiredPermissions = requiredPermissions.ToList(),

            // TODO Phase 2: Add validation data
        };

        return functionCapability;
    }

    // ========== Helper Methods ==========

    /// <summary>
    /// Checks if any attribute in the list matches the given name.
    /// </summary>
    private static bool HasAttribute(List<AttributeSyntax> attrs, string name)
    {
        return attrs.Any(attr => attr.Name.ToString().Contains(name));
    }

    /// <summary>
    /// Extracts description from [AIDescription] or [Description] attribute.
    /// </summary>
    private static string? GetDescription(List<AttributeSyntax> attrs)
    {
        // Check for [AIDescription] first
        var aiDescAttr = attrs.FirstOrDefault(a => a.Name.ToString().Contains("AIDescription"));
        if (aiDescAttr != null)
        {
            var arg = aiDescAttr.ArgumentList?.Arguments.FirstOrDefault();
            if (arg?.Expression is LiteralExpressionSyntax literal)
                return literal.Token.ValueText;
        }

        // Fallback to [Description]
        var descAttr = attrs.FirstOrDefault(a =>
            a.Name.ToString().Contains("Description") &&
            !a.Name.ToString().Contains("AIDescription"));
        if (descAttr != null)
        {
            var arg = descAttr.ArgumentList?.Arguments.FirstOrDefault();
            if (arg?.Expression is LiteralExpressionSyntax literal)
                return literal.Token.ValueText;
        }

        return null;
    }

    /// <summary>
    /// Extracts custom name from [AIFunction(Name = "...")] attribute.
    /// </summary>
    private static string? GetCustomName(List<AttributeSyntax> attrs)
    {
        // For now, return null (use method name)
        // TODO Phase 2: Extract from AIFunction attribute arguments
        return null;
    }

    /// <summary>
    /// Extracts conditional expression from [ConditionalFunction], [ConditionalSkill], or [ConditionalSubAgent] attribute.
    /// All three attribute types use the same PropertyExpression parameter.
    /// </summary>
    private static string? GetConditionalExpression(List<AttributeSyntax> attrs)
    {
        // Look for any of the conditional attributes (ConditionalFunction, ConditionalSkill, ConditionalSubAgent)
        var conditionalAttr = attrs.FirstOrDefault(a =>
            a.Name.ToString().Contains("ConditionalFunction") ||
            a.Name.ToString().Contains("ConditionalSkill") ||
            a.Name.ToString().Contains("ConditionalSubAgent"));

        if (conditionalAttr != null)
        {
            var arg = conditionalAttr.ArgumentList?.Arguments.FirstOrDefault();
            if (arg?.Expression is LiteralExpressionSyntax literal)
                return literal.Token.ValueText;
        }

        return null;
    }

    /// <summary>
    /// Extracts context type name from generic attributes [AIFunction&lt;TMetadata&gt;], [Skill&lt;TMetadata&gt;], or [SubAgent&lt;TMetadata&gt;].
    /// Returns the context type name if found, null otherwise.
    /// </summary>
    private static string? GetMetadataTypeName(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        // Check for AIFunction<TMetadata>, Skill<TMetadata>, and SubAgent<TMetadata> attributes
        var genericAttributes = method.AttributeLists
            .SelectMany(attrList => attrList.Attributes)
            .Where(attr => attr.Name.ToString().Contains("AIFunction") ||
                          attr.Name.ToString().Contains("Skill") ||
                          attr.Name.ToString().Contains("SubAgent"));

        foreach (var attr in genericAttributes)
        {
            System.Diagnostics.Debug.WriteLine($"[GetMetadataTypeName] Checking attribute: {attr.Name}");

            var symbolInfo = semanticModel.GetSymbolInfo(attr);
            System.Diagnostics.Debug.WriteLine($"[GetMetadataTypeName] Symbol: {symbolInfo.Symbol?.GetType().Name ?? "null"}");

            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                var attributeType = methodSymbol.ContainingType;
                System.Diagnostics.Debug.WriteLine($"[GetMetadataTypeName] AttributeType: {attributeType.Name}, IsGeneric: {attributeType.IsGenericType}");

                // Check if it's a generic attribute (AIFunction<TMetadata>, Skill<TMetadata>, or SubAgent<TMetadata>)
                if (attributeType.IsGenericType && attributeType.TypeArguments.Length == 1)
                {
                    var contextType = attributeType.TypeArguments[0];
                    System.Diagnostics.Debug.WriteLine($"[GetMetadataTypeName] Found context type: {contextType.Name}");
                    return contextType.Name;
                }
            }
        }

        System.Diagnostics.Debug.WriteLine($"[GetMetadataTypeName] No context type found for method {method.Identifier.ValueText}");
        return null;
    }

    /// <summary>
    /// Extracts required permissions from [RequiresPermission] attribute.
    /// </summary>
    private static string[] GetRequiredPermissions(List<AttributeSyntax> attrs)
    {
        // TODO Phase 2: Extract permission strings
        return System.Array.Empty<string>();
    }

    /// <summary>
    /// Extracts parameter description from [AIDescription] or [Description] attribute.
    /// </summary>
    private static string GetParameterDescription(ParameterSyntax param)
    {
        var attrs = param.AttributeLists.SelectMany(al => al.Attributes).ToList();

        // Check for [AIDescription] first
        var aiDescAttr = attrs.FirstOrDefault(a => a.Name.ToString().Contains("AIDescription"));
        if (aiDescAttr != null)
        {
            var arg = aiDescAttr.ArgumentList?.Arguments.FirstOrDefault();
            if (arg?.Expression is LiteralExpressionSyntax literal)
                return literal.Token.ValueText;
        }

        // Fallback to [Description]
        var descAttr = attrs.FirstOrDefault(a =>
            a.Name.ToString().Contains("Description") &&
            !a.Name.ToString().Contains("AIDescription"));
        if (descAttr != null)
        {
            var arg = descAttr.ArgumentList?.Arguments.FirstOrDefault();
            if (arg?.Expression is LiteralExpressionSyntax literal)
                return literal.Token.ValueText;
        }

        return string.Empty;
    }

    /// <summary>
    /// Extracts conditional expression for parameter from [ConditionalParameter] attribute.
    /// </summary>
    private static string? GetParameterConditionalExpression(ParameterSyntax param)
    {
        var attrs = param.AttributeLists.SelectMany(al => al.Attributes).ToList();
        var conditionalAttr = attrs.FirstOrDefault(a => a.Name.ToString().Contains("ConditionalParameter"));
        if (conditionalAttr != null)
        {
            var arg = conditionalAttr.ArgumentList?.Arguments.FirstOrDefault();
            if (arg?.Expression is LiteralExpressionSyntax literal)
                return literal.Token.ValueText;
        }

        return null;
    }

    // ========== Skill Helper Methods (Phase 5: Migrated from SkillAnalyzer) ==========

    /// <summary>
    /// Checks if an invocation is SkillFactory.Create()
    /// </summary>
    private static bool IsSkillFactoryCreate(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        var symbol = symbolInfo.Symbol as IMethodSymbol;

        return symbol?.ContainingType?.Name == "SkillFactory" &&
               symbol?.Name == "Create";
    }

    /// <summary>
    /// Analyzes a reference (function or skill) from a string literal.
    /// Phase 5: Migrated from SkillAnalyzer.AnalyzeReference().
    /// </summary>
    private static ReferenceInfo? AnalyzeSkillReference(
        ExpressionSyntax expression,
        SemanticModel semanticModel)
    {
        // Extract string literal: "FileSystemPlugin.ReadFile"
        var reference = ExtractStringLiteral(expression, semanticModel);

        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        // Parse "PluginName.FunctionName" format
        var parts = reference!.Split('.');

        if (parts.Length != 2)
        {
            return null;
        }

        var pluginName = parts[0];
        var methodName = parts[1];

        if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(methodName))
        {
            return null;
        }

        return new ReferenceInfo
        {
            ReferenceType = ReferenceType.Function,
            PluginType = pluginName,
            MethodName = methodName,
            FullName = reference,
            Location = expression.GetLocation()
        };
    }

    /// <summary>
    /// Extracts SkillOptions from object creation expression or method chain
    /// Phase 5: Migrated from SkillAnalyzer - supports fluent API (AddDocument/AddDocumentFromFile calls)
    /// </summary>
    private static SkillOptionsInfo ExtractSkillOptions(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        var options = new SkillOptionsInfo();

        // Handle object creation with initializer (old style)
        if (expression is ObjectCreationExpressionSyntax objectCreation)
        {
            if (objectCreation.Initializer != null)
            {
                foreach (var assignment in objectCreation.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
                {
                    var propertyName = (assignment.Left as IdentifierNameSyntax)?.Identifier.ValueText;
                    var value = assignment.Right;

                    switch (propertyName)
                    {
                        case "InstructionDocuments":
                            // Extract array of strings
                            if (value is ArrayCreationExpressionSyntax arrayCreation &&
                                arrayCreation.Initializer != null)
                            {
                                options.InstructionDocuments = arrayCreation.Initializer.Expressions
                                    .Select(expr => ExtractStringLiteral(expr, semanticModel))
                                    .Where(s => s != null)
                                    .ToList()!;
                            }
                            break;

                        case "InstructionDocumentBaseDirectory":
                            var baseDir = ExtractStringLiteral(value, semanticModel);
                            if (baseDir != null)
                                options.InstructionDocumentBaseDirectory = baseDir;
                            break;
                    }
                }
            }
        }

        // Handle fluent API method chains (new SkillOptions().AddDocument(...).AddDocumentFromFile(...))
        // Walk up the expression tree to find all chained method calls
        System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer] ExtractSkillOptions() - Walking method chain");
        var currentExpr = expression;
        int chainDepth = 0;
        while (currentExpr != null)
        {
            chainDepth++;
            System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer]   Chain depth {chainDepth}: {currentExpr.GetType().Name}");

            if (currentExpr is InvocationExpressionSyntax invocation)
            {
                var methodSymbol = semanticModel.GetSymbolInfo(invocation.Expression).Symbol as IMethodSymbol;
                var methodName = methodSymbol?.Name;
                System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer]   Method: {methodName}");

                if (methodName == "AddDocument")
                {
                    System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer]   Found AddDocument() call");
                    ExtractAddDocumentCall(invocation, semanticModel, options);
                }
                else if (methodName == "AddDocumentFromFile")
                {
                    System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer]   Found AddDocumentFromFile() call");
                    ExtractAddDocumentFromFileCall(invocation, semanticModel, options);
                }
                else if (methodName == "AddDocumentFromUrl")
                {
                    System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer]   Found AddDocumentFromUrl() call");
                    ExtractAddDocumentFromUrlCall(invocation, semanticModel, options);
                }

                // Move to the expression that this method is called on (the receiver)
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    currentExpr = memberAccess.Expression;
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        System.Diagnostics.Debug.WriteLine($"[CapabilityAnalyzer] ExtractSkillOptions() - Final: {options.DocumentUploads.Count} uploads, {options.DocumentReferences.Count} references");

        return options;
    }

    /// <summary>
    /// Extracts AddDocument() call arguments (document reference by ID)
    /// API signature: AddDocument(string documentId, string? descriptionOverride = null)
    /// </summary>
    private static void ExtractAddDocumentCall(InvocationExpressionSyntax invocation, SemanticModel semanticModel, SkillOptionsInfo options)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 1)
            return;

        var documentId = ExtractStringLiteral(args[0].Expression, semanticModel);
        if (string.IsNullOrWhiteSpace(documentId))
            return;

        string? descriptionOverride = null;
        if (args.Count >= 2)
        {
            descriptionOverride = ExtractStringLiteral(args[1].Expression, semanticModel);
        }

        options.DocumentReferences.Add(new DocumentReferenceInfo
        {
            DocumentId = documentId,
            DescriptionOverride = descriptionOverride
        });
    }

    /// <summary>
    /// Extracts AddDocumentFromFile() call arguments (file path)
    /// API signature: AddDocumentFromFile(string filePath, string description, string? documentId = null)
    /// </summary>
    private static void ExtractAddDocumentFromFileCall(InvocationExpressionSyntax invocation, SemanticModel semanticModel, SkillOptionsInfo options)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 2)
            return;

        // Extract arguments in correct order
        var filePath = ExtractStringLiteral(args[0].Expression, semanticModel);
        var description = ExtractStringLiteral(args[1].Expression, semanticModel);
        var documentId = args.Count >= 3 ? ExtractStringLiteral(args[2].Expression, semanticModel) : null;

        if (!string.IsNullOrWhiteSpace(filePath) && !string.IsNullOrWhiteSpace(description))
        {
            // Auto-derive document ID from filename if not provided (matches runtime behavior)
            var effectiveDocumentId = string.IsNullOrWhiteSpace(documentId)
                ? DeriveDocumentId(filePath)
                : documentId;

            options.DocumentUploads.Add(new DocumentUploadInfo
            {
                FilePath = filePath,
                Description = description,
                DocumentId = effectiveDocumentId,
                SourceType = DocumentSourceType.FilePath
            });
        }
    }

    /// <summary>
    /// Extracts AddDocumentFromUrl() call arguments (URL upload)
    /// API signature: AddDocumentFromUrl(string url, string description, string? documentId = null)
    /// </summary>
    private static void ExtractAddDocumentFromUrlCall(InvocationExpressionSyntax invocation, SemanticModel semanticModel, SkillOptionsInfo options)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 2)
            return;

        // Extract arguments in correct order
        var url = ExtractStringLiteral(args[0].Expression, semanticModel);
        var description = ExtractStringLiteral(args[1].Expression, semanticModel);
        var documentId = args.Count >= 3 ? ExtractStringLiteral(args[2].Expression, semanticModel) : null;

        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(description))
        {
            // Auto-derive document ID from URL if not provided (matches runtime behavior)
            var effectiveDocumentId = string.IsNullOrWhiteSpace(documentId)
                ? DeriveDocumentIdFromUrl(url)
                : documentId;

            options.DocumentUploads.Add(new DocumentUploadInfo
            {
                Url = url,
                Description = description,
                DocumentId = effectiveDocumentId,
                SourceType = DocumentSourceType.Url
            });
        }
    }

    /// <summary>
    /// Extracts string literal from expression
    /// Phase 5: Migrated from SkillAnalyzer
    /// </summary>
    private static string? ExtractStringLiteral(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        // Handle string literals
        if (expression is LiteralExpressionSyntax literal &&
            literal.Token.IsKind(SyntaxKind.StringLiteralToken))
        {
            return literal.Token.ValueText;
        }

        // Handle verbatim string literals (@"...")
        if (expression is LiteralExpressionSyntax verbatim &&
            verbatim.Token.IsKind(SyntaxKind.StringLiteralToken))
        {
            return verbatim.Token.ValueText;
        }

        // Try to evaluate constant
        var constantValue = semanticModel.GetConstantValue(expression);
        if (constantValue.HasValue && constantValue.Value is string str)
        {
            return str;
        }

        return null;
    }

    /// <summary>
    /// Derives a document ID from a file path (matches runtime SkillOptions.DeriveDocumentId behavior)
    /// Example: "./docs/debugging-workflow.md" -> "debugging-workflow"
    /// </summary>
    private static string DeriveDocumentId(string filePath)
    {
        var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);

        // Normalize to lowercase-kebab-case
        return fileName.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");
    }

    /// <summary>
    /// Derives a document ID from a URL (matches runtime SkillOptions.DeriveDocumentIdFromUrl behavior)
    /// Example: "https://docs.company.com/sops/financial-health.md" -> "financial-health"
    /// </summary>
    private static string DeriveDocumentIdFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "unknown";

        var fileName = System.IO.Path.GetFileNameWithoutExtension(uri.LocalPath);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            // If no filename, use host: "https://example.com" -> "example-com"
            fileName = uri.Host.Replace(".", "-");
        }

        return fileName.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");
    }
}
