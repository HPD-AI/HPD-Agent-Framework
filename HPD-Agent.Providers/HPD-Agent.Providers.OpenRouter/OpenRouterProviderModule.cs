using System.Runtime.CompilerServices;
using HPD.Agent.Providers;

namespace HPD.Agent.Providers.OpenRouter;

/// <summary>
/// Auto-discovers and registers the OpenRouter provider on assembly load.
/// </summary>
public static class OpenRouterProviderModule
{
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    [ModuleInitializer]
#pragma warning restore CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    public static void Initialize()
    {
        ProviderDiscovery.RegisterProviderFactory(() => new OpenRouterProvider());
    }
}
