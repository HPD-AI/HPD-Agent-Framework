using Microsoft.Extensions.AI;

/// <summary>
/// Filter interface for processing completed message turns.
/// Executes after agent response and all tool calls are complete.
/// Applications implement this to capture state changes made by agent tool calls.
/// </summary>
public interface IMessageTurnFilter
{
    /// <summary>
    /// Processes a completed message turn
    /// </summary>
    /// <param name="context">Context containing turn details and metadata</param>
    /// <param name="next">Next filter in the pipeline</param>
    /// <returns>Task representing the async filter operation</returns>
    Task InvokeAsync(
        MessageTurnFilterContext context,
        Func<MessageTurnFilterContext, Task> next);
}