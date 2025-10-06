using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

/// <summary>
/// AOT-compatible AGUI types - copied and optimized for Native AOT
/// </summary>

// Input Types
public sealed record RunAgentInput
{
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("state")]
    public required JsonElement State { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<BaseMessage> Messages { get; init; } = [];

    [JsonPropertyName("tools")]
    public required IReadOnlyList<Tool> Tools { get; init; } = [];

    [JsonPropertyName("context")]
    public required IReadOnlyList<Context> Context { get; init; } = [];

    [JsonPropertyName("forwardedProps")]
    public required JsonElement ForwardedProps { get; init; }
}

// Message Types (simplified for AOT)
public abstract record BaseMessage
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    
    [JsonPropertyName("role")]
    public required string Role { get; init; }
    
    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

public sealed record UserMessage : BaseMessage;
public sealed record AssistantMessage : BaseMessage;
public sealed record SystemMessage : BaseMessage;
public sealed record DeveloperMessage : BaseMessage;
public sealed record ToolMessage : BaseMessage;

// Event Types (with JsonSourceGenerator support)
[JsonDerivedType(typeof(RunStartedEvent), "RUN_STARTED")]
[JsonDerivedType(typeof(RunFinishedEvent), "RUN_FINISHED")]
[JsonDerivedType(typeof(RunErrorEvent), "RUN_ERROR")]
[JsonDerivedType(typeof(StepStartedEvent), "STEP_STARTED")]
[JsonDerivedType(typeof(StepFinishedEvent), "STEP_FINISHED")]
[JsonDerivedType(typeof(TextMessageStartEvent), "TEXT_MESSAGE_START")]
[JsonDerivedType(typeof(TextMessageContentEvent), "TEXT_MESSAGE_CONTENT")]
[JsonDerivedType(typeof(TextMessageEndEvent), "TEXT_MESSAGE_END")]
[JsonDerivedType(typeof(TextMessageChunkEvent), "TEXT_MESSAGE_CHUNK")]
[JsonDerivedType(typeof(ThinkingTextMessageStartEvent), "THINKING_TEXT_MESSAGE_START")]
[JsonDerivedType(typeof(ThinkingTextMessageContentEvent), "THINKING_TEXT_MESSAGE_CONTENT")]
[JsonDerivedType(typeof(ThinkingTextMessageEndEvent), "THINKING_TEXT_MESSAGE_END")]
[JsonDerivedType(typeof(ThinkingStartEvent), "THINKING_START")]
[JsonDerivedType(typeof(ThinkingEndEvent), "THINKING_END")]
[JsonDerivedType(typeof(ToolCallStartEvent), "TOOL_CALL_START")]
[JsonDerivedType(typeof(ToolCallArgsEvent), "TOOL_CALL_ARGS")]
[JsonDerivedType(typeof(ToolCallEndEvent), "TOOL_CALL_END")]
[JsonDerivedType(typeof(ToolCallChunkEvent), "TOOL_CALL_CHUNK")]
[JsonDerivedType(typeof(ToolCallResultEvent), "TOOL_CALL_RESULT")]
[JsonDerivedType(typeof(StateSnapshotEvent), "STATE_SNAPSHOT")]
[JsonDerivedType(typeof(StateDeltaEvent), "STATE_DELTA")]
[JsonDerivedType(typeof(CustomEvent), "CUSTOM")]
[JsonDerivedType(typeof(RawEvent), "RAW")]
[JsonDerivedType(typeof(MessagesSnapshotEvent), "MESSAGES_SNAPSHOT")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
public abstract record BaseEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }
}

public sealed record RunStartedEvent : BaseEvent
{
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }
    
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }
}

public sealed record RunFinishedEvent : BaseEvent
{
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }
    
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }
}

public sealed record TextMessageContentEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }
    
    [JsonPropertyName("delta")]
    public required string Delta { get; init; }
}

public sealed record TextMessageStartEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }
}

public sealed record TextMessageEndEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }
}

public sealed record RunErrorEvent : BaseEvent
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed record ToolCallStartEvent : BaseEvent
{
    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; init; }
    
    [JsonPropertyName("toolCallName")]
    public required string ToolCallName { get; init; }
    
    [JsonPropertyName("parentMessageId")]
    public required string ParentMessageId { get; init; }
}

public sealed record ToolCallArgsEvent : BaseEvent
{
    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; init; }
    
    [JsonPropertyName("delta")]
    public required string Delta { get; init; }
}

public sealed record ToolCallEndEvent : BaseEvent
{
    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; init; }
}

public sealed record CustomEvent : BaseEvent
{
    [JsonPropertyName("data")]
    public required JsonElement Data { get; init; }
}

// New AG-UI Events - Text Message Chunk
public sealed record TextMessageChunkEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public string? MessageId { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("delta")]
    public string? Delta { get; init; }
}

// New AG-UI Events - Thinking/Reasoning
public sealed record ThinkingStartEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }
}

public sealed record ThinkingEndEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }
}

public sealed record ThinkingTextMessageStartEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }
}

public sealed record ThinkingTextMessageContentEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("delta")]
    public required string Delta { get; init; }
}

public sealed record ThinkingTextMessageEndEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }
}

// New AG-UI Events - Tool Call Chunk and Result
public sealed record ToolCallChunkEvent : BaseEvent
{
    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("toolCallName")]
    public string? ToolCallName { get; init; }

    [JsonPropertyName("parentMessageId")]
    public string? ParentMessageId { get; init; }

    [JsonPropertyName("delta")]
    public string? Delta { get; init; }
}

public sealed record ToolCallResultEvent : BaseEvent
{
    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }
}

public sealed record MessagesSnapshotEvent : BaseEvent
{
    [JsonPropertyName("messages")]
    public required IReadOnlyList<BaseMessage> Messages { get; init; }
}

public sealed record RawEvent : BaseEvent
{
    [JsonPropertyName("data")]
    public required JsonElement Data { get; init; }
}

public sealed record StateDeltaEvent : BaseEvent
{
    [JsonPropertyName("delta")]
    public required JsonElement Delta { get; init; }
}

public sealed record StateSnapshotEvent : BaseEvent
{
    [JsonPropertyName("state")]
    public required JsonElement State { get; init; }
}

public sealed record StepFinishedEvent : BaseEvent
{
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }
    
    [JsonPropertyName("stepName")]
    public required string StepName { get; init; }
    
    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }
}

public sealed record StepStartedEvent : BaseEvent
{
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }
    
    [JsonPropertyName("stepName")]
    public required string StepName { get; init; }
    
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

// Tool Types
public sealed record Tool
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    
    [JsonPropertyName("description")]
    public required string Description { get; init; }
    
    [JsonPropertyName("parameters")]
    public required JsonElement Parameters { get; init; }
}

public sealed record Context
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("data")]
    public required JsonElement Data { get; init; }
}

// AOT-compatible interface
public interface IAGUIAgent
{
    Task RunAsync(RunAgentInput input, ChannelWriter<BaseEvent> events, CancellationToken cancellationToken = default);
}
