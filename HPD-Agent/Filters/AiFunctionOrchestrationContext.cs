using Microsoft.Extensions.AI;

/// <summary>
/// Represents the context of an orchestration step where a tool may be invoked.
/// This context is now much richer, providing access to the entire conversation.
/// Native AOT compatible - does not inherit from FunctionInvocationContext.
/// </summary>
public class AiFunctionContext :  FunctionInvocationContext

{
    /// <summary>
    /// The conversation that this orchestration step belongs to.
    /// </summary>
    public Conversation Conversation { get; }

    /// <summary>
    /// The raw tool call request from the Language Model.
    /// </summary>
    public ToolCallRequest ToolCallRequest { get; }

    /// <summary>
    /// A flag to allow a filter to terminate the pipeline.
    /// </summary>
    public bool IsTerminated { get; set; } = false;

    /// <summary>
    /// The result of the function invocation, to be set by the final step.
    /// </summary>
    public object? Result { get; set; }

    /// <summary>
    /// The AI function being invoked (if available).
    /// </summary>
    public new AIFunction? Function { get; set; }

    

    /// <summary>
    /// Arguments for the function call (AOT-safe access).
    /// </summary>
    public new AIFunctionArguments Arguments { get; }

    public AiFunctionContext(Conversation conversation, ToolCallRequest toolCallRequest)
    {
        Conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        ToolCallRequest = toolCallRequest ?? throw new ArgumentNullException(nameof(toolCallRequest));
    }
}


/// <summary>
/// The filter interface remains the same, but it will now operate
/// on the new, richer AiFunctionContext.
/// </summary>
public interface IAiFunctionFilter
{
    Task InvokeAsync(
        AiFunctionContext context,
        Func<AiFunctionContext, Task> next);
}

/// <summary>
/// Represents a function call request from the LLM.
/// </summary>
public class ToolCallRequest
{
    public string FunctionName { get; set; }
    public IDictionary<string, object?> Arguments { get; set; }
}
