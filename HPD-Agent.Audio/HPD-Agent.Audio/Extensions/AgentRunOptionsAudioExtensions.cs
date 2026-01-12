// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.Audio;

/// <summary>
/// Extension methods for configuring audio options on AgentRunOptions.
/// Uses the slim AudioRunOptions API for common runtime customizations.
/// </summary>
public static class AgentRunOptionsAudioExtensions
{
    /// <summary>
    /// Configures audio options for this run using AudioRunOptions (slim API).
    /// </summary>
    /// <param name="options">The agent run options.</param>
    /// <param name="audio">The audio runtime options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithAudio(this AgentRunOptions options, AudioRunOptions audio)
    {
        options.Audio = audio;
        return options;
    }

    /// <summary>
    /// Configures audio options for this run using a builder action.
    /// </summary>
    /// <param name="options">The agent run options.</param>
    /// <param name="configure">Action to configure the audio options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithAudio(this AgentRunOptions options, Action<AudioRunOptions> configure)
    {
        var audio = GetOrCreateAudioRunOptions(options);
        configure(audio);
        return options;
    }

    /// <summary>Voice conversation with captions (most common).</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithVoiceConversation(this AgentRunOptions options)
        => options.WithAudio(new AudioRunOptions { IOMode = AudioIOMode.AudioToAudioAndText });

    /// <summary>Voice input only, text output.</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithVoiceInput(this AgentRunOptions options)
        => options.WithAudio(new AudioRunOptions { IOMode = AudioIOMode.AudioToText });

    /// <summary>Text input, voice + text output.</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithVoiceOutput(this AgentRunOptions options)
        => options.WithAudio(new AudioRunOptions { IOMode = AudioIOMode.TextToAudioAndText });

    /// <summary>Disable audio for this request.</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithTextOnly(this AgentRunOptions options)
        => options.WithAudio(new AudioRunOptions { Disabled = true });

    /// <summary>Full voice conversation without captions.</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithFullVoice(this AgentRunOptions options)
        => options.WithAudio(new AudioRunOptions { IOMode = AudioIOMode.AudioToAudio });

    /// <summary>Native processing with captions (GPT-4o Realtime, Gemini Live).</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithNativeAudio(this AgentRunOptions options)
        => options.WithAudio(new AudioRunOptions { ProcessingMode = AudioProcessingMode.Native, IOMode = AudioIOMode.AudioToAudioAndText });

    /// <summary>
    /// Sets the TTS voice for this request.
    /// </summary>
    /// <param name="options">The agent run options.</param>
    /// <param name="voice">The voice to use (e.g., "nova", "alloy", "shimmer").</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithVoice(this AgentRunOptions options, string voice)
    {
        var audio = GetOrCreateAudioRunOptions(options);
        audio.Voice = voice;
        return options;
    }

    /// <summary>
    /// Sets the TTS model for this request.
    /// </summary>
    /// <param name="options">The agent run options.</param>
    /// <param name="model">The model to use (e.g., "tts-1", "tts-1-hd").</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithTtsModel(this AgentRunOptions options, string model)
    {
        var audio = GetOrCreateAudioRunOptions(options);
        audio.TtsModel = model;
        return options;
    }

    /// <summary>
    /// Sets the TTS speed for this request.
    /// </summary>
    /// <param name="options">The agent run options.</param>
    /// <param name="speed">The speed multiplier (0.25 to 4.0).</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithTtsSpeed(this AgentRunOptions options, float speed)
    {
        var audio = GetOrCreateAudioRunOptions(options);
        audio.TtsSpeed = speed;
        return options;
    }

    /// <summary>
    /// Gets the audio runtime options from the run options, or null if not set.
    /// </summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The audio runtime options, or null.</returns>
    public static AudioRunOptions? GetAudioRunOptions(this AgentRunOptions options)
        => options.Audio as AudioRunOptions;

    private static AudioRunOptions GetOrCreateAudioRunOptions(AgentRunOptions options)
    {
        if (options.Audio is AudioRunOptions existing)
            return existing;

        var audio = new AudioRunOptions();
        options.Audio = audio;
        return audio;
    }
}
