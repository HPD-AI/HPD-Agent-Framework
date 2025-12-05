using System.Runtime.CompilerServices;
using HPD.Providers.Core;

namespace HPD.Providers.Mistral;

/// <summary>
/// Auto-discovers and registers the Mistral provider on assembly load.
/// </summary>
public static class MistralProviderModule
{
    #pragma warning disable CA2255
    /// <summary>
    /// Registers a MistralProvider instance with the global ProviderRegistry when the assembly is initialized.
    /// </summary>
    /// <remarks>
    /// Invoked automatically at module initialization time via the ModuleInitializer attribute.
    /// </remarks>
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        ProviderRegistry.Instance.Register(new MistralProvider());
    }
}