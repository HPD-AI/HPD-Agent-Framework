// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using Microsoft.Extensions.AI;
using HPD.Agent.Audio.Providers;

namespace HPD.Agent.Audio.ElevenLabs;

/// <summary>
/// ElevenLabs audio provider implementing IAudioProviderFeatures.
/// Provides access to ElevenLabs high-quality voices and Scribe transcription.
/// </summary>
public sealed class ElevenLabsAudioProvider
{
    // This class is just a simple wrapper now - actual implementation is in ElevenLabsAudioProviderFeatures
}

/// <summary>
/// Features supported by the ElevenLabs audio provider.
/// </summary>
public sealed class ElevenLabsAudioProviderFeatures : IAudioProviderFeatures
{
    public string ProviderKey => "elevenlabs";
    public string DisplayName => "ElevenLabs";

    public ITextToSpeechClient? CreateTextToSpeechClient(AudioProviderConfig config, IServiceProvider? services = null)
    {
        var elevenLabsConfig = MapToElevenLabsConfig(config);
        return new ElevenLabsTextToSpeechClient(elevenLabsConfig);
    }

    public ISpeechToTextClient? CreateSpeechToTextClient(AudioProviderConfig config, IServiceProvider? services = null)
    {
        var elevenLabsConfig = MapToElevenLabsConfig(config);
        return new ElevenLabsSpeechToTextClient(elevenLabsConfig);
    }

    public IVoiceActivityDetector? CreateVoiceActivityDetector(AudioProviderConfig config, IServiceProvider? services = null)
    {
        // ElevenLabs doesn't provide VAD
        return null;
    }

    public AudioProviderMetadata GetMetadata()
    {
        return new AudioProviderMetadata
        {
            ProviderKey = "elevenlabs",
            DisplayName = "ElevenLabs",
            SupportsTTS = true,
            SupportsSTT = true,
            SupportsVAD = false,
            SupportsStreaming = true,
            SupportedLanguages = new[]
            {
                "en", "de", "pl", "es", "it", "fr", "pt", "hi", "ar",
                "ja", "zh", "ko", "nl", "tr", "sv", "id", "fi", "no",
                "da", "hu", "cs", "ro", "bg", "el", "sk", "hr", "ms",
                "ta", "uk", "ru", "vi"
            },
            SupportedFormats = new[]
            {
                "mp3_44100_64", "mp3_44100_96", "mp3_44100_128", "mp3_44100_192",
                "pcm_16000", "pcm_22050", "pcm_24000", "pcm_44100", "ulaw_8000"
            },
            DocumentationUrl = "https://elevenlabs.io/docs",
            CustomProperties = new Dictionary<string, object>
            {
                ["VoiceDesign"] = true,
                ["VoiceCloning"] = true,
                ["WebRTC"] = true
            }
        };
    }

    public AudioProviderValidationResult ValidateConfiguration(AudioProviderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return AudioProviderValidationResult.Failure("API key is required for ElevenLabs");
        }

        return AudioProviderValidationResult.Success();
    }

    private static ElevenLabsAudioConfig MapToElevenLabsConfig(AudioProviderConfig config)
    {
        return new ElevenLabsAudioConfig
        {
            ApiKey = config.ApiKey ?? throw new ArgumentException("API key is required")
        };
    }
}
