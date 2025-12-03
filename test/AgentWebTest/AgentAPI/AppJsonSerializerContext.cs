using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
// App-specific DTOs only - FrontendTools types come from HPDJsonContext in the library
[JsonSerializable(typeof(ConversationDto))]
[JsonSerializable(typeof(StreamRequest))]
[JsonSerializable(typeof(StreamMessage))]
[JsonSerializable(typeof(StreamMessage[]))]
[JsonSerializable(typeof(PermissionResponseRequest))]
[JsonSerializable(typeof(SuccessResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(FrontendToolResponseRequest))]
[JsonSerializable(typeof(FrontendToolContentDto))]
[JsonSerializable(typeof(FrontendToolContentDto[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
