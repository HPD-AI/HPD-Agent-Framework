/// <summary>
/// Configuration for agent system messages.
/// Allows customization of messages for internationalization, branding, or context-specific needs.
/// </summary>
public class AgentMessagesConfig
{
    /// <summary>
    /// Message shown when the maximum iteration limit is reached.
    /// Placeholders: {maxIterations}
    /// Default: "Maximum iteration limit reached ({maxIterations} iterations). The agent was unable to complete the task within the allowed number of turns."
    /// </summary>
    public string MaxIterationsReached { get; set; } =
        "Maximum iteration limit reached ({maxIterations} iterations). The agent was unable to complete the task within the allowed number of turns.";

    /// <summary>
    /// Message shown when circuit breaker triggers due to repeated identical tool calls.
    /// Placeholders: {toolName}, {count}
    /// Default: "Circuit breaker triggered: '{toolName}' called {count} times with the same arguments. This may indicate the agent is stuck in a loop."
    /// </summary>
    public string CircuitBreakerTriggered { get; set; } =
        "Circuit breaker triggered: '{toolName}' called {count} times with the same arguments. This may indicate the agent is stuck in a loop.";

    /// <summary>
    /// Message shown when maximum consecutive errors is exceeded.
    /// Placeholders: {maxErrors}
    /// Default: "Exceeded maximum consecutive errors ({maxErrors}). The agent is unable to proceed due to repeated failures."
    /// </summary>
    public string MaxConsecutiveErrors { get; set; } =
        "Exceeded maximum consecutive errors ({maxErrors}). The agent is unable to proceed due to repeated failures.";

    /// <summary>
    /// Default message sent to LLM when a tool execution is denied by permission filter without a custom reason.
    /// This is used when user denies permission but doesn't provide a specific denial reason.
    /// Set to empty string if you want no message sent to LLM.
    /// Default: "Permission denied by user."
    /// </summary>
    public string PermissionDeniedDefault { get; set; } =
        "Permission denied by user.";

    /// <summary>
    /// Formats the max iterations message with the actual value.
    /// </summary>
    public string FormatMaxIterationsReached(int maxIterations)
    {
        return MaxIterationsReached.Replace("{maxIterations}", maxIterations.ToString());
    }

    /// <summary>
    /// Formats the circuit breaker message with tool name and count.
    /// </summary>
    public string FormatCircuitBreakerTriggered(string toolName, int count)
    {
        return CircuitBreakerTriggered
            .Replace("{toolName}", toolName)
            .Replace("{count}", count.ToString());
    }

    /// <summary>
    /// Formats the max consecutive errors message with the actual value.
    /// </summary>
    public string FormatMaxConsecutiveErrors(int maxErrors)
    {
        return MaxConsecutiveErrors.Replace("{maxErrors}", maxErrors.ToString());
    }
}
