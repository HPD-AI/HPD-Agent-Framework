using System.Collections.Concurrent;
using System.Threading.Tasks;
using HPD.Agent;

namespace HPD_Agent.Tests.Infrastructure;

/// <summary>
/// In-memory implementation of IPermissionStorage for testing.
/// Stores permission preferences in memory without any persistence.
/// Uses implicit scoping based on whether conversationId is provided.
/// </summary>
public class InMemoryPermissionStorage : IPermissionStorage
{
    private readonly ConcurrentDictionary<string, PermissionChoice> _permissions = new();

    /// <summary>
    /// Gets a stored permission preference for a specific function.
    /// Respects permission scoping (Conversation > Project > Global).
    /// </summary>
    public Task<PermissionChoice?> GetStoredPermissionAsync(
        string functionName,
        string conversationId,
        string? projectId)
    {
        lock (_lock)
        {
            // Try conversation-scoped first (most specific)
            var conversationKey = GetKey(functionName, PermissionScope.Conversation, conversationId, projectId);
            if (_permissions.TryGetValue(conversationKey, out var conversationPerm))
            {
                return Task.FromResult<PermissionChoice?>(conversationPerm.Choice);
            }

            // Try project-scoped next
            if (!string.IsNullOrEmpty(projectId))
            {
                var projectKey = GetKey(functionName, PermissionScope.Project, conversationId, projectId);
                if (_permissions.TryGetValue(projectKey, out var projectPerm))
                {
                    return Task.FromResult<PermissionChoice?>(projectPerm.Choice);
                }
            }

            // Try global-scoped last (least specific)
            var globalKey = GetKey(functionName, PermissionScope.Global, conversationId, projectId);
            if (_permissions.TryGetValue(globalKey, out var globalPerm))
            {
                return Task.FromResult<PermissionChoice?>(globalPerm.Choice);
            }

            // No stored permission found
            return Task.FromResult<PermissionChoice?>(null);
        }
    }

    /// <summary>
    /// Saves a permission preference for a specific function with the specified scope.
    /// </summary>
    public Task SavePermissionAsync(
        string functionName,
        PermissionChoice choice,
        PermissionScope scope,
        string conversationId,
        string? projectId)
    {
        lock (_lock)
        {
            var key = GetKey(functionName, scope, conversationId, projectId);
            var permission = new StoredPermission(functionName, choice, scope, conversationId, projectId);
            _permissions[key] = permission;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets all stored permissions (for testing/debugging).
    /// </summary>
    public IReadOnlyList<StoredPermission> GetAll()
    {
        lock (_lock)
        {
            return _permissions.Values.ToList();
        }
    }

    /// <summary>
    /// Clears all stored permissions.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _permissions.Clear();
        }
    }

    /// <summary>
    /// Gets the number of stored permissions.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _permissions.Count;
            }
        }
    }

    /// <summary>
    /// Generates a unique key for a permission based on scope.
    /// </summary>
    private static string GetKey(
        string functionName,
        PermissionScope scope,
        string conversationId,
        string? projectId)
    {
        return scope switch
        {
            PermissionScope.Conversation => $"conv:{conversationId}:func:{functionName}",
            PermissionScope.Project => $"proj:{projectId}:func:{functionName}",
            PermissionScope.Global => $"global:func:{functionName}",
            _ => throw new System.ArgumentException($"Unknown permission scope: {scope}")
        };
    }
}
