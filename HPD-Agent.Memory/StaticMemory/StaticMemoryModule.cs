using System.Runtime.CompilerServices;
using HPD.Agent.Memory;

namespace HPD.Agent.Memory;

/// <summary>
/// Auto-discovers and registers StaticMemory configuration on assembly load.
/// Called by MemoryAutoDiscovery ModuleInitializer to register StaticMemory builder.
/// </summary>
public static class StaticMemoryModule
{
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    [ModuleInitializer]
#pragma warning restore CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    public static void Initialize()
    {
        // Register static memory builder with MemoryDiscovery
        // The builder is a lambda that applies WithStaticMemory extension
        MemoryDiscovery.RegisterMemoryBuilder(
            "static",
            (builder, config) =>
            {
                if (config is StaticMemoryConfig staticConfig)
                {
                    return builder.WithStaticMemory(opts =>
                    {
                        opts.StorageDirectory = staticConfig.StorageDirectory;
                        opts.Strategy = staticConfig.Strategy;
                        opts.MaxTokens = staticConfig.MaxTokens;
                        if (!string.IsNullOrEmpty(staticConfig.AgentName))
                            opts.AgentName = staticConfig.AgentName;
                    });
                }
                return builder.WithStaticMemory(opts => { });
            });
    }
}
