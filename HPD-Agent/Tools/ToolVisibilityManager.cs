using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;

namespace HPD.Agent.Collapsing;

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
    /// 1. Collapse containers (skill class containers with [Collapse])
    /// 2. Plugin containers (Collapse plugins with [Collapse])
    /// 3. Skill containers (type-safe Skills with IsContainer=true)
    /// 4. Non-Collapsed functions (always visible)
    /// 5. Expanded plugin functions
    /// 6. Expanded skill functions
    ///
    /// Key insight: Functions in plugins that are ONLY referenced by Collapsed skills
    /// are hidden until their parent skill is expanded. This prevents "orphan" functions
    /// from appearing when the skill class Collapse is not expanded.
    /// </summary>
    /// <param name="allTools">All available tools</param>
    /// <param name="expandedContainers">Unified set of expanded containers (both plugins and skills)</param>
    public List<AIFunction> GetToolsForAgentTurn(
        List<AIFunction> allTools,
        ImmutableHashSet<string> expandedContainers)
    {
        // Use the same set for both plugins and skills (unified container tracking)
        return GetToolsForAgentTurn(allTools, expandedContainers, expandedContainers);
    }

    /// <summary>
    /// Gets tools visible for the current agent turn based on expansion state.
    /// Handles plugin containers and type-safe Skill containers.
    ///
    /// Ordering strategy:
    /// 1. Collapse containers (skill class containers with [Collapse])
    /// 2. Plugin containers (Collapse plugins with [Collapse])
    /// 3. Skill containers (type-safe Skills with IsContainer=true)
    /// 4. Non-Collapsed functions (always visible)
    /// 5. Expanded plugin functions
    /// 6. Expanded skill functions
    ///
    /// Key insight: Functions in plugins that are ONLY referenced by Collapsed skills
    /// are hidden until their parent skill is expanded. This prevents "orphan" functions
    /// from appearing when the skill class Collapse is not expanded.
    /// </summary>
    /// <param name="allTools">All available tools</param>
    /// <param name="expandedCollapsedPluginContainers">Set of expanded plugin containers</param>
    /// <param name="expandedSkillContainers">Set of expanded skill containers</param>
    /// <remarks>
    /// This overload is maintained for backward compatibility. Prefer using the single-parameter
    /// overload with unified ExpandedContainers.
    /// </remarks>
    public List<AIFunction> GetToolsForAgentTurn(
        List<AIFunction> allTools,
        ImmutableHashSet<string> expandedCollapsedPluginContainers,
        ImmutableHashSet<string> expandedSkillContainers)
    {
        // Phase 1: Build context (first pass - identify relationships)
        var context = BuildVisibilityContext(allTools, expandedCollapsedPluginContainers, expandedSkillContainers);

        var CollapseContainers = new List<AIFunction>();
        var skillContainers = new List<AIFunction>();
        var nonCollapsedFunctions = new List<AIFunction>();
        var expandedPluginFunctions = new List<AIFunction>();
        var expandedSkillFunctions = new List<AIFunction>();

        // Phase 2: Categorize tools using visibility rules (second pass)
        foreach (var tool in allTools)
        {
            var containerType = GetContainerType(tool);

            switch (containerType)
            {
                case ContainerType.CollapseAttributeContainer:
                case ContainerType.CollapsedPluginContainer:
                    // Both types are Collapse/plugin containers - treat identically
                    if (IsCollapseContainerVisible(tool, context))
                    {
                        CollapseContainers.Add(tool);
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
                            case FunctionVisibility.NonCollapsed:
                                nonCollapsedFunctions.Add(tool);
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
        // Order: Collapse containers -> skill containers -> non-Collapsed -> expanded functions
        var result = CollapseContainers.OrderBy(c => c.Name)
            .Concat(skillContainers.OrderBy(c => c.Name))
            .Concat(nonCollapsedFunctions.OrderBy(f => f.Name))
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

    private string? GetParentCollapseContainer(AIFunction function) =>
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

        // Check for IsCollapse flag (highest priority - [Collapse] attribute)
        if (function.AdditionalProperties?.TryGetValue("IsCollapse", out var CollapseVal) == true &&
            CollapseVal is bool CollapseFlag && CollapseFlag)
        {
            return ContainerType.CollapseAttributeContainer;
        }

        // Check for IsSkill flag ([Skill] method container)
        if (function.AdditionalProperties?.TryGetValue("IsSkill", out var skillVal) == true &&
            skillVal is bool skillFlag && skillFlag)
        {
            return ContainerType.SkillMethodContainer;
        }

        // Container with no special flags = legacy Collapsed plugin
        return ContainerType.CollapsedPluginContainer;
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
        /// Metadata: IsCollapse=true, IsContainer=true
        /// Generated by SkillCodeGenerator.GenerateCollapseContainer()
        /// </summary>
        CollapseAttributeContainer,

        /// <summary>
        /// Container for a Collapsed plugin WITHOUT skills (plugin-level Collapsing only).
        /// Example: [Collapse("Math")] on MathPlugin class with only [AIFunction] methods
        /// Metadata: IsContainer=true, no IsSkill/IsCollapse flags
        /// Generated by HPDPluginSourceGenerator.GeneratePluginContainer()
        /// Note: Both CollapseAttributeContainer and CollapsedPluginContainer are treated identically at runtime.
        /// </summary>
        CollapsedPluginContainer,

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
        NonCollapsed,        // Always visible (goes into nonCollapsedFunctions list)
        ExpandedPlugin,   // Visible because parent plugin expanded (goes into expandedPluginFunctions list)
        ExpandedSkill     // Visible because skill expanded (goes into expandedSkillFunctions list)
    }

    /// <summary>
    /// Rule: Collapse container is visible IFF:
    /// 1. It is NOT expanded, AND
    /// 2. It is NOT implicitly registered via skills (unless explicitly registered)
    /// Collapse containers can be tracked in either expandedCollapsedPluginContainers or ExpandedSkillContainers.
    /// </summary>
    private bool IsCollapseContainerVisible(AIFunction container, VisibilityContext context)
    {
        // For CollapsedPluginContainer, use PluginName. For CollapseAttributeContainer, use Name.
        // Both should work with the same string since they represent the same Collapse.
        var CollapseName = GetPluginName(container);
        if (string.IsNullOrEmpty(CollapseName))
        {
            CollapseName = container.Name ?? string.Empty;
        }

        // Hide Collapse containers for plugins that were ONLY implicitly registered via skills
        // (i.e., referenced by skills but NOT explicitly registered by the user)
        if (context.PluginsWithCollapsedSkills.Contains(CollapseName) &&
            !_explicitlyRegisteredPlugins.Contains(CollapseName))
        {
            _logger?.LogDebug($"[VISIBILITY] Collapse container {CollapseName}: HIDDEN (implicitly registered via skills)");
            return false;
        }

        // Hide if expanded (in either set)
        if (context.ExpandedCollapsedPluginContainers.Contains(CollapseName) ||
            context.ExpandedSkillContainers.Contains(CollapseName))
        {
            _logger?.LogDebug($"[VISIBILITY] Collapse container {CollapseName}: HIDDEN (expanded)");
            return false;
        }

        _logger?.LogDebug($"[VISIBILITY] Collapse container {CollapseName}: VISIBLE (not expanded)");
        return true;
    }

    /// <summary>
    /// Rule: Skill container is visible IFF:
    /// 1. It is NOT expanded, AND
    /// 2. Parent Collapse is expanded (if it has a parent Collapse)
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

        var parentCollapse = GetParentCollapseContainer(container);

        // Case 1: No parent Collapse - treat like regular skill
        if (string.IsNullOrEmpty(parentCollapse))
        {
            _logger?.LogDebug($"[VISIBILITY] Skill container {skillName}: VISIBLE (no parent Collapse)");
            return true;
        }

        // Case 2: Parent Collapse doesn't exist - treat like standalone skill
        if (!context.SkillClassesWithCollapse.Contains(parentCollapse))
        {
            _logger?.LogDebug($"[VISIBILITY] Skill container {skillName}: VISIBLE (parent Collapse {parentCollapse} doesn't exist)");
            return true;
        }

        // Case 3: Parent Collapse exists - must be expanded
        if (context.ExpandedSkillContainers.Contains(parentCollapse) ||
            context.ExpandedCollapsedPluginContainers.Contains(parentCollapse))
        {
            _logger?.LogDebug($"[VISIBILITY] Skill container {skillName}: VISIBLE (parent Collapse {parentCollapse} expanded)");
            return true;
        }

        // Otherwise: parent Collapse not expanded - hide
        _logger?.LogDebug($"[VISIBILITY] Skill container {skillName}: HIDDEN (parent Collapse {parentCollapse} not expanded)");
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
    /// PRIORITY 1: Parent plugin Collapse check with skill bypass
    ///   - If plugin has [Collapse] container AND function is referenced by an expanded skill → VISIBLE (skill bypass)
    ///   - If plugin has [Collapse] container AND parent plugin is expanded → VISIBLE
    ///   - If plugin has [Collapse] container AND not expanded → HIDDEN
    /// PRIORITY 2: Explicit registration check (always show if explicitly registered)
    /// PRIORITY 3: Skill reference check (show if any referencing skill is expanded)
    /// PRIORITY 4: Special case - read_skill_document
    /// PRIORITY 5: Orphan check (hide functions in implicitly-registered plugins that aren't referenced)
    /// DEFAULT: Non-Collapsed, non-referenced, non-orphan functions are always visible
    /// </summary>
    private FunctionVisibility GetFunctionVisibility(AIFunction function, VisibilityContext context)
    {
        var functionName = function.Name ?? string.Empty;
        var parentPlugin = GetParentPlugin(function);

        // PRIORITY 1: If plugin has [Collapse] container, check skill bypass first
        if (parentPlugin != null && context.PluginsWithContainers.Contains(parentPlugin))
        {
            // Check if this function is referenced by an expanded skill (skill bypass for Collapsed plugins)
            if (context.FunctionsReferencedBySkills.Contains(functionName))
            {
                var referencingSkills = context.SkillsReferencingFunction.GetValueOrDefault(functionName, new List<string>());
                bool anySkillExpanded = referencingSkills.Any(s => context.ExpandedSkillContainers.Contains(s));

                if (anySkillExpanded)
                {
                    _logger?.LogDebug($"[VISIBILITY] Function {functionName}: VISIBLE (skill bypass for Collapsed plugin {parentPlugin})");
                    return FunctionVisibility.ExpandedSkill;
                }
            }

            // Otherwise, Collapsed plugin function - only show if parent expanded
            if (context.ExpandedCollapsedPluginContainers.Contains(parentPlugin))
            {
                _logger?.LogDebug($"[VISIBILITY] Function {functionName}: VISIBLE (Collapsed parent {parentPlugin} expanded)");
                return FunctionVisibility.ExpandedPlugin;
            }

            _logger?.LogDebug($"[VISIBILITY] Function {functionName}: HIDDEN (Collapsed parent {parentPlugin} not expanded)");
            return FunctionVisibility.Hidden;
        }

        // PRIORITY 2: If plugin is explicitly registered (and NOT Collapsed), show all its functions
        // (Explicit registration takes precedence over skill references)
        if (parentPlugin != null && _explicitlyRegisteredPlugins.Contains(parentPlugin))
        {
            // Explicitly registered plugin - always show functions
            _logger?.LogDebug($"[VISIBILITY] Function {functionName}: VISIBLE (explicitly registered plugin)");
            return FunctionVisibility.NonCollapsed;
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
        if (parentPlugin != null && context.PluginsWithCollapsedSkills.Contains(parentPlugin))
        {
            // This function is in a plugin that was auto-registered via Collapsed skills
            // BUT this function is NOT referenced by any skill (it's an orphan)
            // Hide it - don't add to any list
            _logger?.LogDebug($"[VISIBILITY] Function {functionName}: HIDDEN (orphan in implicitly-registered plugin {parentPlugin})");
            return FunctionVisibility.Hidden;
        }

        // DEFAULT: Non-Collapsed function - always visible
        _logger?.LogDebug($"[VISIBILITY] Function {functionName}: VISIBLE (non-Collapsed, default)");
        return FunctionVisibility.NonCollapsed;
    }

    /// <summary>
    /// Builds the visibility context by analyzing all tools and computing relationships.
    /// This is the first pass that identifies Collapsed items and parent-child relationships.
    /// </summary>
    private VisibilityContext BuildVisibilityContext(
        List<AIFunction> allTools,
        ImmutableHashSet<string> expandedCollapsedPluginContainers,
        ImmutableHashSet<string> expandedSkillContainers)
    {
        var context = new VisibilityContext
        {
            AllTools = allTools,
            ExpandedCollapsedPluginContainers = expandedCollapsedPluginContainers,
            ExpandedSkillContainers = expandedSkillContainers,
            PluginsWithContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SkillClassesWithCollapse = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FunctionsReferencedBySkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SkillsReferencingFunction = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
            PluginsWithCollapsedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var tool in allTools)
        {
            var containerType = GetContainerType(tool);

            switch (containerType)
            {
                case ContainerType.CollapseAttributeContainer:
                    // Collapse container (can be class-level or plugin-level)
                    var CollapseName = tool.Name ?? string.Empty;
                    context.SkillClassesWithCollapse.Add(CollapseName);
                    // Also track as a plugin with container so functions get hidden/shown properly
                    context.PluginsWithContainers.Add(CollapseName);
                    break;

                case ContainerType.CollapsedPluginContainer:
                    var pluginName = GetPluginName(tool);
                    context.PluginsWithContainers.Add(pluginName);
                    break;

                case ContainerType.SkillMethodContainer:
                    // Track which functions this skill references
                    var skillName = GetSkillName(tool);
                    var referencedFunctions = GetReferencedFunctions(tool);
                    var referencedPlugins = GetReferencedPlugins(tool);
                    var parentSkillContainer = GetParentSkillContainer(tool);

                    // Mark plugins as having Collapsed skills ONLY if they are from a DIFFERENT plugin
                    // (i.e., skills referencing functions from external plugins)
                    foreach (var referencedPlugin in referencedPlugins)
                    {
                        // Only add if the referenced plugin is different from the skill's parent container
                        if (!string.Equals(referencedPlugin, parentSkillContainer, StringComparison.OrdinalIgnoreCase))
                        {
                            context.PluginsWithCollapsedSkills.Add(referencedPlugin);
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
        public required ImmutableHashSet<string> ExpandedCollapsedPluginContainers { get; init; }
        public required ImmutableHashSet<string> ExpandedSkillContainers { get; init; }
        public required HashSet<string> PluginsWithContainers { get; init; }
        public required HashSet<string> SkillClassesWithCollapse { get; init; }
        public required HashSet<string> FunctionsReferencedBySkills { get; init; }
        public required Dictionary<string, List<string>> SkillsReferencingFunction { get; init; }
        public required HashSet<string> PluginsWithCollapsedSkills { get; init; }
    }
}
