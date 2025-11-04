using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
/// Creates detailed OpenTelemetry spans (Activities) and metrics for each AI function call.
/// This provides both qualitative traces and quantitative metrics for the agent's tool execution process.
/// </summary>
public class ObservabilityAiFunctionFilter : IAiFunctionFilter
{
    private readonly ActivitySource _activitySource;
    private readonly Counter<long> _toolCallCounter;
    private readonly Histogram<double> _toolCallDuration;
    private readonly Counter<long> _toolCallErrorCounter;

    public ObservabilityAiFunctionFilter(ActivitySource activitySource, Meter meter)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));

        // Create the metric instruments. These are created once and reused.
        _toolCallCounter = meter.CreateCounter<long>("agent.tool_calls.count", description: "Number of tool calls initiated by the agent.");
        _toolCallDuration = meter.CreateHistogram<double>("agent.tool_calls.duration", unit: "ms", description: "Duration of tool calls.");
        _toolCallErrorCounter = meter.CreateCounter<long>("agent.tool_calls.errors", description: "Number of failed tool calls.");
    }

    public async Task InvokeAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var functionName = context.ToolCallRequest?.FunctionName ?? "unknown_function";
        var tags = new TagList { { "gen_ai.tool.name", functionName } };

        // Increment the total tool call counter
        _toolCallCounter.Add(1, tags);

        var stopwatch = Stopwatch.StartNew();

        // Start a new child span for the tool call
        using var activity = _activitySource.StartActivity($"execute_tool {functionName}");

        // Add useful tags (attributes) to the span - only if activity was created
        activity?.SetTag("agent.name", context.AgentName);
        activity?.SetTag("gen_ai.tool.name", functionName);

        if (activity is not null && context.ToolCallRequest?.Arguments is { Count: > 0 })
        {
            try
            {
                activity.SetTag("gen_ai.tool.arguments", JsonSerializer.Serialize(context.ToolCallRequest.Arguments, HPDJsonContext.Default.IDictionaryStringObject));
            }
            catch
            {
                // Silently ignore serialization errors for arguments
                activity.SetTag("gen_ai.tool.arguments", "<serialization_failed>");
            }
        }

        try
        {
            await next(context); // Execute the actual function

            // Record the result on the span
            if (activity is not null && context.Result is not null)
            {
                try
                {
                    // Attempt to serialize the result
                    var resultString = context.Result.ToString() ?? string.Empty;
                    activity.SetTag("gen_ai.tool.result", resultString);
                }
                catch
                {
                    // If serialization fails, just use string representation
                    activity.SetTag("gen_ai.tool.result", "<result_available>");
                }
            }
        }
        catch (Exception ex)
        {
            // Increment the error counter if an exception occurs
            _toolCallErrorCounter.Add(1, tags);

            // If the tool fails, record the exception details on the span
            if (activity is not null)
            {
                activity.SetTag("error", true);
                activity.SetTag("exception.type", ex.GetType().FullName);
                activity.SetTag("exception.message", ex.Message);
                activity.SetTag("exception.stacktrace", ex.StackTrace);
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
            throw;
        }
        finally
        {
            // Record the duration in the histogram
            stopwatch.Stop();
            _toolCallDuration.Record(stopwatch.Elapsed.TotalMilliseconds, tags);
        }
    }
}
