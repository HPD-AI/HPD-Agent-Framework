using System.Text.Json;
using HPD.Agent;

/// <summary>
/// Event handler that formats agent events as Server-Sent Events (SSE)
/// </summary>
public class SseEventHandler : IAgentEventHandler
{
    private readonly StreamWriter _writer;
    private readonly JsonSerializerOptions _jsonOptions;

    public SseEventHandler(StreamWriter writer)
    {
        _writer = writer;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        };
    }

    public async Task OnEventAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        string? eventType = null;
        object? eventData = null;

        switch (evt)
        {
            case TextDeltaEvent textDelta:
                var textPreview = textDelta.Text?.Length > 50 ? textDelta.Text.Substring(0, 50) : textDelta.Text;
                Console.WriteLine($"[SSE] 📤 Sending text_delta: {textPreview}...");
                eventType = "text_delta";
                eventData = new { text = textDelta.Text };
                break;

            case Reasoning reasoning when reasoning.Phase == ReasoningPhase.Delta:
                var reasoningPreview = reasoning.Text?.Length > 50 ? reasoning.Text.Substring(0, 50) : reasoning.Text;
                Console.WriteLine($"[SSE] 📤 Sending reasoning_delta: {reasoningPreview}...");
                eventType = "reasoning_delta";
                eventData = new { text = reasoning.Text };
                break;

            case ToolCallStartEvent toolStart:
                Console.WriteLine($"[SSE] 📤 Sending tool_call_start: {toolStart.Name} (ID: {toolStart.CallId})");
                eventType = "tool_call_start";
                eventData = new { name = toolStart.Name, call_id = toolStart.CallId };
                break;

            case ToolCallResultEvent toolResult:
                Console.WriteLine($"[SSE] 📤 Sending tool_call_result: {toolResult.CallId}");
                eventType = "tool_call_result";
                eventData = new { call_id = toolResult.CallId };
                break;

            case AgentTurnStartedEvent turnStart:
                Console.WriteLine($"[SSE] 📤 Sending agent_turn_started: iteration {turnStart.Iteration}");
                eventType = "agent_turn_started";
                eventData = new { iteration = turnStart.Iteration };
                break;

            case AgentTurnFinishedEvent turnFinished:
                Console.WriteLine($"[SSE] 📤 Sending agent_turn_finished: iteration {turnFinished.Iteration}");
                eventType = "agent_turn_finished";
                eventData = new { iteration = turnFinished.Iteration };
                break;

            case MessageTurnFinishedEvent:
                Console.WriteLine($"[SSE] 📤 Sending message_turn_finished");
                eventType = "message_turn_finished";
                eventData = new { };
                break;

            case PermissionRequestEvent permReq:
                Console.WriteLine($"[SSE] 📤 Sending permission_request: {permReq.FunctionName}");
                eventType = "permission_request";
                eventData = new
                {
                    permission_id = permReq.PermissionId,
                    function_name = permReq.FunctionName,
                    description = permReq.Description,
                    call_id = permReq.CallId,
                    arguments = permReq.Arguments
                };
                break;

            case PermissionApprovedEvent permApproved:
                Console.WriteLine($"[SSE] 📤 Sending permission_approved: {permApproved.PermissionId}");
                eventType = "permission_approved";
                eventData = new { permission_id = permApproved.PermissionId };
                break;

            case PermissionDeniedEvent permDenied:
                Console.WriteLine($"[SSE] 📤 Sending permission_denied: {permDenied.Reason}");
                eventType = "permission_denied";
                eventData = new
                {
                    permission_id = permDenied.PermissionId,
                    reason = permDenied.Reason
                };
                break;
        }

        if (eventType != null && eventData != null)
        {
            var eventJson = JsonSerializer.Serialize(new { type = eventType, data = eventData }, _jsonOptions);
            await _writer.WriteAsync($"data: {eventJson}\n\n");
            await _writer.FlushAsync(cancellationToken);
        }
    }
}
