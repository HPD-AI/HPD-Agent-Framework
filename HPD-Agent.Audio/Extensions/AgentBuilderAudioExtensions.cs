// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.Audio;

/// <summary>
/// Extension methods for configuring audio pipeline on AgentBuilder.
/// </summary>
public static class AgentBuilderAudioExtensions
{
    /// <summary>
    /// Adds audio pipeline middleware with configuration.
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="configure">Action to configure the audio pipeline middleware.</param>
    /// <returns>The agent builder for chaining.</returns>
    public static AgentBuilder UseAudioPipeline(
        this AgentBuilder builder,
        Action<AudioPipelineMiddleware> configure)
    {
        var middleware = new AudioPipelineMiddleware();
        configure(middleware);
        return builder.WithMiddleware(middleware);
    }

    /// <summary>
    /// Adds audio pipeline middleware with default configuration.
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <returns>The agent builder for chaining.</returns>
    public static AgentBuilder UseAudioPipeline(this AgentBuilder builder)
    {
        return builder.WithMiddleware(new AudioPipelineMiddleware());
    }

    /// <summary>
    /// Adds audio pipeline middleware with TTS client configuration.
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="ttsClient">The text-to-speech client to use.</param>
    /// <param name="configure">Optional additional configuration.</param>
    /// <returns>The agent builder for chaining.</returns>
    public static AgentBuilder UseAudioPipeline(
        this AgentBuilder builder,
        ITextToSpeechClient ttsClient,
        Action<AudioPipelineMiddleware>? configure = null)
    {
        var middleware = new AudioPipelineMiddleware
        {
            TextToSpeechClient = ttsClient
        };
        configure?.Invoke(middleware);
        return builder.WithMiddleware(middleware);
    }

    /// <summary>
    /// Adds audio pipeline middleware with full provider configuration.
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="ttsClient">The text-to-speech client to use.</param>
    /// <param name="sttClient">The speech-to-text client to use (from Microsoft.Extensions.AI).</param>
    /// <param name="vad">The voice activity detector to use.</param>
    /// <param name="turnDetector">The turn detector to use.</param>
    /// <param name="configure">Optional additional configuration.</param>
    /// <returns>The agent builder for chaining.</returns>
    public static AgentBuilder UseAudioPipeline(
        this AgentBuilder builder,
        ITextToSpeechClient? ttsClient,
        Microsoft.Extensions.AI.ISpeechToTextClient? sttClient,
        IVoiceActivityDetector? vad = null,
        ITurnDetector? turnDetector = null,
        Action<AudioPipelineMiddleware>? configure = null)
    {
        var middleware = new AudioPipelineMiddleware
        {
            TextToSpeechClient = ttsClient,
            SpeechToTextClient = sttClient,
            Vad = vad,
            TurnDetector = turnDetector ?? new HeuristicTurnDetector()
        };
        configure?.Invoke(middleware);
        return builder.WithMiddleware(middleware);
    }
}
