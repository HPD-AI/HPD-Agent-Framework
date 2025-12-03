using HPD.Agent;
using HPD.Agent.ErrorHandling;
using HPD.Agent.Middleware;
using HPD.Agent.Middleware.Function;
using Microsoft.Extensions.AI;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// Integration tests for ExecuteFunctionAsync pipeline in AgentMiddlewarePipeline.
/// Tests the onion architecture and middleware chaining behavior.
/// </summary>
public class ExecuteFunctionPipelineTests
{
    #region Pipeline Execution Order

    [Fact]
    public async Task ExecuteFunctionAsync_NoMiddleware_ExecutesDirectly()
    {
        // Arrange
        var pipeline = new AgentMiddlewarePipeline(Array.Empty<IAgentMiddleware>());
        var context = CreateContext();

        bool innerCalled = false;
        Func<ValueTask<object?>> innerCall = () =>
        {
            innerCalled = true;
            return ValueTask.FromResult<object?>("Result");
        };

        // Act
        var result = await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert
        Assert.True(innerCalled);
        Assert.Equal("Result", result);
    }

    [Fact]
    public async Task ExecuteFunctionAsync_SingleMiddleware_CallsMiddleware()
    {
        // Arrange
        var trackingMiddleware = new TrackingMiddleware("M1");
        var pipeline = new AgentMiddlewarePipeline(new[] { trackingMiddleware });
        var context = CreateContext();

        Func<ValueTask<object?>> innerCall = () => ValueTask.FromResult<object?>("Result");

        // Act
        var result = await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert
        Assert.Equal("Result", result);
        Assert.Equal(2, trackingMiddleware.ExecutionOrder.Count); // Before and After
        Assert.Equal("M1:Before", trackingMiddleware.ExecutionOrder[0]);
        Assert.Equal("M1:After", trackingMiddleware.ExecutionOrder[1]);
    }

    [Fact]
    public async Task ExecuteFunctionAsync_MultipleMiddleware_ExecutesInRegistrationOrder()
    {
        // Arrange - Register in order: M1, M2, M3
        var m1 = new TrackingMiddleware("M1");
        var m2 = new TrackingMiddleware("M2");
        var m3 = new TrackingMiddleware("M3");
        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { m1, m2, m3 });
        var context = CreateContext();

        var sharedOrder = new List<string>();
        m1.SharedExecutionOrder = sharedOrder;
        m2.SharedExecutionOrder = sharedOrder;
        m3.SharedExecutionOrder = sharedOrder;

        Func<ValueTask<object?>> innerCall = () =>
        {
            sharedOrder.Add("Inner");
            return ValueTask.FromResult<object?>("Result");
        };

