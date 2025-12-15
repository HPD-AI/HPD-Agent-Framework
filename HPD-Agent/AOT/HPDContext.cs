using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using HPD.Agent;

/// <summary>
/// JSON serialization context for HPD-Agent core types (AOT-compatible).
/// Does not include FFI-specific types - see HPDFFIJsonContext in HPD-Agent.FFI project.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true, 
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
// --- Framework-specific types ---
[JsonSerializable(typeof(ValidationErrorResponse))]
[JsonSerializable(typeof(ValidationError))]
[JsonSerializable(typeof(List<ValidationError>))]

// --- Common primitive and collection types for AI function return values ---
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(IDictionary<string, object>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(List<double>))]
[JsonSerializable(typeof(List<object>))]

// --- JSON Node types for AOT compatibility ---
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonNode))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonArray))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonValue))]

// --- Agent configuration types ---
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(ProviderConfig))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(McpConfig))]

// --- Conversation and messaging types ---
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatRole))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(ChatOptions))]
[JsonSerializable(typeof(Dictionary<string, object>))]

// --- Extensions.AI types for conversation support ---
[JsonSerializable(typeof(Microsoft.Extensions.AI.ChatMessage))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.ChatRole))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.ChatOptions))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.UsageDetails))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.AdditionalPropertiesDictionary))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.ChatFinishReason))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.TextContent))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.ChatResponseUpdate))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.FunctionCallContent))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.FunctionResultContent))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.AIContent))]
[JsonSerializable(typeof(List<Microsoft.Extensions.AI.ChatMessage>))]
[JsonSerializable(typeof(List<Microsoft.Extensions.AI.AIContent>))]
[JsonSerializable(typeof(IList<Microsoft.Extensions.AI.ChatMessage>))]
[JsonSerializable(typeof(IEnumerable<Microsoft.Extensions.AI.ChatMessage>))]

// --- Conversation storage and serialization types ---
[JsonSerializable(typeof(SessionCheckpoint))]
[JsonSerializable(typeof(HistoryReductionStateData))]
[JsonSerializable(typeof(CachedReduction))]

// --- Checkpointing types ---
[JsonSerializable(typeof(HPD.Agent.Session.PendingWrite))]
[JsonSerializable(typeof(List<HPD.Agent.Session.PendingWrite>))]
[JsonSerializable(typeof(HPD.Agent.Session.CheckpointManifestEntry))]
[JsonSerializable(typeof(List<HPD.Agent.Session.CheckpointManifestEntry>))]

// --- Document store types ---
[JsonSerializable(typeof(HPD.Agent.Skills.DocumentStore.GlobalDocumentInfo))]

// --- Client Tools types ---
[JsonSerializable(typeof(HPD.Agent.ClientTools.ClientToolGroupDefinition))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.ClientToolGroupDefinition[]))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.ClientToolDefinition))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.ClientToolDefinition[]))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.ClientSkillDefinition))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.ClientSkillDefinition[]))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.ClientSkillReference))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.ClientSkillReference[]))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.ClientSkillDocument))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.ClientSkillDocument[]))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.ContextItem))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.ContextItem[]))]
[JsonSerializable(typeof(HPD.Agent.ClientTools.AgentRunInput))]

// --- Internal storage state types (nested classes) ---
// Note: Nested classes need full type paths for AOT
// These are internal implementation details but need serialization support

// --- Additional utility types for generic serialization ---
[JsonSerializable(typeof(object[]))]  // For dynamic object arrays in logging

public partial class HPDJsonContext : JsonSerializerContext
{
}
