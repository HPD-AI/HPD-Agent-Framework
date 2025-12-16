// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.Audio;

/// <summary>
/// Extension methods for configuring audio options on AgentRunOptions.
/// </summary>
public static class AgentRunOptionsAudioExtensions
{
    /// <summary>
    /// Configures audio options for this run.
    /// </summary>
    /// <param name="options">The agent run options.</param>
    /// <param name="audio">The audio run options.</param>
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
        var audio = GetOrCreateAudioOptions(options);
        configure(audio);
        return options;
    }

    /// <summary>Voice conversation with captions (most common).</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithVoiceConversation(this AgentRunOptions options)
        => options.WithAudio(AudioRunOptions.VoiceConversation());

    /// <summary>Voice input only, text output.</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithVoiceInput(this AgentRunOptions options)
        => options.WithAudio(AudioRunOptions.VoiceInput());

    /// <summary>Text input, voice + text output.</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithVoiceOutput(this AgentRunOptions options)
        => options.WithAudio(AudioRunOptions.VoiceOutput());

    /// <summary>Disable audio for this request.</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithTextOnly(this AgentRunOptions options)
        => options.WithAudio(AudioRunOptions.TextOnly());

    /// <summary>Full voice conversation without captions.</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithFullVoice(this AgentRunOptions options)
        => options.WithAudio(AudioRunOptions.FullVoice());

    /// <summary>Native processing with captions (GPT-4o Realtime, Gemini Live).</summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithNativeAudio(this AgentRunOptions options)
        => options.WithAudio(AudioRunOptions.NativeWithCaptions());

    /// <summary>
    /// Sets the TTS voice for this request.
    /// </summary>
    /// <param name="options">The agent run options.</param>
    /// <param name="voice">The voice to use (e.g., "nova", "alloy").</param>
    /// <returns>The agent run options for chaining.</returns>
    public static AgentRunOptions WithVoice(this AgentRunOptions options, string voice)
    {
        var audio = GetOrCreateAudioOptions(options);
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
        var audio = GetOrCreateAudioOptions(options);
        audio.Model = model;
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
        var audio = GetOrCreateAudioOptions(options);
        audio.Speed = speed;
        return options;
    }

    /// <summary>
    /// Gets the audio options from the run options, or null if not set.
    /// </summary>
    /// <param name="options">The agent run options.</param>
    /// <returns>The audio run options, or null.</returns>
    public static AudioRunOptions? GetAudioOptions(this AgentRunOptions options)
        => options.Audio as AudioRunOptions;

    private static AudioRunOptions GetOrCreateAudioOptions(AgentRunOptions options)
    {
        if (options.Audio is AudioRunOptions existing)
            return existing;

        var audio = new AudioRunOptions();
        options.Audio = audio;
        return audio;
    }
}
