// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent.Audio;

namespace HPD.Agent.Audio.Providers;

/// <summary>
/// Represents all capabilities provided by a specific audio provider (TTS/STT).
/// Implementations are contributed by audio provider packages via ModuleInitializer.
/// </summary>
public interface IAudioProviderFeatures
{
    /// <summary>
    /// Unique identifier for this audio provider (e.g., "openai-audio", "elevenlabs").
    /// Must be lowercase and URL-safe (used in JSON config).
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Display name for UI purposes (e.g., "OpenAI Audio", "ElevenLabs").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Create a TTS client for this provider from configuration.
    /// </summary>
    /// <param name="config">Provider-specific configuration</param>
    /// <param name="services">Optional service provider for DI</param>
    /// <returns>Configured ITextToSpeechClient instance, or null if not supported</returns>
    ITextToSpeechClient? CreateTextToSpeechClient(AudioProviderConfig config, IServiceProvider? services = null);

    /// <summary>
    /// Create a STT client for this provider from configuration.
    /// </summary>
    /// <param name="config">Provider-specific configuration</param>
    /// <param name="services">Optional service provider for DI</param>
    /// <returns>Configured ISpeechToTextClient instance, or null if not supported</returns>
    Microsoft.Extensions.AI.ISpeechToTextClient? CreateSpeechToTextClient(AudioProviderConfig config, IServiceProvider? services = null);

    /// <summary>
    /// Create a VAD for this provider from configuration.
    /// </summary>
    /// <param name="config">Provider-specific configuration</param>
    /// <param name="services">Optional service provider for DI</param>
    /// <returns>Configured IVoiceActivityDetector instance, or null if not supported</returns>
    IVoiceActivityDetector? CreateVoiceActivityDetector(AudioProviderConfig config, IServiceProvider? services = null);

    /// <summary>
    /// Get metadata about this audio provider's capabilities.
    /// </summary>
    /// <returns>Provider metadata including supported features</returns>
    AudioProviderMetadata GetMetadata();

    /// <summary>
    /// Validate provider-specific configuration (synchronous).
    /// </summary>
    /// <param name="config">Configuration to validate</param>
    /// <returns>Validation result with error messages if invalid</returns>
    AudioProviderValidationResult ValidateConfiguration(AudioProviderConfig config);

    /// <summary>
    /// Validate provider-specific configuration asynchronously with live API testing.
    /// This method can perform network requests to validate API keys, test voice availability, etc.
    /// Providers that don't support async validation should return null (default implementation).
    /// </summary>
    /// <param name="config">Configuration to validate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result, or null if async validation is not supported</returns>
    Task<AudioProviderValidationResult>? ValidateConfigurationAsync(AudioProviderConfig config, CancellationToken cancellationToken = default)
        => null; // Default implementation - providers can override
}

/// <summary>
/// Provider-specific configuration for audio services.
/// Each audio provider can define its own configuration schema.
/// </summary>
public class AudioProviderConfig
{
    /// <summary>Provider key (e.g., "openai-audio").</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>API key or authentication token.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Base URL for API endpoint (optional override).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Provider-specific settings as key-value pairs.</summary>
    public Dictionary<string, object>? Settings { get; set; }

    /// <summary>Timeout for API requests in seconds.</summary>
    public int? TimeoutSeconds { get; set; }
}

/// <summary>
/// Metadata about an audio provider's capabilities.
/// </summary>
public class AudioProviderMetadata
{
    public string ProviderKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool SupportsTTS { get; init; } = false;
    public bool SupportsSTT { get; init; } = false;
    public bool SupportsVAD { get; init; } = false;
    public bool SupportsStreaming { get; init; } = false;
    public string[]? SupportedVoices { get; init; }
    public string[]? SupportedLanguages { get; init; }
    public string[]? SupportedFormats { get; init; }
    public string? DocumentationUrl { get; init; }
    public Dictionary<string, object>? CustomProperties { get; init; }
}

/// <summary>
/// Result of audio provider configuration validation.
/// </summary>
public class AudioProviderValidationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = new();
    public List<string> Warnings { get; init; } = new();

    public static AudioProviderValidationResult Success() => new() { IsValid = true };

    public static AudioProviderValidationResult Failure(params string[] errors) =>
        new() { IsValid = false, Errors = new List<string>(errors) };
}
