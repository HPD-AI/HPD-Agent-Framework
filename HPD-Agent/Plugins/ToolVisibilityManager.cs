using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;

namespace HPD_Agent.Scoping;

public class ToolVisibilityManager
{
    private readonly ILogger<ToolVisibilityManager>? _logger;
    private readonly Dictionary<string, AIFunction> _allFunctionsByReference;
    private readonly ImmutableHashSet<string> _explicitlyRegisteredPlugins;

    public ToolVisibilityManager(
        IEnumerable<AIFunction> allFunctions,
        ILogger<ToolVisibilityManager>? logger = null)
        : this(allFunctions, ImmutableHashSet<string>.Empty, logger)
    {
    }

    public ToolVisibilityManager(
        IEnumerable<AIFunction> allFunctions,
        ImmutableHashSet<string> explicitlyRegisteredPlugins,
        ILogger<ToolVisibilityManager>? logger = null)
    {
        _logger = logger;
        _explicitlyRegisteredPlugins = explicitlyRegisteredPlugins ?? ImmutableHashSet<string>.Empty;
        _allFunctionsByReference = BuildFunctionLookup(allFunctions);
    }

    /// <summary>
    /// Gets tools visible for the current agent turn based on expansion state.
    /// Handles plugin containers and type-safe Skill containers.
    ///
    /// Ordering strategy:
    /// 1. Scope containers (skill class containers with [Collapse])
    /// 2. Plugin containers (Collapse plugins with [Collapse])
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
        ImmutableHashSet<string> expandedScopedPluginContainers,
        ImmutableHashSet<string> expandedSkillContainers)
    {
        // Phase 1: Build context (first pass - identify relationships)
        var context = BuildVisibilityContext(allTools, expandedScopedPluginContainers, expandedSkillContainers);

        var scopeContainers = new List<AIFunction>();
        var skillContainers = new List<AIFunction>();
        var nonScopedFunctions = new List<AIFunction>();
        var expandedPluginFunctions = new List<AIFunction>();
        var expandedSkillFunctions = new List<AIFunction>();

        // Phase 2: Categorize tools using visibility rules (second pass)
        foreach (var tool in allTools)
        {
            var containerType = GetContainerType(tool);

            switch (containerType)
            {
                case ContainerType.ScopeAttributeContainer:
                case ContainerType.ScopedPluginContainer:
                    // Both types are scope/plugin containers - treat identically
                    if (IsScopeContainerVisible(tool, context))
                    {
                        scopeContainers.Add(tool);
                    }
                    break;

                case ContainerType.SkillMethodContainer:
                    if (IsSkillContainerVisible(tool, context))
                    {
                        skillContainers.Add(tool);
                    }
                    break;

                case ContainerType.NotAContainer:
                    // Not a container - check if it's a skill or regular function
                    if (IsSkill(tool))
                    {
                        if (IsSkillVisible(tool, context))
                        {
                            expandedSkillFunctions.Add(tool);
                        }
                    }
                    else
                    {
                        // Regular function - categorize by visibility reason
                        var visibility = GetFunctionVisibility(tool, context);

                        switch (visibility)
                        {
                            case FunctionVisibility.NonScoped:
                                nonScopedFunctions.Add(tool);
                                break;

                            case FunctionVisibility.ExpandedPlugin:
                                expandedPluginFunctions.Add(tool);
                                break;

                            case FunctionVisibility.ExpandedSkill:
                                expandedSkillFunctions.Add(tool);
                                break;

                            case FunctionVisibility.Hidden:
                                // Not visible this turn
                                break;
                        }
                    }
                    break;
            }
        }

        // Phase 3: Combine in priority order and deduplicate
        // Order: scope containers -> skill containers -> non-scoped -> expanded functions
        var result = scopeContainers.OrderBy(c => c.Name)
            .Concat(skillContainers.OrderBy(c => c.Name))
            .Concat(nonScopedFunctions.OrderBy(f => f.Name))
            .Concat(expandedPluginFunctions.OrderBy(f => f.Name))
            .Concat(expandedSkillFunctions.OrderBy(f => f.Name))
            .DistinctBy(f => f.Name)
            .ToList();

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

    /// <summary>
    /// Checks if a skill container has documents attached.
    /// </summary>
    private bool HasDocuments(AIFunction skillContainer)
    {
        if (skillContainer.AdditionalProperties == null)
            return false;

        // Check for DocumentUploads
        if (skillContainer.AdditionalProperties.TryGetValue("DocumentUploads", out var uploadsObj) &&
            uploadsObj is Array uploadsArray && uploadsArray.Length > 0)
        {
            return true;
        }

        // Check for DocumentReferences
        if (skillContainer.AdditionalProperties.TryGetValue("DocumentReferences", out var refsObj) &&
            refsObj is Array refsArray && refsArray.Length > 0)
        {
            return true;
        }

        return false;
    }

    private string ExtractFunctionName(string reference)
    {
        // "PluginName.FunctionName" -> "FunctionName"
        var lastDot = reference.LastIndexOf('.');
        return lastDot >= 0 ? reference.Substring(lastDot + 1) : reference;
    }

    /// <summary>
    /// Determines the type of container based on metadata flags.
    /// This is the single source of truth for container type classification.
    /// </summary>
    private ContainerType GetContainerType(AIFunction function)
    {
        if (!IsContainer(function))
            return ContainerType.NotAContainer;

        // Check for IsScope flag (highest priority - [Collapse] attribute)
        if (function.AdditionalProperties?.TryGetValue("IsScope", out var scopeVal) == true &&
            scopeVal is bool scopeFlag && scopeFlag)
        {
            return ContainerType.ScopeAttributeContainer;
        }

        // Check for IsSkill flag ([Skill] method container)
        if (function.AdditionalProperties?.TryGetValue("IsSkill", out var skillVal) == true &&
            skillVal is bool skillFlag && skillFlag)
        {
            return ContainerType.SkillMethodContainer;
        }

        // Container with no special flags = legacy scoped plugin
        return ContainerType.ScopedPluginContainer;
    }

    // ============================================
    // VISIBILITY RULES (Formal Model Implementation)
    // ============================================

    /// <summary>
    /// Container types in HPD-Agent, distinguished by metadata flags.
    /// </summary>
    private enum ContainerType
    {
        /// <summary>Not a container at all.</summary>
        NotAContainer,

        /// <summary>
        /// Container created by [Collapse] attribute on skill class WITH skills.
        /// Example: [Collapse("Analysis")] on FinancialAnalysisSkills class that has [Skill] methods
        /// Metadata: IsScope=true, IsContainer=true
        /// Generated by SkillCodeGenerator.GenerateScopeContainer()
        /// </summary>
        ScopeAttributeContainer,

        /// <summary>
        /// Container for a scoped plugin WITHOUT skills (plugin-level scoping only).
        /// Example: [Collapse("Math")] on MathPlugin class with only [AIFunction] methods
        /// Metadata: IsContainer=true, no IsSkill/IsScope flags
        /// Generated by HPDPluginSourceGenerator.GeneratePluginContainer()
        /// Note: Both ScopeAttributeContainer and ScopedPluginContainer are treated identically at runtime.
        /// </summary>
        ScopedPluginContainer,

        /// <summary>
        /// Container created by [Skill] method.
        /// Example: [Skill] public Skill QuickAnalysis()
        /// Metadata: IsSkill=true, IsContainer=true
        /// </summary>
        SkillMethodContainer
    }

    /// <summary>
    /// Indicates why a function is visible (or if it's hidden).
    /// Maps directly to the categorization lists in GetToolsForAgentTurn.
    /// </summary>
    private enum FunctionVisibility
    {
        Hidden,           // Not visible
        NonScoped,        // Always visible (goes into nonScopedFunctions list)
        ExpandedPlugin,   // Visible because parent plugin expanded (goes into expandedPluginFunctions list)
        ExpandedSkill     // Visible because skill expanded (goes into expandedSkillFunctions list)
    }

    /// <summary>
    /// Rule: Scope container is visible IFF:
    /// 1. It is NOT expanded, AND
    /// 2. It is NOT implicitly registered via skills (unless explicitly registered)
    /// Scope containers can be tracked in either expandedScopedPluginContainers or ExpandedSkillContainers.
    /// </summary>
    private bool IsScopeContainerVisible(AIFunction container, VisibilityContext context)
    {
        // For ScopedPluginContainer, use PluginName. For ScopeAttributeContainer, use Name.
        // Both should work with the same string since they represent the same scope.
        var scopeName = GetPluginName(container);
        if (string.IsNullOrEmpty(scopeName))
        {
            scopeName = container.Name ?? string.Empty;
        }

        // Hide scope containers for plugins that were ONLY implicitly registered via skills
        // (i.e., referenced by skills but NOT explicitly registered by the user)
        if (context.PluginsWithScopedSkills.Contains(scopeName) &&
            !_explicitlyRegisteredPlugins.Contains(scopeName))
        {
            _logger?.LogDebug($"[VISIBILITY] Scope container {scopeName}: HIDDEN (implicitly registered via skills)");
            return false;
        }

        // Hide if expanded (in either set)
        if (context.ExpandedScopedPluginContainers.Contains(scopeName) ||
            context.ExpandedSkillContainers.Contains(scopeName))
        {
            _logger?.LogDebug($"[VISIBILITY] Scope container {scopeName}: HIDDEN (expanded)");
            return false;
        }

        _logger?.LogDebug($"[VISIBILITY] Scope container {scopeName}: VISIBLE (not expanded)");
        return true;
    }

    /// <summary>
    /// Rule: Skill container is visible IFF:
    /// 1. It is NOT expanded, AND
    /// 2. Parent scope is expanded (if it has a parent scope)
    /// </summary>
    private bool IsSkillContainerVisible(AIFunction container, VisibilityContext context)
    {
        var skillName = GetSkillName(container);

        // Check if skill itself is expanded
        if (context.ExpandedSkillContainers.Contains(skillName))
        {
            _logger?.LogDebug($"[VISIBILITY] Skill container {skillName}: HIDDEN (skill expanded)");
            return false;
        }

        var parentScope = GetParentScopeContainer(container);

        // Case 1: No parent scope - treat like regular skill
        if (string.IsNullOrEmpty(parentScope))
        {
            _logger?.LogDebug($"[VISIBILITY] Skill container {skillName}: VISIBLE (no parent scope)");
            return true;
        }

        // Case 2: Parent scope doesn't exist - treat like standalone skill
        if (!context.SkillClassesWithScope.Contains(parentScope))
        {
            _logger?.LogDebug($"[VISIBILITY] Skill container {skillName}: VISIBLE (parent scope {parentScope} doesn't exist)");
            return true;
        }

        // Case 3: Parent scope exists - must be expanded
        if (context.ExpandedSkillContainers.Contains(parentScope) ||
            context.ExpandedScopedPluginContainers.Contains(parentScope))
        {
            _logger?.LogDebug($"[VISIBILITY] Skill container {skillName}: VISIBLE (parent scope {parentScope} expanded)");
            return true;
        }

        // Otherwise: parent scope not expanded - hide
        _logger?.LogDebug($"[VISIBILITY] Skill container {skillName}: HIDDEN (parent scope {parentScope} not expanded)");
        return false;
    }

    /// <summary>
    /// Rule: Type-safe Skill is visible IFF:
    /// Parent skill container is expanded (or no parent)
    /// </summary>
    private bool IsSkillVisible(AIFunction skill, VisibilityContext context)
    {
        var parentContainer = GetParentSkillContainer(skill);

        if (string.IsNullOrEmpty(parentContainer))
        {
            _logger?.LogDebug($"[VISIBILITY] Skill {skill.Name}: VISIBLE (no parent container)");
            return true;
        }

        if (context.ExpandedSkillContainers.Contains(parentContainer))
        {
            _logger?.LogDebug($"[VISIBILITY] Skill {skill.Name}: VISIBLE (parent container {parentContainer} expanded)");
            return true;
        }

        _logger?.LogDebug($"[VISIBILITY] Skill {skill.Name}: HIDDEN (parent container {parentContainer} not expanded)");
        return false;
    }

    /// <summary>
    /// Determines visibility and categorization for a function.
    /// Returns the visibility reason, which determines which list the function goes into.
    ///
    /// PRIORITY 1: Parent plugin scope check with skill bypass
    ///   - If plugin has [Collapse] container AND function is referenced by an expanded skill → VISIBLE (skill bypass)
    ///   - If plugin has [Collapse] container AND parent plugin is expanded → VISIBLE
    ///   - If plugin has [Collapse] container AND not expanded → HIDDEN
    /// PRIORITY 2: Explicit registration check (always show if explicitly registered)
    /// PRIORITY 3: Skill reference check (show if any referencing skill is expanded)
    /// PRIORITY 4: Special case - read_skill_document
    /// PRIORITY 5: Orphan check (hide functions in implicitly-registered plugins that aren't referenced)
    /// DEFAULT: Non-scoped, non-referenced, non-orphan functions are always visible
    /// </summary>
    private FunctionVisibility GetFunctionVisibility(AIFunction function, VisibilityContext context)
    {
        var functionName = function.Name ?? string.Empty;
        var parentPlugin = GetParentPlugin(function);

        // PRIORITY 1: If plugin has [Collapse] container, check skill bypass first
        if (parentPlugin != null && context.PluginsWithContainers.Contains(parentPlugin))
        {
            // Check if this function is referenced by an expanded skill (skill bypass for scoped plugins)
            if (context.FunctionsReferencedBySkills.Contains(functionName))
            {
                var referencingSkills = context.SkillsReferencingFunction.GetValueOrDefault(functionName, new List<string>());
                bool anySkillExpanded = referencingSkills.Any(s => context.ExpandedSkillContainers.Contains(s));

                if (anySkillExpanded)
                {
                    _logger?.LogDebug($"[VISIBILITY] Function {functionName}: VISIBLE (skill bypass for scoped plugin {parentPlugin})");
                    return FunctionVisibility.ExpandedSkill;
                }
            }

            // Otherwise, scoped plugin function - only show if parent expanded
            if (context.ExpandedScopedPluginContainers.Contains(parentPlugin))
            {
                _logger?.LogDebug($"[VISIBILITY] Function {functionName}: VISIBLE (scoped parent {parentPlugin} expanded)");
                return FunctionVisibility.ExpandedPlugin;
            }

            _logger?.LogDebug($"[VISIBILITY] Function {functionName}: HIDDEN (scoped parent {parentPlugin} not expanded)");
            return FunctionVisibility.Hidden;
        }

        // PRIORITY 2: If plugin is explicitly registered (and NOT scoped), show all its functions
        // (Explicit registration takes precedence over skill references)
        if (parentPlugin != null && _explicitlyRegisteredPlugins.Contains(parentPlugin))
        {
            // Explicitly registered plugin - always show functions
            _logger?.LogDebug($"[VISIBILITY] Function {functionName}: VISIBLE (explicitly registered plugin)");
            return FunctionVisibility.NonScoped;
        }

        // PRIORITY 3: Check if this function is referenced by any skills
        if (context.FunctionsReferencedBySkills.Contains(functionName))
        {
            // Function is referenced by skill(s)
            // Only show if at least one referencing skill is expanded
            var referencingSkills = context.SkillsReferencingFunction.GetValueOrDefault(functionName, new List<string>());
            bool anySkillExpanded = referencingSkills.Any(s => context.ExpandedSkillContainers.Contains(s));

            if (anySkillExpanded)
            {
                _logger?.LogDebug($"[VISIBILITY] Function {functionName}: VISIBLE (referenced by expanded skill)");
                return FunctionVisibility.ExpandedSkill;
            }

            // If no skills expanded, function is hidden
            _logger?.LogDebug($"[VISIBILITY] Function {functionName}: HIDDEN (referenced by skills, none expanded)");
            return FunctionVisibility.Hidden;
        }

        // PRIORITY 4: Special handling for read_skill_document
        if (functionName.Equals("read_skill_document", StringComparison.OrdinalIgnoreCase) ||
            functionName.Equals("ReadSkillDocument", StringComparison.OrdinalIgnoreCase))
        {
            // This function should only be visible when a skill with documents is expanded
            bool anySkillWithDocumentsExpanded = context.ExpandedSkillContainers.Any(skillName =>
            {
                // Find the skill container
                var skillContainer = context.AllTools.FirstOrDefault(t =>
                    GetContainerType(t) == ContainerType.SkillMethodContainer &&
                    GetSkillName(t).Equals(skillName, StringComparison.OrdinalIgnoreCase));

                if (skillContainer == null) return false;

                // Check if skill has documents
                return HasDocuments(skillContainer);
            });

            if (anySkillWithDocumentsExpanded)
            {
                _logger?.LogDebug($"[VISIBILITY] Function {functionName}: VISIBLE (skill with documents expanded)");
                return FunctionVisibility.ExpandedSkill;
            }

            // If no skill with documents is expanded, function is hidden
            _logger?.LogDebug($"[VISIBILITY] Function {functionName}: HIDDEN (no skill with documents expanded)");
            return FunctionVisibility.Hidden;
        }

        // PRIORITY 5: Orphan check
        if (parentPlugin != null && context.PluginsWithScopedSkills.Contains(parentPlugin))
        {
            // This function is in a plugin that was auto-registered via scoped skills
            // BUT this function is NOT referenced by any skill (it's an orphan)
            // Hide it - don't add to any list
            _logger?.LogDebug($"[VISIBILITY] Function {functionName}: HIDDEN (orphan in implicitly-registered plugin {parentPlugin})");
            return FunctionVisibility.Hidden;
        }

        // DEFAULT: Non-scoped function - always visible
        _logger?.LogDebug($"[VISIBILITY] Function {functionName}: VISIBLE (non-scoped, default)");
        return FunctionVisibility.NonScoped;
    }

    /// <summary>
    /// Builds the visibility context by analyzing all tools and computing relationships.
    /// This is the first pass that identifies scoped items and parent-child relationships.
    /// </summary>
    private VisibilityContext BuildVisibilityContext(
        List<AIFunction> allTools,
        ImmutableHashSet<string> expandedScopedPluginContainers,
        ImmutableHashSet<string> expandedSkillContainers)
    {
        var context = new VisibilityContext
        {
            AllTools = allTools,
            ExpandedScopedPluginContainers = expandedScopedPluginContainers,
            ExpandedSkillContainers = expandedSkillContainers,
            PluginsWithContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SkillClassesWithScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FunctionsReferencedBySkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SkillsReferencingFunction = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
            PluginsWithScopedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var tool in allTools)
        {
            var containerType = GetContainerType(tool);

            switch (containerType)
            {
                case ContainerType.ScopeAttributeContainer:
                    // Scope container (can be class-level or plugin-level)
                    var scopeName = tool.Name ?? string.Empty;
                    context.SkillClassesWithScope.Add(scopeName);
                    // Also track as a plugin with container so functions get hidden/shown properly
                    context.PluginsWithContainers.Add(scopeName);
                    break;

                case ContainerType.ScopedPluginContainer:
                    var pluginName = GetPluginName(tool);
                    context.PluginsWithContainers.Add(pluginName);
                    break;

                case ContainerType.SkillMethodContainer:
                    // Track which functions this skill references
                    var skillName = GetSkillName(tool);
                    var referencedFunctions = GetReferencedFunctions(tool);
                    var referencedPlugins = GetReferencedPlugins(tool);
                    var parentSkillContainer = GetParentSkillContainer(tool);

                    // Mark plugins as having scoped skills ONLY if they are from a DIFFERENT plugin
                    // (i.e., skills referencing functions from external plugins)
                    foreach (var referencedPlugin in referencedPlugins)
                    {
                        // Only add if the referenced plugin is different from the skill's parent container
                        if (!string.Equals(referencedPlugin, parentSkillContainer, StringComparison.OrdinalIgnoreCase))
                        {
                            context.PluginsWithScopedSkills.Add(referencedPlugin);
                        }
                    }

                    foreach (var funcRef in referencedFunctions)
                    {
                        // Extract function name from "PluginName.FunctionName" format
                        var funcName = ExtractFunctionName(funcRef);
                        context.FunctionsReferencedBySkills.Add(funcName);

                        if (!context.SkillsReferencingFunction.ContainsKey(funcName))
                            context.SkillsReferencingFunction[funcName] = [];

                        context.SkillsReferencingFunction[funcName].Add(skillName);
                    }
                    break;

                case ContainerType.NotAContainer:
                    // Not a container - nothing to track in context
                    break;
            }
        }

        return context;
    }

    /// <summary>
    /// Context object holding all computed relationships for visibility checks.
    /// Built once per GetToolsForAgentTurn call for efficiency.
    /// </summary>
    private class VisibilityContext
    {
        public required List<AIFunction> AllTools { get; init; }
        public required ImmutableHashSet<string> ExpandedScopedPluginContainers { get; init; }
        public required ImmutableHashSet<string> ExpandedSkillContainers { get; init; }
        public required HashSet<string> PluginsWithContainers { get; init; }
        public required HashSet<string> SkillClassesWithScope { get; init; }
        public required HashSet<string> FunctionsReferencedBySkills { get; init; }
        public required Dictionary<string, List<string>> SkillsReferencingFunction { get; init; }
        public required HashSet<string> PluginsWithScopedSkills { get; init; }
    }
}
