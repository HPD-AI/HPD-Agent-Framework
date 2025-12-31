// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace HPD.Agent.Audio.ElevenLabs;

/// <summary>
/// ElevenLabs text-to-speech client implementing ITextToSpeechClient.
/// Supports HTTP streaming TTS using the ElevenLabs SDK.
/// </summary>
public sealed class ElevenLabsTextToSpeechClient : ITextToSpeechClient
{
    private readonly ElevenLabsAudioConfig _config;
    private readonly global::ElevenLabs.TextToSpeechClient _client;
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

        _client = new global::ElevenLabs.TextToSpeechClient(
            httpClient: httpClient,
            authorizations: [
                new global::ElevenLabs.EndPointAuthorization
                {
                    Type = "ApiKey",
                    Location = "Header",
                    Name = "xi-api-key",
                    Value = _config.ApiKey!
                }
            ]
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
        var audioBytes = await _client.CreateTextToSpeechByVoiceIdStreamAsync(
            voiceId: voiceId,
            text: text,
            modelId: options?.ModelId ?? _config.ModelId,
            languageCode: options?.Language ?? _config.LanguageCode,
            cancellationToken: cancellationToken
        );

        return new TextToSpeechResponse
        {
            Audio = new DataContent(audioBytes, "audio/mpeg"),
            ModelId = options?.ModelId ?? _config.ModelId,
            Voice = voiceId
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

        var voiceId = options?.Voice ?? _config.DefaultVoiceId;
        int sequenceNumber = 0;

        await foreach (var textChunk in textChunks.WithCancellation(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(textChunk))
            {
                continue;
            }

            var audioBytes = await _client.CreateTextToSpeechByVoiceIdStreamAsync(
                voiceId: voiceId,
                text: textChunk,
                modelId: options?.ModelId ?? _config.ModelId,
                languageCode: options?.Language ?? _config.LanguageCode,
                cancellationToken: cancellationToken
            );

            yield return new TextToSpeechResponseUpdate
            {
                Audio = new DataContent(audioBytes, "audio/mpeg"),
                SequenceNumber = sequenceNumber++
            };
        }
    }

    /// <summary>
    /// Gets a service of the specified type.
    /// </summary>
    public object? GetService(Type serviceType, object? key = null)
    {
        if (serviceType == typeof(ElevenLabsAudioConfig))
        {
            return _config;
        }

        if (serviceType == typeof(global::ElevenLabs.TextToSpeechClient))
        {
            return _client;
        }

        return null;
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
}
