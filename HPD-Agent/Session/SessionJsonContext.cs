using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;


namespace HPD.Agent;

/// <summary>
/// JSON serialization context for Session types (AOT-compatible).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
// Session types
[JsonSerializable(typeof(SessionSnapshot))]
[JsonSerializable(typeof(ExecutionCheckpoint))]
[JsonSerializable(typeof(CheckpointManifestEntry))]
[JsonSerializable(typeof(List<CheckpointManifestEntry>))]
[JsonSerializable(typeof(PendingWrite))]
[JsonSerializable(typeof(List<PendingWrite>))]
[JsonSerializable(typeof(CheckpointMetadata))]
[JsonSerializable(typeof(CheckpointTuple))]

// Legacy types (deprecated, kept for backward compatibility)
#pragma warning disable CS0618 // Suppress obsolete warning
[JsonSerializable(typeof(SessionCheckpoint))]
#pragma warning restore CS0618

// Common types needed for serialization
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(AgentLoopState))]
[JsonSerializable(typeof(JsonElement))]

// Tool validation types
[JsonSerializable(typeof(ValidationErrorResponse))]

public partial class SessionJsonContext : JsonSerializerContext
{
}
