using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using HPD.Agent.ErrorHandling;

namespace HPD.Agent.Providers.HuggingFace;

/// <summary>
/// Error handler for HuggingFace Inference API.
/// Handles both ApiException from the HuggingFace SDK and standard HTTP exceptions.
/// </summary>
internal partial class HuggingFaceErrorHandler : IProviderErrorHandler
{
    // Source-generated regex patterns for AOT compatibility
    [GeneratedRegex(@"model.*loading", RegexOptions.IgnoreCase)]
    private static partial Regex ModelLoadingPattern();

    [GeneratedRegex(@"rate.*limit", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitPattern();

    [GeneratedRegex(@"quota.*exceeded", RegexOptions.IgnoreCase)]
    private static partial Regex QuotaExceededPattern();

    [GeneratedRegex(@"token.*invalid|unauthorized", RegexOptions.IgnoreCase)]
    private static partial Regex AuthErrorPattern();

    public ProviderErrorDetails? ParseError(Exception exception)
    {
        // Check if this is the HuggingFace ApiException type
        var exceptionTypeName = exception.GetType().FullName;
        if (exceptionTypeName == "HuggingFace.ApiException" ||
            exceptionTypeName?.StartsWith("HuggingFace.ApiException`") == true)
        {
            return ParseApiException(exception);
        }

        // Handle standard HttpRequestException
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
        // Retry on rate limits, server errors, and transient issues (like model loading)
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

    private static ProviderErrorDetails ParseApiException(Exception exception)
    {
        // Extract status code from ApiException using duck typing (AOT-safe)
        int? statusCode = null;
        try
        {
            // Both ApiException and ApiException<T> have a StatusCode property of type HttpStatusCode
            dynamic ex = exception;
            statusCode = (int)ex.StatusCode;
        }
        catch
        {
            // If we can't get the status code, fall back to null
        }

        var message = exception.Message;

        // Try to get response body if available
        string? responseBody = null;
        string? errorCode = null;
        try
        {
            dynamic ex = exception;
            responseBody = ex.ResponseBody as string;

            // Try to extract error from ErrorResponse if available
            if (ex.ResponseObject is not null)
            {
                var responseObj = ex.ResponseObject;
                var errorProp = responseObj.GetType().GetProperty("Error");
                if (errorProp != null)
                {
                    var error = errorProp.GetValue(responseObj);
                    if (error != null)
                    {
                        errorCode = error.ToString();
                    }
                }
            }
        }
        catch
        {
            // If we can't get response details, continue with what we have
        }

        // Use response body for classification if available
        var classificationMessage = responseBody ?? message;

        return new ProviderErrorDetails
        {
            StatusCode = statusCode,
            Category = ClassifyError(statusCode, classificationMessage),
            Message = message,
            ErrorCode = errorCode
        };
    }

    private static ErrorCategory ClassifyError(int? status, string message)
    {
        return status switch
        {
            // Client errors - invalid request
            400 => ErrorCategory.ClientError, // Bad request - invalid parameters
            404 => ErrorCategory.ClientError, // Model not found
            413 => ErrorCategory.ClientError, // Payload too large

            // Authentication/Authorization errors
            401 => ErrorCategory.AuthError, // Unauthorized - invalid or missing API token
            403 => ErrorCategory.AuthError, // Forbidden - insufficient permissions or token revoked

            // Rate limiting
            429 => ErrorCategory.RateLimitRetryable, // Too many requests - rate limit hit

            // Service errors - temporary, should retry
            503 => ClassifyServiceUnavailable(message), // Could be model loading or actual service issue

            // Server errors - retryable
            >= 500 and < 600 => ErrorCategory.ServerError,

            // Unknown status code - classify by message content
            _ => ClassifyByMessage(message)
        };
    }

    private static ErrorCategory ClassifyServiceUnavailable(string message)
    {
        // 503 in HuggingFace can mean the model is still loading (transient)
        // or actual service unavailability (also transient but different cause)
        if (ModelLoadingPattern().IsMatch(message))
        {
            return ErrorCategory.Transient; // Model is loading - will be ready soon
        }

        return ErrorCategory.Transient; // General service unavailability
    }

    private static ErrorCategory ClassifyByMessage(string message)
    {
        // Additional classification based on message content
        var lowerMessage = message.ToLowerInvariant();

        // Authentication-related errors
        if (AuthErrorPattern().IsMatch(message))
        {
            return ErrorCategory.AuthError;
        }

        // Rate limiting
        if (RateLimitPattern().IsMatch(message) || QuotaExceededPattern().IsMatch(message))
        {
            return ErrorCategory.RateLimitRetryable;
        }

        // Model loading
        if (ModelLoadingPattern().IsMatch(message))
        {
            return ErrorCategory.Transient;
        }

        // Transient errors
        if (lowerMessage.Contains("timeout") ||
            lowerMessage.Contains("temporary") ||
            lowerMessage.Contains("unavailable") ||
            lowerMessage.Contains("try again") ||
            lowerMessage.Contains("connection") ||
            lowerMessage.Contains("network"))
        {
            return ErrorCategory.Transient;
        }

        // Client errors
        if (lowerMessage.Contains("invalid") ||
            lowerMessage.Contains("bad request") ||
            lowerMessage.Contains("malformed") ||
            lowerMessage.Contains("not found"))
        {
            return ErrorCategory.ClientError;
        }

        return ErrorCategory.Unknown;
    }
}
