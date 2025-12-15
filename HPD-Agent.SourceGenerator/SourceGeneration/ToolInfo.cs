using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using HPD.Agent.SourceGenerator.Capabilities;

/// <summary>
/// Information about a plugin discovered during source generation.
/// </summary>
internal class ToolInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;

    // ========== UNIFIED DATA STRUCTURE (Phase 5: Complete) ==========

    /// <summary>
    /// Unified list of all capabilities (Functions, Skills, SubAgents).
    /// Phase 5: Single source of truth for all capability types.
    /// </summary>
    public List<ICapability> Capabilities { get; set; } = new();

    // ========== HELPER PROPERTIES (Type-Specific Queries) ==========

    /// <summary>
    /// Gets all skill capabilities from the unified Capabilities list.
    /// This provides backward compatibility for code that needs to filter by type.
    /// </summary>
    public IEnumerable<SkillCapability> SkillCapabilities =>
        Capabilities.OfType<SkillCapability>();

    /// <summary>
    /// Gets all function capabilities from the unified Capabilities list.
    /// </summary>
    public IEnumerable<FunctionCapability> FunctionCapabilities =>
        Capabilities.OfType<FunctionCapability>();

    /// <summary>
    /// Gets all sub-agent capabilities from the unified Capabilities list.
    /// </summary>
    public IEnumerable<SubAgentCapability> SubAgentCapabilities =>
        Capabilities.OfType<SubAgentCapability>();

    /// <summary>
    /// Total count of all capabilities.
    /// </summary>
    public int CapabilityCount => Capabilities.Count;

    /// <summary>
    /// Whether this plugin requires an instance parameter in CreatePlugin().
    /// All capability types (Functions, Skills, SubAgents) can access instance state.
    /// This enables dynamic container instructions via instance methods.
    /// Phase 4: Uses only unified Capabilities list.
    /// </summary>
    public bool RequiresInstance =>
        Capabilities.Any(c => c.RequiresInstance);

    /// <summary>
    /// Whether any capabilities have conditional logic or dynamic descriptions requiring context resolution.
    /// Phase 4: Uses only unified Capabilities list.
    /// </summary>
    public bool RequiresContext =>
        Capabilities.Any(c => c.IsConditional || c.HasDynamicDescription);

    /// <summary>
    /// Whether this plugin has a parameterless constructor.
    /// Only plugins with parameterless constructors can be included in the ToolRegistry.All catalog.
    /// Plugins without parameterless constructors (e.g., those requiring DI) must be registered through
    /// special extension methods like WithDynamicMemory() or WithPlanMode().
    /// </summary>
    public bool HasParameterlessConstructor { get; set; } = true;

    /// <summary>
    /// Whether this plugin class is publicly accessible.
    /// Only publicly accessible classes can be included in the ToolRegistry.All catalog.
    /// Private/internal classes (e.g., test fixtures) are still processed for individual Registration files
    /// but are excluded from the registry.
    /// </summary>
    public bool IsPubliclyAccessible { get; set; } = true;

    /// <summary>
    /// Diagnostics collected during plugin analysis (e.g., dual-context validation errors).
    /// These are reported during source generation in GenerateToolRegistrations.
    /// </summary>
    public List<Diagnostic> Diagnostics { get; set; } = new();

    /// <summary>
    /// Whether this container has the [Collapse] attribute
    /// </summary>
    public bool HasCollapseAttribute { get; set; }

    /// <summary>
    /// Description from [Collapse] attribute (if present)
    /// </summary>
    public string? CollapseDescription { get; set; }

    /// <summary>
    /// Instructions returned as FUNCTION RESULT when container is activated (literal value).
    /// </summary>
    public string? FunctionResult { get; set; }

    /// <summary>
    /// Instructions returned as FUNCTION RESULT when container is activated (expression/method call).
    /// </summary>
    public string? FunctionResultExpression { get; set; }

    /// <summary>
    /// Whether FunctionResultExpression is a static member (true) or instance member (false).
    /// </summary>
    public bool FunctionResultIsStatic { get; set; } = true;

    /// <summary>
    /// Instructions injected into SYSTEM PROMPT persistently after activation (literal value).
    /// </summary>
    public string?SystemPrompt { get; set; }

    /// <summary>
    /// Instructions injected into SYSTEM PROMPT persistently after activation (expression/method call).
    /// </summary>
    public string?SystemPromptExpression { get; set; }

    /// <summary>
    /// WhetherSystemPromptExpression is a static member (true) or instance member (false).
    /// </summary>
    public bool SystemPromptIsStatic { get; set; } = true;
}

// ========== OLD CLASSES REMOVED (Phase 4) ==========
// FunctionInfo, ParameterInfo, and ValidationData have been removed.
// These are now defined in the Capabilities namespace:
// - HPD.Agent.SourceGenerator.Capabilities.FunctionCapability (replaces FunctionInfo)
// - HPD.Agent.SourceGenerator.Capabilities.ParameterInfo (shared by all capabilities)
// - HPD.Agent.SourceGenerator.Capabilities.ValidationData (part of FunctionCapability)
