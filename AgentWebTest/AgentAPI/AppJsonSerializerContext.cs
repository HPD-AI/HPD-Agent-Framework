using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using A2A;

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
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
    /// <summary>
    /// Combined type info resolver that includes App, HPD, AGUI, and partial A2A types.
    /// Note: Some A2A types (AgentCard, AgentCapabilities, AgentTask, Message, Artifact) 
    /// cannot use source generation due to AgentTransport dependencies and will fall back to runtime serialization.
    /// </summary>
    public static IJsonTypeInfoResolver Combined { get; } = 
        JsonTypeInfoResolver.Combine(
            Default, 
            HPDJsonContext.Default,
            AGUIJsonContext.Default,
            A2AJsonSerializerContext.Default);
}

// Streaming response types for AOT compatibility
public record StreamContentResponse(string content);
public record StreamFinishResponse(bool finished, string reason);
public record StreamErrorResponse(string error);

// Event types for frontend compatibility
public record ContentEvent(string type, string content);
public record FinishEvent(string type, string reason);