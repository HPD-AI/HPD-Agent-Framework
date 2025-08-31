using System.Text.Json.Serialization;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

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

// Streaming response types for AOT compatibility
[JsonSerializable(typeof(StreamContentResponse))]
[JsonSerializable(typeof(StreamFinishResponse))]
[JsonSerializable(typeof(StreamErrorResponse))]
[JsonSerializable(typeof(ContentEvent))]
[JsonSerializable(typeof(FinishEvent))]

// Microsoft.Extensions.AI types
[JsonSerializable(typeof(ChatResponseUpdate))]
[JsonSerializable(typeof(TextContent))]
[JsonSerializable(typeof(ChatFinishReason))]

// AG-UI BaseEvent types for native streaming (all event classes)
[JsonSerializable(typeof(BaseEvent))]
[JsonSerializable(typeof(RunStartedEvent))]
[JsonSerializable(typeof(RunFinishedEvent))]
[JsonSerializable(typeof(RunErrorEvent))]
[JsonSerializable(typeof(StepStartedEvent))]
[JsonSerializable(typeof(StepFinishedEvent))]
[JsonSerializable(typeof(TextMessageStartEvent))]
[JsonSerializable(typeof(TextMessageContentEvent))]
[JsonSerializable(typeof(TextMessageEndEvent))]
[JsonSerializable(typeof(ToolCallStartEvent))]
[JsonSerializable(typeof(ToolCallArgsEvent))]
[JsonSerializable(typeof(ToolCallEndEvent))]
[JsonSerializable(typeof(StateSnapshotEvent))]
[JsonSerializable(typeof(StateDeltaEvent))]
[JsonSerializable(typeof(CustomEvent))]
[JsonSerializable(typeof(RawEvent))]
[JsonSerializable(typeof(MessagesSnapshotEvent))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}

// Streaming response types for AOT compatibility
public record StreamContentResponse(string content);
public record StreamFinishResponse(bool finished, string reason);
public record StreamErrorResponse(string error);

// Event types for frontend compatibility
public record ContentEvent(string type, string content);
public record FinishEvent(string type, string reason);