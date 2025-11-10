using Microsoft.Extensions.AI;

/// <summary>
/// Filter interface for function invocation pipeline.
/// Operates on FunctionInvocationContext which provides full orchestration capabilities
/// including bidirectional communication, event emission, and filter pipeline control.
/// </summary>
public interface IAiFunctionFilter
{
    Task InvokeAsync(
        HPD.Agent.FunctionInvocationContext context,
        Func<HPD.Agent.FunctionInvocationContext, Task> next);
}

/// <summary>
/// Represents a function call request from the LLM.
/// </summary>
public class ToolCallRequest
{
    public required string FunctionName { get; set; }
    public required IDictionary<string, object?> Arguments { get; set; }
}
