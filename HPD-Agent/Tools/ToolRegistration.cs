using Microsoft.Extensions.AI;

/// <summary>
/// Lightweight registration record for DI-required plugins.
/// Only used for plugins that cannot be instantiated via the AOT-compatible PluginRegistry
/// (e.g., plugins requiring dependency injection like AgentPlanPlugin or DynamicMemoryPlugin).
///
/// For all other plugins, use the generated PluginRegistry.All catalog via WithPlugin&lt;T&gt;().
/// </summary>
public record PluginInstanceRegistration(
    /// <summary>
    /// The plugin instance (pre-created, typically via DI)
    /// </summary>
    object Instance,

    /// <summary>
    /// The plugin type name (for lookup in generated Registration classes)
    /// </summary>
    string PluginTypeName,

    /// <summary>
    /// Optional function filter - if set, only these functions will be included.
    /// Phase 4.5: Used for selective function registration from skills.
    /// </summary>
    string[]? FunctionFilter = null
);
