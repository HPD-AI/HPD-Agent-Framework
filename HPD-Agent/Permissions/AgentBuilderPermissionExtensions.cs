using System.Threading.Tasks;
using HPD.Agent;
using System.Collections.Concurrent;

public static class AgentBuilderPermissionExtensions
{
    /// <summary>
    /// Adds the unified permission filter that works with any protocol (Console, AGUI, Web, etc.).
    /// Permission requests are emitted as events that you handle in your application code.
    /// This gives you full control over how permission prompts are displayed to users.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="permissionStorage">Optional permission storage for persistent decisions</param>
    /// <returns>The agent builder for chaining</returns>
    /// <example>
    /// // In your Program.cs:
    /// var agent = new AgentBuilder()
    ///     .WithPermissions(storage)  // Use new unified filter
    ///     .Build();
    ///
    /// // Then handle events in your event loop (see FILTER_EVENTS_USAGE.md)
    /// </example>
    public static AgentBuilder WithPermissions(
        this AgentBuilder builder,
        IPermissionStorage? permissionStorage = null)
    {
        var storage = permissionStorage ?? new InMemoryPermissionStorage();
        var filter = new PermissionFilter(storage, builder.Config);
        return builder.WithPermissionFilter(filter);
    }

    /// <summary>
    /// Adds an auto-approve permission filter for testing and automation scenarios.
    /// </summary>
    public static AgentBuilder WithAutoApprovePermissions(this AgentBuilder builder)
    {
        var filter = new AutoApprovePermissionFilter();
        return builder.WithPermissionFilter(filter);
    }
}

/// <summary>
/// A default, non-persistent implementation of IPermissionStorage for development and testing.
/// </summary>
public class InMemoryPermissionStorage : IPermissionStorage
{
    private readonly ConcurrentDictionary<string, PermissionChoice> _functionChoices = new();

    public Task<PermissionChoice?> GetStoredPermissionAsync(string functionName, string conversationId, string? projectId)
    {
        if (_functionChoices.TryGetValue(functionName, out var choice))
        {
            return Task.FromResult((PermissionChoice?)choice);
        }
        return Task.FromResult((PermissionChoice?)null);
    }

    public Task SavePermissionAsync(string functionName, PermissionChoice choice, PermissionScope scope, string conversationId, string? projectId)
    {
        if (choice != PermissionChoice.Ask)
        {
            _functionChoices[functionName] = choice;
        }
        return Task.CompletedTask;
    }
}
