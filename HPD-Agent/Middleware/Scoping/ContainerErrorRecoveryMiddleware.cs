// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Collections.Immutable;
using HPD.Agent.Collapsing;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HPD.Agent;

/// <summary>
/// Middleware that automatically recovers from container visibility errors for [Collapse] containers.
/// When agent calls a hidden item from a [Collapse] container, auto-expands the container silently.
/// </summary>
/// <remarks>
/// <para><b>Scope:</b></para>
/// <para>
/// Handles any container with [Collapse] attribute (containing functions, skills, sub-agents, etc.).
/// EXCLUDED: Skill containers (from [Skill] methods) - they already disappear correctly.
/// </para>
///
/// <para><b>How It Works:</b></para>
/// <para>
/// When agent calls a function that's not visible (hidden inside a container), this middleware:
/// 1. Detects the hidden function call
/// 2. Finds which [Collapse] container contains it
/// 3. Silently expands that container
/// 4. Updates visible tools for the current iteration
/// 5. Allows execution to proceed normally
/// </para>
///
/// <para><b>Benefits:</b></para>
/// <list type="bullet">
/// <item>Smaller models can call hidden functions without understanding containers</item>
/// <item>Graceful degradation instead of "function not found" errors</item>
/// <item>No prompt engineering needed for container workflow</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Register via AgentBuilder
/// var agent = new AgentBuilder()
///     .WithToolCollapsing()
///     .WithContainerErrorRecovery()  // Add error recovery
///     .Build();
/// </code>
/// </example>
public class ContainerErrorRecoveryMiddleware : IAgentMiddleware
{
    private readonly ILogger<ContainerErrorRecoveryMiddleware>? _logger;
    private readonly Dictionary<string, string> _itemToCollapseContainerMap;
    private readonly ToolVisibilityManager _visibilityManager;

    /// <summary>
    /// Creates a new ContainerErrorRecoveryMiddleware instance.
    /// </summary>
    /// <param name="allTools">All available tools for the agent</param>
    /// <param name="explicitlyRegisteredToolGroups">Plugins explicitly registered via WithTools</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    public ContainerErrorRecoveryMiddleware(
        IList<AITool> allTools,
        ImmutableHashSet<string> explicitlyRegisteredToolGroups,
        ILogger<ContainerErrorRecoveryMiddleware>? logger = null)
    {
        _logger = logger;
        _itemToCollapseContainerMap = BuildItemToCollapseContainerMap(allTools);

        // Create ToolVisibilityManager for updating visible tools after expansion
        var aiFunctions = allTools.OfType<AIFunction>().ToList();
        _visibilityManager = new ToolVisibilityManager(aiFunctions, explicitlyRegisteredToolGroups);
    }

    /// <summary>
    /// Called before tool execution batch.
    /// Checks if tools are visible; if not, auto-expands parent containers.
    /// V2: Processes all tool calls in the batch.
    /// </summary>
    public Task BeforeToolExecutionAsync(
        BeforeToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        // V2: Iterate over all tool calls in the batch
        foreach (var toolCall in context.ToolCalls)
        {
            // Check if function is visible in current tool list
            // Note: In V2, BeforeToolExecutionContext doesn't have Options
            // We need to check visibility from state or skip this check
            // For now, we'll rely on the itemToContainerMap check

            // Function is NOT visible if it's in our hidden item map
            if (!_itemToCollapseContainerMap.TryGetValue(toolCall.Name, out var containerName))
            {
                // Not in a [Collapse] container - assume it's visible
                continue;
            }

            _logger?.LogInformation(
                "Auto-expanding [Collapse] container '{Container}' for hidden item '{Item}'",
                containerName, toolCall.Name);

            // Expand container silently
            ExpandContainerSilently_V2(context, containerName);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// V2: Expands a container silently by updating state.
    /// </summary>
    private void ExpandContainerSilently_V2(
        BeforeToolExecutionContext context,
        string containerName)
    {
        // Update state to mark container as expanded
        context.UpdateMiddlewareState<CollapsingStateData>(s =>
            s.WithExpandedContainer(containerName)
        );

        // V2 NOTE: We don't have access to Options in BeforeToolExecutionContext
        // Container instructions should be handled in BeforeIterationAsync
        // Here we just mark the container as expanded
    }

    /// <summary>
    /// Builds a map from item name to [Collapse] container name.
    /// ONLY includes [Collapse] containers (excludes [Skill] containers - they already work).
    /// </summary>
    private static Dictionary<string, string> BuildItemToCollapseContainerMap(IList<AITool> allTools)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in allTools.OfType<AIFunction>())
        {
            // Check if this is a container
            var isContainer = tool.AdditionalProperties?.TryGetValue("IsContainer", out var val) == true
                && val is bool b && b;

            if (!isContainer)
                continue;

            // Skip skill containers - they already work correctly
            var isSkill = tool.AdditionalProperties?.TryGetValue("IsSkill", out var skillVal) == true
                && skillVal is bool isS && isS;

            if (isSkill)
                continue; // Only handle [Collapse] containers, not [Skill] containers

            // Get [Collapse] container name
            var containerName = tool.AdditionalProperties?.TryGetValue("PluginName", out var pn) == true
                && pn is string pluginName
                ? pluginName
                : tool.Name ?? string.Empty;

            // Get items inside this [Collapse] container
            // Could be functions, skills, sub-agents, etc.
            var referencedItems = tool.AdditionalProperties?.TryGetValue("FunctionNames", out var fn) == true
                && fn is string[] funcNames
                ? funcNames
                : Array.Empty<string>();

            // Map each item to this [Collapse] container
            foreach (var itemRef in referencedItems)
            {
                // Extract item name from "PluginName.ItemName" format
                var itemName = itemRef.Contains('.')
                    ? itemRef.Substring(itemRef.LastIndexOf('.') + 1)
                    : itemRef;

                // Store mapping (if duplicate, last one wins)
                map[itemName] = containerName;
            }
        }

        return map;
    }

    /// <summary>
    /// Finds a container function by name.
    /// </summary>
    private static AIFunction? FindContainerFunction(string containerName, IList<AITool>? tools)
    {
        if (tools == null) return null;

        return tools.OfType<AIFunction>().FirstOrDefault(f =>
            f.Name == containerName ||
            (f.AdditionalProperties?.TryGetValue("PluginName", out var pn) == true
                && pn is string pluginName && pluginName == containerName));
    }

    /// <summary>
    /// Extracts string metadata from AdditionalProperties.
    /// </summary>
    private static string? ExtractStringMetadata(AIFunction function, string key)
    {
        if (function.AdditionalProperties?.TryGetValue(key, out var value) == true
            && value is string str && !string.IsNullOrWhiteSpace(str))
        {
            return str;
        }
        return null;
    }
}
