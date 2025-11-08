using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;

namespace HPD_Agent.Scoping;

/// <summary>
/// Unified scoping manager that handles both plugin and skill scoping.
/// Merges the functionality of PluginScopingManager and SkillScopingManager.
/// </summary>
public class UnifiedScopingManager
{
    private readonly ILogger<UnifiedScopingManager>? _logger;
    private readonly Dictionary<string, AIFunction> _allFunctionsByReference;
    private readonly Dictionary<string, HPD_Agent.Skills.SkillDefinition> _skillsByName;

    public UnifiedScopingManager(
        IEnumerable<HPD_Agent.Skills.SkillDefinition> skills,
        IEnumerable<AIFunction> allFunctions,
        ILogger<UnifiedScopingManager>? logger = null)
    {
        _logger = logger;
        _allFunctionsByReference = BuildFunctionLookup(allFunctions);
        _skillsByName = skills.ToDictionary(s => s.Name, s => s);
    }

    /// <summary>
    /// Gets tools visible for the current agent turn based on expansion state.
    /// Handles both plugin containers and skill containers.
    ///
    /// Ordering strategy:
    /// 1. Plugin containers (collapsed plugins with [PluginScope])
    /// 2. Skill containers (collapsed skills with ScopingMode.Scoped)
    /// 3. Non-scoped functions (always visible)
    /// 4. Skills (InstructionOnly mode - always visible)
    /// 5. Expanded plugin functions
    /// 6. Expanded skill functions
    /// </summary>
    public List<AIFunction> GetToolsForAgentTurn(
        List<AIFunction> allTools,
        ImmutableHashSet<string> expandedPlugins,
        ImmutableHashSet<string> expandedSkills)
    {
        var pluginContainers = new List<AIFunction>();
        var skillContainers = new List<AIFunction>();
        var nonScopedFunctions = new List<AIFunction>();
        var instructionOnlySkills = new List<AIFunction>();
        var expandedPluginFunctions = new List<AIFunction>();
        var expandedSkillFunctions = new List<AIFunction>();

        // Get hidden functions from non-expanded scoped skills
        var hiddenFunctions = GetHiddenFunctionsBySkills(expandedSkills);

        // First pass: identify scoped plugins
        var pluginsWithContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in allTools)
        {
            if (IsPluginContainer(tool))
            {
                var pluginName = GetPluginName(tool);
                pluginsWithContainers.Add(pluginName);
            }
        }

        // Second pass: categorize all tools
        foreach (var tool in allTools)
        {
            // Skip functions hidden by scoped skills
            if (!IsContainer(tool) && !IsSkill(tool) && hiddenFunctions.Contains(tool.Name))
            {
                continue;
            }

            if (IsPluginContainer(tool))
            {
                // Plugin container - show if not expanded
                var pluginName = GetPluginName(tool);
                if (!expandedPlugins.Contains(pluginName))
                {
                    pluginContainers.Add(tool);
                }
            }
            else if (IsSkillContainer(tool))
            {
                // Skill container (Scoped mode) - show if not expanded
                var skillName = GetSkillName(tool);
                if (!expandedSkills.Contains(skillName))
                {
                    skillContainers.Add(tool);
                }
            }
            else if (IsSkill(tool))
            {
                // Skill (InstructionOnly mode) - always visible if parent container expanded
                var parentContainer = GetParentSkillContainer(tool);
                if (string.IsNullOrEmpty(parentContainer) || expandedSkills.Contains(parentContainer))
                {
                    instructionOnlySkills.Add(tool);
                }
            }
            else
            {
                // Regular function
                var parentPlugin = GetParentPlugin(tool);

                if (parentPlugin != null && pluginsWithContainers.Contains(parentPlugin))
                {
                    // Scoped plugin function - only show if parent expanded
                    if (expandedPlugins.Contains(parentPlugin))
                    {
                        expandedPluginFunctions.Add(tool);
                    }
                }
                else
                {
                    // Non-scoped function - always visible
                    nonScopedFunctions.Add(tool);
                }
            }
        }

        // Get functions from expanded skills
        var expandedSkillReferencedFunctions = GetFunctionsForExpandedSkills(expandedSkills, allTools);
        expandedSkillFunctions.AddRange(expandedSkillReferencedFunctions);

