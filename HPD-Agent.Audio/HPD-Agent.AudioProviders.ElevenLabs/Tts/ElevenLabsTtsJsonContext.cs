// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Text.Json.Serialization;

namespace HPD.Agent.AudioProviders.ElevenLabs.Tts;

/// <summary>
/// JSON serialization context for ElevenLabs TTS provider-specific configuration.
/// Used for deserializing TtsConfig.ProviderOptionsJson → ElevenLabsTtsConfig.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ElevenLabsTtsConfig))]
public partial class ElevenLabsTtsJsonContext : JsonSerializerContext
{
}
