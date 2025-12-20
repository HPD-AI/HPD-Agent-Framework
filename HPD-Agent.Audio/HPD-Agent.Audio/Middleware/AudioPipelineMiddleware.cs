// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Audio;

/// <summary>
/// Middleware that orchestrates STT → LLM → TTS pipeline.
/// Uses IAgentMiddleware hooks for streaming interception.
/// </summary>
public partial class AudioPipelineMiddleware : IAgentMiddleware
{
    //
    // PROCESSING MODE (HOW audio is processed)
    //

    /// <summary>How audio is processed internally. Default: Pipeline.</summary>
    public AudioProcessingMode ProcessingMode { get; set; } = AudioProcessingMode.Pipeline;

    //
    // I/O MODE (WHAT goes in/out)
    //

    /// <summary>What input/output modalities to use. Default: AudioToAudioAndText.</summary>
    public AudioIOMode IOMode { get; set; } = AudioIOMode.AudioToAudioAndText;

    // Derived helpers for checking I/O capabilities
    /// <summary>Whether audio input is expected.</summary>
    public bool HasAudioInput => IOMode is AudioIOMode.AudioToText
                                        or AudioIOMode.AudioToAudio
                                        or AudioIOMode.AudioToAudioAndText;

    /// <summary>Whether audio output is enabled.</summary>
    public bool HasAudioOutput => IOMode is AudioIOMode.TextToAudio
                                         or AudioIOMode.AudioToAudio
                                         or AudioIOMode.AudioToAudioAndText
                                         or AudioIOMode.TextToAudioAndText;

    /// <summary>Whether text output is enabled (in addition to or instead of audio).</summary>
    public bool HasTextOutput => IOMode is AudioIOMode.AudioToText
                                        or AudioIOMode.AudioToAudioAndText
                                        or AudioIOMode.TextToAudioAndText;

    //
    // PROVIDERS (injected)
    //

    /// <summary>STT client (from Microsoft.Extensions.AI).</summary>
    public ISpeechToTextClient? SpeechToTextClient { get; set; }

    /// <summary>TTS client.</summary>
    public ITextToSpeechClient? TextToSpeechClient { get; set; }

    /// <summary>Voice activity detector (optional, for fast interruption).</summary>
    public IVoiceActivityDetector? Vad { get; set; }

    /// <summary>Turn detector (optional, for end-of-speech detection).</summary>
    public ITurnDetector? TurnDetector { get; set; }

    //
    // VAD CONFIGURATION (like PIIMiddleware per-type strategies)
    //

    /// <summary>Minimum speech duration to confirm speech start. Default: 50ms.</summary>
    public float VadMinSpeechDuration { get; set; } = 0.05f;

    /// <summary>Minimum silence duration to confirm speech end. Default: 550ms.</summary>
    public float VadMinSilenceDuration { get; set; } = 0.55f;

    /// <summary>Audio to buffer before speech confirmed. Default: 500ms.</summary>
    public float VadPrefixPaddingDuration { get; set; } = 0.5f;

    /// <summary>Speech probability threshold. Default: 0.5.</summary>
    public float VadActivationThreshold { get; set; } = 0.5f;

    //
    // TURN DETECTION CONFIGURATION (strategy pattern like PIIMiddleware)
    //

    /// <summary>Turn detection strategy for silence. Default: FastPath.</summary>
    public TurnDetectionStrategy SilenceStrategy { get; set; } = TurnDetectionStrategy.FastPath;

    /// <summary>Turn detection strategy for ML. Default: OnAmbiguous.</summary>
    public TurnDetectionStrategy MlStrategy { get; set; } = TurnDetectionStrategy.OnAmbiguous;

    /// <summary>Silence threshold for fast-path rejection. Default: 1.5s.</summary>
    public float SilenceFastPathThreshold { get; set; } = 1.5f;

    /// <summary>Min endpointing delay when ML is confident. Default: 0.3s.</summary>
    public float MinEndpointingDelay { get; set; } = 0.3f;

