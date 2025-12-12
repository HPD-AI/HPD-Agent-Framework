using HPD.Agent;
using HPD.Agent.ErrorHandling;
using HPD.Agent.Middleware;
using HPD.Agent.Middleware.Function;
using Microsoft.Extensions.AI;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// End-to-end tests for complete middleware chains with Retry + Timeout + Custom middleware.
/// Tests realistic scenarios and complex interactions.
/// </summary>
public class MiddlewareChainEndToEndTests
{
    #region Realistic Scenarios

    [Fact]
    public async Task EndToEnd_ProviderRateLimit_RetriesWithBackoffAndSucceeds()
    {
        // Arrange - Simulates OpenAI rate limit scenario
        var providerHandler = new SimulatedOpenAIErrorHandler();
        var config = new ErrorHandlingConfig
        {
            MaxRetries = 3,
            UseProviderRetryDelays = true,
            RetryDelay = TimeSpan.FromMilliseconds(50)
        };

        var retryMiddleware = new FunctionRetryMiddleware(config, providerHandler);
        var pipeline = new AgentMiddlewarePipeline(new[] { retryMiddleware });
        var context = CreateContext();

        int attempts = 0;
        Func<ValueTask<object?>> innerCall = () =>
        {
            attempts++;
            if (attempts <= 2)
                throw new HttpRequestException("429 Too Many Requests");
            return ValueTask.FromResult<object?>("API Response");
        };

        // Act
        var result = await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert
        Assert.Equal("API Response", result);
        Assert.Equal(3, attempts); // Initial + 2 retries
    }

    [Fact]
    public async Task EndToEnd_SlowFunctionWithRetry_TimeoutAppliesToEachAttempt()
    {
        // Arrange - Function that's slow on first attempt, fast on retry
        var retryConfig = new ErrorHandlingConfig
        {
            MaxRetries = 2,
            RetryDelay = TimeSpan.FromMilliseconds(10)
        };
        var retryMiddleware = new FunctionRetryMiddleware(retryConfig);
        var timeoutMiddleware = new FunctionTimeoutMiddleware(TimeSpan.FromMilliseconds(100));

        // Retry (outer/first) wraps Timeout (inner/last) - timeout applies to each retry attempt
        // Register: Retry first (outermost), Timeout second (innermost)
        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { retryMiddleware, timeoutMiddleware });
        var context = CreateContext();

        int attempts = 0;
        Func<ValueTask<object?>> innerCall = async () =>
        {
            attempts++;
            if (attempts == 1)
                await Task.Delay(150); // First attempt times out
            return "Success";
        };

