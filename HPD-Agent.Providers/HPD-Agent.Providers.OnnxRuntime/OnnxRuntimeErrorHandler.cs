using System;
using HPD.Agent.ErrorHandling;

namespace HPD.Agent.Providers.OnnxRuntime;

internal class OnnxRuntimeErrorHandler : IProviderErrorHandler
{
    public ProviderErrorDetails? ParseError(Exception exception)
    {
        // ONNX errors are typically not HTTP-based, so we handle generic exceptions
        return new ProviderErrorDetails
        {
            StatusCode = null, // No HTTP status
            Category = ErrorCategory.ClientError, // Assume client error (e.g., bad model path, corrupted model)
            Message = exception.Message,
            ErrorCode = exception.GetType().Name
        };
    }

    public TimeSpan? GetRetryDelay(ProviderErrorDetails details, int attempt, TimeSpan initialDelay, double multiplier, TimeSpan maxDelay)
    {
        // No retry for local ONNX models by default
        return null;
    }

    public bool RequiresSpecialHandling(ProviderErrorDetails details)
    {
        return false;
    }
}