    /// <summary>Max endpointing delay when ML is uncertain. Default: 1.5s.</summary>
    public float MaxEndpointingDelay { get; set; } = 1.5f;

    //
    // QUICK ANSWER (Low-Latency TTS)
    //

    /// <summary>Enable TTS on first complete sentence. Default: true.</summary>
    public bool EnableQuickAnswer { get; set; } = true;

    //
    // SPEED ADAPTATION (built-in, no separate SpeedManager class)
    //

    /// <summary>Enable adaptive endpointing based on user speaking speed. Default: true.</summary>
    public bool EnableSpeedAdaptation { get; set; } = true;

    private float _currentWpm = 150f; // Internal state
    private TurnMetrics? _turnMetrics; // Metrics for current turn

    /// <summary>Current estimated user words-per-minute.</summary>
    public float CurrentWpm => _currentWpm;

    //
    // BACKCHANNEL DETECTION (built-in, no separate IBackchannelDetector)
    //

    /// <summary>How to handle short utterances during bot speech. Default: IgnoreShortUtterances.</summary>
    public BackchannelStrategy BackchannelStrategy { get; set; } = BackchannelStrategy.IgnoreShortUtterances;

    /// <summary>Minimum words required to trigger interruption. Default: 2.</summary>
    public int MinWordsForInterruption { get; set; } = 2;

    //
    // FILLER AUDIO (built-in, no separate FillerAudioConfig)
    //

    /// <summary>Enable filler audio during LLM thinking. Default: false.</summary>
    public bool EnableFillerAudio { get; set; } = false;

    /// <summary>Silence duration before playing filler. Default: 1.5s.</summary>
    public float FillerSilenceThreshold { get; set; } = 1.5f;

    /// <summary>Filler phrases to synthesize. Default: ["Um...", "Let me see..."].</summary>
    public string[] FillerPhrases { get; set; } = ["Um...", "Let me see...", "One moment..."];

    //
    // TEXT FILTERING (clean text before TTS)
    //

    /// <summary>Enable filtering of markdown/code from TTS input. Default: true.</summary>
    public bool EnableTextFiltering { get; set; } = true;

    /// <summary>Remove code blocks (```...```) from TTS. Default: true.</summary>
    public bool FilterCodeBlocks { get; set; } = true;

    /// <summary>Remove markdown tables from TTS. Default: true.</summary>
    public bool FilterTables { get; set; } = true;

    /// <summary>Remove URLs from TTS (speaks domain only). Default: true.</summary>
    public bool FilterUrls { get; set; } = true;

    /// <summary>Remove markdown formatting (**bold**, *italic*, etc). Default: true.</summary>
    public bool FilterMarkdownFormatting { get; set; } = true;

    /// <summary>Remove emoji characters from TTS. Default: true. ( )</summary>
    public bool FilterEmoji { get; set; } = true;

    //
    // FALSE INTERRUPTION RECOVERY ( handles noise without speech)
    //

    /// <summary>Enable false interruption recovery. Default: true. ( )</summary>
    public bool EnableFalseInterruptionRecovery { get; set; } = true;

    /// <summary>Time to wait for transcript after interruption before resuming. Default: 2.0s. ( )</summary>
    public float FalseInterruptionTimeout { get; set; } = 2.0f;

    /// <summary>Resume paused speech if no transcript received. Default: true. ( )</summary>
    public bool ResumeFalseInterruption { get; set; } = true;

    //
    // PREEMPTIVE GENERATION ( speculative LLM inference for lower latency)
    //

    /// <summary>Start LLM inference before turn is confirmed. Reduces latency but uses more compute. Default: false. ( )</summary>
    public bool EnablePreemptiveGeneration { get; set; } = false;

    /// <summary>Minimum turn completion probability to trigger preemptive generation. Default: 0.7. ( )</summary>
    public float PreemptiveGenerationThreshold { get; set; } = 0.7f;

