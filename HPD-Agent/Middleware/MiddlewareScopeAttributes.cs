namespace HPD.Agent.Middleware;

/// <summary>
/// Specifies the Collapse at which a middleware operates.
/// Used for filtering middleware execution based on context.
/// </summary>
public enum MiddlewareCollapse
{
    /// <summary>Middleware applies to all functions globally</summary>
    Global = 0,

    /// <summary>Middleware applies to all functions from a specific plugin</summary>
    Plugin = 1,

    /// <summary>Middleware applies to skill container and functions called by a specific skill</summary>
    Skill = 2,

    /// <summary>Middleware applies to a specific function only</summary>
    Function = 3
}

/// <summary>
/// Internal metadata for Collapsed middleware registration.
/// Stores Collapse information attached to middleware instances.
/// </summary>
internal class MiddlewareCollapseMetadata
{
    public MiddlewareCollapse Collapse { get; init; } = MiddlewareCollapse.Global;
    public string? Target { get; init; } // Plugin type name, skill name, or function name

    public MiddlewareCollapseMetadata(MiddlewareCollapse Collapse, string? target = null)
    {
        Collapse = Collapse;
        Target = target;
    }

    /// <summary>
    /// Determines if this middleware should apply to the given context.
    /// </summary>
    public bool AppliesTo(AgentMiddlewareContext context)
    {
        var functionName = context.Function?.Name;
        var pluginName = context.PluginName;
        var skillName = context.SkillName;
        var isSkillContainer = context.IsSkillContainer;

        return Collapse switch
        {
            MiddlewareCollapse.Global => true,

            MiddlewareCollapse.Plugin => !string.IsNullOrEmpty(pluginName) &&
                                       string.Equals(Target, pluginName, StringComparison.Ordinal),

            MiddlewareCollapse.Skill =>
                // Apply if this function IS the skill container itself
                (isSkillContainer && string.Equals(Target, functionName, StringComparison.Ordinal)) ||
                // OR if this function is called FROM this skill (via mapping)
                (!string.IsNullOrEmpty(skillName) && string.Equals(Target, skillName, StringComparison.Ordinal)),

            MiddlewareCollapse.Function => string.Equals(Target, functionName, StringComparison.Ordinal),

            _ => false
        };
    }
}

/// <summary>
/// Extension methods for Collapsed middleware registration.
/// Provides fluent API for attaching Collapse metadata to middleware instances.
/// </summary>
public static class MiddlewareCollapseExtensions
{
    // Internal dictionary to store Collapse metadata per middleware instance
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IAgentMiddleware, MiddlewareCollapseMetadata>
        _CollapseMetadata = new();

    /// <summary>
    /// Marks this middleware as global (applies to all functions).
    /// This is the default if no Collapse is specified.
    /// </summary>
    public static IAgentMiddleware AsGlobal(this IAgentMiddleware middleware)
    {
        _CollapseMetadata.AddOrUpdate(middleware, new MiddlewareCollapseMetadata(MiddlewareCollapse.Global));
        return middleware;
    }

    /// <summary>
    /// Marks this middleware as plugin-Collapsed (applies only to functions from the specified plugin).
    /// </summary>
    /// <param name="middleware">The middleware instance</param>
    /// <param name="pluginTypeName">The plugin type name (e.g., "FileSystemPlugin")</param>
    public static IAgentMiddleware ForPlugin(this IAgentMiddleware middleware, string pluginTypeName)
    {
        if (string.IsNullOrWhiteSpace(pluginTypeName))
            throw new ArgumentException("Plugin type name cannot be null or empty", nameof(pluginTypeName));

        _CollapseMetadata.AddOrUpdate(middleware, new MiddlewareCollapseMetadata(MiddlewareCollapse.Plugin, pluginTypeName));
        return middleware;
    }

    /// <summary>
    /// Marks this middleware as skill-Collapsed (applies to skill container and functions called by the skill).
    /// </summary>
    /// <param name="middleware">The middleware instance</param>
    /// <param name="skillName">The skill name (e.g., "analyze_codebase")</param>
    public static IAgentMiddleware ForSkill(this IAgentMiddleware middleware, string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            throw new ArgumentException("Skill name cannot be null or empty", nameof(skillName));

        _CollapseMetadata.AddOrUpdate(middleware, new MiddlewareCollapseMetadata(MiddlewareCollapse.Skill, skillName));
        return middleware;
    }

    /// <summary>
    /// Marks this middleware as function-Collapsed (applies only to the specified function).
    /// </summary>
    /// <param name="middleware">The middleware instance</param>
    /// <param name="functionName">The function name (e.g., "ReadFile")</param>
    public static IAgentMiddleware ForFunction(this IAgentMiddleware middleware, string functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            throw new ArgumentException("Function name cannot be null or empty", nameof(functionName));

        _CollapseMetadata.AddOrUpdate(middleware, new MiddlewareCollapseMetadata(MiddlewareCollapse.Function, functionName));
        return middleware;
    }

    /// <summary>
    /// Gets the Collapse metadata for a middleware instance.
    /// Returns global Collapse if no metadata is attached.
    /// </summary>
    internal static MiddlewareCollapseMetadata GetCollapseMetadata(this IAgentMiddleware middleware)
    {
        if (_CollapseMetadata.TryGetValue(middleware, out var metadata))
            return metadata;

        // Default to global Collapse if no metadata is attached
        return new MiddlewareCollapseMetadata(MiddlewareCollapse.Global);
    }

    /// <summary>
    /// Checks if the middleware should execute in the given context.
    /// </summary>
    internal static bool ShouldExecute(this IAgentMiddleware middleware, AgentMiddlewareContext context)
    {
        var metadata = middleware.GetCollapseMetadata();
        return metadata.AppliesTo(context);
    }
}
