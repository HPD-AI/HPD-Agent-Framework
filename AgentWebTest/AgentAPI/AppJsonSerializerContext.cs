using System.Text.Json.Serialization;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
// ✨ SIMPLIFIED: Application-specific types only
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(AgentChatResponse))]
[JsonSerializable(typeof(UsageInfo))]
[JsonSerializable(typeof(StreamRequest))]
[JsonSerializable(typeof(StreamMessage[]))]
[JsonSerializable(typeof(List<StreamMessage>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(SttResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ProjectDto))]
[JsonSerializable(typeof(IEnumerable<ProjectDto>))]
[JsonSerializable(typeof(ConversationDto))]
[JsonSerializable(typeof(IEnumerable<ConversationDto>))]
[JsonSerializable(typeof(ConversationWithMessagesDto))]
[JsonSerializable(typeof(ConversationMessageDto))]
[JsonSerializable(typeof(ConversationMessageDto[]))]
[JsonSerializable(typeof(CreateProjectRequest))]
[JsonSerializable(typeof(CreateConversationRequest))]
[JsonSerializable(typeof(ProjectDto[]))]
[JsonSerializable(typeof(ConversationDto[]))]

// 🎯 LIBRARY MAGIC: All AG-UI events handled automatically by StreamAGUIResponseAsync()
// No need to manually list BaseEvent, TextMessageContentEvent, ToolCallStartEvent, etc.
// The library uses its own AGUIJsonContext.Default for AG-UI events internally
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}