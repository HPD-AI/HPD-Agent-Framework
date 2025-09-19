using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;


/// <summary>
/// Audio capability options following Extensions.AI patterns
/// </summary>
/// [Experimental("HPDAUDIO001")]
public sealed class AudioCapabilityOptions
{
    /// <summary>Gets or sets the default STT model to use when none is specified.</summary>
    public string? DefaultSttModel { get; set; }

    /// <summary>Gets or sets the default TTS model to use when none is specified.</summary>
    public string? DefaultTtsModel { get; set; }

    /// <summary>Gets or sets the default voice to use for TTS when none is specified.</summary>
    public string? DefaultVoice { get; set; }

    /// <summary>Gets or sets the default language for audio processing.</summary>
    public string? DefaultLanguage { get; set; } = "en-US";

    /// <summary>Gets or sets the maximum allowed audio input duration.</summary>
    public TimeSpan MaxAudioDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets the operation timeout for audio processing requests.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets whether to collect performance metrics.</summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>Gets or sets whether to enable automatic retry on transient failures.</summary>
    public bool EnableRetry { get; set; } = true;

    /// <summary>Gets or sets the maximum number of retry attempts.</summary>
    public int MaxRetryAttempts { get; set; } = 3;
}

/// <summary>
/// Per-conversation audio processing options for STT and TTS operations
/// </summary>
[Experimental("HPDAUDIO001")]
public sealed class AudioProcessingOptions
{
    /// <summary>Gets or sets the STT model to use for this conversation.</summary>
    public string? SttModel { get; set; }

    /// <summary>Gets or sets the TTS model to use for this conversation.</summary>
    public string? TtsModel { get; set; }

    /// <summary>Gets or sets the voice to use for TTS in this conversation.</summary>
    public string? Voice { get; set; }

    /// <summary>Gets or sets the language for this conversation.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets whether to include the transcription in the response.</summary>
    public bool IncludeTranscription { get; set; } = true;

    /// <summary>Gets or sets whether to synthesize audio response.</summary>
    public bool SynthesizeResponse { get; set; }

    /// <summary>Gets or sets any additional properties for this conversation.</summary>
    public AdditionalPropertiesDictionary? AdditionalProperties { get; set; }
}
