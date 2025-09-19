using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// Simplified audio capability for basic STT and TTS operations
/// </summary>
public sealed class AudioCapability : IAsyncDisposable
{
    private readonly AudioCapabilityOptions _options;
    private readonly ISpeechToTextClient? _sttClient;
    private readonly ITextToSpeechClient? _ttsClient;
    private readonly ILogger<AudioCapability>? _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioCapability"/> class
    /// </summary>
    /// <param name="options">Audio capability options</param>
    /// <param name="sttClient">Speech-to-text client (optional)</param>
    /// <param name="ttsClient">Text-to-speech client (optional)</param>
    /// <param name="logger">Logger instance</param>
    public AudioCapability(
        AudioCapabilityOptions options,
        ISpeechToTextClient? sttClient = null,
        ITextToSpeechClient? ttsClient = null,
        ILogger<AudioCapability>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sttClient = sttClient;
        _ttsClient = ttsClient;
        _logger = logger;
    }

    /// <summary>
    /// Transcribe audio input to text
    /// </summary>
    /// <param name="audioInput">The input audio stream</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Transcribed text</returns>
    public async Task<string> TranscribeAsync(
        Stream audioInput,
        CancellationToken cancellationToken = default)
    {
        if (_sttClient == null)
            throw new InvalidOperationException("Speech-to-text client not configured");
        
        var response = await _sttClient.GetTextAsync(audioInput, cancellationToken: cancellationToken);
        return response.Text;
    }

    /// <summary>
    /// Synthesize text to audio
    /// </summary>
    /// <param name="text">The text to synthesize</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Synthesized audio stream</returns>
    public async Task<Stream> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (_ttsClient == null)
            throw new InvalidOperationException("Text-to-speech client not configured");
        
        var response = await _ttsClient.GetAudioAsync(text, cancellationToken: cancellationToken);
        return response.AudioStream;
    }

    /// <summary>Check if STT is available</summary>
    public bool HasSpeechToText => _sttClient != null;
    
    /// <summary>Check if TTS is available</summary>
    public bool HasTextToSpeech => _ttsClient != null;

    /// <summary>Disposes the audio capability and its resources</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _sttClient?.Dispose();
            _ttsClient?.Dispose();
            _disposed = true;
        }
        await Task.CompletedTask;
    }
}