    //
    // TTS DEFAULTS
    //

    /// <summary>Default TTS voice.</summary>
    public string? DefaultVoice { get; set; }

    /// <summary>Default TTS model.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>Default TTS output format.</summary>
    public string? DefaultOutputFormat { get; set; }

    /// <summary>Default TTS sample rate.</summary>
    public int? DefaultSampleRate { get; set; }

    //
    // MIDDLEWARE HOOKS
    //

    /// <summary>
    /// Processes audio input before LLM call (STT conversion).
    /// Converts audio DataContent in messages to text transcriptions.
    /// </summary>
    public async Task BeforeIterationAsync(BeforeIterationContext context, CancellationToken cancellationToken)
    {
        var audioOptions = ResolveAudioOptions(context);

        // Check if audio input processing is needed
        if (audioOptions?.Disabled == true || !HasAudioInput || SpeechToTextClient == null)
            return;

        if (context.Messages == null || context.Messages.Count == 0)
            return;

        var turnStartTime = DateTime.UtcNow;
        _turnMetrics = new TurnMetrics { TurnStartTime = turnStartTime };

        // Process the last user message for audio content
        var lastMessage = context.Messages[^1];
        if (lastMessage.Role != ChatRole.User)
            return;

        var audioContents = lastMessage.Contents?
            .OfType<DataContent>()
            .Where(d => d.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (audioContents == null || audioContents.Count == 0)
            return;

        var transcriptionId = Guid.NewGuid().ToString("N")[..8];
        var sttStartTime = DateTime.UtcNow;

        context.TryEmit(new TranscriptionDeltaEvent(transcriptionId, "", false, null)
        {
            Priority = EventPriority.Normal
        });

        // Transcribe each audio content
        var transcriptions = new List<string>();
        foreach (var audioContent in audioContents)
        {
            try
            {
                var transcription = await TranscribeAudioAsync(audioContent, cancellationToken);
                if (!string.IsNullOrWhiteSpace(transcription))
                {
                    transcriptions.Add(transcription);

                    context.TryEmit(new TranscriptionDeltaEvent(transcriptionId, transcription, false, null)
                    {
                        Priority = EventPriority.Normal
                    });
                }
            }
            catch (Exception ex)
            {
                // Log but continue - don't fail the entire request for STT errors
                context.TryEmit(new AudioPipelineMetricsEvent("error", "stt_error", 1, "count")
                {
                    Priority = EventPriority.Background
                });

                System.Diagnostics.Debug.WriteLine($"STT error: {ex.Message}");
            }
        }

        var sttDuration = DateTime.UtcNow - sttStartTime;
        _turnMetrics.SttDuration = sttDuration;

        if (transcriptions.Count > 0)
        {
            var fullTranscription = string.Join(" ", transcriptions);

            // Emit completion event
            context.TryEmit(new TranscriptionCompletedEvent(transcriptionId, fullTranscription, sttDuration)
            {
                Priority = EventPriority.Normal
            });

            // Update speed estimate based on transcription
            var wordCount = fullTranscription.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (sttDuration.TotalMinutes > 0 && wordCount > 0)
            {
                var wpm = (float)(wordCount / sttDuration.TotalMinutes);
                UpdateSpeedEstimate(wpm, wordCount);
                _turnMetrics.UserWpm = CurrentWpm;
            }

            // Replace audio content with text in the message
            var newContents = new List<AIContent>();
            foreach (var content in lastMessage.Contents!)
            {
                if (content is DataContent dc && dc.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Skip audio content - we'll add transcription instead
                    continue;
                }
                newContents.Add(content);
            }

            // Add transcription as text content
            newContents.Insert(0, new TextContent(fullTranscription));

            // Create new message with updated contents
            var newMessage = new ChatMessage(lastMessage.Role, newContents)
            {
                AuthorName = lastMessage.AuthorName,
                RawRepresentation = lastMessage.RawRepresentation
            };

            // Replace the message in the list
            context.Messages[^1] = newMessage;
        }
    }

    private async Task<string?> TranscribeAudioAsync(DataContent audioContent, CancellationToken cancellationToken)
    {
        if (SpeechToTextClient == null)
            return null;

        // Convert DataContent to the format expected by ISpeechToTextClient
        // ISpeechToTextClient.GetTextAsync expects a stream or audio data
        var audioData = audioContent.Data;
        if (audioData.IsEmpty)
            return null;

        using var stream = new MemoryStream(audioData.ToArray());

        var result = await SpeechToTextClient.GetTextAsync(stream, cancellationToken: cancellationToken);

        return result.Text;
    }

    /// <summary>
    /// Intercepts LLM streaming to enable Quick Answer (TTS on first sentence).
    /// </summary>
    /// <remarks>
    /// V2 TODO: This hook (ExecuteLLMCallAsync) doesn't exist in V2 middleware yet.
    /// Streaming interception needs to be reimplemented when V2 adds streaming hooks.
    /// For now, this method signature is updated but won't be called.
    /// </remarks>
    public async IAsyncEnumerable<ChatResponseUpdate> ExecuteLLMCallAsync(
        BeforeIterationContext context,
        Func<IAsyncEnumerable<ChatResponseUpdate>> next,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Resolve audio options from AgentRunOptions
        var audioOptions = ResolveAudioOptions(context);

        // Check if audio is disabled for this request
        if (audioOptions?.Disabled == true || TextToSpeechClient == null || !HasAudioOutput)
        {
            // Pass through if no TTS configured or disabled
            await foreach (var update in next().WithCancellation(ct))
                yield return update;
            yield break;
        }

        // Create stream handle for interruption support (from Priority Streaming)
        var streams = context.Streams;
        var stream = streams?.Create();
        var sentenceBuffer = new StringBuilder();
        var synthesisId = Guid.NewGuid().ToString("N")[..8];
        var synthesisState = new SynthesisState();

        // Resolve TTS settings (per-request overrides > middleware defaults)
        var voice = audioOptions?.Voice ?? DefaultVoice;
        var model = audioOptions?.Model ?? DefaultModel;
        var speed = audioOptions?.Speed;

        // Initialize metrics if not already set by BeforeIterationAsync
        _turnMetrics ??= new TurnMetrics { TurnStartTime = DateTime.UtcNow };
        var ttsStartTime = DateTime.UtcNow;
        DateTime? firstAudioTime = null;

        try
        {
            context.TryEmit(new SynthesisStartedEvent(synthesisId, model, voice)
            {
                Priority = EventPriority.Normal,
                StreamId = stream?.StreamId
            });

            await foreach (var update in next().WithCancellation(ct))
            {
                // Extract text from update
                var text = ExtractText(update);
                if (text != null && EnableQuickAnswer)
                {
                    sentenceBuffer.Append(text);

                    // Quick Answer: synthesize on sentence boundary
                    if (IsSentenceBoundary(sentenceBuffer.ToString()))
                    {
                        var textToSynthesize = FilterTextForTts(sentenceBuffer.ToString());
                        if (!string.IsNullOrWhiteSpace(textToSynthesize))
                        {
                            await foreach (var chunk in SynthesizeAndEmitAsync(
                                context, textToSynthesize, stream, synthesisId, voice, model, speed, synthesisState, ct))
                            {
                                // Track time to first audio
                                firstAudioTime ??= DateTime.UtcNow;
                            }
                        }
                        sentenceBuffer.Clear();
                    }
                }

                yield return update;
            }

            // Flush remaining text
            if (sentenceBuffer.Length > 0)
            {
                var textToSynthesize = FilterTextForTts(sentenceBuffer.ToString());
                if (!string.IsNullOrWhiteSpace(textToSynthesize))
                {
                    await foreach (var chunk in SynthesizeAndEmitAsync(
                        context, textToSynthesize, stream, synthesisId, voice, model, speed, synthesisState, ct))
                    {
                        // Track time to first audio
                        firstAudioTime ??= DateTime.UtcNow;
                    }
                }
            }
        }
        finally
        {
            var wasInterrupted = stream?.IsInterrupted ?? false;

            context.TryEmit(new SynthesisCompletedEvent(synthesisId, wasInterrupted, synthesisState.ChunkIndex, synthesisState.ChunkIndex)
            {
                Priority = EventPriority.Control,
                StreamId = stream?.StreamId,
                CanInterrupt = false
            });
            stream?.Complete();

            // Update and emit metrics
            var ttsEndTime = DateTime.UtcNow;
            _turnMetrics.TtsDuration = ttsEndTime - ttsStartTime;
            _turnMetrics.WasInterrupted = wasInterrupted;
            _turnMetrics.TotalChunks = synthesisState.ChunkIndex;
            _turnMetrics.DeliveredChunks = synthesisState.ChunkIndex;

            if (firstAudioTime.HasValue)
            {
                _turnMetrics.TimeToFirstAudio = firstAudioTime.Value - _turnMetrics.TurnStartTime;
            }

            EmitTurnMetrics(context);
        }
    }

    /// <summary>
    /// Emits metrics for the completed audio turn.
    /// </summary>
    private void EmitTurnMetrics(HookContext context)
    {
        if (_turnMetrics == null)
            return;

        var totalLatency = DateTime.UtcNow - _turnMetrics.TurnStartTime;

        // Emit individual metrics
        if (_turnMetrics.SttDuration.HasValue)
        {
            context.TryEmit(new AudioPipelineMetricsEvent("latency", "stt_duration", _turnMetrics.SttDuration.Value.TotalMilliseconds, "ms")
            {
                Priority = EventPriority.Background
            });
        }

        if (_turnMetrics.TtsDuration.HasValue)
        {
            context.TryEmit(new AudioPipelineMetricsEvent("latency", "tts_duration", _turnMetrics.TtsDuration.Value.TotalMilliseconds, "ms")
            {
                Priority = EventPriority.Background
            });
        }

        if (_turnMetrics.TimeToFirstAudio.HasValue)
        {
            context.TryEmit(new AudioPipelineMetricsEvent("latency", "time_to_first_audio", _turnMetrics.TimeToFirstAudio.Value.TotalMilliseconds, "ms")
            {
                Priority = EventPriority.Background
            });
        }

        context.TryEmit(new AudioPipelineMetricsEvent("latency", "total_latency", totalLatency.TotalMilliseconds, "ms")
        {
            Priority = EventPriority.Background
        });

        if (_turnMetrics.UserWpm.HasValue)
        {
            context.TryEmit(new AudioPipelineMetricsEvent("quality", "user_wpm", _turnMetrics.UserWpm.Value, "wpm")
            {
                Priority = EventPriority.Background
            });
        }

        if (_turnMetrics.WasInterrupted)
        {
            context.TryEmit(new AudioPipelineMetricsEvent("quality", "was_interrupted", 1, "bool")
            {
                Priority = EventPriority.Background
            });
        }

        context.TryEmit(new AudioPipelineMetricsEvent("throughput", "total_chunks", _turnMetrics.TotalChunks, "chunks")
        {
            Priority = EventPriority.Background
        });

        // Reset metrics for next turn
        _turnMetrics = null;
    }

    //
    // VAD INTERRUPT HANDLING (uses core IStreamRegistry)
    //

    internal void OnVadStartOfSpeech(HookContext context, string? transcribedText)
    {
        // Check backchannel strategy before interrupting
        if (BackchannelStrategy == BackchannelStrategy.IgnoreShortUtterances)
        {
            var wordCount = transcribedText?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
            if (wordCount < MinWordsForInterruption)
                return; // Don't interrupt for short utterances
        }

        if (BackchannelStrategy == BackchannelStrategy.IgnoreKnownBackchannels)
        {
            if (IsKnownBackchannel(transcribedText))
                return; // Don't interrupt for "uh-huh", etc.
        }

        // Interrupt all active audio streams (uses core IStreamRegistry)
        context.Streams?.InterruptAll();

        context.Emit(new UserInterruptedEvent(transcribedText));
    }

    //
    // SPEED ADAPTATION (built into middleware)
    //

    internal void UpdateSpeedEstimate(float? wordsPerMinute, int wordCount)
    {
        if (!EnableSpeedAdaptation || !wordsPerMinute.HasValue || wordCount == 0)
            return;

        // Weighted learning rate based on utterance length
        var weight = Math.Min(1f, 0.1f * (wordCount + 3f) / 8f);
        _currentWpm = _currentWpm * (1 - weight) + wordsPerMinute.Value * weight;
    }

    internal float AdjustEndpointingDelay(float baseDelay)
    {
        if (!EnableSpeedAdaptation) return baseDelay;

        // Fast speakers get shorter delays
        var speedCoefficient = _currentWpm / 150f;
        return baseDelay / speedCoefficient;
    }

    //
    // TURN DETECTION (hybrid strategy)
    //

    internal float CalculateEndpointingDelay(string text, float silenceDuration)
    {
        // Fast path: long silence = definitely done (no ML needed)
        if (SilenceStrategy == TurnDetectionStrategy.FastPath &&
            silenceDuration >= SilenceFastPathThreshold)
        {
            return 0; // Respond immediately
        }

        // ML path: get completion probability
        float probability = 0.5f;
        if (TurnDetector != null && MlStrategy != TurnDetectionStrategy.Disabled)
        {
            probability = TurnDetector.GetCompletionProbability(text);
        }

        // Interpolate delay based on probability
        var baseDelay = probability > 0.7f
            ? MinEndpointingDelay
            : MaxEndpointingDelay - (probability * (MaxEndpointingDelay - MinEndpointingDelay));

        return AdjustEndpointingDelay(baseDelay);
    }

    //
    // TEXT FILTERING
    //

    internal string FilterTextForTts(string text)
    {
        if (!EnableTextFiltering)
            return text;

        var filtered = text;

        // Remove code blocks
        if (FilterCodeBlocks)
        {
            filtered = CodeBlockRegex().Replace(filtered, " [code omitted] ");
        }

        // Remove tables
        if (FilterTables)
        {
            filtered = TableRegex().Replace(filtered, " [table omitted] ");
        }

        // Remove URLs (keep domain for context)
        if (FilterUrls)
        {
            filtered = UrlRegex().Replace(filtered, m =>
            {
                try
                {
                    var uri = new Uri(m.Value);
                    return $" {uri.Host} ";
                }
                catch
                {
                    return " [link] ";
                }
            });
        }

        // Remove markdown formatting
        if (FilterMarkdownFormatting)
        {
            // Bold
            filtered = BoldRegex().Replace(filtered, "$1");
            // Italic
            filtered = ItalicRegex().Replace(filtered, "$1");
            // Strikethrough
            filtered = StrikethroughRegex().Replace(filtered, "$1");
            // Inline code
            filtered = InlineCodeRegex().Replace(filtered, "$1");
            // Headers
            filtered = HeaderRegex().Replace(filtered, "$1");
        }

        // Remove emoji
        if (FilterEmoji)
        {
            filtered = EmojiRegex().Replace(filtered, "");
        }

        // Clean up multiple spaces
        filtered = MultipleSpacesRegex().Replace(filtered, " ").Trim();

        return filtered;
    }

    //
    // HELPERS
    //

    /// <summary>
    /// State for tracking synthesis progress across async enumeration.
    /// </summary>
    private sealed class SynthesisState
    {
        public int ChunkIndex { get; set; }
    }

    private async IAsyncEnumerable<AudioChunkEvent> SynthesizeAndEmitAsync(
        HookContext context,
        string text,
        IStreamHandle? stream,
        string synthesisId,
        string? voice,
        string? model,
        float? speed,
        SynthesisState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (stream?.IsInterrupted == true) yield break;

        var options = new TextToSpeechOptions
        {
            Voice = voice,
            ModelId = model,
            Speed = speed,
            OutputFormat = DefaultOutputFormat,
            SampleRate = DefaultSampleRate
        };

        await foreach (var chunk in TextToSpeechClient!.GetStreamingSpeechAsync(
            ToAsyncEnumerable(text), options, ct))
        {
            if (stream?.IsInterrupted == true) break;

            var audioData = chunk.Audio?.Data ?? ReadOnlyMemory<byte>.Empty;
            var audioChunk = new AudioChunkEvent(
                synthesisId,
                Convert.ToBase64String(audioData.ToArray()),
                "audio/mpeg",
                state.ChunkIndex++,
                chunk.Duration ?? TimeSpan.Zero,
                chunk.IsLast)
            {
                Priority = EventPriority.Normal,
                StreamId = stream?.StreamId,
                CanInterrupt = true
            };

            context.Emit(audioChunk);
            yield return audioChunk;
        }
    }

    private AudioRunOptions? ResolveAudioOptions(HookContext context)
    {
        // V2: AudioRunOptions should be accessed from BeforeMessageTurnContext.RunOptions.Audio
        // For now, AudioPipelineMiddleware doesn't have a way to access RunOptions from iteration context
        // This is a known limitation - audio options should be configured on the middleware instance itself
        // or passed through middleware state if dynamic per-turn configuration is needed.

        // TODO: Add AudioRunOptions to middleware state if dynamic per-turn configuration is needed
        return null;
    }

    private static bool IsSentenceBoundary(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?');
    }

    private static bool IsKnownBackchannel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var cleaned = text.Trim().ToLowerInvariant();
        return BackchannelPatterns.Any(p => p.IsMatch(cleaned));
    }

