// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.Audio.ElevenLabs;

/// <summary>
/// Module for registering ElevenLabs audio services.
/// Provides extension methods for easy integration.
/// </summary>
public static class ElevenLabsAudioProviderModule
{
    /// <summary>
    /// Creates an ElevenLabs audio provider with the specified configuration.
    /// </summary>
    /// <param name="config">ElevenLabs configuration</param>
    /// <param name="httpClient">Optional HttpClient for connection pooling</param>
    /// <returns>Configured ElevenLabs audio provider</returns>
    public static ElevenLabsAudioProvider CreateProvider(
        ElevenLabsAudioConfig config,
        HttpClient? httpClient = null)
    {
        return new ElevenLabsAudioProvider(config, httpClient);
    }

    /// <summary>
    /// Creates an ElevenLabs audio provider using API key from environment.
    /// Looks for ELEVENLABS_API_KEY or ELEVEN_LABS_API_KEY environment variable.
    /// </summary>
    /// <param name="httpClient">Optional HttpClient for connection pooling</param>
    /// <returns>Configured ElevenLabs audio provider</returns>
    /// <exception cref="InvalidOperationException">If API key not found in environment</exception>
    public static ElevenLabsAudioProvider CreateProviderFromEnvironment(HttpClient? httpClient = null)
    {
        var apiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY")
                  ?? Environment.GetEnvironmentVariable("ELEVEN_LABS_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "ElevenLabs API key not found in environment. " +
                "Set ELEVENLABS_API_KEY or ELEVEN_LABS_API_KEY environment variable. " +
                "Get your API key at: https://elevenlabs.io/app/settings/api-keys");
        }

        var config = new ElevenLabsAudioConfig
        {
            ApiKey = apiKey
        };

        return new ElevenLabsAudioProvider(config, httpClient);
    }

    /// <summary>
    /// Gets metadata about the ElevenLabs provider's capabilities.
    /// </summary>
    public static IAudioProviderFeatures GetFeatures() => ElevenLabsAudioProvider.GetMetadata();
}
