namespace HPD.Agent.ErrorHandling;

/// <summary>
/// Generic error handler used when no provider-specific handler is available.
/// Provides basic HTTP status code classification.
/// </summary>
internal class GenericErrorHandler : IProviderErrorHandler
{
    public ProviderErrorDetails? ParseError(Exception exception)
    {
        // Try to extract HTTP status from common exception types
        int? statusCode = ExtractStatusCode(exception);

        if (statusCode == null)
        {
            return new ProviderErrorDetails
            {
                Category = ErrorCategory.Unknown,
                Message = exception.Message
            };
        }

        return new ProviderErrorDetails
        {
            StatusCode = statusCode,
            Category = ClassifyStatusCode(statusCode.Value),
            Message = exception.Message
        };
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

        // Exponential backoff with jitter
        var baseMs = initialDelay.TotalMilliseconds;
        var expDelayMs = baseMs * Math.Pow(multiplier, attempt);
        var cappedDelayMs = Math.Min(expDelayMs, maxDelay.TotalMilliseconds);
        var jitter = 0.9 + (Random.Shared.NextDouble() * 0.2); // ±10%

        return TimeSpan.FromMilliseconds(cappedDelayMs * jitter);
    }

    public bool RequiresSpecialHandling(ProviderErrorDetails details)
    {
        return details.Category == ErrorCategory.AuthError;
    }

    private static int? ExtractStatusCode(Exception exception)
    {
        // HttpRequestException (standard)
        if (exception is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            return (int)httpEx.StatusCode.Value;
        }

        // AOT-compatible: Try to parse status code from exception message
        // Many HTTP exceptions include status codes in their messages like "Status: 429" or "(429)"
        var message = exception.Message;

        // Look for "Status: 429" pattern
        var statusMatch = System.Text.RegularExpressions.Regex.Match(
            message,
            @"Status:\s*(\d{3})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (statusMatch.Success && int.TryParse(statusMatch.Groups[1].Value, out var statusCode))
        {
            return statusCode;
        }

        // Look for "(429)" or "429" pattern in parentheses
        var parenMatch = System.Text.RegularExpressions.Regex.Match(
            message,
            @"\((\d{3})\)");

        if (parenMatch.Success && int.TryParse(parenMatch.Groups[1].Value, out var parenStatusCode))
        {
            return parenStatusCode;
        }

        return null;
    }

    private static ErrorCategory ClassifyStatusCode(int status)
    {
        return status switch
        {
            400 => ErrorCategory.ClientError,
            401 => ErrorCategory.AuthError,
            429 => ErrorCategory.RateLimitRetryable,
            >= 500 and < 600 => ErrorCategory.ServerError,
            _ => ErrorCategory.Unknown
        };
    }
}