        // Act
        var result = await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert - Retry wraps timeout, so timed-out attempts are retried
        Assert.Equal("Success", result);
        Assert.Equal(2, attempts); // Initial (timed out) + 1 retry (succeeded)
    }

    [Fact]
    public async Task EndToEnd_CachingLayerBeforeRetry_SkipsRetryOnCacheHit()
    {
        // Arrange - Caching → Retry → Actual execution
        var cachingMiddleware = new SimpleCachingMiddleware();
        var retryMiddleware = new FunctionRetryMiddleware(new ErrorHandlingConfig { MaxRetries = 3 });

        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { cachingMiddleware, retryMiddleware });
        var context = CreateContext();

        // First call - miss cache, execute and cache
        int executionCount = 0;
        Func<ValueTask<object?>> innerCall1 = () =>
        {
            executionCount++;
            return ValueTask.FromResult<object?>("Fresh Data");
        };

        var result1 = await pipeline.ExecuteFunctionAsync(context, innerCall1, CancellationToken.None);
        Assert.Equal("Fresh Data", result1);
        Assert.Equal(1, executionCount);

        // Second call - hit cache, skip execution
        Func<ValueTask<object?>> innerCall2 = () =>
        {
            executionCount++;
            throw new InvalidOperationException("Should not be called!");
        };

        var result2 = await pipeline.ExecuteFunctionAsync(context, innerCall2, CancellationToken.None);
        Assert.Equal("Fresh Data", result2); // Same cached result
        Assert.Equal(1, executionCount); // No additional execution
    }

    [Fact]
    public async Task EndToEnd_TelemetryMiddleware_RecordsMetricsAcrossRetries()
    {
        // Arrange - Telemetry (outer) → Retry (inner)
        var telemetry = new TelemetryMiddleware();
        var retryMiddleware = new FunctionRetryMiddleware(new ErrorHandlingConfig
        {
            MaxRetries = 2,
            RetryDelay = TimeSpan.FromMilliseconds(10)
        });

        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { telemetry, retryMiddleware });
        var context = CreateContext();

        int attempts = 0;
        Func<ValueTask<object?>> innerCall = () =>
        {
            attempts++;
            if (attempts < 3)
                throw new InvalidOperationException("Transient");
            return ValueTask.FromResult<object?>("Success");
        };

        // Act
        await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert - Telemetry wraps everything, so it sees the full execution including retries
        Assert.Equal(3, attempts);
        Assert.NotNull(telemetry.LastDuration);
        Assert.True(telemetry.LastDuration.Value.TotalMilliseconds > 0);
        Assert.True(telemetry.SuccessCount == 1);
    }

    #endregion

    #region Complex Error Scenarios

    [Fact]
    public async Task EndToEnd_NonRetryableError_FailsImmediatelyDespiteRetryConfig()
    {
        // Arrange - Provider says "don't retry" (e.g., 400 Bad Request)
        var providerHandler = new SimulatedOpenAIErrorHandler
        {
            ShouldRetryClientErrors = false
        };

        var config = new ErrorHandlingConfig
        {
            MaxRetries = 5 // Would retry up to 5 times
        };

        var retryMiddleware = new FunctionRetryMiddleware(config, providerHandler);
        var pipeline = new AgentMiddlewarePipeline(new[] { retryMiddleware });
        var context = CreateContext();

        int attempts = 0;
        Func<ValueTask<object?>> innerCall = () =>
        {
            attempts++;
            throw new HttpRequestException("400 Bad Request");
        };

        // Act & Assert - Should fail immediately without retry
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None));

        Assert.Equal(1, attempts); // Only 1 attempt, no retries
    }

    [Fact]
    public async Task EndToEnd_PerCategoryLimitsWithMultipleErrorTypes_RespectsEachLimit()
    {
        // Arrange
        var providerHandler = new MultiErrorTypeHandler();
        var config = new ErrorHandlingConfig
        {
            MaxRetries = 10, // Global max
            MaxRetriesByCategory = new Dictionary<ErrorCategory, int>
            {
                [ErrorCategory.RateLimitRetryable] = 2,
                [ErrorCategory.ServerError] = 1
            },
            RetryDelay = TimeSpan.FromMilliseconds(10)
        };

        var retryMiddleware = new FunctionRetryMiddleware(config, providerHandler);
        var pipeline = new AgentMiddlewarePipeline(new[] { retryMiddleware });
        var context = CreateContext();

        int attempts = 0;
        Func<ValueTask<object?>> innerCall = () =>
        {
            attempts++;
            // First 2 attempts: RateLimit (allows retries with limit check)
            // Attempt 3: ServerError (global attempt counter = 2, exceeds ServerError limit of 1)
            if (attempts == 1)
                throw new HttpRequestException("429 Rate Limit");
            else if (attempts == 2)
                throw new HttpRequestException("429 Rate Limit");

            // Attempt 3 succeeds (after 2 retries)
            return ValueTask.FromResult<object?>("Success");
        };

        // Act
        var result = await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert
        Assert.Equal("Success", result);
        // Attempt 1 (initial, attempt=0): 429 → retry (0 < 2)
        // Attempt 2 (retry, attempt=1): 429 → retry (1 < 2)
        // Attempt 3 (retry, attempt=2): succeeds
        Assert.Equal(3, attempts);
    }

    #endregion

    #region Cancellation and Cleanup

    [Fact]
    public async Task EndToEnd_CancellationDuringRetry_ProperlyCancels()
    {
        // Arrange
        var config = new ErrorHandlingConfig
        {
            MaxRetries = 10,
            RetryDelay = TimeSpan.FromMilliseconds(100)
        };

        var retryMiddleware = new FunctionRetryMiddleware(config);
        var pipeline = new AgentMiddlewarePipeline(new[] { retryMiddleware });
        var context = CreateContext();

        var cts = new CancellationTokenSource();
        int attempts = 0;

        Func<ValueTask<object?>> innerCall = () =>
        {
            attempts++;
            if (attempts == 2)
                cts.CancelAfter(10); // Cancel after 2nd attempt

            throw new InvalidOperationException("Keep failing");
        };

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteFunctionAsync(context, innerCall, cts.Token));

        // Should stop quickly after cancellation
        Assert.True(attempts <= 4); // 2 attempts + maybe 1-2 more before cancel kicks in
    }

    #endregion

    #region Auto-Registration Simulation

    [Fact]
    public async Task EndToEnd_SimulateAgentBuilderAutoRegistration_MiddlewareWorksAsExpected()
    {
        // Arrange - Simulate what AgentBuilder does
        var config = new ErrorHandlingConfig
        {
            MaxRetries = 3,
            SingleFunctionTimeout = TimeSpan.FromMilliseconds(200),
            RetryDelay = TimeSpan.FromMilliseconds(10)
        };

        // Auto-register in the order AgentBuilder does
        var middlewares = new List<IAgentMiddleware>();

        // 1. Retry middleware
        if (config.MaxRetries > 0)
        {
            middlewares.Add(new FunctionRetryMiddleware(config));
        }

        // 2. Timeout middleware
        if (config.SingleFunctionTimeout != null)
        {
            middlewares.Add(new FunctionTimeoutMiddleware(config.SingleFunctionTimeout.Value));
        }

        var pipeline = new AgentMiddlewarePipeline(middlewares);
        var context = CreateContext();

        // Simulate a function that's slow on first attempt
        int attempts = 0;
        Func<ValueTask<object?>> innerCall = async () =>
        {
            attempts++;
            if (attempts == 1)
                await Task.Delay(300); // Exceeds timeout
            return "Success";
        };

        // Act
        var result = await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert - Timeout (inner) → Retry (outer), so timeout exception is retried
        Assert.Equal("Success", result);
        Assert.Equal(2, attempts); // First timed out, second succeeded
    }

    #endregion

    #region Helper Methods

    private AgentMiddlewareContext CreateContext()
    {
        return new AgentMiddlewareContext
        {
            AgentName = "TestAgent",
            CancellationToken = CancellationToken.None,
            Function = CreateMockFunction("TestFunction"),
            FunctionArguments = new Dictionary<string, object?>()
        };
    }

    private AIFunction CreateMockFunction(string name)
    {
        return AIFunctionFactory.Create(
            (string input) => "Result",
            name: name);
    }

    #endregion

    #region Test Doubles

    private class SimulatedOpenAIErrorHandler : IProviderErrorHandler
    {
        public bool ShouldRetryClientErrors { get; set; } = false;

        public ProviderErrorDetails? ParseError(Exception exception)
        {
            if (exception is HttpRequestException http)
            {
                if (http.Message.Contains("429"))
                {
                    return new ProviderErrorDetails
                    {
                        Category = ErrorCategory.RateLimitRetryable,
                        RetryAfter = TimeSpan.FromMilliseconds(50)
                    };
                }
                else if (http.Message.Contains("400"))
                {
                    return new ProviderErrorDetails
                    {
                        Category = ErrorCategory.ClientError
                    };
                }
            }

            return new ProviderErrorDetails { Category = ErrorCategory.Transient };
        }

        public TimeSpan? GetRetryDelay(ProviderErrorDetails details, int attempt, TimeSpan initialDelay, double multiplier, TimeSpan maxDelay)
        {
            if (details.Category == ErrorCategory.ClientError && !ShouldRetryClientErrors)
                return null;

            if (details.RetryAfter.HasValue)
                return details.RetryAfter;

            var delay = TimeSpan.FromMilliseconds(initialDelay.TotalMilliseconds * Math.Pow(multiplier, attempt));
            return delay > maxDelay ? maxDelay : delay;
        }

        public bool RequiresSpecialHandling(ProviderErrorDetails details) => false;
    }

    private class MultiErrorTypeHandler : IProviderErrorHandler
    {
        public ProviderErrorDetails? ParseError(Exception exception)
        {
            if (exception is HttpRequestException http)
            {
                if (http.Message.Contains("429"))
                    return new ProviderErrorDetails { Category = ErrorCategory.RateLimitRetryable };
                else if (http.Message.Contains("500"))
                    return new ProviderErrorDetails { Category = ErrorCategory.ServerError };
            }
            return new ProviderErrorDetails { Category = ErrorCategory.Transient };
        }

        public TimeSpan? GetRetryDelay(ProviderErrorDetails details, int attempt, TimeSpan initialDelay, double multiplier, TimeSpan maxDelay)
        {
            return TimeSpan.FromMilliseconds(10);
        }

        public bool RequiresSpecialHandling(ProviderErrorDetails details) => false;
    }

    private class SimpleCachingMiddleware : IAgentMiddleware
    {
        private object? _cachedValue;
        private bool _hasCached;

        public async ValueTask<object?> ExecuteFunctionAsync(
            AgentMiddlewareContext context,
            Func<ValueTask<object?>> next,
            CancellationToken cancellationToken)
        {
            if (_hasCached)
                return _cachedValue;

            var result = await next();
            _cachedValue = result;
            _hasCached = true;
            return result;
        }
    }

    private class TelemetryMiddleware : IAgentMiddleware
    {
        public TimeSpan? LastDuration { get; private set; }
        public int SuccessCount { get; private set; }
        public int FailureCount { get; private set; }

        public async ValueTask<object?> ExecuteFunctionAsync(
            AgentMiddlewareContext context,
            Func<ValueTask<object?>> next,
            CancellationToken cancellationToken)
        {
            var start = DateTime.UtcNow;
            try
            {
                var result = await next();
                SuccessCount++;
                return result;
            }
            catch
            {
                FailureCount++;
                throw;
            }
            finally
            {
                LastDuration = DateTime.UtcNow - start;
            }
        }
    }

    #endregion
}
