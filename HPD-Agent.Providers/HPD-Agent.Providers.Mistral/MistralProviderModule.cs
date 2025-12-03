using System.Runtime.CompilerServices;
using HPD.Agent.Providers;

namespace HPD.Agent.Providers.Mistral;

/// <summary>
/// Auto-discovers and registers the Mistral provider on assembly load.
/// </summary>
public static class MistralProviderModule
{
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    [ModuleInitializer]
#pragma warning restore CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    public static void Initialize()
    {
        ProviderDiscovery.RegisterProviderFactory(() => new MistralProvider());
    }
}
