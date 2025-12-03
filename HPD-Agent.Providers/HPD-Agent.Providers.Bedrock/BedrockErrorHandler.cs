using System;
using System.Net.Http;
using HPD.Agent.ErrorHandling;

namespace HPD.Agent.Providers.Bedrock;

internal class BedrockErrorHandler : IProviderErrorHandler
{
    public ProviderErrorDetails? ParseError(Exception exception)
    {
        // AWS SDK exceptions often have StatusCode property
        dynamic? awsEx = exception;
        try
        {
            int? statusCode = (int?)awsEx.StatusCode;
            return new ProviderErrorDetails
            {
                StatusCode = statusCode,
                Category = ClassifyError(statusCode, exception.Message),
                Message = exception.Message,
                ErrorCode = awsEx.ErrorCode
            };
        }
        catch
        {
            // Fallback for non-AWS exceptions
            if (exception is HttpRequestException httpEx)
            {
                return new ProviderErrorDetails
                {
                    StatusCode = (int?)httpEx.StatusCode,
                    Category = ClassifyError((int?)httpEx.StatusCode, httpEx.Message),
                    Message = httpEx.Message
                };
            }
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
        if (status == 403) return ErrorCategory.AuthError;
        if (status == 429) return ErrorCategory.RateLimitRetryable; // ThrottlingException
        if (status >= 500 && status < 600) return ErrorCategory.ServerError;
        return ErrorCategory.Unknown;
    }
}
