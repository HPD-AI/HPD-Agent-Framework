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
// NOTE: Using concrete record instead of abstract for simpler JSON deserialization
// The 'role' property indicates the message type without needing polymorphic deserialization
public record BaseMessage
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }
}

// Keep derived types for compatibility, but they're essentially aliases now
public sealed record UserMessage : BaseMessage;
public sealed record AssistantMessage : BaseMessage;
public sealed record SystemMessage : BaseMessage;
public sealed record DeveloperMessage : BaseMessage;
public sealed record ToolMessage : BaseMessage;

// Event Types (with JsonSourceGenerator support)
// NOTE: JsonDerivedType is REQUIRED for proper serialization of derived type properties
// We use UnknownDerivedTypeHandling.FallBackToBaseType to avoid $type in output
[JsonDerivedType(typeof(RunStartedEvent), "RUN_STARTED")]
[JsonDerivedType(typeof(RunFinishedEvent), "RUN_FINISHED")]
[JsonDerivedType(typeof(RunErrorEvent), "RUN_ERROR")]
[JsonDerivedType(typeof(StepStartedEvent), "STEP_STARTED")]
[JsonDerivedType(typeof(StepFinishedEvent), "STEP_FINISHED")]
[JsonDerivedType(typeof(TextMessageStartEvent), "TEXT_MESSAGE_START")]
[JsonDerivedType(typeof(TextMessageContentEvent), "TEXT_MESSAGE_CONTENT")]
[JsonDerivedType(typeof(TextMessageEndEvent), "TEXT_MESSAGE_END")]
[JsonDerivedType(typeof(TextMessageChunkEvent), "TEXT_MESSAGE_CHUNK")]
[JsonDerivedType(typeof(ReasoningStartEvent), "REASONING_START")]
[JsonDerivedType(typeof(ReasoningEndEvent), "REASONING_END")]
[JsonDerivedType(typeof(ReasoningMessageStartEvent), "REASONING_MESSAGE_START")]
[JsonDerivedType(typeof(ReasoningMessageContentEvent), "REASONING_MESSAGE_CONTENT")]
[JsonDerivedType(typeof(ReasoningMessageEndEvent), "REASONING_MESSAGE_END")]
[JsonDerivedType(typeof(ToolCallStartEvent), "TOOL_CALL_START")]
[JsonDerivedType(typeof(ToolCallArgsEvent), "TOOL_CALL_ARGS")]
[JsonDerivedType(typeof(ToolCallEndEvent), "TOOL_CALL_END")]
[JsonDerivedType(typeof(ToolCallChunkEvent), "TOOL_CALL_CHUNK")]
[JsonDerivedType(typeof(ToolCallResultEvent), "TOOL_CALL_RESULT")]
[JsonDerivedType(typeof(StateSnapshotEvent), "STATE_SNAPSHOT")]
[JsonDerivedType(typeof(StateDeltaEvent), "STATE_DELTA")]
[JsonDerivedType(typeof(FunctionPermissionRequestEvent), "function")]
[JsonDerivedType(typeof(ContinuationPermissionRequestEvent), "continuation")]
[JsonDerivedType(typeof(CustomEvent), "CUSTOM")]
[JsonDerivedType(typeof(RawEvent), "RAW")]
[JsonDerivedType(typeof(MessagesSnapshotEvent), "MESSAGES_SNAPSHOT")]
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

// Official AG-UI Reasoning Events (Replacement for deprecated THINKING events)
public sealed record ReasoningStartEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("encryptedContent")]
    public string? EncryptedContent { get; init; }
}

public sealed record ReasoningEndEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }
}

public sealed record ReasoningMessageStartEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }
}

public sealed record ReasoningMessageContentEvent : BaseEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("delta")]
    public required string Delta { get; init; }
}

public sealed record ReasoningMessageEndEvent : BaseEvent
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

// Permission Events defined in /Permissions/PermissionEvents.cs
// (FunctionPermissionRequestEvent, ContinuationPermissionRequestEvent)

// AOT-compatible interface
public interface IAGUIAgent
{
    Task RunAsync(RunAgentInput input, ChannelWriter<BaseEvent> events, CancellationToken cancellationToken = default);
}
