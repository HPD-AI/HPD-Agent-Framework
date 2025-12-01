using System.Text.Json;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Prevents infinite loops by detecting repeated identical function calls.
/// Triggers circuit breaker when the same tool is called with identical arguments
/// more than <see cref="MaxConsecutiveCalls"/> times consecutively.
/// </summary>
/// <remarks>
/// <para><b>STATELESS MIDDLEWARE:</b></para>
/// <para>
/// This middleware is stateless - all state flows through the context via
/// <see cref="CircuitBreakerState"/>. This preserves Agent's thread-safety
/// guarantee for concurrent RunAsync() calls.
/// </para>
///
/// <para><b>Lifecycle:</b></para>
/// <para>
/// This middleware uses the <see cref="IAgentMiddleware.BeforeToolExecutionAsync"/> hook
/// which runs AFTER the LLM returns tool calls but BEFORE any tools execute.
/// This allows predictive checking: we calculate what the count WOULD BE if the tool executes.
/// </para>
///
/// <para><b>Key behavior:</b></para>
/// <list type="number">
/// <item>Computes function signature from tool name + serialized arguments</item>
/// <item>Compares against last signature for that tool (from CircuitBreakerState)</item>
/// <item>Calculates predicted count (current + 1 if identical, else 1)</item>
/// <item>Triggers if predicted count >= MaxConsecutiveCalls</item>
/// </list>
///
/// <para><b>When triggered:</b></para>
/// <list type="number">
/// <item>Sets <see cref="AgentMiddlewareContext.SkipToolExecution"/> to true</item>
/// <item>Emits a <see cref="TextDeltaEvent"/> for user visibility</item>
/// <item>Emits a <see cref="CircuitBreakerTriggeredEvent"/> for observability</item>
/// <item>Signals termination via Properties["IsTerminated"] = true</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Register via AgentBuilder
/// var agent = new AgentBuilder()
///     .WithMiddleware(new CircuitBreakerMiddleware { MaxConsecutiveCalls = 3 })
///     .Build();
/// </code>
/// </example>
public class CircuitBreakerMiddleware : IAgentMiddleware
{
    //     
    // CONFIGURATION (not state - set at registration time)
    //     

    /// <summary>
    /// Maximum number of consecutive identical calls allowed before triggering.
    /// Default: 3 (matches typical agent configuration).
    /// </summary>
    public int MaxConsecutiveCalls { get; set; } = 3;

    /// <summary>
    /// Custom termination message template.
    /// Placeholders: {toolName}, {count}
    /// </summary>
    public string TerminationMessageTemplate { get; set; } =
        "⚠️ Circuit breaker triggered: Function '{toolName}' with same arguments would be called {count} times consecutively. " +
        "Stopping to prevent infinite loop.";

    //     
    // HOOKS
    //     

    /// <summary>
    /// Called AFTER LLM returns tool calls but BEFORE tools execute.
    /// Checks if executing these tools would exceed the threshold.
    /// </summary>
    public Task BeforeToolExecutionAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        // No tool calls = nothing to check
        if (context.ToolCalls.Count == 0)
            return Task.CompletedTask;

        // Read state from context (type-safe via generated property)
        var cbState = context.State.MiddlewareState.CircuitBreaker ?? new();

        foreach (var toolCall in context.ToolCalls)
        {
            var toolName = toolCall.Name ?? "_unknown";
            var signature = ComputeFunctionSignature(toolCall);

            // Calculate what the count WOULD BE if we execute this tool
            var predictedCount = cbState.GetPredictedCount(toolName, signature);

            // Check if executing this tool would exceed the limit
            if (predictedCount >= MaxConsecutiveCalls)
            {
                TriggerCircuitBreaker(context, toolName, predictedCount);
                return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called AFTER tool execution completes.
    /// Updates circuit breaker state with executed tool signatures.
    /// </summary>
    public Task AfterIterationAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        // No tool calls = nothing to track
        if (context.ToolCalls.Count == 0)
            return Task.CompletedTask;

        // Update state immutably via context
        var currentState = context.State.MiddlewareState.CircuitBreaker ?? new();
        var updatedState = currentState;

        foreach (var toolCall in context.ToolCalls)
        {
            var toolName = toolCall.Name ?? "_unknown";
            var signature = ComputeFunctionSignature(toolCall);
            updatedState = updatedState.RecordToolCall(toolName, signature);
        }

        context.UpdateState(s => s with
        {
            MiddlewareState = s.MiddlewareState.WithCircuitBreaker(updatedState)
        });

        return Task.CompletedTask;
    }

    //     
    // HELPERS
    //     

    /// <summary>
    /// Computes a deterministic signature for a function call.
    /// </summary>
    internal static string ComputeFunctionSignature(FunctionCallContent toolCall)
    {
        var name = toolCall.Name ?? "_unknown";

        // Serialize arguments to JSON for consistent comparison
        string argsJson;
        if (toolCall.Arguments == null || toolCall.Arguments.Count == 0)
        {
            argsJson = "{}";
        }
        else
        {
            try
            {
                // Sort keys for deterministic ordering
                var sortedArgs = toolCall.Arguments
                    .OrderBy(kvp => kvp.Key)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                argsJson = JsonSerializer.Serialize(sortedArgs);
            }
            catch
            {
                // Fallback if serialization fails
                argsJson = string.Join(",", toolCall.Arguments.Select(kvp => $"{kvp.Key}={kvp.Value}"));
            }
        }

        return $"{name}({argsJson})";
    }

    /// <summary>
    /// Triggers the circuit breaker, preventing tool execution and terminating the loop.
    /// </summary>
    private void TriggerCircuitBreaker(AgentMiddlewareContext context, string toolName, int count)
    {
        // Skip tool execution
        context.SkipToolExecution = true;

        // Format termination message
        var message = TerminationMessageTemplate
            .Replace("{toolName}", toolName)
            .Replace("{count}", count.ToString());

        // Provide final response (for LLM flow compatibility)
        context.Response = new ChatMessage(
            ChatRole.Assistant,
            message);

        // Clear tool calls (no further work)
        context.ToolCalls = Array.Empty<FunctionCallContent>();

        // Signal termination via properties
        context.Properties["IsTerminated"] = true;
        context.Properties["TerminationReason"] = $"Circuit breaker: '{toolName}' with same arguments would be called {count} times consecutively";

        // Emit TextDeltaEvent for user visibility
        try
        {
            context.Emit(new TextDeltaEvent(message, Guid.NewGuid().ToString()));
        }
        catch (InvalidOperationException)
        {
            // EventCoordinator not configured - event emission is optional
        }

        // Emit CircuitBreakerTriggeredEvent for observability
        try
        {
            context.Emit(new CircuitBreakerTriggeredEvent(
                AgentName: context.AgentName,
                FunctionName: toolName,
                ConsecutiveCount: count,
                Iteration: context.Iteration,
                Timestamp: DateTimeOffset.UtcNow));
        }
        catch (InvalidOperationException)
        {
            // EventCoordinator not configured - event emission is optional
        }
    }
}
