// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Collections.Immutable;
using System.Text.Json;
using HPD.Agent.FrontendTools;

namespace HPD.Agent;

/// <summary>
/// State for frontend tool middleware. Tracks registered plugins, visibility,
/// and pending augmentations during the current message turn.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b></para>
/// <para>
/// This state is immutable and flows through the context.
/// It is NOT stored in middleware instance fields, preserving thread safety
/// for concurrent RunAsync() calls.
/// </para>
///
/// <para><b>Usage:</b></para>
/// <code>
/// // Read state
/// var ftState = context.State.MiddlewareState.FrontendTool ?? new();
/// var isExpanded = ftState.ExpandedPlugins.Contains("ECommercePlugin");
///
/// // Update state
/// context.UpdateState(s => s with
/// {
///     MiddlewareState = s.MiddlewareState.WithFrontendTool(
///         ftState.WithExpandedPlugin("ECommercePlugin"))
/// });
/// </code>
///
/// <para><b>Lifecycle:</b></para>
/// <para>
/// - RegisteredPlugins persist across message turns (unless ResetFrontendState=true)
/// - ExpandedPlugins and HiddenTools can be modified via augmentation
/// - PendingAugmentation is applied at the start of each iteration
/// </para>
/// </remarks>
[MiddlewareState]
public sealed record FrontendToolStateData
{
    /// <summary>
    /// Registered plugins (source of truth for tools).
    /// Key is plugin name, value is the plugin definition.
    /// </summary>
    public ImmutableDictionary<string, FrontendPluginDefinition> RegisteredPlugins { get; init; }
        = ImmutableDictionary<string, FrontendPluginDefinition>.Empty;

    /// <summary>
    /// Plugins that are currently expanded (showing their tools).
    /// </summary>
    public ImmutableHashSet<string> ExpandedPlugins { get; init; }
        = ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Tools that are currently hidden (not visible to LLM but still registered).
    /// </summary>
    public ImmutableHashSet<string> HiddenTools { get; init; }
        = ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Context items provided by frontend.
    /// Key is the effective key (Key property or Description if Key is null).
    /// </summary>
    public ImmutableDictionary<string, ContextItem> Context { get; init; }
        = ImmutableDictionary<string, ContextItem>.Empty;

    /// <summary>
    /// Application state from the frontend.
    /// Opaque to the agent but available to tools.
    /// </summary>
    public JsonElement? State { get; init; }

    /// <summary>
    /// Pending augmentation to apply at the start of next iteration.
    /// Set when a frontend tool response includes augmentation data.
    /// </summary>
    public FrontendToolAugmentation? PendingAugmentation { get; init; }

    // ========== PLUGIN METHODS ==========

    /// <summary>
    /// Registers a new plugin.
    /// </summary>
    public FrontendToolStateData WithRegisteredPlugin(FrontendPluginDefinition plugin)
    {
        return this with
        {
            RegisteredPlugins = RegisteredPlugins.SetItem(plugin.Name, plugin)
        };
    }

    /// <summary>
    /// Removes a registered plugin.
    /// </summary>
    public FrontendToolStateData WithoutRegisteredPlugin(string pluginName)
    {
        return this with
        {
            RegisteredPlugins = RegisteredPlugins.Remove(pluginName),
            ExpandedPlugins = ExpandedPlugins.Remove(pluginName)
        };
    }

    /// <summary>
    /// Marks a plugin as expanded.
    /// </summary>
    public FrontendToolStateData WithExpandedPlugin(string pluginName)
    {
        return this with
        {
            ExpandedPlugins = ExpandedPlugins.Add(pluginName)
        };
    }

    /// <summary>
    /// Marks a plugin as collapsed.
    /// </summary>
    public FrontendToolStateData WithCollapsedPlugin(string pluginName)
    {
        return this with
        {
            ExpandedPlugins = ExpandedPlugins.Remove(pluginName)
        };
    }

    // ========== TOOL VISIBILITY METHODS ==========

    /// <summary>
    /// Hides a tool.
    /// </summary>
    public FrontendToolStateData WithHiddenTool(string toolName)
    {
        return this with
        {
            HiddenTools = HiddenTools.Add(toolName)
        };
    }

    /// <summary>
    /// Shows a previously hidden tool.
    /// </summary>
    public FrontendToolStateData WithVisibleTool(string toolName)
    {
        return this with
        {
            HiddenTools = HiddenTools.Remove(toolName)
        };
    }

    // ========== CONTEXT METHODS ==========

    /// <summary>
    /// Adds or updates a context item.
    /// </summary>
    public FrontendToolStateData WithContextItem(ContextItem item)
    {
        return this with
        {
            Context = Context.SetItem(item.EffectiveKey, item)
        };
    }

    /// <summary>
    /// Adds multiple context items.
    /// </summary>
    public FrontendToolStateData WithContext(IEnumerable<ContextItem> items)
    {
        var builder = Context.ToBuilder();
        foreach (var item in items)
        {
            builder[item.EffectiveKey] = item;
        }
        return this with { Context = builder.ToImmutable() };
    }

    /// <summary>
    /// Removes a context item.
    /// </summary>
    public FrontendToolStateData WithouTMetadata(string key)
    {
        return this with
        {
            Context = Context.Remove(key)
        };
    }

    /// <summary>
    /// Clears all context items.
    /// </summary>
    public FrontendToolStateData ClearContext()
    {
        return this with
        {
            Context = ImmutableDictionary<string, ContextItem>.Empty
        };
    }

    // ========== STATE METHODS ==========

    /// <summary>
    /// Updates the application state.
    /// </summary>
    public FrontendToolStateData WithState(JsonElement? state)
    {
        return this with { State = state };
    }

    // ========== AUGMENTATION METHODS ==========

    /// <summary>
    /// Sets the pending augmentation to apply at next iteration.
    /// </summary>
    public FrontendToolStateData WithPendingAugmentation(FrontendToolAugmentation? augmentation)
    {
        return this with { PendingAugmentation = augmentation };
    }

    /// <summary>
    /// Clears the pending augmentation.
    /// </summary>
    public FrontendToolStateData ClearPendingAugmentation()
    {
        return this with { PendingAugmentation = null };
    }
}
