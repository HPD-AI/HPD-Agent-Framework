using System.Runtime.CompilerServices;
using HPD.Providers.Core;

namespace HPD.Providers.AzureAIInference;

/// <summary>
/// Auto-discovers and registers the Azure AI Inference provider on assembly load.
/// </summary>
public static class AzureAIInferenceProviderModule
{
    #pragma warning disable CA2255
    /// <summary>
    /// Registers the Azure AI Inference provider with the global ProviderRegistry when the assembly is initialized.
    /// </summary>
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        ProviderRegistry.Instance.Register(new AzureAIInferenceProvider());
    }
}