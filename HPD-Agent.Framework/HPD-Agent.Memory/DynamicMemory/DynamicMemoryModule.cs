using System.Runtime.CompilerServices;
using HPD.Agent.Memory;

namespace HPD.Agent.Memory;

/// <summary>
/// Auto-discovers and registers DynamicMemory configuration on assembly load.
/// Called by MemoryAutoDiscovery ModuleInitializer to register DynamicMemory builder.
/// </summary>
public static class DynamicMemoryModule
{
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    [ModuleInitializer]
#pragma warning restore CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    public static void Initialize()
    {
        // Register dynamic memory builder with MemoryDiscovery
        // The builder is a lambda that applies WithDynamicMemory extension
        MemoryDiscovery.RegisterMemoryBuilder(
            "dynamic",
            (builder, config) =>
            {
                if (config is DynamicMemoryConfig dynamicConfig)
                {
                    return builder.WithDynamicMemory(opts =>
                    {
                        opts.StorageDirectory = dynamicConfig.StorageDirectory;
                        opts.MaxTokens = dynamicConfig.MaxTokens;
                        opts.EnableAutoEviction = dynamicConfig.EnableAutoEviction;
                        opts.AutoEvictionThreshold = dynamicConfig.AutoEvictionThreshold;
                    });
                }
                return builder.WithDynamicMemory();
            });
    }
}
