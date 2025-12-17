using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Tracks consecutive errors across iterations and terminates execution when threshold is exceeded.
/// Analyzes tool results in AfterIterationAsync to detect errors and manage failure state.
/// </summary>
/// <remarks>
/// <para><b>STATELESS MIDDLEWARE:</b></para>
/// <para>
/// This middleware is stateless - all state flows through the context via
/// <see cref="ErrorTrackingState"/>. This preserves Agent's thread-safety
/// guarantee for concurrent RunAsync() calls.
/// </para>
///
/// <para><b>Error detection happens in AfterIterationAsync:</b></para>
/// <list type="number">
/// <item>Analyzes ToolResults for errors (exceptions or error patterns in results)</item>
/// <item>Updates ErrorTrackingState via context.UpdateState()</item>
/// <item>Checks threshold and triggers termination if exceeded</item>
/// </list>
///
/// <para><b>When triggered:</b></para>
/// <list type="number">
/// <item>Emits a <see cref="MaxConsecutiveErrorsExceededEvent"/> for observability</item>
/// <item>Signals termination via Properties["IsTerminated"] = true</item>
/// <item>Provides a termination message explaining the error threshold trigger</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Register via AgentBuilder
/// var agent = new AgentBuilder()
///     .WithMiddleware(new ErrorTrackingMiddleware { MaxConsecutiveErrors = 3 })
///     .Build();
/// </code>
/// </example>
public class ErrorTrackingMiddleware : IAgentMiddleware
{
    //     
    // CONFIGURATION (not state - set at registration time)
    //     

    /// <summary>
    /// Maximum number of consecutive errors allowed before triggering termination.
    /// Default: 3 (matches typical agent configuration).
    /// </summary>
    public int MaxConsecutiveErrors { get; set; } = 3;

    /// <summary>
    /// Custom error detection function. If not provided, uses default error detection.
    /// Return true if the FunctionResultContent represents an error.
    /// </summary>
    public Func<FunctionResultContent, bool>? CustomErrorDetector { get; set; }

    /// <summary>
    /// Custom termination message template.
    /// Placeholders: {count}, {max}
    /// </summary>
    public string TerminationMessageTemplate { get; set; } =
        "Maximum consecutive errors ({count}/{max}) exceeded. " +
        "Stopping execution to prevent infinite error loop.";

    //     
    // HOOKS
    //     

    /// <summary>
    /// Called BEFORE the LLM call begins.
    /// Checks if consecutive failures already at/above threshold to prevent wasted LLM calls.
    /// </summary>
    public Task BeforeIterationAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        // Skip check on first iteration (no previous errors to check)
        if (context.Iteration == 0)
            return Task.CompletedTask;

        // Read state from context (type-safe!)
        var errState = context.State.MiddlewareState.ErrorTracking ?? new();

        if (errState.ConsecutiveFailures >= MaxConsecutiveErrors)
        {
            // Already at threshold - terminate before wasting LLM call
            TriggerTermination(context, errState.ConsecutiveFailures);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called AFTER all tools complete for this iteration.
    /// Analyzes tool results for errors and updates state.
    /// </summary>
    public Task AfterIterationAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        // If no tool results, nothing to analyze
        if (context.ToolResults.Count == 0)
            return Task.CompletedTask;

        // Detect errors in tool results
        var hasErrors = context.ToolResults.Any(IsError);

        // Update state and check threshold
        if (hasErrors)
        {
            // Increment failure counter via context.UpdateState
            var currentState = context.State.MiddlewareState.ErrorTracking ?? new();
            var newState = currentState.IncrementFailures();
            context.UpdateState(s => s with
            {
                MiddlewareState = s.MiddlewareState.WithErrorTracking(newState)
            });

            // Get the updated failure count from pending state
            var pendingState = context.GetPendingState();
            var newFailureCount = pendingState?.MiddlewareState.ErrorTracking?.ConsecutiveFailures
                ?? context.State.MiddlewareState.ErrorTracking?.ConsecutiveFailures + 1 ?? 1;

            // Check if this exceeds threshold
            if (newFailureCount >= MaxConsecutiveErrors)
            {
                TriggerTermination(context, newFailureCount);
            }
        }
        else
        {
            // Reset failure counter on success
            var currentState = context.State.MiddlewareState.ErrorTracking ?? new();
            var newState = currentState.ResetFailures();
            context.UpdateState(s => s with
            {
                MiddlewareState = s.MiddlewareState.WithErrorTracking(newState)
            });
        }

        return Task.CompletedTask;
    }

