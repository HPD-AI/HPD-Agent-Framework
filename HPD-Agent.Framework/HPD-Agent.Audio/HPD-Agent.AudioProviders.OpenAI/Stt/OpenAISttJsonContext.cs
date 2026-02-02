// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Text.Json.Serialization;

namespace HPD.Agent.AudioProviders.OpenAI.Stt;

/// <summary>
/// JSON serialization context for OpenAI STT provider-specific configuration.
/// Used for deserializing SttConfig.ProviderOptionsJson → OpenAISttConfig.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenAISttConfig))]
public partial class OpenAISttJsonContext : JsonSerializerContext
{
}
