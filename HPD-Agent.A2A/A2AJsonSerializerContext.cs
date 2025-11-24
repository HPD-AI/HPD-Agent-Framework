using System.Text.Json.Serialization;
using System.Collections.Generic;
using A2A;

/// <summary>
/// JSON serialization context for A2A protocol types.
/// Handles Agent-to-Agent communication protocol serialization.
/// Only includes types that work with source generation.
/// Note: Some A2A types like AgentTransport cannot be serialized with source generation.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
// A2A types that work with source generation (excluding ones with AgentTransport dependencies)
[JsonSerializable(typeof(AgentSkill))]
[JsonSerializable(typeof(List<AgentSkill>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
public partial class A2AJsonSerializerContext : JsonSerializerContext
{
}
