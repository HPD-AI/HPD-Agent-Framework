// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using Microsoft.Extensions.AI;

namespace HPD.Agent.Audio.ElevenLabs;

/// <summary>
/// ElevenLabs audio provider implementing both TTS and STT.
/// Provides access to ElevenLabs high-quality voices and Scribe transcription.
/// </summary>
public sealed class ElevenLabsAudioProvider : IAudioProvider, IDisposable
{
    private readonly ElevenLabsAudioConfig _config;
    private readonly HttpClient? _httpClient;
    private ITextToSpeechClient? _ttsClient;
    private ISpeechToTextClient? _sttClient;
    private bool _disposed;

    /// <summary>
    /// Creates a new ElevenLabs audio provider.
    /// </summary>
    /// <param name="config">ElevenLabs configuration</param>
    /// <param name="httpClient">Optional shared HttpClient for connection pooling</param>
    public ElevenLabsAudioProvider(
        ElevenLabsAudioConfig config,
        HttpClient? httpClient = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Validate();
        _httpClient = httpClient;
    }

    /// <summary>
    /// Gets the TTS client instance.
    /// </summary>
    public ITextToSpeechClient GetTextToSpeechClient()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ElevenLabsAudioProvider));
        }

        return _ttsClient ??= new ElevenLabsTextToSpeechClient(_config, _httpClient);
    }

    /// <summary>
    /// Gets the STT client instance.
    /// </summary>
    public ISpeechToTextClient GetSpeechToTextClient()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ElevenLabsAudioProvider));
        }

        return _sttClient ??= new ElevenLabsSpeechToTextClient(_config, _httpClient);
    }

    /// <summary>
    /// Gets metadata about the provider's capabilities.
    /// </summary>
    public static IAudioProviderFeatures GetMetadata()
    {
        return new ElevenLabsAudioProviderFeatures();
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        (_ttsClient as IDisposable)?.Dispose();
        (_sttClient as IDisposable)?.Dispose();
    }
}

/// <summary>
/// Features supported by the ElevenLabs audio provider.
/// </summary>
internal sealed class ElevenLabsAudioProviderFeatures : IAudioProviderFeatures
{
    public string ProviderName => "ElevenLabs";

    public bool SupportsTextToSpeech => true;

    public bool SupportsSpeechToText => true;

    public bool SupportsStreaming => true;

    public bool SupportsWebRTC => true;  // Via ConvAI agents

    public bool SupportsVoiceCloning => true;

    public bool SupportsMultilingualTTS => true;

    public string[] SupportedLanguages => new[]
    {
        "en", "de", "pl", "es", "it", "fr", "pt", "hi", "ar",
        "ja", "zh", "ko", "nl", "tr", "sv", "id", "fi", "no",
        "da", "hu", "cs", "ro", "bg", "el", "sk", "hr", "ms",
        "ta", "uk", "ru", "vi"  // 32 languages total
    };

    public string[] SupportedVoiceModels => new[]
    {
        "eleven_flash_v2",              // Default, <75ms latency
        "eleven_flash_v2_5",            // English only, faster
        "eleven_turbo_v2",              // Low latency
        "eleven_turbo_v2_5",            // 32 languages, fast
        "eleven_multilingual_v2",       // High quality, 29 languages
        "eleven_monolingual_v1"         // Legacy English
    };

    public string[] SupportedOutputFormats => new[]
    {
        "mp3_44100_64",
        "mp3_44100_96",
        "mp3_44100_128",  // Default
        "mp3_44100_192",  // Creator+ tier
        "pcm_16000",
        "pcm_22050",
        "pcm_24000",
        "pcm_44100",      // Indie+ tier
        "ulaw_8000"
    };

    public IDictionary<string, object> AdditionalCapabilities => new Dictionary<string, object>
    {
        ["VoiceDesign"] = true,
        ["VoiceCloning"] = true,
        ["Dubbing"] = true,
        ["SoundGeneration"] = true,
        ["MusicGeneration"] = true,
        ["ConversationalAI"] = true,
        ["PhoneIntegration"] = true,
        ["PronunciationDictionary"] = true,
        ["CharacterLevelTimestamps"] = true,
        ["MaxTier"] = "Enterprise"
    };
}
