using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HPD_Agent.Skills;

/// <summary>
/// Manages skill scoping and function visibility based on expansion state.
/// Works identically to PluginScopingManager but for skill-based function references.
/// Handles deduplication when multiple skills reference the same function.
/// </summary>
public class SkillScopingManager
{
    private readonly ILogger<SkillScopingManager>? _logger;
    private readonly Dictionary<string, AIFunction> _allFunctionsByReference;
    private readonly Dictionary<string, SkillDefinition> _skillsByName;

    /// <summary>
    /// Initializes the skill scoping manager with registered skills and functions.
    /// </summary>
    /// <param name="skills">All registered skill definitions</param>
    /// <param name="allFunctions">All available functions (from plugins and other sources)</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    public SkillScopingManager(
        IEnumerable<SkillDefinition> skills,
        IEnumerable<AIFunction> allFunctions,
        ILogger<SkillScopingManager>? logger = null)
    {
        _logger = logger;

        // Build function lookup by reference (for quick resolution)
        _allFunctionsByReference = BuildFunctionLookup(allFunctions);

        // Build skill lookup by name
        _skillsByName = skills.ToDictionary(s => s.Name, s => s);
    }

    /// <summary>
    /// Builds a lookup dictionary for functions by their reference identifier.
    /// Functions are indexed by:
    /// 1. "PluginName.FunctionName" (if ParentPlugin metadata exists)
    /// 2. "FunctionName" (for non-plugin functions or as fallback)
    /// </summary>
    private Dictionary<string, AIFunction> BuildFunctionLookup(IEnumerable<AIFunction> functions)
    {
        var lookup = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);

        foreach (var function in functions)
        {
            if (string.IsNullOrEmpty(function.Name))
                continue;

            // Add by function name alone
            lookup[function.Name] = function;

            // Add by "PluginName.FunctionName" if parent plugin exists
            var parentPlugin = GetParentPlugin(function);
            if (!string.IsNullOrEmpty(parentPlugin))
            {
                var qualifiedName = $"{parentPlugin}.{function.Name}";
                lookup[qualifiedName] = function;
            }
        }

        return lookup;
    }

    /// <summary>
    /// Gets functions that should be hidden based on non-expanded Scoped skills.
    /// Functions referenced by skills with ScopingMode.Scoped are hidden until the skill is expanded.
    /// </summary>
    /// <param name="expandedSkills">Set of skill names that have been expanded</param>
    /// <returns>Set of function names that should be hidden</returns>
    public HashSet<string> GetHiddenFunctionsBySkills(HashSet<string> expandedSkills)
    {
        var hiddenFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in _skillsByName.Values)
        {
            // Only hide functions if skill is NOT expanded AND has Scoped mode
            if (!expandedSkills.Contains(skill.Name) &&
                !skill.AutoExpand &&
                skill.ScopingMode == SkillScopingMode.Scoped)
            {
                foreach (var reference in skill.ResolvedFunctionReferences)
                {
                    // Extract function name (strip plugin prefix if present)
                    var functionName = ExtractFunctionName(reference);
                    hiddenFunctions.Add(functionName);
                }
            }
        }

        return hiddenFunctions;
    }

    /// <summary>
    /// Gets plugin names whose containers should be suppressed by skills.
    /// Returns plugins that are referenced by skills with SuppressPluginContainers = true.
    /// </summary>
    /// <returns>Set of plugin names to suppress</returns>
    public HashSet<string> GetSuppressedPluginContainers()
    {
        var suppressedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in _skillsByName.Values)
        {
            if (skill.SuppressPluginContainers && skill.PluginReferences?.Length > 0)
            {
                foreach (var pluginRef in skill.PluginReferences)
                {
                    suppressedPlugins.Add(pluginRef);
                }
            }
        }

        return suppressedPlugins;
    }

    /// <summary>
    /// Gets all function names referenced by ANY skill (for Skills-Only Mode).
    /// Returns the complete set of functions that should be visible when SkillsOnlyMode is enabled.
    /// </summary>
    /// <returns>Set of function names referenced by all skills</returns>
    public HashSet<string> GetAllSkillReferencedFunctions()
    {
        var referencedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in _skillsByName.Values)
        {
            foreach (var reference in skill.ResolvedFunctionReferences)
            {
                // Extract function name (strip plugin prefix if present)
                var functionName = ExtractFunctionName(reference);
                referencedFunctions.Add(functionName);
            }
        }

        return referencedFunctions;
    }

    /// <summary>
    /// Gets the functions to include based on expanded skills, with deduplication.
    /// When multiple expanded skills reference the same function, it appears only once.
    /// </summary>
    /// <param name="expandedSkills">Set of skill names that have been expanded this message turn</param>
    /// <returns>Deduplicated list of functions from all expanded skills</returns>
    public List<AIFunction> GetFunctionsForExpandedSkills(HashSet<string> expandedSkills)
    {
        if (expandedSkills.Count == 0)
        {
            return new List<AIFunction>();
        }

        // Collect all unique function references from expanded skills
        var uniqueReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expandedSkillsList = new List<string>();

        foreach (var skillName in expandedSkills)
        {
            if (_skillsByName.TryGetValue(skillName, out var skill))
            {
                expandedSkillsList.Add(skillName);
                foreach (var reference in skill.ResolvedFunctionReferences)
                {
                    uniqueReferences.Add(reference);
                }
            }
            else
            {
                _logger?.LogWarning(
                    "Expanded skill '{SkillName}' not found in registered skills",
                    skillName);
            }
        }

        // Resolve each unique reference to its AIFunction (deduplication happens here)
        var functions = new List<AIFunction>();
        var missingReferences = new List<string>();

        foreach (var reference in uniqueReferences)
        {
            if (_allFunctionsByReference.TryGetValue(reference, out var function))
            {
                functions.Add(function);
            }
            else
            {
                missingReferences.Add(reference);
            }
        }

        // Log diagnostics
        if (functions.Count > 0)
        {
            _logger?.LogDebug(
                "Expanded skills '{Skills}' provide {Count} unique functions: {Functions}",
                string.Join(", ", expandedSkillsList),
                functions.Count,
                string.Join(", ", functions.Select(f => f.Name)));
        }

        if (missingReferences.Count > 0)
        {
            _logger?.LogWarning(
                "Failed to resolve {Count} function references from expanded skills: {References}",
                missingReferences.Count,
                string.Join(", ", missingReferences));
        }

        return functions.OrderBy(f => f.Name).ToList();
    }

    /// <summary>
    /// Extracts the function name from a reference.
    /// "FileSystemPlugin.ReadFile" -> "ReadFile"
    /// "ReadFile" -> "ReadFile"
    /// </summary>
    private static string ExtractFunctionName(string reference)
    {
        var lastDot = reference.LastIndexOf('.');
        return lastDot >= 0 ? reference.Substring(lastDot + 1) : reference;
    }

    /// <summary>
    /// Gets all skill container functions that haven't been expanded yet.
    /// Auto-expanded skills are excluded from container list.
    /// </summary>
    /// <param name="expandedSkills">Set of skill names that have been expanded this message turn</param>
    /// <returns>List of unexpanded skill containers</returns>
    public List<AIFunction> GetUnexpandedSkillContainers(HashSet<string> expandedSkills)
    {
        return _skillsByName.Values
            .Where(skill => !expandedSkills.Contains(skill.Name) && !skill.AutoExpand)
            .Select(skill => skill.CreateContainer())
            .OrderBy(c => c.Name)
            .ToList();
    }

    /// <summary>
    /// Gets all registered skills.
    /// </summary>
    /// <returns>All skill definitions</returns>
    public IEnumerable<SkillDefinition> GetSkills()
    {
        return _skillsByName.Values;
    }

    /// <summary>
    /// Checks if a function is a skill container.
    /// </summary>
    public bool IsSkillContainer(AIFunction function)
    {
        return function.AdditionalProperties
            ?.TryGetValue("IsSkill", out var value) == true
            && value is bool isSkill
            && isSkill;
    }

    /// <summary>
    /// Gets the skill name from a skill container function.
    /// </summary>
    public string GetSkillName(AIFunction function)
    {
        return function.AdditionalProperties
            ?.TryGetValue("SkillName", out var value) == true
            && value is string skillName
            ? skillName
            : function.Name ?? string.Empty;
    }

    /// <summary>
    /// Gets the parent plugin name from a function's metadata.
    /// </summary>
    private string? GetParentPlugin(AIFunction function)
    {
        return function.AdditionalProperties
            ?.TryGetValue("ParentPlugin", out var value) == true
            && value is string parentPlugin
            ? parentPlugin
            : null;
    }

    /// <summary>
    /// Gets the function references from a skill container.
    /// </summary>
    public string[] GetFunctionReferences(AIFunction skillContainer)
    {
        return skillContainer.AdditionalProperties
            ?.TryGetValue("FunctionReferences", out var value) == true
            && value is string[] references
            ? references
            : Array.Empty<string>();
    }
}
