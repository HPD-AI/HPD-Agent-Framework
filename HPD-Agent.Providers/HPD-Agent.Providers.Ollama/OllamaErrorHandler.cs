using System;
using System.Net.Http;
using HPD.Agent.ErrorHandling;

namespace HPD.Agent.Providers.Ollama;

internal class OllamaErrorHandler : IProviderErrorHandler
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
                ErrorCode = null // Ollama errors are typically in the response body, not headers
            };
        }
        return null;
    }

    public TimeSpan? GetRetryDelay(ProviderErrorDetails details, int attempt, TimeSpan initialDelay, double multiplier, TimeSpan maxDelay)
    {
        // Ollama is typically local, so we only retry on transient/server errors, not rate limits.
        if (details.Category is ErrorCategory.ServerError or ErrorCategory.Transient)
        {
            var baseMs = initialDelay.TotalMilliseconds;
            var expDelayMs = baseMs * Math.Pow(multiplier, attempt);
            return TimeSpan.FromMilliseconds(Math.Min(expDelayMs, maxDelay.TotalMilliseconds));
        }
        return null;
    }

    public bool RequiresSpecialHandling(ProviderErrorDetails details)
    {
        return false; // No special handling like auth refresh needed for local Ollama
    }

    private static ErrorCategory ClassifyError(int? status, string message)
    {
        if (status == 400) return ErrorCategory.ClientError;
        if (status == 404) return ErrorCategory.ClientError; // Model not found
        if (status == 503) return ErrorCategory.Transient; // Model is loading
        if (status >= 500 && status < 600) return ErrorCategory.ServerError;
        return ErrorCategory.Unknown;
    }
}
