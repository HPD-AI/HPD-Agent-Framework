using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Analyzes sub-agent methods and extracts sub-agent information
/// Similar to SkillAnalyzer but for [SubAgent] methods
/// </summary>
internal static class SubAgentAnalyzer
{
    /// <summary>
    /// Checks if a class contains sub-agent methods
    /// A sub-agent method is: [SubAgent] public SubAgent MethodName()
    /// Can be static or instance method.
    /// </summary>
    public static bool HasSubAgentMethods(ClassDeclarationSyntax classDecl, SemanticModel semanticModel)
    {
        return classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(method => IsSubAgentMethod(method, semanticModel));
    }

    /// <summary>
    /// Checks if a method is a sub-agent method.
    /// Must be: [SubAgent] public SubAgent MethodName()
    /// Can be static or instance method (static is not required).
    /// </summary>
    public static bool IsSubAgentMethod(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        var methodName = method.Identifier.ValueText;
        System.Diagnostics.Debug.WriteLine($"[IsSubAgentMethod] Checking: {methodName}");

        // Must have [SubAgent] attribute
        var hasSubAgentAttribute = false;
        foreach (var attr in method.AttributeLists.SelectMany(al => al.Attributes))
        {
            var attrSymbol = semanticModel.GetSymbolInfo(attr).Symbol?.ContainingType;
            System.Diagnostics.Debug.WriteLine($"[IsSubAgentMethod]   Attribute: {attr.Name}, Symbol: {attrSymbol?.ToDisplayString() ?? "NULL"}");

            if (attrSymbol == null)
                continue;

            // Check both simple name and fully qualified name
            // SubAgentAttribute is in global namespace (no namespace)
            if (attrSymbol.Name == "SubAgentAttribute" ||
                attrSymbol.Name == "SubAgent")
            {
                hasSubAgentAttribute = true;
                System.Diagnostics.Debug.WriteLine($"[IsSubAgentMethod]   ✅ Found [SubAgent] attribute");
                break;
            }
        }

        if (!hasSubAgentAttribute)
        {
            System.Diagnostics.Debug.WriteLine($"[IsSubAgentMethod]   ❌ No [SubAgent] attribute found");
            return false;
        }

        // Must be public
        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword))
        {
            System.Diagnostics.Debug.WriteLine($"[IsSubAgentMethod]   ❌ Not public");
            return false;
        }

        // Must return SubAgent type
        var returnTypeSymbol = semanticModel.GetTypeInfo(method.ReturnType).Type;
        System.Diagnostics.Debug.WriteLine($"[IsSubAgentMethod]   Return type symbol: {returnTypeSymbol?.ToDisplayString() ?? "NULL"}");

        if (returnTypeSymbol == null)
        {
            System.Diagnostics.Debug.WriteLine($"[IsSubAgentMethod]   ❌ Return type symbol is NULL");
            return false;
        }

        // Check if return type is SubAgent (in HPD.Agent namespace or global namespace)
        var isSubAgentType = returnTypeSymbol.Name == "SubAgent" &&
                             (returnTypeSymbol.ContainingNamespace?.IsGlobalNamespace == true ||
                              returnTypeSymbol.ContainingNamespace?.ToDisplayString() == "HPD.Agent");

        if (!isSubAgentType)
        {
            System.Diagnostics.Debug.WriteLine($"[IsSubAgentMethod]   ❌ Return type is not SubAgent: {returnTypeSymbol.ToDisplayString()}");
            return false;
        }

        System.Diagnostics.Debug.WriteLine($"[IsSubAgentMethod]   ✅ ALL CHECKS PASSED");
        return true;
    }

    /// <summary>
    /// Analyzes a sub-agent method and extracts sub-agent information
    /// </summary>
    public static SubAgentInfo? AnalyzeSubAgent(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel,
        string className,
        string namespaceName)
    {
        System.Diagnostics.Debug.WriteLine($"[AnalyzeSubAgent] Analyzing: {method.Identifier.ValueText}");

        var methodName = method.Identifier.ValueText;
        var isStatic = method.Modifiers.Any(SyntaxKind.StaticKeyword);

        // Extract sub-agent name and description by analyzing method body
        // Look for SubAgentFactory.Create() or SubAgentFactory.CreateStateful() calls
        var (name, description, threadMode) = ExtractSubAgentMetadata(method, semanticModel);

        if (string.IsNullOrWhiteSpace(name))
        {
            System.Diagnostics.Debug.WriteLine($"[AnalyzeSubAgent]   ❌ Could not extract sub-agent name");
            return null;
        }

        var subAgentInfo = new SubAgentInfo
        {
            MethodName = methodName,
            SubAgentName = name,
            Description = description ?? $"Sub-agent: {name}",
            ClassName = className,
            Namespace = namespaceName,
            IsStatic = isStatic,
            ThreadMode = threadMode
        };

        System.Diagnostics.Debug.WriteLine($"[AnalyzeSubAgent]   ✅ Successfully analyzed sub-agent: {name}");
        return subAgentInfo;
    }

    /// <summary>
    /// Extracts sub-agent name, description, and thread mode from method body
    /// Looks for SubAgentFactory.Create() calls
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

                    // Extract arguments
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

    /// <summary>
    /// Extracts all sub-agent methods from a class
    /// </summary>
    public static List<SubAgentInfo> ExtractSubAgents(
        ClassDeclarationSyntax classDecl,
        SemanticModel semanticModel,
        string className,
        string namespaceName)
    {
        var subAgents = new List<SubAgentInfo>();

        var methods = classDecl.Members.OfType<MethodDeclarationSyntax>();
        foreach (var method in methods)
        {
            if (IsSubAgentMethod(method, semanticModel))
            {
                var subAgentInfo = AnalyzeSubAgent(method, semanticModel, className, namespaceName);
                if (subAgentInfo != null)
                {
                    subAgents.Add(subAgentInfo);
                }
            }
        }

        return subAgents;
    }
}

/// <summary>
/// Information about a sub-agent method
/// </summary>
internal class SubAgentInfo
{
    public string MethodName { get; set; } = string.Empty;
    public string SubAgentName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
    public string ThreadMode { get; set; } = "Stateless";
}
