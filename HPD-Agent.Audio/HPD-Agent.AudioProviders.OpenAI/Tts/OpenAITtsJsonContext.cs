// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Text.Json.Serialization;

namespace HPD.Agent.AudioProviders.OpenAI.Tts;

/// <summary>
/// JSON serialization context for OpenAI TTS provider-specific configuration.
/// Used for deserializing TtsConfig.ProviderOptionsJson → OpenAITtsConfig.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenAITtsConfig))]
public partial class OpenAITtsJsonContext : JsonSerializerContext
{
}