        // Act
        await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert - Execution order: M1 (first/outermost) → M2 → M3 (last/innermost) → Inner → M3:After → M2:After → M1:After (onion architecture)
        Assert.Equal(new[] { "M1:Before", "M2:Before", "M3:Before", "Inner", "M3:After", "M2:After", "M1:After" }, sharedOrder);
    }

    #endregion

    #region Retry + Timeout Integration

    [Fact]
    public async Task ExecuteFunctionAsync_TimeoutWrapsRetry_TimeoutAppliesToSingleAttempt()
    {
        // Arrange - Timeout (outer) → Retry (inner)
        // Want timeout to wrap the ENTIRE retry operation
        var retryConfig = new ErrorHandlingConfig
        {
            MaxRetries = 3,
            RetryDelay = TimeSpan.FromMilliseconds(10)
        };
        var retryMiddleware = new FunctionRetryMiddleware(retryConfig);
        var timeoutMiddleware = new FunctionTimeoutMiddleware(TimeSpan.FromMilliseconds(100));

        // Register: Timeout first, Retry second (first registered = outermost)
        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { timeoutMiddleware, retryMiddleware });
        var context = CreateContext();

        int attempts = 0;
        Func<ValueTask<object?>> innerCall = async () =>
        {
            attempts++;
            await Task.Delay(150); // Exceeds timeout
            return "Success";
        };

        // Act & Assert - Should timeout on first attempt (before retry logic kicks in)
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None));

        Assert.Equal(1, attempts); // Only one attempt (timed out before retry)
    }

    [Fact]
    public async Task ExecuteFunctionAsync_RetryWrapsTimeout_RetriesTimeoutedAttempts()
    {
        // Arrange - Retry (outer) → Timeout (inner)
        // Want retry to wrap individual timeout attempts
        var retryConfig = new ErrorHandlingConfig
        {
            MaxRetries = 2,
            RetryDelay = TimeSpan.FromMilliseconds(10)
        };
        var retryMiddleware = new FunctionRetryMiddleware(retryConfig);
        var timeoutMiddleware = new FunctionTimeoutMiddleware(TimeSpan.FromMilliseconds(100));

        // Register: Retry first, Timeout second (first registered = outermost)
        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { retryMiddleware, timeoutMiddleware });
        var context = CreateContext();

        int attempts = 0;
        Func<ValueTask<object?>> innerCall = async () =>
        {
            attempts++;
            if (attempts < 3)
                await Task.Delay(150); // First 2 attempts timeout
            return "Success";
        };

        // Act
        var result = await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert - Retry wraps timeout, so it retries timeout exceptions
        Assert.Equal("Success", result);
        Assert.Equal(3, attempts); // Initial + 2 retries (all timed out except last)
    }

    #endregion

    #region Custom Middleware Integration

    [Fact]
    public async Task ExecuteFunctionAsync_CustomMiddleware_CanTransformResult()
    {
        // Arrange
        var transformMiddleware = new ResultTransformMiddleware(result => $"Transformed: {result}");
        var pipeline = new AgentMiddlewarePipeline(new[] { transformMiddleware });
        var context = CreateContext();

        Func<ValueTask<object?>> innerCall = () => ValueTask.FromResult<object?>("Original");

        // Act
        var result = await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert
        Assert.Equal("Transformed: Original", result);
    }

    [Fact]
    public async Task ExecuteFunctionAsync_CustomMiddleware_CanSkipExecution()
    {
        // Arrange
        var cachingMiddleware = new CachingMiddleware(cachedValue: "Cached");
        var pipeline = new AgentMiddlewarePipeline(new[] { cachingMiddleware });
        var context = CreateContext();

        bool innerCalled = false;
        Func<ValueTask<object?>> innerCall = () =>
        {
            innerCalled = true;
            return ValueTask.FromResult<object?>("Fresh");
        };

        // Act
        var result = await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert - Should return cached value without calling inner
        Assert.Equal("Cached", result);
        Assert.False(innerCalled);
    }

    [Fact]
    public async Task ExecuteFunctionAsync_MultipleCustomMiddleware_ChainsProperly()
    {
        // Arrange - Transform (outer/first) → Caching (inner/last)
        var cachingMiddleware = new CachingMiddleware(cachedValue: "Cached");
        var transformMiddleware = new ResultTransformMiddleware(result => $"Transformed: {result}");

        // Register: Transform first (outermost), Caching second (innermost)
        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { transformMiddleware, cachingMiddleware });
        var context = CreateContext();

        Func<ValueTask<object?>> innerCall = () => ValueTask.FromResult<object?>("Fresh");

        // Act
        var result = await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert - Transform wraps Caching, so it transforms the cached value
        Assert.Equal("Transformed: Cached", result);
    }

    #endregion

    #region Scope Filtering

    [Fact]
    public async Task ExecuteFunctionAsync_ScopedMiddleware_OnlyExecutesWhenScopeMatches()
    {
        // Arrange
        var globalMiddleware = new TrackingMiddleware("Global");
        var scopedMiddleware = new ScopedTrackingMiddleware("Scoped", scope: MiddlewareScope.Function);

        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { globalMiddleware, scopedMiddleware });
        var context = CreateContext();

        Func<ValueTask<object?>> innerCall = () => ValueTask.FromResult<object?>("Result");

        // Act
        await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None);

        // Assert
        Assert.Equal(2, globalMiddleware.ExecutionOrder.Count); // Global executes (Before and After)
        Assert.Equal(2, scopedMiddleware.ExecutionOrder.Count); // Scoped executes (function scope, Before and After)
    }

    #endregion

    #region Error Propagation

    [Fact]
    public async Task ExecuteFunctionAsync_MiddlewareThrows_PropagatesException()
    {
        // Arrange
        var throwingMiddleware = new ThrowingMiddleware();
        var pipeline = new AgentMiddlewarePipeline(new[] { throwingMiddleware });
        var context = CreateContext();

        Func<ValueTask<object?>> innerCall = () => ValueTask.FromResult<object?>("Result");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteFunctionAsync_InnerThrows_PropagatesThroughMiddleware()
    {
        // Arrange
        var trackingMiddleware = new TrackingMiddleware("M1");
        var pipeline = new AgentMiddlewarePipeline(new[] { trackingMiddleware });
        var context = CreateContext();

        Func<ValueTask<object?>> innerCall = () => throw new InvalidOperationException("Inner error");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteFunctionAsync(context, innerCall, CancellationToken.None));

        // Middleware was called before error
        Assert.Single(trackingMiddleware.ExecutionOrder);
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

    #region Test Middleware

    private class TrackingMiddleware : IAgentMiddleware
    {
        private readonly string _name;
        public List<string> ExecutionOrder { get; } = new();
        public List<string>? SharedExecutionOrder { get; set; }

        public TrackingMiddleware(string name)
        {
            _name = name;
        }

        public async ValueTask<object?> ExecuteFunctionAsync(
            AgentMiddlewareContext context,
            Func<ValueTask<object?>> next,
            CancellationToken cancellationToken)
        {
            ExecutionOrder.Add($"{_name}:Before");
            SharedExecutionOrder?.Add($"{_name}:Before");

            var result = await next();

            ExecutionOrder.Add($"{_name}:After");
            SharedExecutionOrder?.Add($"{_name}:After");

            return result;
        }
    }

    private class ScopedTrackingMiddleware : TrackingMiddleware
    {
        private readonly MiddlewareScope _scope;

        public ScopedTrackingMiddleware(string name, MiddlewareScope scope) : base(name)
        {
            _scope = scope;
        }

        public MiddlewareScope Scope => _scope;
    }

    private class ResultTransformMiddleware : IAgentMiddleware
    {
        private readonly Func<object?, object?> _transform;

        public ResultTransformMiddleware(Func<object?, object?> transform)
        {
            _transform = transform;
        }

        public async ValueTask<object?> ExecuteFunctionAsync(
            AgentMiddlewareContext context,
            Func<ValueTask<object?>> next,
            CancellationToken cancellationToken)
        {
            var result = await next();
            return _transform(result);
        }
    }

    private class CachingMiddleware : IAgentMiddleware
    {
        private readonly object? _cachedValue;

        public CachingMiddleware(object? cachedValue)
        {
            _cachedValue = cachedValue;
        }

        public ValueTask<object?> ExecuteFunctionAsync(
            AgentMiddlewareContext context,
            Func<ValueTask<object?>> next,
            CancellationToken cancellationToken)
        {
            // Return cached value without calling next
            return ValueTask.FromResult(_cachedValue);
        }
    }

    private class ThrowingMiddleware : IAgentMiddleware
    {
        public ValueTask<object?> ExecuteFunctionAsync(
            AgentMiddlewareContext context,
            Func<ValueTask<object?>> next,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Middleware error");
        }
    }

    #endregion
}
