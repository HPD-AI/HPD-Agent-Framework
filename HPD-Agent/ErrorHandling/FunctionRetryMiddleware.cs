using HPD.Agent.ErrorHandling;
using HPD.Agent;

namespace HPD.Agent.Middleware.Function;

/// <summary>
/// Middleware that provides provider-aware retry logic for function execution.
/// Automatically retries transient failures using intelligent backoff strategies.
/// </summary>
/// <remarks>
/// <para><b>Retry Logic:</b></para>
/// <para>Uses a 3-tier priority system:</para>
/// <list type="number">
/// <item><b>Priority 1: Custom Strategy</b> - User-provided retry logic (if configured)</item>
/// <item><b>Priority 2: Provider-Aware</b> - Parse error with IProviderErrorHandler, respect Retry-After headers</item>
/// <item><b>Priority 3: Exponential Backoff</b> - Fallback strategy with jitter</item>
/// </list>
///
/// <para><b>Features:</b></para>
/// <list type="bullet">
/// <item>Respects provider Retry-After headers (e.g., OpenAI 429 responses)</item>
/// <item>Per-error-category retry limits (e.g., more retries for rate limits, fewer for server errors)</item>
/// <item>Smart error classification (transient, rate limit, client error, etc.)</item>
/// <item>Exponential backoff with jitter to avoid thundering herd</item>
/// <item>Emits retry events for observability</item>
/// </list>
/// </remarks>
public class FunctionRetryMiddleware : IAgentMiddleware
{
    private readonly ErrorHandlingConfig _config;
    private readonly IProviderErrorHandler _providerHandler;

    /// <summary>
    /// Creates a new function retry middleware with the specified configuration and error handler.
    /// </summary>
    /// <param name="config">Error handling configuration</param>
    /// <param name="providerErrorHandler">Provider-specific error handler for intelligent retry logic</param>
    public FunctionRetryMiddleware(ErrorHandlingConfig config, IProviderErrorHandler? providerErrorHandler = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _providerHandler = providerErrorHandler ?? new GenericErrorHandler();
    }

    /// <summary>
    /// Executes the function with provider-aware retry logic.
    /// </summary>
    public async ValueTask<object?> ExecuteFunctionAsync(
        AgentMiddlewareContext context,
        Func<ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var maxRetries = _config.MaxRetries;
        Exception? lastException = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Call next middleware (or actual function)
                return await next();
            }
            catch (Exception ex)
            {
                lastException = ex;

                // Check if we should retry
                var delay = await CalculateRetryDelayAsync(ex, attempt, cancellationToken);

                if (!delay.HasValue || attempt >= maxRetries)
                {
                    // Non-retryable or exhausted retries
                    throw;
                }

                // Emit retry event for observability (if coordinator available)
                if (context.EventCoordinator != null)
                {
                    context.Emit(new FunctionRetryEvent(
                        FunctionName: context.Function?.Name ?? "Unknown",
                        Attempt: attempt + 1,
                        MaxRetries: maxRetries,
                        Delay: delay.Value,
                        Exception: ex,
                        ExceptionType: ex.GetType().Name,
                        ErrorMessage: ex.Message));
                }

                // Wait before retry
                await Task.Delay(delay.Value, cancellationToken);
            }
        }

        throw lastException!;
    }

    /// <summary>
    /// Calculates retry delay using 3-tier priority system.
    /// </summary>
    private async Task<TimeSpan?> CalculateRetryDelayAsync(
        Exception ex,
        int attempt,
        CancellationToken cancellationToken)
    {
        //     
        // PRIORITY 1: Custom Retry Strategy (user-provided)
        //     
        if (_config.CustomRetryStrategy != null)
        {
            var customDelay = await _config.CustomRetryStrategy(ex, attempt, cancellationToken);
            // Custom strategy result is authoritative - don't fall through
            return customDelay;
        }

        //     
        // PRIORITY 2: Provider-Aware Handling
        //     
        var errorDetails = _providerHandler.ParseError(ex);
        if (errorDetails != null)
        {
            // Check per-category retry limits
            if (_config.MaxRetriesByCategory != null &&
                _config.MaxRetriesByCategory.TryGetValue(errorDetails.Category, out var categoryMax))
            {
                if (attempt >= categoryMax)
                    return null; // Exceeded category-specific limit
            }

            // Get provider-calculated delay (respects Retry-After headers)
            var providerDelay = _providerHandler.GetRetryDelay(
                errorDetails,
                attempt,
                _config.RetryDelay,
                _config.BackoffMultiplier,
                _config.MaxRetryDelay);

            // Provider result is authoritative - don't fall through
            return providerDelay;
        }

        //     
        // PRIORITY 3: Exponential Backoff (fallback - only if no provider)
        //     
        return CalculateExponentialBackoff(attempt);
    }

    /// <summary>
    /// Calculates exponential backoff with jitter.
    /// </summary>
    private TimeSpan CalculateExponentialBackoff(int attempt)
    {
        // Use defaults if config values are not set
        var baseDelay = _config.RetryDelay;
        var backoffMultiplier = _config.BackoffMultiplier;
        var maxDelay = _config.MaxRetryDelay;

        var baseMs = baseDelay.TotalMilliseconds;
        var expDelayMs = baseMs * Math.Pow(backoffMultiplier, attempt);
        var cappedDelayMs = Math.Min(expDelayMs, maxDelay.TotalMilliseconds);

        // Add jitter (0.9x to 1.1x) to avoid thundering herd
        var jitter = 0.9 + (Random.Shared.NextDouble() * 0.2);

        return TimeSpan.FromMilliseconds(cappedDelayMs * jitter);
    }
}
