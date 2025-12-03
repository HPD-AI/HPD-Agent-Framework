using System;
using System.Text.RegularExpressions;
using HPD.Agent.ErrorHandling;

namespace HPD.Agent.Providers.AzureAIInference;

internal partial class AzureAIInferenceErrorHandler : IProviderErrorHandler
{
    public ProviderErrorDetails? ParseError(Exception exception)
    {
        // Azure AI Inference uses Azure.RequestFailedException
        var exceptionTypeName = exception.GetType().FullName;
        if (exceptionTypeName != "Azure.RequestFailedException")
        {
            return null;
        }

        var message = exception.Message;
        int? status = ExtractStatusCodeFromMessage(message);

        return new ProviderErrorDetails
        {
            StatusCode = status,
            Category = ClassifyError(status, message),
            Message = message,
            ErrorCode = ExtractErrorCode(message)
        };
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

    private static int? ExtractStatusCodeFromMessage(string message)
    {
        var statusMatch = Regex.Match(message, @"Status:\s*(\d{3})", RegexOptions.IgnoreCase);
        if (statusMatch.Success && int.TryParse(statusMatch.Groups[1].Value, out var statusCode))
        {
            return statusCode;
        }
        return null;
    }

    private static string? ExtractErrorCode(string message)
    {
        // Look for a 'code' field in a JSON-like structure in the message
        var codeMatch = Regex.Match(message, @"""code"":\s*""([^""]+)""");
        if (codeMatch.Success)
        {
            return codeMatch.Groups[1].Value;
        }
        return null;
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
