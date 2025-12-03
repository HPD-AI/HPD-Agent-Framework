using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace HPD.Agent.Memory;

/// <summary>
/// Global discovery mechanism for memory subsystem modules.
/// ModuleInitializers register memory builders and configs here.
/// Similar pattern to ProviderDiscovery but for memory subsystems.
/// </summary>
public static class MemoryDiscovery
{
    /// <summary>
    /// Delegate for memory builder extension methods.
    /// </summary>
    public delegate AgentBuilder MemoryBuilderDelegate(AgentBuilder builder, dynamic? config);

    private static readonly Dictionary<string, MemoryBuilderDelegate> _memoryBuilders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, dynamic?> _memoryConfigs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>
    /// Called by memory module ModuleInitializers to register a memory builder.
    /// </summary>
    public static void RegisterMemoryBuilder(string memoryType, MemoryBuilderDelegate builder, dynamic? config = null)
    {
        lock (_lock)
        {
            _memoryBuilders[memoryType] = builder;
            if (config != null)
            {
                _memoryConfigs[memoryType] = config;
            }
        }
    }

    /// <summary>
    /// Get a memory builder by type name (e.g., "dynamic", "static", "planmode").
    /// </summary>
    internal static MemoryBuilderDelegate? GetMemoryBuilder(string memoryType)
    {
        lock (_lock)
        {
            _memoryBuilders.TryGetValue(memoryType, out var builder);
            return builder;
        }
    }

    /// <summary>
    /// Get memory configuration by type name.
    /// </summary>
    internal static dynamic? GetMemoryConfig(string memoryType)
    {
        lock (_lock)
        {
            _memoryConfigs.TryGetValue(memoryType, out var config);
            return config;
        }
    }

    /// <summary>
    /// Get all registered memory types.
    /// </summary>
    internal static IEnumerable<string> GetRegisteredMemoryTypes()
    {
        lock (_lock)
        {
            return _memoryBuilders.Keys.ToList();
        }
    }

    /// <summary>
    /// For testing: clear discovery registry.
    /// </summary>
    internal static void ClearForTesting()
    {
        lock (_lock)
        {
            _memoryBuilders.Clear();
            _memoryConfigs.Clear();
        }
    }

    /// <summary>
    /// Explicitly loads a memory module to trigger its ModuleInitializer.
    /// Required for Native AOT scenarios where automatic assembly loading is not available.
    /// </summary>
    /// <typeparam name="TMemoryModule">The memory module type</typeparam>
    public static void LoadMemoryModule<TMemoryModule>() where TMemoryModule : class
    {
        RuntimeHelpers.RunModuleConstructor(typeof(TMemoryModule).Module.ModuleHandle);
    }
}
