using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Information about a plugin discovered during source generation.
/// </summary>
internal class PluginInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public List<FunctionInfo> Functions { get; set; } = new();

    /// <summary>
    /// Skills defined in this class (empty if no skills)
    /// </summary>
    public List<SkillInfo> Skills { get; set; } = new();

    /// <summary>
    /// Sub-agents defined in this class (empty if no sub-agents)
    /// </summary>
    public List<SubAgentInfo> SubAgents { get; set; } = new();

    /// <summary>
    /// Whether this plugin requires an instance parameter in CreatePlugin().
    /// All capability types (Functions, Skills, SubAgents) can access instance state.
    /// This enables dynamic container instructions via instance methods.
    /// </summary>
    public bool RequiresInstance => Functions.Any() || Skills.Any() || SubAgents.Any();

    /// <summary>
    /// Whether any functions have conditional logic requiring context resolution
    /// </summary>
    public bool RequiresContext => Functions.Any(f => f.RequiresContext);

    /// <summary>
    /// Whether this plugin has a parameterless constructor.
    /// Only plugins with parameterless constructors can be included in the PluginRegistry.All catalog.
    /// Plugins without parameterless constructors (e.g., those requiring DI) must be registered through
    /// special extension methods like WithDynamicMemory() or WithPlanMode().
    /// </summary>
    public bool HasParameterlessConstructor { get; set; } = true;

    /// <summary>
    /// Whether this plugin class is publicly accessible.
    /// Only publicly accessible classes can be included in the PluginRegistry.All catalog.
    /// Private/internal classes (e.g., test fixtures) are still processed for individual Registration files
    /// but are excluded from the registry.
    /// </summary>
    public bool IsPubliclyAccessible { get; set; } = true;

    /// <summary>
    /// Whether this container has the [Collapse] attribute
    /// </summary>
    public bool HasCollapseAttribute { get; set; }

    /// <summary>
    /// Description from [Collapse] attribute (if present)
    /// </summary>
    public string? CollapseDescription { get; set; }

    /// <summary>
    /// Post-expansion instructions from [Collapse] attribute (if present)
    /// These instructions are shown to the agent after the container is expanded.
    /// </summary>
    public string? PostExpansionInstructions { get; set; }

    /// <summary>
    /// Post-expansion instructions from [Collapse] attribute (if it's an expression)
    /// This stores the method call or property access as a string.
    /// </summary>
    public string? PostExpansionInstructionsExpression { get; set; }
}

/// <summary>
/// Information about a function discovered during source generation.
/// </summary>
internal class FunctionInfo
{
    public string Name { get; set; } = string.Empty;
    public string? CustomName { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<ParameterInfo> Parameters { get; set; } = new();
    public string ReturnType { get; set; } = string.Empty;
    public bool IsAsync { get; set; }
    public List<string> RequiredPermissions { get; set; } = new();
    /// <summary>
    /// Whether the function is marked with [RequiresPermission].
    /// </summary>
    public bool RequiresPermission { get; set; }
    
    /// <summary>
    /// Context type name from [AIFunction&lt;TContext&gt;] (null for non-generic)
    /// </summary>
    public string? ContextTypeName { get; set; }
    
    /// <summary>
    /// Conditional expression for function visibility (null if always visible)
    /// </summary>
    public string? ConditionalExpression { get; set; }
    
    /// <summary>
    /// Whether this function uses the generic AIFunction&lt;TContext&gt; attribute
    /// </summary>
    public bool HasTypedContext => !string.IsNullOrEmpty(ContextTypeName);
    
    /// <summary>
    /// Whether this function requires context for conditions or dynamic descriptions
    /// </summary>
    public bool RequiresContext => HasTypedContext && (IsConditional || HasDynamicDescription || HasConditionalParameters);
    
    /// <summary>
    /// Whether this function has conditional inclusion logic
    /// </summary>
    public bool IsConditional => !string.IsNullOrEmpty(ConditionalExpression);
    
    /// <summary>
    /// Whether this function has dynamic description templates
    /// </summary>
    public bool HasDynamicDescription => Description.Contains("{context.");
    
    /// <summary>
    /// Whether this function has any conditional parameters
    /// </summary>
    public bool HasConditionalParameters => Parameters.Any(p => p.IsConditional);

    /// <summary>
    /// Validation data for later processing
    /// </summary>
    public ValidationData? ValidationData { get; set; }

    /// <summary>
    /// Effective function name (custom or method name)
    /// </summary>
    public string FunctionName => CustomName ?? Name;
}

/// <summary>
/// Information about a function parameter discovered during source generation.
/// </summary>
internal class ParameterInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool HasDefaultValue { get; set; }
    public string? DefaultValue { get; set; }
    
    /// <summary>
    /// Conditional expression for parameter visibility (null if always visible)
    /// </summary>
    public string? ConditionalExpression { get; set; }
    
    /// <summary>
    /// Whether this parameter has conditional visibility
    /// </summary>
    public bool IsConditional => !string.IsNullOrEmpty(ConditionalExpression);
    
    /// <summary>
    /// Whether this parameter has dynamic description templates
    /// </summary>
    public bool HasDynamicDescription => Description.Contains("{context.");
    
    /// <summary>
    /// Whether this parameter should be serialized (not special framework types)
    /// </summary>
    public bool IsSerializable => Type != "CancellationToken" && Type != "AIFunctionArguments" && Type != "IServiceProvider";
    
    /// <summary>
    /// Whether this parameter is nullable (simple heuristic)
    /// </summary>
    public bool IsNullable => Type.EndsWith("?");
}

/// <summary>
/// Validation data for functions that need validation after source generation.
/// </summary>
internal class ValidationData
{
    public MethodDeclarationSyntax Method { get; set; } = null!;
    public SemanticModel SemanticModel { get; set; } = null!;
    public bool NeedsValidation { get; set; }
}
