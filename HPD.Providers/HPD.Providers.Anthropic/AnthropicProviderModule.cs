using System.Runtime.CompilerServices;
using System.Text.Json;
using HPD.Providers.Core;

namespace HPD.Providers.Anthropic;

/// <summary>
/// Auto-discovers and registers Anthropic providers on assembly load.
/// Serves both HPD-Agent (chat) and HPD-Agent.Memory (embeddings).
/// </summary>
public static class AnthropicProviderModule
{
#pragma warning disable CA2255
    /// <summary>
    /// Auto-registers the Anthropic provider and its configuration with the global registries when the assembly is loaded.
    /// </summary>
    /// <remarks>
    /// Registers an AnthropicProvider instance with ProviderRegistry and registers the AnthropicProviderConfig type with ProviderConfigRegistry using AOT-friendly JSON (de)serializers.
    /// </remarks>
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        // Register provider with the global provider registry
        ProviderRegistry.Instance.Register(new AnthropicProvider());

        // Register provider-specific config type for FFI/JSON serialization (AOT-compatible)
        ProviderConfigRegistry.Register<AnthropicProviderConfig>(
            "anthropic",
            json => JsonSerializer.Deserialize(json, AnthropicJsonContext.Default.AnthropicProviderConfig),
            config => JsonSerializer.Serialize(config, AnthropicJsonContext.Default.AnthropicProviderConfig));
    }
}