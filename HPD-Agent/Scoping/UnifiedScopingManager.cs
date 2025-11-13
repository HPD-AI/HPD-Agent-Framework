using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;

namespace HPD_Agent.Scoping;

/// <summary>
/// Unified scoping manager that handles plugin and skill scoping.
/// Skills are handled via the type-safe Skill class and plugin system.
/// </summary>
public class UnifiedScopingManager
{
    private readonly ILogger<UnifiedScopingManager>? _logger;
    private readonly Dictionary<string, AIFunction> _allFunctionsByReference;
    private readonly ImmutableHashSet<string> _explicitlyRegisteredPlugins;

    public UnifiedScopingManager(
        IEnumerable<AIFunction> allFunctions,
        ILogger<UnifiedScopingManager>? logger = null)
        : this(allFunctions, ImmutableHashSet<string>.Empty, logger)
    {
    }

    public UnifiedScopingManager(
        IEnumerable<AIFunction> allFunctions,
        ImmutableHashSet<string> explicitlyRegisteredPlugins,
        ILogger<UnifiedScopingManager>? logger = null)
    {
        _logger = logger;
        _explicitlyRegisteredPlugins = explicitlyRegisteredPlugins ?? ImmutableHashSet<string>.Empty;
        _allFunctionsByReference = BuildFunctionLookup(allFunctions);
        
        // DEBUG: Log explicitly registered plugins
        if (_explicitlyRegisteredPlugins.Count > 0)
        {
            Console.WriteLine($"[UnifiedScopingManager] 🔍 Explicitly Registered Plugins: {string.Join(", ", _explicitlyRegisteredPlugins)}");
        }
        else
        {
            Console.WriteLine($"[UnifiedScopingManager] ⚠️ No explicitly registered plugins!");
        }
    }

