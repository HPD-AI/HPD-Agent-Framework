// HPD-Agent/Agent/CapabilityManager.cs

using System;
using System.Collections.Generic;

/// <summary>
/// Manages the agent's extended capabilities, such as Audio and MCP.
/// </summary>
public class CapabilityManager
{
    private readonly Dictionary<string, object> _capabilities = new();

    /// <summary>
    /// Gets a capability by name and type.
    /// </summary>
    /// <typeparam name="T">The type of capability to retrieve.</typeparam>
    /// <param name="name">The name of the capability.</param>
    /// <returns>The capability instance if found, otherwise null.</returns>
    public T? GetCapability<T>(string name) where T : class
        => _capabilities.TryGetValue(name, out var capability) ? capability as T : null;

    /// <summary>
    /// Adds or updates a capability.
    /// </summary>
    /// <param name="name">The name of the capability.</param>
    /// <param name="capability">The capability instance.</param>
    public void AddCapability(string name, object capability)
        => _capabilities[name] = capability ?? throw new ArgumentNullException(nameof(capability));

    /// <summary>
    /// Removes a capability by name.
    /// </summary>
    /// <param name="name">The name of the capability to remove.</param>
    /// <returns>True if the capability was removed, false if it wasn't found.</returns>
    public bool RemoveCapability(string name)
        => _capabilities.Remove(name);
}