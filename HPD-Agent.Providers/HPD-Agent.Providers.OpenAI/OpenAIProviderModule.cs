using System.Runtime.CompilerServices;
using HPD.Agent.Providers;

namespace HPD.Agent.Providers.OpenAI;

/// <summary>
/// Auto-discovers and registers OpenAI providers on assembly load.
/// </summary>
public static class OpenAIProviderModule
{
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    [ModuleInitializer]
#pragma warning restore CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    public static void Initialize()
    {
        // Register with the global discovery registry
        ProviderDiscovery.RegisterProviderFactory(() => new OpenAIProvider());
        ProviderDiscovery.RegisterProviderFactory(() => new AzureOpenAIProvider());
    }
}
