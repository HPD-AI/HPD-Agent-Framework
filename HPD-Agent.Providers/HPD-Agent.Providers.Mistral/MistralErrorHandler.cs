using System;
using System.Net.Http;
using HPD.Agent.ErrorHandling;

namespace HPD.Agent.Providers.Mistral;

internal class MistralErrorHandler : IProviderErrorHandler
{
    public ProviderErrorDetails? ParseError(Exception exception)
    {
        if (exception is HttpRequestException httpEx)
        {
            var statusCode = (int?)httpEx.StatusCode;
            return new ProviderErrorDetails
            {
                StatusCode = statusCode,
                Category = ClassifyError(statusCode, httpEx.Message),
                Message = httpEx.Message,
                ErrorCode = null
            };
        }
        return null;
    }

    public TimeSpan? GetRetryDelay(ProviderErrorDetails details, int attempt, TimeSpan initialDelay, double multiplier, TimeSpan maxDelay)
    {
        if (details.Category is ErrorCategory.RateLimitRetryable or ErrorCategory.ServerError or ErrorCategory.Transient)
        {
            var baseMs = initialDelay.TotalMilliseconds;
            var expDelayMs = baseMs * Math.Pow(multiplier, attempt);
            return TimeSpan.FromMilliseconds(Math.Min(expDelayMs, maxDelay.TotalMilliseconds));
        }
        return null;
    }

    public bool RequiresSpecialHandling(ProviderErrorDetails details)
    {
        return details.Category == ErrorCategory.AuthError;
    }

    private static ErrorCategory ClassifyError(int? status, string message)
    {
        if (status == 400) return ErrorCategory.ClientError;
        if (status == 401 || status == 403) return ErrorCategory.AuthError;
        if (status == 429) return ErrorCategory.RateLimitRetryable;
        if (status >= 500 && status < 600) return ErrorCategory.ServerError;
        return ErrorCategory.Unknown;
    }
}
