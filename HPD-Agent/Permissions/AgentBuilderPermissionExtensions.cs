using System.Threading.Tasks;
using HPD.Agent;
using System.Collections.Concurrent;

public static class AgentBuilderPermissionExtensions
{
    /// <summary>
    /// Adds the unified permission Middleware that works with any protocol (Console, AGUI, Web, etc.).
    /// Permission requests are emitted as events that you handle in your application code.
    /// This gives you full control over how permission prompts are displayed to users.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="permissionStorage">Optional permission storage for persistent decisions</param>
    /// <returns>The agent builder for chaining</returns>
    /// <example>
    /// // In your Program.cs:
    /// var agent = new AgentBuilder()
    ///     .WithPermissions(storage)  // Use new unified Middleware
    ///     .Build();
    ///
    /// // Then handle events in your event loop (see Middleware_EVENTS_USAGE.md)
    /// </example>
    public static AgentBuilder WithPermissions(
        this AgentBuilder builder,
        IPermissionStorage? permissionStorage = null)
    {
        var storage = permissionStorage ?? new InMemoryPermissionStorage();
        var middleware = new HPD.Agent.Permissions.PermissionMiddleware(storage, builder.Config, overrideRegistry: builder._permissionOverrides);
        builder._middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Adds an auto-approve permission Middleware for testing and automation scenarios.
    /// </summary>
    public static AgentBuilder WithAutoApprovePermissions(this AgentBuilder builder)
    {
        var middleware = new HPD.Agent.Permissions.AutoApprovePermissionMiddleware();
        builder._middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Forces a specific function to require permission, overriding the [RequiresPermission] attribute.
    /// Useful when using third-party plugins that don't have permission checks on sensitive functions.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="functionName">The name of the function to require permission for</param>
    /// <returns>The agent builder for chaining</returns>
    /// <example>
    /// var agent = new AgentBuilder()
    ///     .WithTools&lt;ThirdPartyDatabasePlugin&gt;()
    ///     .RequirePermissionFor("DeleteAllData")  // Force permission even if attribute says no
    ///     .Build();
    /// </example>
    public static AgentBuilder RequirePermissionFor(this AgentBuilder builder, string functionName)
    {
        builder._permissionOverrides.RequirePermission(functionName);
        return builder;
    }

    /// <summary>
    /// Disables permission requirement for a specific function, overriding the [RequiresPermission] attribute.
    /// Useful when you trust a function and don't want to be prompted.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="functionName">The name of the function to disable permission for</param>
    /// <returns>The agent builder for chaining</returns>
    /// <example>
    /// var agent = new AgentBuilder()
    ///     .WithTools&lt;FileSystemPlugin&gt;()
    ///     .DisablePermissionFor("ReadFile")  // Remove permission requirement
    ///     .Build();
    /// </example>
    public static AgentBuilder DisablePermissionFor(this AgentBuilder builder, string functionName)
    {
        builder._permissionOverrides.DisablePermission(functionName);
        return builder;
    }

    /// <summary>
    /// Clears any permission override for a function, restoring attribute-based behavior.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="functionName">The name of the function to clear override for</param>
    /// <returns>The agent builder for chaining</returns>
    public static AgentBuilder ClearPermissionOverride(this AgentBuilder builder, string functionName)
    {
        builder._permissionOverrides.ClearOverride(functionName);
        return builder;
    }
}

/// <summary>
/// A default, non-persistent implementation of IPermissionStorage for development and testing.
/// Uses implicit Collapsing based on the parameters provided.
/// </summary>
public class InMemoryPermissionStorage : IPermissionStorage
{
    private readonly ConcurrentDictionary<string, PermissionChoice> _permissions = new();

    public Task<PermissionChoice?> GetStoredPermissionAsync(
        string functionName,
        string? conversationId = null)
    {
        var key = BuildKey(functionName, conversationId);
        _permissions.TryGetValue(key, out var choice);
        return Task.FromResult((PermissionChoice?)choice);
    }

    public Task SavePermissionAsync(
        string functionName,
        PermissionChoice choice,
        string? conversationId = null)
    {
        if (choice != PermissionChoice.Ask)
        {
            var key = BuildKey(functionName, conversationId);
            _permissions[key] = choice;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds a storage key with implicit Collapsing based on provided parameters.
    /// </summary>
    private static string BuildKey(string functionName, string? conversationId)
    {
        // Collapsing is implicit in the key structure:
        // - conversation-Collapsed: "conversationId:functionName"
        // - global: "functionName"
        if (!string.IsNullOrEmpty(conversationId))
            return $"{conversationId}:{functionName}";
        return functionName;
    }
}
