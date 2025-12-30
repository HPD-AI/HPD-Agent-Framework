// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.Audio;

/// <summary>
/// Comprehensive configuration for AudioPipelineMiddleware.
/// Can be used as middleware defaults or per-request overrides via AudioRunOptions.
/// </summary>
public class AudioPipelineConfig
{
    //
    // CORE TTS/STT SETTINGS
    //

    /// <summary>Default TTS voice. Default: "alloy".</summary>
    public string? Voice { get; set; } = "alloy";

    /// <summary>Default TTS model. Default: "tts-1".</summary>
    public string? Model { get; set; } = "tts-1";

    /// <summary>Default audio output format. Default: "mp3".</summary>
    public string? OutputFormat { get; set; } = "mp3";

    /// <summary>Default sample rate. Default: 24000.</summary>
    public int? SampleRate { get; set; } = 24000;

    /// <summary>TTS speech speed (0.25 to 4.0). Default: null (provider default).</summary>
    public float? Speed { get; set; }

    /// <summary>Disable audio processing for this request. Default: false.</summary>
    public bool? Disabled { get; set; }

    //
    // VAD CONFIGURATION
    //

    /// <summary>Minimum speech duration to confirm speech start. Default: 50ms.</summary>
    public float? VadMinSpeechDuration { get; set; } = 0.05f;

    /// <summary>Minimum silence duration to confirm speech end. Default: 550ms.</summary>
    public float? VadMinSilenceDuration { get; set; } = 0.55f;

    /// <summary>Audio to buffer before speech confirmed. Default: 500ms.</summary>
    public float? VadPrefixPaddingDuration { get; set; } = 0.5f;

    /// <summary>Speech probability threshold. Default: 0.5.</summary>
    public float? VadActivationThreshold { get; set; } = 0.5f;

    //
    // TURN DETECTION
    //

    /// <summary>Turn detection strategy for silence. Default: FastPath.</summary>
    public TurnDetectionStrategy? SilenceStrategy { get; set; } = TurnDetectionStrategy.FastPath;

    /// <summary>Turn detection strategy for ML. Default: OnAmbiguous.</summary>
    public TurnDetectionStrategy? MlStrategy { get; set; } = TurnDetectionStrategy.OnAmbiguous;

    /// <summary>Silence threshold for fast-path rejection. Default: 1.5s.</summary>
    public float? SilenceFastPathThreshold { get; set; } = 1.5f;

    /// <summary>Min endpointing delay when ML is confident. Default: 0.3s.</summary>
    public float? MinEndpointingDelay { get; set; } = 0.3f;

    /// <summary>Max endpointing delay when ML is uncertain. Default: 1.5s.</summary>
    public float? MaxEndpointingDelay { get; set; } = 1.5f;

    //
    // SILENCE-BASED PROBABILITY BOOST (NEW)
    //

    /// <summary>Multiplier for silence-based probability boost. Default: 0.7.</summary>
    public float? SilenceBoostMultiplier { get; set; } = 0.7f;

    /// <summary>Whether to combine ML and silence probabilities. Default: true.</summary>
    public bool? UseCombinedProbability { get; set; } = true;

    //
    // TRAILING WORD DETECTION (NEW)
    //

    /// <summary>Custom trailing words that indicate incomplete thoughts. Default: null (uses built-in list).</summary>
    public HashSet<string>? CustomTrailingWords { get; set; }

    /// <summary>Probability penalty for trailing incomplete words. Default: 0.6.</summary>
    public float? TrailingWordPenalty { get; set; } = 0.6f;

    //
    // QUICK ANSWER
    //

    /// <summary>Enable TTS on first complete sentence. Default: true.</summary>
    public bool? EnableQuickAnswer { get; set; } = true;

    //
    // SPEED ADAPTATION
    //

    /// <summary>Enable adaptive endpointing based on user speaking speed. Default: true.</summary>
    public bool? EnableSpeedAdaptation { get; set; } = true;

    //
    // BACKCHANNEL DETECTION
    //

    /// <summary>How to handle short utterances during bot speech. Default: IgnoreShortUtterances.</summary>
    public BackchannelStrategy? BackchannelStrategy { get; set; } = Audio.BackchannelStrategy.IgnoreShortUtterances;

    /// <summary>Minimum words required to trigger interruption. Default: 2.</summary>
    public int? MinWordsForInterruption { get; set; } = 2;

