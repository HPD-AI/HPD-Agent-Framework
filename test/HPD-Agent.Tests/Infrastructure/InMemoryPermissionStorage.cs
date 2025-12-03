using System.Collections.Concurrent;
using System.Threading.Tasks;
using HPD.Agent;

namespace HPD.Agent.Tests.Infrastructure;

/// <summary>
/// In-memory implementation of IPermissionStorage for testing.
/// Stores permission preferences in memory without any persistence.
/// </summary>
public class InMemoryPermissionStorage : IPermissionStorage
{
    private readonly ConcurrentDictionary<string, PermissionChoice> _permissions = new();
    private readonly object _lock = new();

    /// <summary>
    /// Gets a stored permission preference for a specific function.
    /// Returns null if no stored permission exists.
    /// </summary>
    public Task<PermissionChoice?> GetStoredPermissionAsync(
        string functionName,
        string? conversationId = null)
    {
        lock (_lock)
        {
            // Try conversation-scoped first (if conversationId provided)
            if (!string.IsNullOrEmpty(conversationId))
            {
                var conversationKey = $"conv:{conversationId}:{functionName}";
                if (_permissions.TryGetValue(conversationKey, out var conversationPerm))
                {
                    return Task.FromResult<PermissionChoice?>(conversationPerm);
                }
            }

            // Try global-scoped (no conversationId in key)
            var globalKey = $"global:{functionName}";
            if (_permissions.TryGetValue(globalKey, out var globalPerm))
            {
                return Task.FromResult<PermissionChoice?>(globalPerm);
            }

            // No stored permission found
            return Task.FromResult<PermissionChoice?>(null);
        }
    }

    /// <summary>
    /// Saves a permission preference for a specific function.
    /// </summary>
    public Task SavePermissionAsync(
        string functionName,
        PermissionChoice choice,
        string? conversationId = null)
    {
        lock (_lock)
        {
            if (choice != PermissionChoice.Ask)
            {
                var key = string.IsNullOrEmpty(conversationId)
                    ? $"global:{functionName}"
                    : $"conv:{conversationId}:{functionName}";
                _permissions[key] = choice;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets all stored permissions (for testing/debugging).
    /// </summary>
    public IReadOnlyList<(string Key, PermissionChoice Value)> GetAll()
    {
        lock (_lock)
        {
            return _permissions.Select(kvp => (kvp.Key, kvp.Value)).ToList();
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
}
