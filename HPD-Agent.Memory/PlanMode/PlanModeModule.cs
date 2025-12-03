using System.Runtime.CompilerServices;
using HPD.Agent.Memory;

namespace HPD.Agent.Memory;

/// <summary>
/// Auto-discovers and registers PlanMode configuration on assembly load.
/// Called by MemoryAutoDiscovery ModuleInitializer to register PlanMode builder.
/// </summary>
public static class PlanModeModule
{
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    [ModuleInitializer]
#pragma warning restore CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    public static void Initialize()
    {
        // Register plan mode builder with MemoryDiscovery
        // The builder is a lambda that applies WithPlanMode extension
        MemoryDiscovery.RegisterMemoryBuilder(
            "planmode",
            (builder, config) =>
            {
                if (config is PlanModeConfig planConfig)
                {
                    return builder.WithPlanMode(opts =>
                    {
                        opts.Enabled = planConfig.Enabled;
                        if (!string.IsNullOrEmpty(planConfig.CustomInstructions))
                            opts.CustomInstructions = planConfig.CustomInstructions;
                    });
                }
                return builder.WithPlanMode();
            });
    }
}