    //
    // FALSE INTERRUPTION RECOVERY (NEW)
    //

    /// <summary>Enable false interruption recovery (pause before full interrupt). Default: true.</summary>
    public bool? EnableFalseInterruptionRecovery { get; set; } = true;

    /// <summary>Timeout before resuming paused speech (seconds). Default: 2.0.</summary>
    public float? FalseInterruptionTimeout { get; set; } = 2.0f;

    /// <summary>Whether to resume synthesis after timeout. Default: true.</summary>
    public bool? ResumeFalseInterruption { get; set; } = true;

    /// <summary>Max audio chunks to buffer during pause. Default: 100.</summary>
    public int? MaxBufferedChunksDuringPause { get; set; } = 100;

    //
    // FILLER AUDIO (NEW)
    //

    /// <summary>Enable filler audio during LLM thinking. Default: false.</summary>
    public bool? EnableFillerAudio { get; set; } = false;

    /// <summary>Silence duration before playing filler. Default: 1.5s.</summary>
    public float? FillerSilenceThreshold { get; set; } = 1.5f;

    /// <summary>Filler phrases to pre-cache. Default: ["Um...", "Let me see...", "One moment..."].</summary>
    public string[]? FillerPhrases { get; set; } = ["Um...", "Let me see...", "One moment..."];

    /// <summary>Filler selection strategy. Default: Random.</summary>
    public FillerStrategy? FillerSelectionStrategy { get; set; } = FillerStrategy.Random;

    /// <summary>Max filler plays per turn. Default: 1.</summary>
    public int? MaxFillerPlaysPerTurn { get; set; } = 1;

    /// <summary>Voice override for filler audio. Default: null (uses main voice).</summary>
    public string? FillerVoice { get; set; }

    /// <summary>Speed override for filler audio. Default: 0.95 (slightly slower).</summary>
    public float? FillerSpeed { get; set; } = 0.95f;

    //
    // TEXT FILTERING
    //

    /// <summary>Enable text filtering for TTS. Default: true.</summary>
    public bool? EnableTextFiltering { get; set; } = true;

    /// <summary>Filter code blocks. Default: true.</summary>
    public bool? FilterCodeBlocks { get; set; } = true;

    /// <summary>Filter tables. Default: true.</summary>
    public bool? FilterTables { get; set; } = true;

    /// <summary>Filter URLs. Default: true.</summary>
    public bool? FilterUrls { get; set; } = true;

    /// <summary>Filter markdown formatting. Default: true.</summary>
    public bool? FilterMarkdownFormatting { get; set; } = true;

    /// <summary>Filter emoji. Default: true.</summary>
    public bool? FilterEmoji { get; set; } = true;

    //
    // PREEMPTIVE GENERATION
    //

    /// <summary>Enable preemptive generation. Default: false.</summary>
    public bool? EnablePreemptiveGeneration { get; set; } = false;

    /// <summary>Confidence threshold for preemptive generation. Default: 0.7.</summary>
    public float? PreemptiveGenerationThreshold { get; set; } = 0.7f;

    //
    // HELPER METHODS
    //

    /// <summary>
    /// Creates a config with all features disabled (audio-only passthrough).
    /// </summary>
    public static AudioPipelineConfig CreateDisabled() => new()
    {
        Disabled = true
    };

    /// <summary>
    /// Creates a minimal config for basic STT/TTS without advanced features.
    /// </summary>
    public static AudioPipelineConfig Basic() => new()
    {
        EnableQuickAnswer = false,
        EnableSpeedAdaptation = false,
        EnableFalseInterruptionRecovery = false,
        EnableFillerAudio = false,
        EnableTextFiltering = false,
        EnablePreemptiveGeneration = false
    };

