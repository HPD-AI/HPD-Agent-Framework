using System.Text.RegularExpressions;

namespace HPD.Agent.ErrorHandling.Providers;

/// <summary>
/// Error handler for OpenAI and Azure OpenAI providers.
/// Handles both Azure SDK exceptions and standard OpenAI SDK exceptions.
/// </summary>
internal partial class OpenAIErrorHandler : IProviderErrorHandler
{
    public ProviderErrorDetails? ParseError(Exception exception)
    {
        // Try Azure.RequestFailedException (Azure OpenAI)
        if (TryParseAzureException(exception, out var azureDetails))
        {
            return azureDetails;
        }

        // Try HttpRequestException (standard OpenAI)
        if (exception is HttpRequestException httpEx)
        {
            return new ProviderErrorDetails
            {
                StatusCode = (int?)httpEx.StatusCode,
                Category = ClassifyError((int?)httpEx.StatusCode, httpEx.Message),
                Message = httpEx.Message,
                ErrorCode = ExtractErrorCode(httpEx.Message)
            };
        }

        return null; // Unknown exception type
    }

    public TimeSpan? GetRetryDelay(ProviderErrorDetails details, int attempt,
        TimeSpan initialDelay, double multiplier, TimeSpan maxDelay)
    {
        // Don't retry terminal errors
        if (details.Category is ErrorCategory.ClientError or
            ErrorCategory.ContextWindow or ErrorCategory.RateLimitTerminal)
        {
            return null;
        }

        // Priority 1: Use API-specified retry delay
        if (details.RetryAfter.HasValue)
        {
            return details.RetryAfter.Value;
        }

        // Priority 2: Exponential backoff for retryable errors
        if (details.Category is ErrorCategory.RateLimitRetryable or
            ErrorCategory.ServerError or ErrorCategory.Transient)
        {
            var baseMs = initialDelay.TotalMilliseconds;
            var expDelayMs = baseMs * Math.Pow(multiplier, attempt);
            var cappedDelayMs = Math.Min(expDelayMs, maxDelay.TotalMilliseconds);
            var jitter = 0.9 + (Random.Shared.NextDouble() * 0.2); // ±10%

            return TimeSpan.FromMilliseconds(cappedDelayMs * jitter);
        }

        return null;
    }

    public bool RequiresSpecialHandling(ProviderErrorDetails details)
    {
        return details.Category == ErrorCategory.AuthError;
    }

    private static bool TryParseAzureException(Exception exception, out ProviderErrorDetails? details)
    {
        details = null;

        // AOT-compatible: Check exception type name without reflection
        // Azure.RequestFailedException inherits from Exception and has a well-known structure
        var exceptionTypeName = exception.GetType().FullName;
        if (exceptionTypeName != "Azure.RequestFailedException")
        {
            return false;
        }

        // For Azure exceptions, parse the message for status code and error details
        // Azure.RequestFailedException message format: "Service request failed.\nStatus: 429 (Too Many Requests)"
        var message = exception.Message;
        int? status = ExtractStatusCodeFromMessage(message);

        // Try parsing retry delay from error message
        TimeSpan? retryAfter = ExtractRetryDelayFromMessage(message);

        // Extract request ID from message if present
        // Azure often includes request IDs in the message like "RequestId: abc-123"
        string? requestId = ExtractRequestIdFromMessage(message);

        details = new ProviderErrorDetails
        {
            StatusCode = status,
            Category = ClassifyError(status, message),
            Message = message,
            RequestId = requestId,
            RetryAfter = retryAfter,
            ErrorCode = ExtractErrorCode(message)
        };

        return true;
    }

    /// <summary>
    /// Extracts HTTP status code from Azure exception message.
    /// Azure format: "Service request failed.\nStatus: 429 (Too Many Requests)"
    /// </summary>
    private static int? ExtractStatusCodeFromMessage(string message)
    {
        // Look for pattern "Status: 429" in the message
        var statusMatch = Regex.Match(message, @"Status:\s*(\d{3})", RegexOptions.IgnoreCase);
        if (statusMatch.Success && int.TryParse(statusMatch.Groups[1].Value, out var statusCode))
        {
            return statusCode;
        }

        return null;
    }

    /// <summary>
    /// Extracts request ID from Azure exception message.
    /// Azure may include "RequestId: xxx" or similar patterns.
    /// </summary>
    private static string? ExtractRequestIdFromMessage(string message)
    {
        // Look for pattern "RequestId: xxx" or "Request-Id: xxx"
        var requestIdMatch = Regex.Match(message, @"Request[-\s]?Id:\s*([a-zA-Z0-9\-]+)", RegexOptions.IgnoreCase);
        if (requestIdMatch.Success)
        {
            return requestIdMatch.Groups[1].Value;
        }

        return null;
    }

    private static ErrorCategory ClassifyError(int? status, string message)
    {
        if (status == 400)
        {
            // Check for context window in message
            if (message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorCategory.ContextWindow;
            }
            return ErrorCategory.ClientError;
        }

        if (status == 401) return ErrorCategory.AuthError;

        if (status == 429)
        {
            // Check if it's terminal quota
            if (message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("exceeded your current quota", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorCategory.RateLimitTerminal;
            }
            return ErrorCategory.RateLimitRetryable;
        }

        if (status >= 500 && status < 600) return ErrorCategory.ServerError;

        return ErrorCategory.Unknown;
    }

    private static TimeSpan? ExtractRetryDelayFromMessage(string message)
    {
        // Parse: "Please try again in 1.898s" or "Please try again in 28ms"
        var match = RetryDelayRegex().Match(message);
        if (match.Success)
        {
            var value = double.Parse(match.Groups[1].Value);
            var unit = match.Groups[2].Value;
            return unit == "s"
                ? TimeSpan.FromSeconds(value)
                : TimeSpan.FromMilliseconds(value);
        }

        return null;
    }

    private static string? ExtractErrorCode(string message)
    {
        // Common error codes in OpenAI messages
        if (message.Contains("rate_limit_exceeded")) return "rate_limit_exceeded";
        if (message.Contains("context_length_exceeded")) return "context_length_exceeded";
        if (message.Contains("insufficient_quota")) return "insufficient_quota";

        return null;
    }

#if NET7_0_OR_GREATER
    [GeneratedRegex(@"try again in ([\d.]+)(s|ms)", RegexOptions.IgnoreCase)]
    private static partial Regex RetryDelayRegex();
#else
    private static Regex RetryDelayRegex() => _retryDelayRegex;
    private static readonly Regex _retryDelayRegex = new(@"try again in ([\d.]+)(s|ms)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
#endif
}
