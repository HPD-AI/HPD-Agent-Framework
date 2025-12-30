// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace HPD.Agent.Audio.ElevenLabs;

/// <summary>
/// ElevenLabs speech-to-text client implementing Microsoft.Extensions.AI ISpeechToTextClient.
/// Uses ElevenLabs Scribe model for audio transcription.
/// </summary>
public sealed class ElevenLabsSpeechToTextClient : ISpeechToTextClient
{
    private readonly ElevenLabsAudioConfig _config;
    private readonly global::ElevenLabs.SpeechToTextClient _client;
    private bool _disposed;

    /// <summary>
    /// Creates a new ElevenLabs STT client.
    /// </summary>
    /// <param name="config">ElevenLabs configuration</param>
    /// <param name="httpClient">Optional HttpClient for connection pooling</param>
    public ElevenLabsSpeechToTextClient(
        ElevenLabsAudioConfig config,
        HttpClient? httpClient = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Validate();

        // The ElevenLabs SDK already implements ISpeechToTextClient
        // We can use it directly with our config
        _client = new global::ElevenLabs.SpeechToTextClient(
            apiKey: _config.ApiKey,
            httpClient: httpClient
        );
    }

    /// <summary>
    /// Transcribe audio to text (non-streaming).
    /// </summary>
    public async Task<SpeechToTextResponse> GetTextAsync(
        Stream audioSpeechStream,
        SpeechToTextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioSpeechStream);

        // Delegate to ElevenLabs SDK's implementation
        return await _client.GetTextAsync(audioSpeechStream, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Transcribe streaming audio to text.
    /// </summary>
    public IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioSpeechChunks,
        SpeechToTextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioSpeechChunks);

        // Delegate to ElevenLabs SDK's implementation
        return _client.GetStreamingTextAsync(audioSpeechChunks, options, cancellationToken);
    }

    /// <summary>
    /// Gets metadata about this STT provider.
    /// </summary>
    public AIModelInformation GetModelInformation(CancellationToken cancellationToken = default)
    {
        return new AIModelInformation
        {
            ProviderName = "ElevenLabs",
            ModelId = _config.SttModelId,
            Metadata = new Dictionary<string, object?>
            {
                ["ResponseFormat"] = _config.SttResponseFormat,
                ["SupportsStreaming"] = true
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
}
