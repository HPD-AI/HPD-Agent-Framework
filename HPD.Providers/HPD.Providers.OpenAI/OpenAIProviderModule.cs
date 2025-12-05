using System.Runtime.CompilerServices;
using HPD.Providers.Core;

namespace HPD.Providers.OpenAI;

/// <summary>
/// Auto-discovers and registers OpenAI providers on assembly load.
/// Serves both HPD-Agent (chat) and HPD-Agent.Memory (embeddings).
/// </summary>
public static class OpenAIProviderModule
{
#pragma warning disable CA2255
    /// <summary>
    /// Automatically registers OpenAI-related providers with the global provider registry when the module initializes.
    /// </summary>
    /// <remarks>
    /// Registers instances of <see cref="OpenAIProvider"/> and <see cref="AzureOpenAIProvider"/> with <see cref="ProviderRegistry.Instance"/>, enabling automatic discovery of OpenAI providers (e.g., chat and embeddings) without manual registration.
    /// </remarks>
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        // Register with the global provider registry
        ProviderRegistry.Instance.Register(new OpenAIProvider());
        ProviderRegistry.Instance.Register(new AzureOpenAIProvider());
    }
}