    /// <summary>
    /// Merges per-request overrides with middleware defaults.
    /// Per-request values take precedence over defaults.
    /// </summary>
    public AudioPipelineConfig MergeWith(AudioPipelineConfig? overrides)
    {
        if (overrides == null) return this;

        return new AudioPipelineConfig
        {
            // Core TTS/STT
            Voice = overrides.Voice ?? Voice,
            Model = overrides.Model ?? Model,
            OutputFormat = overrides.OutputFormat ?? OutputFormat,
            SampleRate = overrides.SampleRate ?? SampleRate,
            Speed = overrides.Speed ?? Speed,
            Disabled = overrides.Disabled ?? Disabled,

            // VAD
            VadMinSpeechDuration = overrides.VadMinSpeechDuration ?? VadMinSpeechDuration,
            VadMinSilenceDuration = overrides.VadMinSilenceDuration ?? VadMinSilenceDuration,
            VadPrefixPaddingDuration = overrides.VadPrefixPaddingDuration ?? VadPrefixPaddingDuration,
            VadActivationThreshold = overrides.VadActivationThreshold ?? VadActivationThreshold,

            // Turn Detection
            SilenceStrategy = overrides.SilenceStrategy ?? SilenceStrategy,
            MlStrategy = overrides.MlStrategy ?? MlStrategy,
            SilenceFastPathThreshold = overrides.SilenceFastPathThreshold ?? SilenceFastPathThreshold,
            MinEndpointingDelay = overrides.MinEndpointingDelay ?? MinEndpointingDelay,
            MaxEndpointingDelay = overrides.MaxEndpointingDelay ?? MaxEndpointingDelay,

            // Silence Boost
            SilenceBoostMultiplier = overrides.SilenceBoostMultiplier ?? SilenceBoostMultiplier,
            UseCombinedProbability = overrides.UseCombinedProbability ?? UseCombinedProbability,

            // Trailing Words
            CustomTrailingWords = overrides.CustomTrailingWords ?? CustomTrailingWords,
            TrailingWordPenalty = overrides.TrailingWordPenalty ?? TrailingWordPenalty,

            // Features
            EnableQuickAnswer = overrides.EnableQuickAnswer ?? EnableQuickAnswer,
            EnableSpeedAdaptation = overrides.EnableSpeedAdaptation ?? EnableSpeedAdaptation,

            // Backchannel
            BackchannelStrategy = overrides.BackchannelStrategy ?? BackchannelStrategy,
            MinWordsForInterruption = overrides.MinWordsForInterruption ?? MinWordsForInterruption,

            // False Interruption Recovery
            EnableFalseInterruptionRecovery = overrides.EnableFalseInterruptionRecovery ?? EnableFalseInterruptionRecovery,
            FalseInterruptionTimeout = overrides.FalseInterruptionTimeout ?? FalseInterruptionTimeout,
            ResumeFalseInterruption = overrides.ResumeFalseInterruption ?? ResumeFalseInterruption,
            MaxBufferedChunksDuringPause = overrides.MaxBufferedChunksDuringPause ?? MaxBufferedChunksDuringPause,

            // Filler Audio
            EnableFillerAudio = overrides.EnableFillerAudio ?? EnableFillerAudio,
            FillerSilenceThreshold = overrides.FillerSilenceThreshold ?? FillerSilenceThreshold,
            FillerPhrases = overrides.FillerPhrases ?? FillerPhrases,
            FillerSelectionStrategy = overrides.FillerSelectionStrategy ?? FillerSelectionStrategy,
            MaxFillerPlaysPerTurn = overrides.MaxFillerPlaysPerTurn ?? MaxFillerPlaysPerTurn,
            FillerVoice = overrides.FillerVoice ?? FillerVoice,
            FillerSpeed = overrides.FillerSpeed ?? FillerSpeed,

            // Text Filtering
            EnableTextFiltering = overrides.EnableTextFiltering ?? EnableTextFiltering,
            FilterCodeBlocks = overrides.FilterCodeBlocks ?? FilterCodeBlocks,
            FilterTables = overrides.FilterTables ?? FilterTables,
            FilterUrls = overrides.FilterUrls ?? FilterUrls,
            FilterMarkdownFormatting = overrides.FilterMarkdownFormatting ?? FilterMarkdownFormatting,
            FilterEmoji = overrides.FilterEmoji ?? FilterEmoji,

            // Preemptive Generation
            EnablePreemptiveGeneration = overrides.EnablePreemptiveGeneration ?? EnablePreemptiveGeneration,
            PreemptiveGenerationThreshold = overrides.PreemptiveGenerationThreshold ?? PreemptiveGenerationThreshold
        };
    }
}

/// <summary>
/// Strategy for selecting filler audio phrases.
/// </summary>
public enum FillerStrategy
{
    /// <summary>Random selection from available phrases.</summary>
    Random,

    /// <summary>Round-robin through phrases in order.</summary>
    RoundRobin,

    /// <summary>Select based on duration (shortest first for quick responses).</summary>
    ShortestFirst
}
