// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent.Audio;
using HPD.Agent.Audio.OpenAI;
using HPD.Agent.Audio.Providers;
using Microsoft.Extensions.AI;

namespace HPD.Agent.AudioProviders.OpenAI;

/// <summary>
/// OpenAI audio provider implementation.
/// Provides TTS and STT capabilities via OpenAI API.
/// </summary>
public class OpenAIAudioProvider : IAudioProviderFeatures
{
    public string ProviderKey => "openai-audio";
    public string DisplayName => "OpenAI Audio";

    public ITextToSpeechClient? CreateTextToSpeechClient(AudioProviderConfig config, IServiceProvider? services = null)
    {
        var apiKey = config.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenAI API key is required. Set 'ApiKey' in config or 'OPENAI_API_KEY' environment variable.");

        // Get optional settings
        var model = config.Settings?.GetValueOrDefault("model")?.ToString() ?? "tts-1";
        var voice = config.Settings?.GetValueOrDefault("voice")?.ToString() ?? "alloy";

        return new OpenAITextToSpeechClient(apiKey, model, voice);
    }

    public ISpeechToTextClient? CreateSpeechToTextClient(AudioProviderConfig config, IServiceProvider? services = null)
    {
        // OpenAI STT client not yet implemented
        // TODO: Implement OpenAISpeechToTextClient
        return null;
    }

    public IVoiceActivityDetector? CreateVoiceActivityDetector(AudioProviderConfig config, IServiceProvider? services = null)
    {
        // OpenAI doesn't provide VAD - users should use Silero or other VAD providers
        return null;
    }

    public AudioProviderMetadata GetMetadata()
    {
        return new AudioProviderMetadata
        {
            ProviderKey = ProviderKey,
            DisplayName = DisplayName,
            SupportsTTS = true,
            SupportsSTT = false, // TODO: Implement STT client
            SupportsVAD = false,
            SupportsStreaming = true,
            SupportedVoices = ["alloy", "echo", "fable", "onyx", "nova", "shimmer"],
            SupportedFormats = ["mp3", "opus", "aac", "flac", "wav", "pcm"],
            SupportedLanguages = null, // Supports all languages
            DocumentationUrl = "https://platform.openai.com/docs/guides/text-to-speech"
        };
    }

    public AudioProviderValidationResult ValidateConfiguration(AudioProviderConfig config)
    {
        var errors = new List<string>();

        // Validate API key
        var apiKey = config.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            errors.Add("OpenAI API key is required. Set 'ApiKey' in config or 'OPENAI_API_KEY' environment variable.");

        // Validate base URL format if provided
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out _))
                errors.Add($"Invalid BaseUrl format: {config.BaseUrl}");
        }

        return errors.Count > 0
            ? AudioProviderValidationResult.Failure(errors.ToArray())
            : AudioProviderValidationResult.Success();
    }

    public async Task<AudioProviderValidationResult>? ValidateConfigurationAsync(AudioProviderConfig config, CancellationToken cancellationToken = default)
    {
        // First do synchronous validation
        var syncResult = ValidateConfiguration(config);
        if (!syncResult.IsValid)
            return syncResult;

        // Then test API connectivity (optional)
        try
        {
            var client = CreateTextToSpeechClient(config);
            if (client == null)
                return AudioProviderValidationResult.Failure("Failed to create TTS client");

            // Could test API here by making a minimal request
            // For now, just return success if client creation succeeded
            return AudioProviderValidationResult.Success();
        }
        catch (Exception ex)
        {
            return AudioProviderValidationResult.Failure($"Failed to validate OpenAI API: {ex.Message}");
        }
    }
}
