// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Text.Json.Serialization;

namespace HPD.Agent.Audio;

/// <summary>
/// Per-request audio configuration for AgentRunOptions.
/// Follows same pattern as ChatRunOptions, StructuredOutputOptions.
/// JSON-serializable for FFI compatibility.
/// </summary>
public class AudioRunOptions
{
    /// <summary>
    /// How audio is processed internally. Null = use middleware default.
    /// </summary>
    [JsonPropertyName("processingMode")]
    public AudioProcessingMode? ProcessingMode { get; set; }

    /// <summary>
    /// What input/output modalities to use. Null = use middleware default.
    /// </summary>
    [JsonPropertyName("ioMode")]
    public AudioIOMode? IOMode { get; set; }

    /// <summary>
    /// Override TTS voice for this request. Null = use middleware default.
    /// </summary>
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    /// <summary>
    /// Override TTS model for this request. Null = use middleware default.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Override TTS speed for this request. Null = use middleware default.
    /// </summary>
    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    /// <summary>
    /// Disable audio entirely for this request (equivalent to IOMode = AudioToText with no TTS).
    /// </summary>
    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; } = false;

    /// <summary>
    /// Override language for STT/TTS for this request. Null = use middleware default.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Output audio format for this request (e.g., "mp3", "pcm", "opus").
    /// Null = use middleware default.
    /// </summary>
    [JsonPropertyName("outputFormat")]
    public string? OutputFormat { get; set; }

    /// <summary>
    /// Sample rate for output audio in Hz. Null = use middleware default.
    /// </summary>
    [JsonPropertyName("sampleRate")]
    public int? SampleRate { get; set; }

    //
    // Static factory methods for common configurations
    //

    /// <summary>Voice conversation with captions (most common).</summary>
    public static AudioRunOptions VoiceConversation() => new() { IOMode = AudioIOMode.AudioToAudioAndText };

    /// <summary>Voice input only, text output.</summary>
    public static AudioRunOptions VoiceInput() => new() { IOMode = AudioIOMode.AudioToText };

    /// <summary>Text input, voice + text output.</summary>
    public static AudioRunOptions VoiceOutput() => new() { IOMode = AudioIOMode.TextToAudioAndText };

    /// <summary>Disable audio for this request.</summary>
    public static AudioRunOptions TextOnly() => new() { Disabled = true };

    /// <summary>Native processing (GPT-4o Realtime, Gemini Live) with captions.</summary>
    public static AudioRunOptions NativeWithCaptions() => new()
    {
        ProcessingMode = AudioProcessingMode.Native,
        IOMode = AudioIOMode.AudioToAudioAndText
    };

    /// <summary>Full voice conversation without captions.</summary>
    public static AudioRunOptions FullVoice() => new() { IOMode = AudioIOMode.AudioToAudio };

    /// <summary>Text input with voice output only (no text).</summary>
    public static AudioRunOptions TextToVoice() => new() { IOMode = AudioIOMode.TextToAudio };
}