    private static readonly Regex[] BackchannelPatterns =
    [
        BackchannelMhmRegex(),
        BackchannelFillerRegex(),
        BackchannelAffirmativeRegex(),
    ];

    private static string? ExtractText(ChatResponseUpdate update)
    {
        // Extract text content from ChatResponseUpdate
        return update.Contents?.OfType<TextContent>().FirstOrDefault()?.Text;
    }

    private static async IAsyncEnumerable<string> ToAsyncEnumerable(string text)
    {
        yield return text;
        await Task.CompletedTask;
    }

    //
    // COMPILED REGEX PATTERNS
    //

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Compiled)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"\|[^\n]+\|(\n\|[^\n]+\|)+", RegexOptions.Compiled)]
    private static partial Regex TableRegex();

    [GeneratedRegex(@"https?://[^\s]+", RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\*\*([^*]+)\*\*", RegexOptions.Compiled)]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"\*([^*]+)\*", RegexOptions.Compiled)]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"~~([^~]+)~~", RegexOptions.Compiled)]
    private static partial Regex StrikethroughRegex();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.Compiled)]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"^#{1,6}\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"[\p{So}\p{Cs}]+", RegexOptions.Compiled)]
    private static partial Regex EmojiRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"^m+-?hm+$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BackchannelMhmRegex();

    [GeneratedRegex(@"^(um+|uh+|oh+|ah+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BackchannelFillerRegex();

    [GeneratedRegex(@"^(yes|sure|right|really|okay|yeah+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BackchannelAffirmativeRegex();

    //
    // INTERNAL TYPES
    //

    /// <summary>
    /// Tracks metrics for a single audio turn.
    /// </summary>
    private sealed class TurnMetrics
    {
        public DateTime TurnStartTime { get; set; }
        public TimeSpan? SttDuration { get; set; }
        public TimeSpan? TtsDuration { get; set; }
        public TimeSpan? TimeToFirstAudio { get; set; }
        public float? UserWpm { get; set; }
        public bool WasInterrupted { get; set; }
        public int TotalChunks { get; set; }
        public int DeliveredChunks { get; set; }
    }
}
