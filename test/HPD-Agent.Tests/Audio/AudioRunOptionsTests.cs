// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent.Audio;
using System.Text.Json;
using Xunit;

namespace HPD.Agent.Tests.Audio;

/// <summary>
/// Unit tests for AudioRunOptions.
/// </summary>
public class AudioRunOptionsTests
{
    [Fact]
    public void VoiceConversation_ReturnsCorrectIOMode()
    {
        // Act
        var options = AudioRunOptions.VoiceConversation();

        // Assert
        Assert.Equal(AudioIOMode.AudioToAudioAndText, options.IOMode);
        // Disabled is null by default (use middleware default)
        Assert.Null(options.Disabled);
    }

    [Fact]
    public void VoiceInput_ReturnsCorrectIOMode()
    {
        // Act
        var options = AudioRunOptions.VoiceInput();

        // Assert
        Assert.Equal(AudioIOMode.AudioToText, options.IOMode);
    }

    [Fact]
    public void VoiceOutput_ReturnsCorrectIOMode()
    {
        // Act
        var options = AudioRunOptions.VoiceOutput();

        // Assert
        Assert.Equal(AudioIOMode.TextToAudioAndText, options.IOMode);
    }

    [Fact]
    public void TextOnly_SetsDisabledTrue()
    {
        // Act
        var options = AudioRunOptions.TextOnly();

        // Assert
        Assert.True(options.Disabled);
    }

    [Fact]
    public void NativeWithCaptions_SetsProcessingModeAndIOMode()
    {
        // Act
        var options = AudioRunOptions.NativeWithCaptions();

        // Assert
        Assert.Equal(AudioProcessingMode.Native, options.ProcessingMode);
        Assert.Equal(AudioIOMode.AudioToAudioAndText, options.IOMode);
    }

    [Fact]
    public void FullVoice_ReturnsCorrectIOMode()
    {
        // Act
        var options = AudioRunOptions.FullVoice();

        // Assert
        Assert.Equal(AudioIOMode.AudioToAudio, options.IOMode);
    }

    [Fact]
    public void TextToVoice_ReturnsCorrectIOMode()
    {
        // Act
        var options = AudioRunOptions.TextToVoice();

        // Assert
        Assert.Equal(AudioIOMode.TextToAudio, options.IOMode);
    }

    [Fact]
    public void AllPropertiesCanBeSet()
    {
        // Act
        var options = new AudioRunOptions
        {
            ProcessingMode = AudioProcessingMode.Native,
            IOMode = AudioIOMode.AudioToAudio,
            Voice = "alloy",
            Model = "tts-1-hd",
            Speed = 1.5f,
            Language = "en-US",
            OutputFormat = "opus",
            SampleRate = 48000,
            Disabled = false
        };

        // Assert
        Assert.Equal(AudioProcessingMode.Native, options.ProcessingMode);
        Assert.Equal(AudioIOMode.AudioToAudio, options.IOMode);
        Assert.Equal("alloy", options.Voice);
        Assert.Equal("tts-1-hd", options.Model);
        Assert.Equal(1.5f, options.Speed);
        Assert.Equal("en-US", options.Language);
        Assert.Equal("opus", options.OutputFormat);
        Assert.Equal(48000, options.SampleRate);
        Assert.False(options.Disabled);
    }

    [Fact]
    public void JsonSerialization_RoundTrip()
    {
        // Arrange
        var options = new AudioRunOptions
        {
            ProcessingMode = AudioProcessingMode.Pipeline,
            IOMode = AudioIOMode.AudioToAudioAndText,
            Voice = "nova",
            Model = "tts-1",
            Speed = 1.2f
        };

        // Act
        var json = JsonSerializer.Serialize(options);
        var deserialized = JsonSerializer.Deserialize<AudioRunOptions>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(options.ProcessingMode, deserialized.ProcessingMode);
        Assert.Equal(options.IOMode, deserialized.IOMode);
        Assert.Equal(options.Voice, deserialized.Voice);
        Assert.Equal(options.Model, deserialized.Model);
        Assert.Equal(options.Speed, deserialized.Speed);
    }

    [Fact]
    public void JsonSerialization_UsesCamelCase()
    {
        // Arrange
        var options = new AudioRunOptions
        {
            ProcessingMode = AudioProcessingMode.Pipeline,
            Voice = "nova"
        };

        // Act
        var json = JsonSerializer.Serialize(options);

        // Assert - AudioRunOptions-specific properties use camelCase via [JsonPropertyName]
        Assert.Contains("\"processingMode\"", json);
        Assert.Contains("\"ioMode\"", json);

        // Inherited AudioPipelineConfig properties use PascalCase (no JSON attributes)
        Assert.Contains("\"Voice\"", json);

        // Should not contain wrong casing for AudioRunOptions-specific properties
        Assert.DoesNotContain("\"ProcessingMode\"", json);
        Assert.DoesNotContain("\"IOMode\"", json);
    }
}
