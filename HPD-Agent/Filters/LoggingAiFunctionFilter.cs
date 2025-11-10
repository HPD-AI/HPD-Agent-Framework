using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using HPD.Agent;

/// <summary>
/// Simple logging filter for orchestration-level AI function calls. Logs input arguments before execution and result after execution.
/// </summary>
public class LoggingAiFunctionFilter : IAiFunctionFilter
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance with optional logger factory
    /// </summary>
    public LoggingAiFunctionFilter(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<LoggingAiFunctionFilter>();
    }
    public async Task InvokeAsync(
        HPD.Agent.FunctionInvocationContext context,
        Func<HPD.Agent.FunctionInvocationContext, Task> next)
    {
        // Pre-invocation logging
        var functionName = context.ToolCallRequest?.FunctionName ?? "<unknown>";
        var args = context.ToolCallRequest?.Arguments ?? context.Arguments;

        var preMessage = $"[LOG][PRE] Function: {functionName}\nArgs: {FormatArgs(args)}";
        LogMessage(preMessage);

        // Invoke next filter or target function
        await next(context);

        // Post-invocation logging
        var result = context.Result;
        var postMessage = $"[LOG][POST] Function: {functionName} Result: {FormatResult(result)}";
        LogMessage(postMessage);
        LogMessage(new string('-', 50));
    }

    private void LogMessage(string message)
    {
        if (_logger != null)
        {
            _logger.LogInformation(message);
        }
        else
        {
            Console.WriteLine(message);
        }
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
        
        // If it's a ValidationErrorResponse, serialize it to JSON for readability
        if (result is ValidationErrorResponse validationError)
        {
            try
            {
                return JsonSerializer.Serialize(validationError, HPDJsonContext.Default.ValidationErrorResponse);
            }
            catch
            {
                return result.ToString() ?? "<unknown>";
            }
        }
        
        // For other objects, try JSON serialization first, fall back to ToString()
        try
        {
            // Attempt to serialize to JSON for better visibility
            return JsonSerializer.Serialize(result, new JsonSerializerOptions 
            { 
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }
        catch
        {
            // Fall back to ToString() if serialization fails
            return result.ToString() ?? "<unknown>";
        }
    }
}