        // Combine and deduplicate
        return pluginContainers.OrderBy(c => c.Name)
            .Concat(skillContainers.OrderBy(c => c.Name))
            .Concat(nonScopedFunctions.OrderBy(f => f.Name))
            .Concat(instructionOnlySkills.OrderBy(s => s.Name))
            .Concat(expandedPluginFunctions.OrderBy(f => f.Name))
            .Concat(expandedSkillFunctions.OrderBy(f => f.Name))
            .DistinctBy(f => f.Name)
            .ToList();
    }

    /// <summary>
    /// Gets functions referenced by expanded skills (deduplicated).
    /// </summary>
    private List<AIFunction> GetFunctionsForExpandedSkills(ImmutableHashSet<string> expandedSkills, List<AIFunction> allTools)
    {
        var uniqueReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skillName in expandedSkills)
        {
            if (_skillsByName.TryGetValue(skillName, out var skill))
            {
                foreach (var reference in skill.ResolvedFunctionReferences)
                {
                    uniqueReferences.Add(reference);
                }
            }
        }

        // Find actual AIFunction objects
        var functions = new List<AIFunction>();
        foreach (var reference in uniqueReferences)
        {
            if (_allFunctionsByReference.TryGetValue(reference, out var function))
            {
                functions.Add(function);
            }
            else
            {
                // Try extracting just the function name
                var functionName = ExtractFunctionName(reference);
                if (_allFunctionsByReference.TryGetValue(functionName, out var funcByName))
                {
                    functions.Add(funcByName);
                }
            }
        }

        return functions;
    }

    /// <summary>
    /// Gets functions that should be hidden by non-expanded scoped skills.
    /// </summary>
    private HashSet<string> GetHiddenFunctionsBySkills(ImmutableHashSet<string> expandedSkills)
    {
        var hiddenFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in _skillsByName.Values)
        {
            // Only hide functions if skill is NOT expanded AND has Scoped mode
            if (!expandedSkills.Contains(skill.Name) &&
                !skill.AutoExpand &&
                skill.ScopingMode == HPD_Agent.Skills.SkillScopingMode.Scoped)
            {
                foreach (var reference in skill.ResolvedFunctionReferences)
                {
                    var functionName = ExtractFunctionName(reference);
                    hiddenFunctions.Add(functionName);
                }
            }
        }

        return hiddenFunctions;
    }

    /// <summary>
    /// Builds function lookup by reference identifier.
    /// </summary>
    private Dictionary<string, AIFunction> BuildFunctionLookup(IEnumerable<AIFunction> functions)
    {
        var lookup = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);

        foreach (var function in functions)
        {
            if (string.IsNullOrEmpty(function.Name))
                continue;

            // Add by function name
            lookup[function.Name] = function;

            // Add by qualified name if parent plugin exists
            var parentPlugin = GetParentPlugin(function);
            if (!string.IsNullOrEmpty(parentPlugin))
            {
                var qualifiedName = $"{parentPlugin}.{function.Name}";
                lookup[qualifiedName] = function;
            }
        }

        return lookup;
    }

    // Helper methods for metadata extraction

    private bool IsContainer(AIFunction function) =>
        function.AdditionalProperties?.TryGetValue("IsContainer", out var v) == true && v is bool b && b;

    private bool IsPluginContainer(AIFunction function) =>
        IsContainer(function) &&
        !(function.AdditionalProperties?.TryGetValue("IsSkill", out var v) == true && v is bool b && b);

    private bool IsSkillContainer(AIFunction function) =>
        IsContainer(function) &&
        function.AdditionalProperties?.TryGetValue("IsSkill", out var v) == true && v is bool b && b;

    private bool IsSkill(AIFunction function) =>
        function.AdditionalProperties?.TryGetValue("IsSkill", out var v) == true && v is bool b && b &&
        !IsContainer(function);

    private string GetPluginName(AIFunction function) =>
        function.AdditionalProperties?.TryGetValue("PluginName", out var v) == true && v is string s ? s : function.Name ?? string.Empty;

    private string GetSkillName(AIFunction function) =>
        function.Name ?? string.Empty;

    private string? GetParentPlugin(AIFunction function) =>
        function.AdditionalProperties?.TryGetValue("ParentPlugin", out var v) == true && v is string s ? s : null;

    private string? GetParentSkillContainer(AIFunction function) =>
        function.AdditionalProperties?.TryGetValue("ParentSkillContainer", out var v) == true && v is string s ? s : null;

    private string ExtractFunctionName(string reference)
    {
        // "PluginName.FunctionName" -> "FunctionName"
        var lastDot = reference.LastIndexOf('.');
        return lastDot >= 0 ? reference.Substring(lastDot + 1) : reference;
    }
}
