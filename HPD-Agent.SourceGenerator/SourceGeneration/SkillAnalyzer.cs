using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Analyzes skill methods and extracts skill information
/// </summary>
internal static class SkillAnalyzer
{
    /// <summary>
    /// Checks if a class contains skill methods
    /// A skill method is: [Skill] public Skill MethodName(SkillOptions? options = null)
    /// Can be static or instance method.
    /// </summary>
    public static bool HasSkillMethods(ClassDeclarationSyntax classDecl, SemanticModel semanticModel)
    {
        return classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(method => IsSkillMethod(method, semanticModel));
    }

    /// <summary>
    /// Checks if a method is a skill method.
    /// PHASE 3 UPDATE: Must have [Skill] attribute for explicit intent.
    /// Must be: [Skill] public Skill MethodName(SkillOptions? options = null)
    /// Can be static or instance method (static is not required).
    /// </summary>
    public static bool IsSkillMethod(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        // Must have [Skill] attribute
        var hasSkillAttribute = method.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(attr =>
            {
                var attrSymbol = semanticModel.GetSymbolInfo(attr).Symbol?.ContainingType;
                return attrSymbol?.Name == "SkillAttribute" ||
                       attrSymbol?.Name == "Skill";
            });

        if (!hasSkillAttribute)
            return false;

        // Must be public
        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword))
            return false;

        // Must return Skill type
        var returnTypeSymbol = semanticModel.GetTypeInfo(method.ReturnType).Type;
        if (returnTypeSymbol?.Name != "Skill")
            return false;

        return true;
    }

    /// <summary>
    /// Analyzes a skill method and extracts skill information
    /// </summary>
    public static SkillInfo? AnalyzeSkill(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel,
        GeneratorSyntaxContext context)
    {
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
            // Missing SkillFactory.Create() call - diagnostics will be reported by main generator
            // For now, just return null
            return null;
        }

        var arguments = invocation.ArgumentList.Arguments;

        // Minimum 3 arguments: name, description, instructions
        if (arguments.Count < 3)
        {
            return null; // TODO: Report diagnostic in Phase 2.5
        }

        // Extract arguments
        var name = ExtractStringLiteral(arguments[0].Expression, semanticModel);
        var description = ExtractStringLiteral(arguments[1].Expression, semanticModel);
        var instructions = ExtractStringLiteral(arguments[2].Expression, semanticModel);

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
        {
            return null; // TODO: Report diagnostic in Phase 2.5
        }

        var methodName = method.Identifier.ValueText;

        // Find SkillOptions argument (optional, can be at position 3 or named)
        SkillOptionsInfo? options = null;
        int referencesStartIndex = 3;

        // Check if position 3 is SkillOptions or a reference
        if (arguments.Count > 3)
        {
            var thirdArg = arguments[3];

            // Check if named parameter "options"
            if (thirdArg.NameColon?.Name.Identifier.ValueText == "options")
            {
                options = ExtractSkillOptions(thirdArg.Expression, semanticModel);
                referencesStartIndex = 4;
            }
            else
            {
                // Check type to determine if it's SkillOptions or a reference
                var thirdArgType = semanticModel.GetTypeInfo(thirdArg.Expression).Type;
                if (thirdArgType?.Name == "SkillOptions")
                {
                    options = ExtractSkillOptions(thirdArg.Expression, semanticModel);
                    referencesStartIndex = 4;
                }
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

            var reference = AnalyzeReference(argExpr, semanticModel);
            if (reference != null)
                references.Add(reference);
        }

        // TODO: Warn if no references in Phase 2.5

        var containingClass = method.Parent as ClassDeclarationSyntax;
        var namespaceName = GetNamespace(containingClass);

        // Phase 3: Extract attribute metadata
        var (category, priority) = ExtractSkillAttributeMetadata(method, semanticModel);

        return new SkillInfo
        {
            MethodName = methodName,
            Name = name,
            Description = description,
            Instructions = instructions,
            Category = category,
            Priority = priority,
            Options = options ?? new SkillOptionsInfo(),
            References = references,
            ContainingClass = containingClass!,
            Namespace = namespaceName
        };
    }

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
    /// Phase 3: Changed from delegate-based to string-based references.
    /// </summary>
    private static ReferenceInfo? AnalyzeReference(
        ExpressionSyntax expression,
        SemanticModel semanticModel)
    {
        // Extract string literal: "FileSystemPlugin.ReadFile"
        var reference = ExtractStringLiteral(expression, semanticModel);
        
        if (string.IsNullOrWhiteSpace(reference))
        {
            // TODO: Report diagnostic in Phase 2.5 - not a string literal
            return null;
        }

        // Parse "PluginName.FunctionName" format
        var parts = reference!.Split('.');
        
        if (parts.Length != 2)
        {
            // TODO: Report diagnostic in Phase 2.5 - invalid format
            return null;
        }

        var pluginName = parts[0];
        var methodName = parts[1];

        if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(methodName))
        {
            // TODO: Report diagnostic in Phase 2.5 - empty plugin or method name
            return null;
        }

        // Note: We assume it's a function reference. Skill references will be handled
        // by checking if the method returns Skill type at runtime or in Phase 2.5 with
        // more sophisticated analysis.
        return new ReferenceInfo
        {
            ReferenceType = ReferenceType.Function,
            PluginType = pluginName,
            MethodName = methodName,
            FullName = reference,
            Location = expression.GetLocation(),
            // Namespace cannot be determined from string - will be resolved at runtime
            PluginNamespace = string.Empty
        };
    }

    /// <summary>
    /// Extracts string literal from expression
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
    /// Extracts SkillOptions from object creation expression or method chain
    /// Phase 3: Supports fluent API (AddDocument/AddDocumentFromFile calls)
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
                        case "ScopingMode":
                            var scopingValue = (value as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText;
                            if (scopingValue != null)
                                options.ScopingMode = scopingValue;
                            break;

                        case "AutoExpand":
                            var autoExpandValue = semanticModel.GetConstantValue(value);
                            if (autoExpandValue.HasValue && autoExpandValue.Value is bool autoExpand)
                                options.AutoExpand = autoExpand;
                            break;

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

        // Phase 3: Handle fluent API method chains (new SkillOptions().AddDocument(...).AddDocumentFromFile(...))
        // Walk up the expression tree to find all chained method calls
        var currentExpr = expression;
        while (currentExpr != null)
        {
            if (currentExpr is InvocationExpressionSyntax invocation)
            {
                var methodSymbol = semanticModel.GetSymbolInfo(invocation.Expression).Symbol as IMethodSymbol;
                var methodName = methodSymbol?.Name;

                if (methodName == "AddDocument")
                {
                    ExtractAddDocumentCall(invocation, semanticModel, options);
                }
                else if (methodName == "AddDocumentFromFile")
                {
                    ExtractAddDocumentFromFileCall(invocation, semanticModel, options);
                }

                // Move to the target of the invocation (the thing before the method call)
                currentExpr = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
            }
            else
            {
                // Reached the end of the chain
                break;
            }
        }

        return options;
    }

    /// <summary>
    /// Extracts information from AddDocument(documentId, description?) call
    /// </summary>
    private static void ExtractAddDocumentCall(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        SkillOptionsInfo options)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 1) return;

        var documentId = ExtractStringLiteral(args[0].Expression, semanticModel);
        if (string.IsNullOrWhiteSpace(documentId)) return;

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
    /// Extracts information from AddDocumentFromFile(filePath, description, documentId?) call
    /// </summary>
    private static void ExtractAddDocumentFromFileCall(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        SkillOptionsInfo options)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 2) return;

        var filePath = ExtractStringLiteral(args[0].Expression, semanticModel);
        var description = ExtractStringLiteral(args[1].Expression, semanticModel);

        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(description))
            return;

        string? documentId = null;
        if (args.Count >= 3)
        {
            documentId = ExtractStringLiteral(args[2].Expression, semanticModel);
        }

        // If documentId not provided, it will be auto-derived at runtime
        // For source generation, we store the explicit ID if provided
        options.DocumentUploads.Add(new DocumentUploadInfo
        {
            FilePath = filePath,
            DocumentId = documentId ?? string.Empty,  // Empty means auto-derive
            Description = description
        });
    }

    /// <summary>
    /// Gets namespace of a class
    /// </summary>
    private static string GetNamespace(ClassDeclarationSyntax? classDecl)
    {
        if (classDecl == null) return string.Empty;

        var namespaceDecl = classDecl.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        if (namespaceDecl != null)
            return namespaceDecl.Name.ToString() ?? string.Empty;

        var fileScopedNamespace = classDecl.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        if (fileScopedNamespace != null)
            return fileScopedNamespace.Name.ToString() ?? string.Empty;

        return string.Empty;
    }

    /// <summary>
    /// Extracts Category and Priority from [Skill] attribute (Phase 3)
    /// </summary>
    private static (string? category, int priority) ExtractSkillAttributeMetadata(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel)
    {
        string? category = null;
        int priority = 0;

        var skillAttribute = method.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(attr =>
            {
                var attrSymbol = semanticModel.GetSymbolInfo(attr).Symbol?.ContainingType;
                return attrSymbol?.Name == "SkillAttribute" ||
                       attrSymbol?.Name == "Skill";
            });

        if (skillAttribute?.ArgumentList == null)
            return (category, priority);

        foreach (var argument in skillAttribute.ArgumentList.Arguments)
        {
            var name = argument.NameEquals?.Name.Identifier.ValueText;

            if (name == "Category")
            {
                category = ExtractStringLiteral(argument.Expression, semanticModel);
            }
            else if (name == "Priority")
            {
                var constantValue = semanticModel.GetConstantValue(argument.Expression);
                if (constantValue.HasValue && constantValue.Value is int p)
                    priority = p;
            }
        }

        return (category, priority);
    }

    // TODO: Phase 2.5 - Add diagnostic reporting support
    // For now, diagnostics are skipped to get basic functionality working
}
