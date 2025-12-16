// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent.Audio;
using Xunit;

namespace HPD.Agent.Tests.Audio;

/// <summary>
/// Unit tests for AgentRunOptionsAudioExtensions.
/// </summary>
public class AudioRunOptionsExtensionsTests
{
    [Fact]
    public void WithAudio_SetsAudioOptions()
    {
        // Arrange
        var runOptions = new AgentRunOptions();
        var audioOptions = new AudioRunOptions { Voice = "nova" };

        // Act
        runOptions.WithAudio(audioOptions);

        // Assert
        var retrieved = runOptions.GetAudioOptions();
        Assert.NotNull(retrieved);
        Assert.Equal("nova", retrieved.Voice);
    }

    [Fact]
    public void WithAudio_Action_ConfiguresOptions()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        runOptions.WithAudio(audio =>
        {
            audio.Voice = "alloy";
            audio.Speed = 1.5f;
        });

        // Assert
        var retrieved = runOptions.GetAudioOptions();
        Assert.NotNull(retrieved);
        Assert.Equal("alloy", retrieved.Voice);
        Assert.Equal(1.5f, retrieved.Speed);
    }

    [Fact]
    public void WithVoiceConversation_SetsCorrectIOMode()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        runOptions.WithVoiceConversation();

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.Equal(AudioIOMode.AudioToAudioAndText, audio.IOMode);
    }

    [Fact]
    public void WithVoiceInput_SetsCorrectIOMode()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        runOptions.WithVoiceInput();

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.Equal(AudioIOMode.AudioToText, audio.IOMode);
    }

    [Fact]
    public void WithVoiceOutput_SetsCorrectIOMode()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        runOptions.WithVoiceOutput();

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.Equal(AudioIOMode.TextToAudioAndText, audio.IOMode);
    }

    [Fact]
    public void WithTextOnly_SetsDisabled()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        runOptions.WithTextOnly();

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.True(audio.Disabled);
    }

    [Fact]
    public void WithFullVoice_SetsCorrectIOMode()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        runOptions.WithFullVoice();

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.Equal(AudioIOMode.AudioToAudio, audio.IOMode);
    }

    [Fact]
    public void WithNativeAudio_SetsProcessingModeAndIOMode()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        runOptions.WithNativeAudio();

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.Equal(AudioProcessingMode.Native, audio.ProcessingMode);
        Assert.Equal(AudioIOMode.AudioToAudioAndText, audio.IOMode);
    }

    [Fact]
    public void WithVoice_SetsVoice()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        runOptions.WithVoice("shimmer");

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.Equal("shimmer", audio.Voice);
    }

    [Fact]
    public void WithTtsModel_SetsModel()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        runOptions.WithTtsModel("tts-1-hd");

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.Equal("tts-1-hd", audio.Model);
    }

    [Fact]
    public void WithTtsSpeed_SetsSpeed()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        runOptions.WithTtsSpeed(1.25f);

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.Equal(1.25f, audio.Speed);
    }

    [Fact]
    public void GetAudioOptions_WhenNotSet_ReturnsNull()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        var audio = runOptions.GetAudioOptions();

        // Assert
        Assert.Null(audio);
    }

    [Fact]
    public void FluentChaining_Works()
    {
        // Act
        var runOptions = new AgentRunOptions()
            .WithVoiceConversation()
            .WithVoice("nova")
            .WithTtsModel("tts-1")
            .WithTtsSpeed(1.1f);

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.Equal(AudioIOMode.AudioToAudioAndText, audio.IOMode);
        Assert.Equal("nova", audio.Voice);
        Assert.Equal("tts-1", audio.Model);
        Assert.Equal(1.1f, audio.Speed);
    }

    [Fact]
    public void MultipleWithAudio_LastWins()
    {
        // Arrange
        var runOptions = new AgentRunOptions();

        // Act
        runOptions.WithVoiceConversation();
        runOptions.WithVoiceInput();

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.Equal(AudioIOMode.AudioToText, audio.IOMode);
    }

    [Fact]
    public void WithAudio_Action_PreservesExistingSettings()
    {
        // Arrange
        var runOptions = new AgentRunOptions();
        runOptions.WithVoice("alloy");

        // Act
        runOptions.WithAudio(audio => audio.Speed = 1.5f);

        // Assert
        var audio = runOptions.GetAudioOptions();
        Assert.NotNull(audio);
        Assert.Equal("alloy", audio.Voice);
        Assert.Equal(1.5f, audio.Speed);
    }
}
