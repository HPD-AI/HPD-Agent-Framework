using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Manages plugin scoping and tool visibility based on expansion state.
/// Implements container-first ordering and filters tools based on which plugins have been expanded.
/// </summary>
public class PluginScopingManager
{
    /// <summary>
    /// Builds the tools list for the current agent turn based on expansion state.
    ///
    /// Ordering strategy (CRITICAL for agent behavior):
    /// 1. Containers (collapsed plugins) - Always first for discoverability
    /// 2. Non-Plugin Functions (core utilities) - Always visible
    /// 3. Expanded Functions (from expanded plugins) - Only when parent expanded
    /// </summary>
    /// <param name="allTools">All available tools (containers + functions)</param>
    /// <param name="expandedPlugins">Set of plugin names that have been expanded this message turn</param>
    /// <returns>Ordered list of tools visible to the agent this turn</returns>
    public List<AIFunction> GetToolsForAgentTurn(
        List<AIFunction> allTools,
        HashSet<string> expandedPlugins)
    {
        var containers = new List<AIFunction>();
        var nonPluginFunctions = new List<AIFunction>();
        var expandedFunctions = new List<AIFunction>();

        // First pass: collect all plugins that have containers (= scoped plugins)
        var pluginsWithContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in allTools)
        {
            if (IsContainer(tool))
            {
                var pluginName = GetPluginName(tool);
                pluginsWithContainers.Add(pluginName);
            }
        }

        // Second pass: categorize functions based on scoping
        foreach (var tool in allTools)
        {
            if (IsContainer(tool))
            {
                // Only show containers that haven't been expanded
                var pluginName = GetPluginName(tool);
                if (!expandedPlugins.Contains(pluginName))
                {
                    containers.Add(tool);
                }
            }
            else
            {
                var parentPlugin = GetParentPlugin(tool);
                if (parentPlugin != null && pluginsWithContainers.Contains(parentPlugin))
                {
                    // Plugin function from SCOPED plugin - only show if parent is expanded
                    if (expandedPlugins.Contains(parentPlugin))
                    {
                        expandedFunctions.Add(tool);
                    }
                }
                else
                {
                    // Non-scoped function - always visible
                    // This includes:
                    // - Functions with no ParentPlugin metadata (old behavior)
                    // - Functions with ParentPlugin but plugin has no [PluginScope] (new behavior)
                    nonPluginFunctions.Add(tool);
                }
            }
        }

        // Order: Containers first, then non-plugin functions, then expanded functions
        // This ordering is MANDATORY for proper agent behavior (see design doc)
        return containers.OrderBy(c => c.Name)
            .Concat(nonPluginFunctions.OrderBy(f => f.Name))
            .Concat(expandedFunctions.OrderBy(f => f.Name))
            .ToList();
    }

    /// <summary>
    /// Checks if a function is a container.
    /// </summary>
    /// <param name="function">The function to check</param>
    /// <returns>True if this is a container function, false otherwise</returns>
    public bool IsContainer(AIFunction function)
    {
        return function.AdditionalProperties
            ?.TryGetValue("IsContainer", out var value) == true
            && value is bool isContainer
            && isContainer;
    }

    /// <summary>
    /// Gets the plugin name from a container function.
    /// </summary>
    /// <param name="function">The container function</param>
    /// <returns>The plugin name, or empty string if not found</returns>
    public string GetPluginName(AIFunction function)
    {
        return function.AdditionalProperties
            ?.TryGetValue("PluginName", out var value) == true
            && value is string pluginName
            ? pluginName
            : function.Name ?? string.Empty;
    }

    /// <summary>
    /// Gets the parent plugin name from a function.
    /// </summary>
    /// <param name="function">The function to check</param>
    /// <returns>The parent plugin name, or null if this is not a plugin function</returns>
    private string? GetParentPlugin(AIFunction function)
    {
        return function.AdditionalProperties
            ?.TryGetValue("ParentPlugin", out var value) == true
            && value is string parentPlugin
            ? parentPlugin
            : null;
    }
}