    /// <summary>
    /// Gets tools visible for the current agent turn based on expansion state.
    /// Handles plugin containers and type-safe Skill containers.
    ///
    /// Ordering strategy:
    /// 1. Scope containers (skill class containers with [Scope])
    /// 2. Plugin containers (collapsed plugins with [Scope])
    /// 3. Skill containers (type-safe Skills with IsContainer=true)
    /// 4. Non-scoped functions (always visible)
    /// 5. Expanded plugin functions
    /// 6. Expanded skill functions
    /// 
    /// Key insight: Functions in plugins that are ONLY referenced by scoped skills
    /// are hidden until their parent skill is expanded. This prevents "orphan" functions
    /// from appearing when the skill class scope is not expanded.
    /// </summary>
    public List<AIFunction> GetToolsForAgentTurn(
        List<AIFunction> allTools,
        ImmutableHashSet<string> expandedPlugins,
        ImmutableHashSet<string> expandedSkills)
    {
        var scopeContainers = new List<AIFunction>();
        var pluginContainers = new List<AIFunction>();
        var skillContainers = new List<AIFunction>();
        var nonScopedFunctions = new List<AIFunction>();
        var expandedPluginFunctions = new List<AIFunction>();
        var expandedSkillFunctions = new List<AIFunction>();

        // First pass: identify scoped items and parent-child relationships
        var pluginsWithContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skillClassesWithScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var functionsReferencedBySkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skillsReferencingFunction = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var pluginsWithScopedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        Console.WriteLine($"[UnifiedScopingManager] 🔍 First Pass - Analyzing {allTools.Count} tools");
        
        foreach (var tool in allTools)
        {
            if (IsScopeContainer(tool))
            {
                // Class-level scope container (groups multiple skills)
                var scopeName = tool.Name ?? string.Empty;
                skillClassesWithScope.Add(scopeName);
                Console.WriteLine($"   📦 Scope Container: {scopeName}");
            }
            else if (IsPluginContainer(tool))
            {
                var pluginName = GetPluginName(tool);
                pluginsWithContainers.Add(pluginName);
                Console.WriteLine($"   🔌 Plugin Container: {pluginName}");
            }
            else if (IsSkillContainer(tool))
            {
                Console.WriteLine($"   🎯 Skill Container: {tool.Name}");
                // Track which functions this skill references
                var skillName = GetSkillName(tool);
                var referencedFunctions = GetReferencedFunctions(tool);
                var referencedPlugins = GetReferencedPlugins(tool);
                
                // Mark plugins as having scoped skills
                foreach (var pluginName in referencedPlugins)
                {
                    pluginsWithScopedSkills.Add(pluginName);
                }
                
                foreach (var funcRef in referencedFunctions)
                {
                    // Extract function name from "PluginName.FunctionName" format
                    var funcName = ExtractFunctionName(funcRef);
                    functionsReferencedBySkills.Add(funcName);
                    
                    if (!skillsReferencingFunction.ContainsKey(funcName))
                        skillsReferencingFunction[funcName] = new List<string>();
                    
                    skillsReferencingFunction[funcName].Add(skillName);
                }
            }
        }

        // Second pass: categorize all tools
        foreach (var tool in allTools)
        {
            if (IsScopeContainer(tool))
            {
                // Skill class scope container - always show (parent of skills)
                scopeContainers.Add(tool);
            }
            else if (IsPluginContainer(tool))
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
                // Skill container - show only if:
                // 1. No parent scope (standalone skill), OR
                // 2. Parent scope doesn't exist as a scope container (was removed), OR
                // 3. Parent scope is expanded
                var skillName = GetSkillName(tool);
                var parentScope = GetParentScopeContainer(tool);
                
                if (string.IsNullOrEmpty(parentScope))
                {
                    // No parent scope - treat like regular skill
                    if (!expandedSkills.Contains(skillName))
                    {
                        skillContainers.Add(tool);
                        Console.WriteLine($"   ✅ Showing skill (no parent): {skillName}");
                    }
                }
                else if (!skillClassesWithScope.Contains(parentScope))
                {
                    // Parent scope doesn't exist - treat like standalone skill
                    if (!expandedSkills.Contains(skillName))
                    {
                        skillContainers.Add(tool);
                        Console.WriteLine($"   ✅ Showing skill (parent doesn't exist): {skillName}");
                    }
                }
                else if (expandedSkills.Contains(parentScope))
                {
                    // Parent scope is expanded - show this skill
                    if (!expandedSkills.Contains(skillName))
                    {
                        skillContainers.Add(tool);
                        Console.WriteLine($"   ✅ Showing skill (parent expanded): {skillName}");
                    }
                }
                else
                {
                    Console.WriteLine($"   ❌ Hiding skill (parent not expanded): {skillName}");
                }
                // Otherwise hide (parent scope not expanded)
            }
            else if (IsSkill(tool))
            {
                // Type-safe Skill - handle based on expansion state
                var parentContainer = GetParentSkillContainer(tool);
                if (string.IsNullOrEmpty(parentContainer) || expandedSkills.Contains(parentContainer))
                {
                    expandedSkillFunctions.Add(tool);
                }
            }
            else
            {
                // Regular function
                var functionName = tool.Name ?? string.Empty;
                var parentPlugin = GetParentPlugin(tool);

                // DEBUG: Log parent plugin info
                if (functionName == "CalculateCurrentRatio" || functionName == "ComprehensiveBalanceSheetAnalysis")
                {
                    Console.WriteLine($"[UnifiedScopingManager] 🔍 {functionName}: parentPlugin='{parentPlugin}', in explicit set? {(parentPlugin != null ? _explicitlyRegisteredPlugins.Contains(parentPlugin) : false)}");
                }

                // PRIORITY 1: If plugin has [Scope] container, hide functions until expanded
                // (This applies even if plugin is explicitly registered)
                if (parentPlugin != null && pluginsWithContainers.Contains(parentPlugin))
                {
                    // Scoped plugin function - only show if parent expanded
                    if (expandedPlugins.Contains(parentPlugin))
                    {
                        Console.WriteLine($"[UnifiedScopingManager] ✅ Showing '{functionName}' (scoped plugin expanded: {parentPlugin})");
                        expandedPluginFunctions.Add(tool);
                    }
                    else
                    {
                        Console.WriteLine($"[UnifiedScopingManager] ⏸️ Hiding '{functionName}' (scoped plugin not expanded: {parentPlugin})");
                    }
                }
                // PRIORITY 2: If plugin is explicitly registered (and NOT scoped), show all its functions
                else if (parentPlugin != null && _explicitlyRegisteredPlugins.Contains(parentPlugin))
                {
                    // Explicitly registered plugin - always show functions
                    Console.WriteLine($"[UnifiedScopingManager] ✅ Showing '{functionName}' (explicit plugin: {parentPlugin})");
                    nonScopedFunctions.Add(tool);
                }
                // Check if this function is referenced by any skills
                else if (functionsReferencedBySkills.Contains(functionName))
                {
                    // Function is referenced by skill(s)
                    // Only show if at least one referencing skill is expanded
                    var referencingSkills = skillsReferencingFunction.GetValueOrDefault(functionName, new List<string>());
                    bool anySkillExpanded = referencingSkills.Any(s => expandedSkills.Contains(s));
                    
                    if (anySkillExpanded)
                    {
                        expandedSkillFunctions.Add(tool);
                    }
                    // If no skills expanded, function is hidden
                }
                else if (parentPlugin != null && pluginsWithScopedSkills.Contains(parentPlugin))
                {
                    // This function is in a plugin that was auto-registered via scoped skills
                    // BUT this function is NOT referenced by any skill (it's an orphan)
                    // Hide it - don't add to any list
                    Console.WriteLine($"[UnifiedScopingManager] ❌ Hiding orphan '{functionName}' (plugin '{parentPlugin}' auto-registered via skills)");
                }
                else
                {
                    // Non-scoped function - always visible
                    // (This includes functions in plugins referenced by skills but not scoped)
                    nonScopedFunctions.Add(tool);
                }
            }
        }

