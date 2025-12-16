// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent.Audio;
using Xunit;

namespace HPD.Agent.Tests.Audio;

/// <summary>
/// Unit tests for AudioPipelineMiddleware configuration and helpers.
/// </summary>
public class AudioPipelineMiddlewareTests
{
    [Fact]
    public void DefaultConfiguration_HasCorrectDefaults()
    {
        // Act
        var middleware = new AudioPipelineMiddleware();

        // Assert - Processing Mode
        Assert.Equal(AudioProcessingMode.Pipeline, middleware.ProcessingMode);
        Assert.Equal(AudioIOMode.AudioToAudioAndText, middleware.IOMode);

        // Assert - VAD defaults
        Assert.Equal(0.05f, middleware.VadMinSpeechDuration);
        Assert.Equal(0.55f, middleware.VadMinSilenceDuration);
        Assert.Equal(0.5f, middleware.VadPrefixPaddingDuration);
        Assert.Equal(0.5f, middleware.VadActivationThreshold);

        // Assert - Turn detection
        Assert.Equal(TurnDetectionStrategy.FastPath, middleware.SilenceStrategy);
        Assert.Equal(TurnDetectionStrategy.OnAmbiguous, middleware.MlStrategy);
        Assert.Equal(1.5f, middleware.SilenceFastPathThreshold);
        Assert.Equal(0.3f, middleware.MinEndpointingDelay);
        Assert.Equal(1.5f, middleware.MaxEndpointingDelay);

        // Assert - Features
        Assert.True(middleware.EnableQuickAnswer);
        Assert.True(middleware.EnableSpeedAdaptation);
        Assert.True(middleware.EnableTextFiltering);

        // Assert - Backchannel
        Assert.Equal(BackchannelStrategy.IgnoreShortUtterances, middleware.BackchannelStrategy);
        Assert.Equal(2, middleware.MinWordsForInterruption);

        // Assert - Filler
        Assert.False(middleware.EnableFillerAudio);
        Assert.Equal(1.5f, middleware.FillerSilenceThreshold);

        // Assert - Text filtering
        Assert.True(middleware.FilterCodeBlocks);
        Assert.True(middleware.FilterTables);
        Assert.True(middleware.FilterUrls);
        Assert.True(middleware.FilterMarkdownFormatting);
        Assert.True(middleware.FilterEmoji);

        // Assert - False interruption
        Assert.True(middleware.EnableFalseInterruptionRecovery);
        Assert.Equal(2.0f, middleware.FalseInterruptionTimeout);
        Assert.True(middleware.ResumeFalseInterruption);

        // Assert - Preemptive generation
        Assert.False(middleware.EnablePreemptiveGeneration);
        Assert.Equal(0.7f, middleware.PreemptiveGenerationThreshold);
    }

    [Fact]
    public void HasAudioInput_CorrectForEachIOMode()
    {
        var middleware = new AudioPipelineMiddleware();

        // AudioToText - has audio input
        middleware.IOMode = AudioIOMode.AudioToText;
        Assert.True(middleware.HasAudioInput);

        // TextToAudio - no audio input
        middleware.IOMode = AudioIOMode.TextToAudio;
        Assert.False(middleware.HasAudioInput);

        // AudioToAudio - has audio input
        middleware.IOMode = AudioIOMode.AudioToAudio;
        Assert.True(middleware.HasAudioInput);

        // AudioToAudioAndText - has audio input
        middleware.IOMode = AudioIOMode.AudioToAudioAndText;
        Assert.True(middleware.HasAudioInput);

        // TextToAudioAndText - no audio input
        middleware.IOMode = AudioIOMode.TextToAudioAndText;
        Assert.False(middleware.HasAudioInput);
    }

    [Fact]
    public void HasAudioOutput_CorrectForEachIOMode()
    {
        var middleware = new AudioPipelineMiddleware();

        // AudioToText - no audio output
        middleware.IOMode = AudioIOMode.AudioToText;
        Assert.False(middleware.HasAudioOutput);

        // TextToAudio - has audio output
        middleware.IOMode = AudioIOMode.TextToAudio;
        Assert.True(middleware.HasAudioOutput);

        // AudioToAudio - has audio output
        middleware.IOMode = AudioIOMode.AudioToAudio;
        Assert.True(middleware.HasAudioOutput);

        // AudioToAudioAndText - has audio output
        middleware.IOMode = AudioIOMode.AudioToAudioAndText;
        Assert.True(middleware.HasAudioOutput);

        // TextToAudioAndText - has audio output
        middleware.IOMode = AudioIOMode.TextToAudioAndText;
        Assert.True(middleware.HasAudioOutput);
    }

    [Fact]
    public void HasTextOutput_CorrectForEachIOMode()
    {
        var middleware = new AudioPipelineMiddleware();

        // AudioToText - has text output
        middleware.IOMode = AudioIOMode.AudioToText;
        Assert.True(middleware.HasTextOutput);

        // TextToAudio - no text output
        middleware.IOMode = AudioIOMode.TextToAudio;
        Assert.False(middleware.HasTextOutput);

        // AudioToAudio - no text output
        middleware.IOMode = AudioIOMode.AudioToAudio;
        Assert.False(middleware.HasTextOutput);

        // AudioToAudioAndText - has text output
        middleware.IOMode = AudioIOMode.AudioToAudioAndText;
        Assert.True(middleware.HasTextOutput);

        // TextToAudioAndText - has text output
        middleware.IOMode = AudioIOMode.TextToAudioAndText;
        Assert.True(middleware.HasTextOutput);
    }

    [Fact]
    public void CurrentWpm_DefaultIs150()
    {
        var middleware = new AudioPipelineMiddleware();
        Assert.Equal(150f, middleware.CurrentWpm);
    }

    [Fact]
    public void CanSetAllProviders()
    {
        // Arrange
        var middleware = new AudioPipelineMiddleware();
        var ttsClient = new FakeTextToSpeechClient();
        var turnDetector = new HeuristicTurnDetector();

        // Act
        middleware.TextToSpeechClient = ttsClient;
        middleware.TurnDetector = turnDetector;

        // Assert
        Assert.Same(ttsClient, middleware.TextToSpeechClient);
        Assert.Same(turnDetector, middleware.TurnDetector);
    }

    [Fact]
    public void CanSetTTSDefaults()
    {
        // Arrange
        var middleware = new AudioPipelineMiddleware
        {
            DefaultVoice = "alloy",
            DefaultModel = "tts-1-hd",
            DefaultOutputFormat = "opus",
            DefaultSampleRate = 48000
        };

        // Assert
        Assert.Equal("alloy", middleware.DefaultVoice);
        Assert.Equal("tts-1-hd", middleware.DefaultModel);
        Assert.Equal("opus", middleware.DefaultOutputFormat);
        Assert.Equal(48000, middleware.DefaultSampleRate);
    }
}
