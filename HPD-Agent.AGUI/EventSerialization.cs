using System.Text.Json;
/// <summary>
/// Internal utility for AG-UI event serialization and creation.
/// This avoids circular dependencies between Agent and AGUIEventConverter.
/// </summary>
public static class EventSerialization
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
            TextMessageChunkEvent chunkEvent => JsonSerializer.Serialize(chunkEvent, AGUIJsonContext.Default.TextMessageChunkEvent),
            ReasoningStartEvent reasoningStartEvent => JsonSerializer.Serialize(reasoningStartEvent, AGUIJsonContext.Default.ReasoningStartEvent),
            ReasoningEndEvent reasoningEndEvent => JsonSerializer.Serialize(reasoningEndEvent, AGUIJsonContext.Default.ReasoningEndEvent),
            ReasoningMessageStartEvent reasoningMessageStartEvent => JsonSerializer.Serialize(reasoningMessageStartEvent, AGUIJsonContext.Default.ReasoningMessageStartEvent),
            ReasoningMessageContentEvent reasoningMessageContentEvent => JsonSerializer.Serialize(reasoningMessageContentEvent, AGUIJsonContext.Default.ReasoningMessageContentEvent),
            ReasoningMessageEndEvent reasoningMessageEndEvent => JsonSerializer.Serialize(reasoningMessageEndEvent, AGUIJsonContext.Default.ReasoningMessageEndEvent),
            ToolCallStartEvent toolStartEvent => JsonSerializer.Serialize(toolStartEvent, AGUIJsonContext.Default.ToolCallStartEvent),
            ToolCallArgsEvent toolArgsEvent => JsonSerializer.Serialize(toolArgsEvent, AGUIJsonContext.Default.ToolCallArgsEvent),
            ToolCallEndEvent toolEndEvent => JsonSerializer.Serialize(toolEndEvent, AGUIJsonContext.Default.ToolCallEndEvent),
            ToolCallChunkEvent toolChunkEvent => JsonSerializer.Serialize(toolChunkEvent, AGUIJsonContext.Default.ToolCallChunkEvent),
            ToolCallResultEvent toolResultEvent => JsonSerializer.Serialize(toolResultEvent, AGUIJsonContext.Default.ToolCallResultEvent),
            RunStartedEvent runStartEvent => JsonSerializer.Serialize(runStartEvent, AGUIJsonContext.Default.RunStartedEvent),
            RunFinishedEvent runFinishEvent => JsonSerializer.Serialize(runFinishEvent, AGUIJsonContext.Default.RunFinishedEvent),
            RunErrorEvent runErrorEvent => JsonSerializer.Serialize(runErrorEvent, AGUIJsonContext.Default.RunErrorEvent),
            StepStartedEvent stepStartEvent => JsonSerializer.Serialize(stepStartEvent, AGUIJsonContext.Default.StepStartedEvent),
            StepFinishedEvent stepFinishEvent => JsonSerializer.Serialize(stepFinishEvent, AGUIJsonContext.Default.StepFinishedEvent),
            StateDeltaEvent stateDeltaEvent => JsonSerializer.Serialize(stateDeltaEvent, AGUIJsonContext.Default.StateDeltaEvent),
            StateSnapshotEvent stateSnapshotEvent => JsonSerializer.Serialize(stateSnapshotEvent, AGUIJsonContext.Default.StateSnapshotEvent),
            MessagesSnapshotEvent messagesSnapshotEvent => JsonSerializer.Serialize(messagesSnapshotEvent, AGUIJsonContext.Default.MessagesSnapshotEvent),
            FunctionPermissionRequestEvent functionPermissionEvent => JsonSerializer.Serialize(functionPermissionEvent, AGUIJsonContext.Default.FunctionPermissionRequestEvent),
            ContinuationPermissionRequestEvent continuationPermissionEvent => JsonSerializer.Serialize(continuationPermissionEvent, AGUIJsonContext.Default.ContinuationPermissionRequestEvent),
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
            Type = "TEXT_MESSAGE_CONTENT",
            MessageId = messageId,
            Delta = delta,
            Timestamp = GetTimestamp()
        };
    }

    public static TextMessageStartEvent CreateTextMessageStart(string messageId, string? role = null) => new()
    {
        Type = "TEXT_MESSAGE_START",
        MessageId = messageId,
        Role = role,
        Timestamp = GetTimestamp()
    };

    public static TextMessageEndEvent CreateTextMessageEnd(string messageId) => new()
    {
        Type = "TEXT_MESSAGE_END",
        MessageId = messageId,
        Timestamp = GetTimestamp()
    };

    public static ToolCallStartEvent CreateToolCallStart(string toolCallId, string toolCallName, string parentMessageId) => new()
    {
        Type = "TOOL_CALL_START",
        ToolCallId = toolCallId,
        ToolCallName = toolCallName,
        ParentMessageId = parentMessageId,
        Timestamp = GetTimestamp()
    };

    public static ToolCallArgsEvent CreateToolCallArgs(string toolCallId, string delta) => new()
    {
        Type = "TOOL_CALL_ARGS",
        ToolCallId = toolCallId,
        Delta = delta,
        Timestamp = GetTimestamp()
    };

    public static ToolCallEndEvent CreateToolCallEnd(string toolCallId) => new()
    {
        Type = "TOOL_CALL_END",
        ToolCallId = toolCallId,
        Timestamp = GetTimestamp()
    };

    public static RunStartedEvent CreateRunStarted(string threadId, string runId) => new()
    {
        Type = "RUN_STARTED",
        ThreadId = threadId,
        RunId = runId,
        Timestamp = GetTimestamp()
    };

    public static RunFinishedEvent CreateRunFinished(string threadId, string runId) => new()
    {
        Type = "RUN_FINISHED",
        ThreadId = threadId,
        RunId = runId,
        Timestamp = GetTimestamp()
    };

    public static RunErrorEvent CreateRunError(string message) => new()
    {
        Type = "RUN_ERROR",
        Message = message,
        Timestamp = GetTimestamp()
    };

    public static FunctionPermissionRequestEvent CreateFunctionPermissionRequest(string permissionId, string functionName, string functionDescription, Dictionary<string, object?> arguments) => new()
    {
        Type = "function",
        PermissionId = permissionId,
        FunctionName = functionName,
        FunctionDescription = functionDescription,
        Arguments = arguments,
        Timestamp = GetTimestamp()
    };

    public static ContinuationPermissionRequestEvent CreateContinuationPermissionRequest(string permissionId, int currentIteration, int maxIterations, string[] completedFunctions, string elapsedTime) => new()
    {
        Type = "continuation",
        PermissionId = permissionId,
        CurrentIteration = currentIteration,
        MaxIterations = maxIterations,
        CompletedFunctions = completedFunctions,
        ElapsedTime = elapsedTime,
        Timestamp = GetTimestamp()
    };

    // Standard event factory methods for consistency - only adding missing ones
    public static StepStartedEvent CreateStepStarted(string stepId, string stepName, string? description = null) => new()
    {
        Type = "STEP_STARTED",
        StepId = stepId,
        StepName = stepName,
        Description = description,
        Timestamp = GetTimestamp()
    };

    public static StepFinishedEvent CreateStepFinished(string stepId, string stepName, JsonElement? result = null) => new()
    {
        Type = "STEP_FINISHED",
        StepId = stepId,
        StepName = stepName,
        Result = result,
        Timestamp = GetTimestamp()
    };

    // New AG-UI Event Factory Methods

    public static TextMessageChunkEvent CreateTextMessageChunk(string? messageId = null, string? role = null, string? delta = null) => new()
    {
        Type = "TEXT_MESSAGE_CHUNK",
        MessageId = messageId,
        Role = role,
        Delta = delta,
        Timestamp = GetTimestamp()
    };


    // Official AG-UI Reasoning Event Factories (Replacement for deprecated THINKING events)
    public static ReasoningStartEvent CreateReasoningStart(string messageId, string? encryptedContent = null) => new()
    {
        Type = "REASONING_START",
        MessageId = messageId,
        EncryptedContent = encryptedContent,
        Timestamp = GetTimestamp()
    };

    public static ReasoningEndEvent CreateReasoningEnd(string messageId) => new()
    {
        Type = "REASONING_END",
        MessageId = messageId,
        Timestamp = GetTimestamp()
    };

    public static ReasoningMessageStartEvent CreateReasoningMessageStart(string messageId, string? role = null) => new()
    {
        Type = "REASONING_MESSAGE_START",
        MessageId = messageId,
        Role = role,
        Timestamp = GetTimestamp()
    };

    public static ReasoningMessageContentEvent CreateReasoningMessageContent(string messageId, string delta) => new()
    {
        Type = "REASONING_MESSAGE_CONTENT",
        MessageId = messageId,
        Delta = delta,
        Timestamp = GetTimestamp()
    };

    public static ReasoningMessageEndEvent CreateReasoningMessageEnd(string messageId) => new()
    {
        Type = "REASONING_MESSAGE_END",
        MessageId = messageId,
        Timestamp = GetTimestamp()
    };

    public static ToolCallChunkEvent CreateToolCallChunk(string? toolCallId = null, string? toolCallName = null, string? parentMessageId = null, string? delta = null) => new()
    {
        Type = "TOOL_CALL_CHUNK",
        ToolCallId = toolCallId,
        ToolCallName = toolCallName,
        ParentMessageId = parentMessageId,
        Delta = delta,
        Timestamp = GetTimestamp()
    };

    public static ToolCallResultEvent CreateToolCallResult(string toolCallId, string result) => new()
    {
        Type = "TOOL_CALL_RESULT",
        ToolCallId = toolCallId,
        Result = result,
        Timestamp = GetTimestamp()
    };
}
