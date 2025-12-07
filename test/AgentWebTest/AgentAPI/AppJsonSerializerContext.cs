using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
[JsonSerializable(typeof(ConversationDto))]
[JsonSerializable(typeof(ConversationDto[]))]
[JsonSerializable(typeof(List<ConversationDto>))]
[JsonSerializable(typeof(StreamRequest))]
[JsonSerializable(typeof(StreamMessage))]
[JsonSerializable(typeof(StreamMessage[]))]
[JsonSerializable(typeof(PermissionResponseRequest))]
[JsonSerializable(typeof(SuccessResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(FrontendToolResponseRequest))]
[JsonSerializable(typeof(FrontendToolContentDto))]
[JsonSerializable(typeof(FrontendToolContentDto[]))]
[JsonSerializable(typeof(MessageDto))]
[JsonSerializable(typeof(List<MessageDto>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
