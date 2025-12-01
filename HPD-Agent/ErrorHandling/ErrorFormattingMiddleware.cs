namespace HPD.Agent.Middleware.Function;

/// <summary>
/// Middleware that formats function execution errors for LLM consumption.
/// Provides security-aware error message formatting to prevent exposing sensitive information.
/// </summary>
/// <remarks>
/// <para><b>Security Features:</b></para>
/// <para>
/// This middleware acts as a security boundary between function exceptions and the LLM.
/// By default, it sanitizes error messages to prevent exposing:
/// - Stack traces
/// - Database connection strings
/// - File system paths
/// - API keys or tokens
/// - Internal implementation details
/// </para>
///
/// <para><b>Configuration:</b></para>
/// <para>
/// Controlled by <c>ErrorHandlingConfig.IncludeDetailedErrorsInChat</c>:
/// - <c>false</c> (default): Returns generic error messages like "Error: Function 'X' failed."
/// - <c>true</c>: Returns detailed error messages with exception information
/// </para>
///
/// <para><b>Observability:</b></para>
/// <para>
/// Regardless of the security setting, the full exception is ALWAYS stored in
/// <c>AgentMiddlewareContext.FunctionException</c> for logging, debugging, and observability.
/// </para>
///
/// <para><b>Recommended Middleware Order:</b></para>
/// <code>
/// .WithFunctionRetry()      // Outermost - retry the entire operation
/// .WithFunctionTimeout()    // Middle - timeout individual attempts
/// .WithErrorFormatting()    // Innermost - format errors after all retries exhausted
/// </code>
///
/// <para><b>Example Usage:</b></para>
/// <code>
/// // Default - sanitized errors for security
/// var agent = new AgentBuilder(config)
///     .WithErrorFormatting()
///     .Build();
///
/// // Allow detailed errors (use only in trusted environments)
/// var config = new AgentConfig
/// {
///     ErrorHandling = new ErrorHandlingConfig
///     {
///         IncludeDetailedErrorsInChat = true
///     }
/// };
/// var agent = new AgentBuilder(config)
///     .WithErrorFormatting()
///     .Build();
/// </code>
/// </remarks>
public class ErrorFormattingMiddleware : IAgentMiddleware
{
    private readonly bool _includeDetailedErrors;

    /// <summary>
    /// Creates a new error formatting middleware with default settings (sanitized errors).
    /// </summary>
    public ErrorFormattingMiddleware()
    {
        _includeDetailedErrors = false;
    }

    /// <summary>
    /// Creates a new error formatting middleware with the specified configuration.
    /// </summary>
    /// <param name="config">Error handling configuration</param>
    public ErrorFormattingMiddleware(ErrorHandlingConfig config)
    {
        _includeDetailedErrors = config?.IncludeDetailedErrorsInChat ?? false;
    }

    /// <summary>
    /// Gets or sets whether to include detailed exception messages in function results sent to the LLM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Security Warning:</b> Setting this to <c>true</c> may expose sensitive information to the LLM and end users:
    /// - Database connection strings
    /// - File system paths
    /// - API keys or tokens
    /// - Internal implementation details
    /// </para>
    /// <para>
    /// When <c>false</c> (default), function errors are reported to the LLM as generic messages like
    /// "Error: Function 'X' failed." The full exception is still available to application code via
    /// <c>AgentMiddlewareContext.FunctionException</c> for logging and debugging.
    /// </para>
    /// <para>
    /// When <c>true</c>, the full exception message is included in the function result, allowing the LLM
    /// to potentially self-correct (e.g., retry with different arguments). Use this only in trusted
    /// environments or with sanitized exceptions.
    /// </para>
    /// </remarks>
    public bool IncludeDetailedErrorsInChat
    {
        get => _includeDetailedErrors;
        init => _includeDetailedErrors = value;
    }

    /// <summary>
    /// Executes the function and formats any errors according to security settings.
    /// </summary>
    public async ValueTask<object?> ExecuteFunctionAsync(
        AgentMiddlewareContext context,
        Func<ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        try
        {
            // Execute next middleware in chain (or actual function)
            return await next();
        }
        catch (Exception ex)
        {
            // ALWAYS store the full exception for observability/logging
            // This is available to application code but not sent to the LLM
            context.FunctionException = ex;

            // Format error message based on security settings
            var functionName = context.Function?.Name ?? "Unknown";

            if (_includeDetailedErrors)
            {
                // Include full exception details (potential security risk)
                // Use this only in trusted environments or for debugging
                return $"Error invoking function '{functionName}': {ex.Message}";
            }
            else
            {
                // Return sanitized error message (secure by default)
                // Full exception is still available via context.FunctionException
                return $"Error: Function '{functionName}' failed.";
            }
        }
    }
}
