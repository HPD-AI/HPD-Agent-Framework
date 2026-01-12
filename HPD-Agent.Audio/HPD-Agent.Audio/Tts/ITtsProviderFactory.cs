// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.Audio.Tts;

/// <summary>
/// Factory for creating TTS clients.
/// Registered via TtsProviderDiscovery in module initializer.
/// </summary>
public interface ITtsProviderFactory
{
    /// <summary>
    /// Creates a TTS client from configuration.
    /// </summary>
    /// <param name="config">TTS-specific configuration</param>
    /// <param name="services">Optional DI service provider</param>
    /// <returns>Configured TTS client</returns>
    ITextToSpeechClient CreateClient(TtsConfig config, IServiceProvider? services = null);

    /// <summary>
    /// Gets metadata about this TTS provider's capabilities.
    /// </summary>
    TtsProviderMetadata GetMetadata();

    /// <summary>
    /// Validates TTS configuration for this provider.
    /// </summary>
    ValidationResult Validate(TtsConfig config);
}
