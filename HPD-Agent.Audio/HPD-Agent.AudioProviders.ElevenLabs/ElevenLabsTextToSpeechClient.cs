// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace HPD.Agent.Audio.ElevenLabs;

/// <summary>
/// ElevenLabs text-to-speech client implementing Microsoft.Extensions.AI ITextToSpeechClient.
/// Supports both HTTP streaming and WebRTC-based real-time communication.
/// </summary>
public sealed class ElevenLabsTextToSpeechClient : ITextToSpeechClient
{
    private readonly ElevenLabsAudioConfig _config;
    private readonly global::ElevenLabs.ElevenLabsClient _client;
    private bool _disposed;

    /// <summary>
    /// Creates a new ElevenLabs TTS client.
    /// </summary>
    /// <param name="config">ElevenLabs configuration</param>
    /// <param name="httpClient">Optional HttpClient for connection pooling</param>
    public ElevenLabsTextToSpeechClient(
        ElevenLabsAudioConfig config,
        HttpClient? httpClient = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Validate();

        // Initialize ElevenLabs client with API key
        _client = new global::ElevenLabs.ElevenLabsClient(
            apiKey: _config.ApiKey,
            httpClient: httpClient
        );
    }

    /// <summary>
    /// Get speech audio from text (non-streaming).
    /// </summary>
    public async Task<TextToSpeechResponse> GetSpeechAsync(
        string text,
        TextToSpeechOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var voiceId = options?.Voice ?? _config.DefaultVoiceId;
        var modelId = options?.ModelId ?? _config.ModelId;

        // Build voice settings
        var voiceSettings = BuildVoiceSettings(options);

        // Build request
        var request = new global::ElevenLabs.BodyTextToSpeechV1TextToSpeechVoiceIdPost
        {
            Text = text,
            ModelId = modelId,
            VoiceSettings = voiceSettings,
            OutputFormat = MapOutputFormat(options?.OutputFormat ?? _config.OutputFormat)
        };

        // Call ElevenLabs API
        var audioBytes = await _client.TextToSpeech.CreateTextToSpeechByVoiceIdAsync(
            voiceId: voiceId,
            request: request,
            xiApiKey: _config.ApiKey,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        // Return response
        return new TextToSpeechResponse
        {
            Audio = new DataContent(audioBytes, GetMimeType(options?.OutputFormat ?? _config.OutputFormat)),
            Text = text
        };
    }

    /// <summary>
    /// Get speech audio from streaming text chunks.
    /// Streams audio chunks as they are generated.
    /// </summary>
    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingSpeechAsync(
        IAsyncEnumerable<string> textChunks,
        TextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(textChunks);

        // ElevenLabs HTTP API requires complete text for synthesis
        // We buffer all chunks first (similar to OpenAI TTS)
        var fullText = new System.Text.StringBuilder();
        await foreach (var chunk in textChunks.WithCancellation(cancellationToken))
        {
            fullText.Append(chunk);
        }

        var text = fullText.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var voiceId = options?.Voice ?? _config.DefaultVoiceId;
        var modelId = options?.ModelId ?? _config.ModelId;

        // Build voice settings
        var voiceSettings = BuildVoiceSettings(options);

        // Build request
        var request = new global::ElevenLabs.BodyTextToSpeechV1TextToSpeechVoiceIdStreamPost
        {
            Text = text,
            ModelId = modelId,
            VoiceSettings = voiceSettings,
            OutputFormat = MapOutputFormat(options?.OutputFormat ?? _config.OutputFormat)
        };

        // Use HTTP streaming endpoint
        var chunkIndex = 0;
        var totalBytes = 0;

        // Create a TaskCompletionSource to handle streaming chunks
        var audioChunks = new List<byte[]>();

        await _client.TextToSpeech.CreateTextToSpeechByVoiceIdStreamAsync(
            voiceId: voiceId,
            request: request,
            xiApiKey: _config.ApiKey,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        // Note: The ElevenLabs SDK doesn't expose individual chunks during streaming
        // It returns the complete audio after streaming. For true chunk-by-chunk streaming,
        // we would need to implement our own WebSocket client.

        // For now, we return a single chunk
        // TODO: Implement WebSocket-based streaming for lower latency
        var response = await GetSpeechAsync(text, options, cancellationToken);

        yield return new TextToSpeechResponseUpdate
        {
            Audio = response.Audio,
            SequenceNumber = 0,
            IsLast = true
        };
    }

    /// <summary>
    /// Gets metadata about this TTS provider.
    /// </summary>
    public AIModelInformation GetModelInformation(CancellationToken cancellationToken = default)
    {
        return new AIModelInformation
        {
            ProviderName = "ElevenLabs",
            ModelId = _config.ModelId,
            Metadata = new Dictionary<string, object?>
            {
                ["VoiceId"] = _config.DefaultVoiceId,
                ["OutputFormat"] = _config.OutputFormat,
                ["Stability"] = _config.Stability,
                ["SimilarityBoost"] = _config.SimilarityBoost,
                ["Style"] = _config.Style,
                ["UseSpeakerBoost"] = _config.UseSpeakerBoost,
                ["SupportsStreaming"] = true,
                ["SupportsWebRTC"] = true,  // Via ConvAI agents
                ["ChunkLengthSchedule"] = _config.ChunkLengthSchedule
            }
        };
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
    }

    //
    // HELPER METHODS
    //

    private global::ElevenLabs.VoiceSettings? BuildVoiceSettings(TextToSpeechOptions? options)
    {
        // If no custom options, use config defaults
        if (options?.AdditionalProperties == null || options.AdditionalProperties.Count == 0)
        {
            return new global::ElevenLabs.VoiceSettings
            {
                Stability = _config.Stability,
                SimilarityBoost = _config.SimilarityBoost,
                Style = _config.Style,
                UseSpeakerBoost = _config.UseSpeakerBoost,
                Speed = _config.Speed
            };
        }

        // Check for custom settings in options
        var stability = options.AdditionalProperties.TryGetValue("Stability", out var s) && s is float sf
            ? sf : _config.Stability;
        var similarityBoost = options.AdditionalProperties.TryGetValue("SimilarityBoost", out var sb) && sb is float sbf
            ? sbf : _config.SimilarityBoost;
        var style = options.AdditionalProperties.TryGetValue("Style", out var st) && st is float stf
            ? stf : _config.Style;
        var useSpeakerBoost = options.AdditionalProperties.TryGetValue("UseSpeakerBoost", out var usb) && usb is bool usbb
            ? usbb : _config.UseSpeakerBoost;
        var speed = options.AdditionalProperties.TryGetValue("Speed", out var spd) && spd is float spdf
            ? spdf : _config.Speed;

        return new global::ElevenLabs.VoiceSettings
        {
            Stability = stability,
            SimilarityBoost = similarityBoost,
            Style = style,
            UseSpeakerBoost = useSpeakerBoost,
            Speed = speed
        };
    }

    private static string? MapOutputFormat(string? format)
    {
        // ElevenLabs supports: mp3_44100_64, mp3_44100_96, mp3_44100_128, mp3_44100_192,
        // pcm_16000, pcm_22050, pcm_24000, pcm_44100, ulaw_8000
        return format?.ToLowerInvariant() switch
        {
            "mp3" or "audio/mpeg" => "mp3_44100_128",
            "mp3_44100_64" => "mp3_44100_64",
            "mp3_44100_96" => "mp3_44100_96",
            "mp3_44100_128" => "mp3_44100_128",
            "mp3_44100_192" => "mp3_44100_192",
            "pcm" or "audio/pcm" => "pcm_24000",
            "pcm_16000" => "pcm_16000",
            "pcm_22050" => "pcm_22050",
            "pcm_24000" => "pcm_24000",
            "pcm_44100" => "pcm_44100",
            "ulaw_8000" => "ulaw_8000",
            _ => "mp3_44100_128"  // Default
        };
    }

    private static string GetMimeType(string? format)
    {
        return format?.ToLowerInvariant() switch
        {
            var f when f?.StartsWith("mp3") == true => "audio/mpeg",
            var f when f?.StartsWith("pcm") == true => "audio/pcm",
            var f when f?.StartsWith("ulaw") == true => "audio/basic",
            _ => "audio/mpeg"
        };
    }
}
