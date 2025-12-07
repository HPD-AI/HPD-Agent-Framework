using HPD.Agent;
using HPD.Agent.Checkpointing;
using HPD.Agent.Checkpointing.Services;
using HPD.Agent.Serialization;

/// <summary>
/// Event handler that formats agent events as Server-Sent Events (SSE).
/// Uses the standard AgentEventSerializer for consistent JSON output.
/// </summary>
/// <remarks>
/// This handler uses the framework's standard event serialization,
/// providing consistent SCREAMING_SNAKE_CASE type discriminators and
/// a version field for future compatibility.
///
/// Output format:
/// <code>
/// data: {"version":"1.0","type":"TEXT_DELTA","text":"Hello","messageId":"msg-123"}
/// </code>
/// </remarks>
public class SseEventHandler : IAgentEventHandler
{
    private readonly StreamWriter _writer;

    public SseEventHandler(StreamWriter writer)
    {
        _writer = writer;
    }

    public async Task OnEventAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        // Use standard serializer - all events are handled automatically!
        var json = AgentEventSerializer.ToJson(evt);

        // Log for debugging
        var typeName = AgentEventSerializer.GetEventTypeName(evt);
        LogEvent(evt, typeName);

        // Send as SSE
        await _writer.WriteAsync($"data: {json}\n\n");
        await _writer.FlushAsync(cancellationToken);
    }

    private static void LogEvent(AgentEvent evt, string typeName)
    {
        switch (evt)
        {
            case TextDeltaEvent textDelta:
                var textPreview = textDelta.Text?.Length > 50 ? textDelta.Text.Substring(0, 50) : textDelta.Text;
                Console.WriteLine($"[SSE] Sending {typeName}: {textPreview}...");
                break;

            case Reasoning reasoning when reasoning.Phase == ReasoningPhase.Delta:
                var reasoningPreview = reasoning.Text?.Length > 50 ? reasoning.Text.Substring(0, 50) : reasoning.Text;
                Console.WriteLine($"[SSE] Sending {typeName}: {reasoningPreview}...");
                break;

            case ToolCallStartEvent toolStart:
                Console.WriteLine($"[SSE] Sending {typeName}: {toolStart.Name} (ID: {toolStart.CallId})");
                break;

            case ToolCallResultEvent toolResult:
                Console.WriteLine($"[SSE] Sending {typeName}: {toolResult.CallId}");
                break;

            case AgentTurnStartedEvent turnStart:
                Console.WriteLine($"[SSE] Sending {typeName}: iteration {turnStart.Iteration}");
                break;

            case AgentTurnFinishedEvent turnFinished:
                Console.WriteLine($"[SSE] Sending {typeName}: iteration {turnFinished.Iteration}");
                break;

            case MessageTurnFinishedEvent:
                Console.WriteLine($"[SSE] Sending {typeName}");
                break;

            case PermissionRequestEvent permReq:
                Console.WriteLine($"[SSE] Sending {typeName}: {permReq.FunctionName}");
                break;

            case PermissionApprovedEvent permApproved:
                Console.WriteLine($"[SSE] Sending {typeName}: {permApproved.PermissionId}");
                break;

            case PermissionDeniedEvent permDenied:
                Console.WriteLine($"[SSE] Sending {typeName}: {permDenied.Reason}");
                break;

            // Branch events removed - branching is now application-level (not part of AgentEvent hierarchy)
            // Branch events are handled separately by ConversationManager

            default:
                // All other events are logged with their type name
                Console.WriteLine($"[SSE] Sending {typeName}");
                break;
        }
    }
}
