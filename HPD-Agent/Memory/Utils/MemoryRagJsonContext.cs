using System.Collections.Generic;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(
    typeof(Dictionary<string, object>),
    TypeInfoPropertyName = nameof(DictionaryStringObject))]
[JsonSerializable(
    typeof(OpenRouterResponse),
    TypeInfoPropertyName = nameof(OpenRouterResponse))]
[JsonSerializable(
    typeof(VoyageAIResponse),
    TypeInfoPropertyName = nameof(VoyageAIResponse))]
public partial class MemoryRagJsonContext : JsonSerializerContext
{
}

