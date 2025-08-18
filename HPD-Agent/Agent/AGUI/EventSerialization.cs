using System.Text.Json;

/// <summary>
/// Internal utility for AG-UI event serialization and creation.
/// This avoids circular dependencies between Agent and AGUIEventConverter.
/// </summary>
internal static class EventSerialization
{
    private static long GetTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Serializes an AG-UI event with proper polymorphic serialization.
    /// Uses type switching to ensure all derived properties are included.
    /// </summary>
    /// <param name="evt">The AG-UI event to serialize</param>
    /// <returns>JSON string with all event properties</returns>
    public static string SerializeEvent(BaseEvent evt)
    {
        return evt switch
        {
            TextMessageContentEvent textEvent => JsonSerializer.Serialize(textEvent, AGUIJsonContext.Default.TextMessageContentEvent),
            TextMessageStartEvent startEvent => JsonSerializer.Serialize(startEvent, AGUIJsonContext.Default.TextMessageStartEvent),
            TextMessageEndEvent endEvent => JsonSerializer.Serialize(endEvent, AGUIJsonContext.Default.TextMessageEndEvent),
            ToolCallStartEvent toolStartEvent => JsonSerializer.Serialize(toolStartEvent, AGUIJsonContext.Default.ToolCallStartEvent),
            ToolCallArgsEvent toolArgsEvent => JsonSerializer.Serialize(toolArgsEvent, AGUIJsonContext.Default.ToolCallArgsEvent),
            ToolCallEndEvent toolEndEvent => JsonSerializer.Serialize(toolEndEvent, AGUIJsonContext.Default.ToolCallEndEvent),
            RunStartedEvent runStartEvent => JsonSerializer.Serialize(runStartEvent, AGUIJsonContext.Default.RunStartedEvent),
            RunFinishedEvent runFinishEvent => JsonSerializer.Serialize(runFinishEvent, AGUIJsonContext.Default.RunFinishedEvent),
            RunErrorEvent runErrorEvent => JsonSerializer.Serialize(runErrorEvent, AGUIJsonContext.Default.RunErrorEvent),
            StepStartedEvent stepStartEvent => JsonSerializer.Serialize(stepStartEvent, AGUIJsonContext.Default.StepStartedEvent),
            StepFinishedEvent stepFinishEvent => JsonSerializer.Serialize(stepFinishEvent, AGUIJsonContext.Default.StepFinishedEvent),
            StateDeltaEvent stateDeltaEvent => JsonSerializer.Serialize(stateDeltaEvent, AGUIJsonContext.Default.StateDeltaEvent),
            StateSnapshotEvent stateSnapshotEvent => JsonSerializer.Serialize(stateSnapshotEvent, AGUIJsonContext.Default.StateSnapshotEvent),
            MessagesSnapshotEvent messagesSnapshotEvent => JsonSerializer.Serialize(messagesSnapshotEvent, AGUIJsonContext.Default.MessagesSnapshotEvent),
            CustomEvent customEvent => JsonSerializer.Serialize(customEvent, AGUIJsonContext.Default.CustomEvent),
            RawEvent rawEvent => JsonSerializer.Serialize(rawEvent, AGUIJsonContext.Default.RawEvent),
            _ => JsonSerializer.Serialize(evt, AGUIJsonContext.Default.BaseEvent)
        };
    }

    // Event factory methods for internal use by AGUIEventConverter
    public static TextMessageContentEvent CreateTextMessageContent(string messageId, string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            throw new ArgumentException("Delta content cannot be null or empty", nameof(delta));
        }
        
        return new()
        {
            Type = "text_message_content",
            MessageId = messageId,
            Delta = delta,
            Timestamp = GetTimestamp()
        };
    }

    public static TextMessageStartEvent CreateTextMessageStart(string messageId) => new()
    {
        Type = "text_message_start",
        MessageId = messageId,
        Timestamp = GetTimestamp()
    };

    public static TextMessageEndEvent CreateTextMessageEnd(string messageId) => new()
    {
        Type = "text_message_end",
        MessageId = messageId,
        Timestamp = GetTimestamp()
    };

    public static ToolCallStartEvent CreateToolCallStart(string toolCallId, string toolCallName, string parentMessageId) => new()
    {
        Type = "tool_call_start",
        ToolCallId = toolCallId,
        ToolCallName = toolCallName,
        ParentMessageId = parentMessageId,
        Timestamp = GetTimestamp()
    };

    public static ToolCallArgsEvent CreateToolCallArgs(string toolCallId, string delta) => new()
    {
        Type = "tool_call_args",
        ToolCallId = toolCallId,
        Delta = delta,
        Timestamp = GetTimestamp()
    };

    public static ToolCallEndEvent CreateToolCallEnd(string toolCallId) => new()
    {
        Type = "tool_call_end",
        ToolCallId = toolCallId,
        Timestamp = GetTimestamp()
    };

    public static RunStartedEvent CreateRunStarted(string threadId, string runId) => new()
    {
        Type = "run_started",
        ThreadId = threadId,
        RunId = runId,
        Timestamp = GetTimestamp()
    };

    public static RunFinishedEvent CreateRunFinished(string threadId, string runId) => new()
    {
        Type = "run_finished",
        ThreadId = threadId,
        RunId = runId,
        Timestamp = GetTimestamp()
    };

    public static RunErrorEvent CreateRunError(string message) => new()
    {
        Type = "run_error",
        Message = message,
        Timestamp = GetTimestamp()
    };
}
