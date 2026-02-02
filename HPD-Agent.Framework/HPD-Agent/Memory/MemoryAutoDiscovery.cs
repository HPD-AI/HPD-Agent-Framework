using System.Runtime.CompilerServices;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;

namespace HPD.Agent.Memory;

/// <summary>
/// Auto-discovers and loads memory subsystem modules when HPD-Agent library is loaded.
/// This ModuleInitializer runs automatically in both JIT and AOT scenarios.
/// Similar pattern to ProviderAutoDiscovery but for memory subsystems.
/// </summary>
internal static class MemoryAutoDiscovery
{
    private static bool _initialized = false;
    private static readonly object _lock = new();

    /// <summary>
    /// Module initializer that runs when HPD-Agent assembly is first loaded.
    /// Attempts to load memory subsystem assemblies to trigger their ModuleInitializers.
    /// </summary>
#pragma warning disable CA2255 // ModuleInitializer is intentionally used in library for auto-discovery
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        lock (_lock)
        {
            if (_initialized)
                return;

            _initialized = true;
        }

#if NATIVE_AOT
        TryInitializeKnownMemoryModules();
#else
        TryScanAndLoadMemoryModules();
#endif
    }

#if NATIVE_AOT
    /// <summary>
    /// Explicitly triggers ModuleInitializers for known memory modules in AOT scenarios.
    /// This prevents the AOT trimmer from removing memory modules that appear unused.
    /// Uses conditional compilation and weak references to only include modules
    /// that the app actually references.
    /// </summary>
    private static void TryInitializeKnownMemoryModules()
    {
        // Each memory module is tried individually with weak references
        // If the app doesn't reference a memory module, the weak reference will fail gracefully
        
        // Use reflection to dynamically check and load, which AOT will handle gracefully
        TryInitializeMemoryModuleByTypeName("HPD.Agent.Memory.DynamicMemory.DynamicMemoryModule, HPD-Agent.Memory");
        TryInitializeMemoryModuleByTypeName("HPD.Agent.Memory.StaticMemory.StaticMemoryModule, HPD-Agent.Memory");
        TryInitializeMemoryModuleByTypeName("HPD.Agent.Memory.PlanMode.PlanModeModule, HPD-Agent.Memory");
    }

    /// <summary>
    /// Attempts to load and initialize a memory module by assembly-qualified type name.
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Agent.Memory.DynamicMemory.DynamicMemoryModule", "HPD-Agent.Memory")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Agent.Memory.StaticMemory.StaticMemoryModule", "HPD-Agent.Memory")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Agent.Memory.PlanMode.PlanModeModule", "HPD-Agent.Memory")]
    private static void TryInitializeMemoryModuleByTypeName(string assemblyQualifiedTypeName)
    {
        try
        {
            var type = Type.GetType(assemblyQualifiedTypeName, throwOnError: false);
            if (type != null)
            {
                RuntimeHelpers.RunModuleConstructor(type.Module.ModuleHandle);
            }
        }
        catch
        {
            // Silently ignore - memory module might not be referenced or available
        }
    }
#endif

#if !NATIVE_AOT
    /// <summary>
    /// Attempts to scan and load memory subsystem assemblies in non-AOT scenarios.
    /// This provides automatic discovery without requiring user configuration.
    /// </summary>
    private static void TryScanAndLoadMemoryModules()
    {
        try
        {
            var appDomain = AppDomain.CurrentDomain;
            var loadedAssemblies = appDomain.GetAssemblies();

            // Look for HPD-Agent.Memory assembly
            var memoryAssembly = loadedAssemblies.FirstOrDefault(a =>
                a.GetName().Name == "HPD-Agent.Memory");

            if (memoryAssembly == null)
            {
                // Try to load it
                try
                {
                    memoryAssembly = Assembly.Load("HPD-Agent.Memory");
                }
                catch
                {
                    // Memory module not available
                    return;
                }
            }

            // Scan for memory module types
            var memoryModuleTypes = memoryAssembly.GetTypes()
                .Where(t => t.Name.EndsWith("Module", StringComparison.Ordinal) &&
                            t.IsClass && !t.IsAbstract &&
                            t.Namespace != null &&
                            (t.Namespace.Contains("DynamicMemory") ||
                             t.Namespace.Contains("StaticMemory") ||
                             t.Namespace.Contains("PlanMode")))
                .ToList();

            // Trigger ModuleInitializers for discovered memory modules
            foreach (var moduleType in memoryModuleTypes)
            {
                try
                {
                    RuntimeHelpers.RunModuleConstructor(moduleType.Module.ModuleHandle);
                }
                catch
                {
                    // Silently ignore individual module loading failures
                }
            }
        }
        catch
        {
            // Silently ignore any errors during memory module discovery
        }
    }
#endif
}