    //     
    // HELPERS
    //     

    /// <summary>
    /// Determines if a function result represents an error.
    /// Uses custom detector if provided, otherwise uses default detection.
    /// </summary>
    private bool IsError(FunctionResultContent result)
    {
        // Use custom detector if provided
        if (CustomErrorDetector != null)
            return CustomErrorDetector(result);

        // Default error detection
        return IsDefaultError(result);
    }

    /// <summary>
    /// Default error detection logic matching the original Agent implementation.
    /// </summary>
    private static bool IsDefaultError(FunctionResultContent result)
    {
        // Primary signal: Exception present
        if (result.Exception != null)
            return true;

        // Secondary signal: Result contains error indicators
        var resultStr = result.Result?.ToString();
        if (string.IsNullOrEmpty(resultStr))
            return false;

        // Check for definitive error patterns (case-insensitive)
        return resultStr.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
               resultStr.StartsWith("Failed:", StringComparison.OrdinalIgnoreCase) ||
               // Exception indicators
               resultStr.Contains("exception occurred", StringComparison.OrdinalIgnoreCase) ||
               resultStr.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase) ||
               resultStr.Contains("exception was thrown", StringComparison.OrdinalIgnoreCase) ||
               // Rate limit indicators
               resultStr.Contains("rate limit exceeded", StringComparison.OrdinalIgnoreCase) ||
               resultStr.Contains("rate limited", StringComparison.OrdinalIgnoreCase) ||
               resultStr.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase) ||
               resultStr.Contains("quota reached", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Triggers termination, preventing further LLM calls and signaling the agent loop.
    /// </summary>
    private void TriggerTermination(AgentMiddlewareContext context, int errorCount)
    {
        // Skip the next LLM call
        context.SkipLLMCall = true;

        // Format termination message
        var message = $"⚠️ {TerminationMessageTemplate}"
            .Replace("{count}", errorCount.ToString())
            .Replace("{max}", MaxConsecutiveErrors.ToString());

        // Provide final response
        context.Response = new ChatMessage(
            ChatRole.Assistant,
            message);

        // Clear tool calls
        context.ToolCalls = Array.Empty<FunctionCallContent>();

        // Signal termination via properties
        context.Properties["IsTerminated"] = true;
        context.Properties["TerminationReason"] = $"Maximum consecutive errors ({errorCount}) exceeded";

        // Emit TextDeltaEvent for user visibility
        try
        {
            context.Emit(new TextDeltaEvent(message, Guid.NewGuid().ToString()));
        }
        catch (InvalidOperationException)
        {
            // EventCoordinator not configured - event emission is optional
        }

        // Emit observability event
        try
        {
            context.Emit(new MaxConsecutiveErrorsExceededEvent(
                AgentName: context.AgentName,
                ConsecutiveErrors: errorCount,
                MaxConsecutiveErrors: MaxConsecutiveErrors,
                Iteration: context.Iteration,
                Timestamp: DateTimeOffset.UtcNow));
        }
        catch (InvalidOperationException)
        {
            // EventCoordinator not configured - event emission is optional
        }
    }
}

/// <summary>
/// Event emitted when max consecutive errors threshold is exceeded.
/// Used for observability and telemetry.
/// </summary>
public record MaxConsecutiveErrorsExceededEvent(
    string AgentName,
    int ConsecutiveErrors,
    int MaxConsecutiveErrors,
    int Iteration,
    DateTimeOffset Timestamp) : AgentEvent, IObservabilityEvent, IErrorEvent
{
    /// <inheritdoc />
    public string ErrorMessage => $"Maximum consecutive errors ({ConsecutiveErrors}/{MaxConsecutiveErrors}) exceeded";

    /// <inheritdoc />
    public Exception? Exception => null;
}
