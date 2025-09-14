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
[JsonDerivedType(typeof(ToolCallStartEvent), "TOOL_CALL_START")]
[JsonDerivedType(typeof(ToolCallArgsEvent), "TOOL_CALL_ARGS")]
[JsonDerivedType(typeof(ToolCallEndEvent), "TOOL_CALL_END")]
[JsonDerivedType(typeof(StateSnapshotEvent), "STATE_SNAPSHOT")]
[JsonDerivedType(typeof(StateDeltaEvent), "STATE_DELTA")]
[JsonDerivedType(typeof(CustomEvent), "CUSTOM")]
[JsonDerivedType(typeof(RawEvent), "RAW")]
[JsonDerivedType(typeof(MessagesSnapshotEvent), "MESSAGES_SNAPSHOT")]
[JsonDerivedType(typeof(OrchestrationStartEvent), "ORCHESTRATION_START")]
[JsonDerivedType(typeof(OrchestrationDecisionEvent), "ORCHESTRATION_DECISION")]
[JsonDerivedType(typeof(AgentEvaluationEvent), "AGENT_EVALUATION")]
[JsonDerivedType(typeof(AgentHandoffEvent), "AGENT_HANDOFF")]
[JsonDerivedType(typeof(OrchestrationCompleteEvent), "ORCHESTRATION_COMPLETE")]
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

// New orchestration events
public sealed record OrchestrationStartEvent : BaseEvent
{
    [JsonPropertyName("strategyName")]
    public required string StrategyName { get; init; }

    [JsonPropertyName("agentCount")]
    public required int AgentCount { get; init; }

    [JsonPropertyName("agentNames")]
    public IReadOnlyList<string>? AgentNames { get; init; }
}

public sealed record OrchestrationDecisionEvent : BaseEvent
{
    [JsonPropertyName("selectedAgentName")]
    public required string SelectedAgentName { get; init; }

    [JsonPropertyName("strategyName")]
    public required string StrategyName { get; init; }

    [JsonPropertyName("agentScores")]
    public Dictionary<string, float>? AgentScores { get; init; }

    [JsonPropertyName("decisionDurationMs")]
    public long? DecisionDurationMs { get; init; }
}

public sealed record AgentEvaluationEvent : BaseEvent
{
    [JsonPropertyName("agentName")]
    public required string AgentName { get; init; }

    [JsonPropertyName("score")]
    public required float Score { get; init; }

    [JsonPropertyName("capabilities")]
    public Dictionary<string, float>? Capabilities { get; init; }

    [JsonPropertyName("evaluationReason")]
    public string? EvaluationReason { get; init; }
}

public sealed record AgentHandoffEvent : BaseEvent
{
    [JsonPropertyName("fromAgentName")]
    public required string FromAgentName { get; init; }

    [JsonPropertyName("toAgentName")]
    public required string ToAgentName { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("handoffContext")]
    public Dictionary<string, object>? HandoffContext { get; init; }
}

public sealed record OrchestrationCompleteEvent : BaseEvent
{
    [JsonPropertyName("finalAgentName")]
    public required string FinalAgentName { get; init; }

    [JsonPropertyName("totalDurationMs")]
    public required long TotalDurationMs { get; init; }

    [JsonPropertyName("totalInvocations")]
    public required int TotalInvocations { get; init; }
}

// AOT-compatible interface
public interface IAGUIAgent
{
    Task RunAsync(RunAgentInput input, ChannelWriter<BaseEvent> events, CancellationToken cancellationToken = default);
}
