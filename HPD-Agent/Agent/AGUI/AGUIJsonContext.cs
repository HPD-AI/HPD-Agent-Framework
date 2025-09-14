using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(RunAgentInput))]
[JsonSerializable(typeof(BaseMessage))]
[JsonSerializable(typeof(UserMessage))]
[JsonSerializable(typeof(AssistantMessage))]
[JsonSerializable(typeof(SystemMessage))]
[JsonSerializable(typeof(DeveloperMessage))]
[JsonSerializable(typeof(ToolMessage))]
[JsonSerializable(typeof(BaseEvent))]
[JsonSerializable(typeof(RunStartedEvent))]
[JsonSerializable(typeof(RunFinishedEvent))]
[JsonSerializable(typeof(RunErrorEvent))]
[JsonSerializable(typeof(TextMessageStartEvent))]
[JsonSerializable(typeof(TextMessageContentEvent))]
[JsonSerializable(typeof(TextMessageEndEvent))]
[JsonSerializable(typeof(ToolCallStartEvent))]
[JsonSerializable(typeof(ToolCallArgsEvent))]
[JsonSerializable(typeof(ToolCallEndEvent))]
[JsonSerializable(typeof(StepStartedEvent))]
[JsonSerializable(typeof(StepFinishedEvent))]
[JsonSerializable(typeof(StateDeltaEvent))]
[JsonSerializable(typeof(StateSnapshotEvent))]
[JsonSerializable(typeof(MessagesSnapshotEvent))]
[JsonSerializable(typeof(OrchestrationStartEvent))]
[JsonSerializable(typeof(OrchestrationDecisionEvent))]
[JsonSerializable(typeof(AgentEvaluationEvent))]
[JsonSerializable(typeof(AgentHandoffEvent))]
[JsonSerializable(typeof(OrchestrationCompleteEvent))]
[JsonSerializable(typeof(CustomEvent))]
[JsonSerializable(typeof(RawEvent))]
[JsonSerializable(typeof(Tool))]
[JsonSerializable(typeof(Context))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))] // FIX: Added for AOT compatibility
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, float>))] // FIX: Added for orchestration event scores
[JsonSerializable(typeof(object))] // FIX: Added for AOT compatibility
[JsonSerializable(typeof(List<BaseMessage>))] // FIX: Added for collections
[JsonSerializable(typeof(List<Tool>))] // FIX: Added for collections
[JsonSerializable(typeof(List<Context>))] // FIX: Added for collections
[JsonSerializable(typeof(System.Text.Json.JsonElement))] // FIX: Added for JsonElement serialization
public partial class AGUIJsonContext : JsonSerializerContext
{
}