using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
[JsonSerializable(typeof(ConversationDto))]
[JsonSerializable(typeof(StreamRequest))]
[JsonSerializable(typeof(StreamMessage))]
[JsonSerializable(typeof(StreamMessage[]))]
[JsonSerializable(typeof(PermissionResponseRequest))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