        // Combine and deduplicate
        // Order: scope containers -> plugin containers -> skill containers -> non-scoped -> expanded functions
        var result = scopeContainers.OrderBy(c => c.Name)
            .Concat(pluginContainers.OrderBy(c => c.Name))
            .Concat(skillContainers.OrderBy(c => c.Name))
            .Concat(nonScopedFunctions.OrderBy(f => f.Name))
            .Concat(expandedPluginFunctions.OrderBy(f => f.Name))
            .Concat(expandedSkillFunctions.OrderBy(f => f.Name))
            .DistinctBy(f => f.Name)
            .ToList();
        
        // DEBUG: Show what's actually returned
        Console.WriteLine($"[UnifiedScopingManager] 🎯 Returning {result.Count} tools:");
        foreach (var tool in result)
        {
            Console.WriteLine($"   - {tool.Name}");
        }
        
        return result;
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

    private bool IsScopeContainer(AIFunction function) =>
        IsContainer(function) &&
        function.AdditionalProperties?.TryGetValue("IsScope", out var v) == true && v is bool b && b;

    private bool IsPluginContainer(AIFunction function) =>
        IsContainer(function) &&
        !(function.AdditionalProperties?.TryGetValue("IsSkill", out var v) == true && v is bool b && b) &&
        !(function.AdditionalProperties?.TryGetValue("IsScope", out var v2) == true && v2 is bool b2 && b2);

    private bool IsSkillContainer(AIFunction function) =>
        IsContainer(function) &&
        function.AdditionalProperties?.TryGetValue("IsSkill", out var v) == true && v is bool b && b &&
        !(function.AdditionalProperties?.TryGetValue("IsScope", out var v2) == true && v2 is bool b2 && b2);

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

    private string? GetParentScopeContainer(AIFunction function) =>
        function.AdditionalProperties?.TryGetValue("ParentSkillContainer", out var v) == true && v is string s ? s : null;

    private string[] GetReferencedFunctions(AIFunction skillContainer)
    {
        if (skillContainer.AdditionalProperties?.TryGetValue("ReferencedFunctions", out var v) == true && v is string[] refs)
            return refs;
        return Array.Empty<string>();
    }

    private string[] GetReferencedPlugins(AIFunction skillContainer)
    {
        if (skillContainer.AdditionalProperties?.TryGetValue("ReferencedPlugins", out var v) == true && v is string[] plugins)
            return plugins;
        return Array.Empty<string>();
    }

    private string ExtractFunctionName(string reference)
    {
        // "PluginName.FunctionName" -> "FunctionName"
        var lastDot = reference.LastIndexOf('.');
        return lastDot >= 0 ? reference.Substring(lastDot + 1) : reference;
    }
}
