using System;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

/// <summary>
/// Simple logging filter for orchestration-level AI function calls. Logs input arguments before execution and result after execution.
/// </summary>
public class LoggingAiFunctionFilter : IAiFunctionFilter
{
    public async Task InvokeAsync(
        AiFunctionContext context,
        Func<AiFunctionContext, Task> next)
    {
        // Pre-invocation logging
        var functionName = context.ToolCallRequest?.FunctionName ?? "<unknown>";
        var args = context.ToolCallRequest?.Arguments ?? context.Arguments;
        var conversationId = context.Conversation?.Id ?? "<no-conversation>";
        Console.WriteLine($"[LOG][PRE] Conversation: {conversationId} Function: {functionName}\nArgs: {FormatArgs(args)}");

        // Invoke next filter or target function
        await next(context);

        // Post-invocation logging
        var result = context.Result;
        Console.WriteLine($"[LOG][POST] Conversation: {conversationId} Function: {functionName} Result: {FormatResult(result)}");
        Console.WriteLine(new string('-', 50));
    }

    private string FormatArgs(object? args)
    {
        if (args == null) return "<null>";
        if (args is System.Collections.IDictionary dict)
        {
            var items = new System.Text.StringBuilder();
            foreach (var key in dict.Keys)
            {
                items.Append($"{key}: {dict[key]}, ");
            }
            return items.Length > 0 ? items.ToString().TrimEnd(',', ' ') : "<empty>";
        }
        return args?.ToString() ?? "<unknown>";
    }

    private string FormatResult(object? result)
    {
        if (result == null) return "<null>";
        return result.ToString() ?? "<unknown>";
    }
}
