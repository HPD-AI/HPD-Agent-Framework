using System.Runtime.CompilerServices;
using HPD.Providers.Core;

namespace HPD.Providers.Ollama;

/// <summary>
/// Auto-discovers and registers the Ollama provider on assembly load.
/// </summary>
public static class OllamaProviderModule
{
    #pragma warning disable CA2255
    /// <summary>
    /// Registers the Ollama provider with the global provider registry when the assembly is initialized.
    /// </summary>
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        ProviderRegistry.Instance.Register(new OllamaProvider());
    }
}