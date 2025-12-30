// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Text.Json.Serialization;

namespace HPD.Agent.Audio;

/// <summary>
/// Per-request audio configuration for AgentRunOptions.
/// Inherits from AudioPipelineConfig for full configuration control.
/// JSON-serializable for FFI compatibility.
/// </summary>
public class AudioRunOptions : AudioPipelineConfig
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
    /// Override language for STT/TTS for this request. Null = use middleware default.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

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

    /// <summary>
    /// Quick Answer only (low latency, minimal features).
    /// </summary>
    public static AudioRunOptions QuickAnswer() => new()
    {
        EnableQuickAnswer = true,
        EnableSpeedAdaptation = false,
        EnableFalseInterruptionRecovery = false,
        EnableFillerAudio = false,
        EnableTextFiltering = true
    };

    /// <summary>
    /// Full feature set (all enhancements enabled).
    /// </summary>
    public static AudioRunOptions FullFeatures() => new()
    {
        EnableQuickAnswer = true,
        EnableSpeedAdaptation = true,
        EnableFalseInterruptionRecovery = true,
        EnableFillerAudio = true,
        EnableTextFiltering = true,
        UseCombinedProbability = true
    };
}
