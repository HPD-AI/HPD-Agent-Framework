using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
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
[JsonSerializable(typeof(TextMessageChunkEvent))]
[JsonSerializable(typeof(ReasoningStartEvent))]
[JsonSerializable(typeof(ReasoningEndEvent))]
[JsonSerializable(typeof(ReasoningMessageStartEvent))]
[JsonSerializable(typeof(ReasoningMessageContentEvent))]
[JsonSerializable(typeof(ReasoningMessageEndEvent))]
[JsonSerializable(typeof(ToolCallStartEvent))]
[JsonSerializable(typeof(ToolCallArgsEvent))]
[JsonSerializable(typeof(ToolCallEndEvent))]
[JsonSerializable(typeof(ToolCallChunkEvent))]
[JsonSerializable(typeof(ToolCallResultEvent))]
[JsonSerializable(typeof(StepStartedEvent))]
[JsonSerializable(typeof(StepFinishedEvent))]
[JsonSerializable(typeof(StateDeltaEvent))]
[JsonSerializable(typeof(StateSnapshotEvent))]
[JsonSerializable(typeof(MessagesSnapshotEvent))]
[JsonSerializable(typeof(FunctionPermissionRequestEvent))]
[JsonSerializable(typeof(ContinuationPermissionRequestEvent))]
[JsonSerializable(typeof(CustomEvent))]
[JsonSerializable(typeof(RawEvent))]
[JsonSerializable(typeof(Tool))]
[JsonSerializable(typeof(Context))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))] // FIX: Added for AOT compatibility
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, float>))] // FIX: Added for orchestration event scores
[JsonSerializable(typeof(object))] // FIX: Added for AOT compatibility
[JsonSerializable(typeof(string))] // FIX: Added for generic serialization
// FIX: Add all common primitive types that plugins/tools might return
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(decimal))] // FIX: This was the missing one causing the error!
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(List<BaseMessage>))] // FIX: Added for collections
[JsonSerializable(typeof(List<Tool>))] // FIX: Added for collections
[JsonSerializable(typeof(List<Context>))] // FIX: Added for collections
[JsonSerializable(typeof(System.Text.Json.JsonElement))] // FIX: Added for JsonElement serialization
public partial class AGUIJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Combined type info resolver that includes AGUI types, HPD types, and OpenRouter types
    /// This ensures we can serialize ANY type that plugins might return
    /// </summary>
    public static IJsonTypeInfoResolver Combined { get; } = 
        JsonTypeInfoResolver.Combine(Default, HPDJsonContext.Default);
}