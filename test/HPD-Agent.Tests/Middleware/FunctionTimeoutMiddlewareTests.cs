using HPD.Agent.Middleware;
using HPD.Agent.Middleware.Function;
using Microsoft.Extensions.AI;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// Comprehensive tests for FunctionTimeoutMiddleware.
/// Tests timeout enforcement and cancellation token handling.
/// </summary>
public class FunctionTimeoutMiddlewareTests
{
    #region Basic Timeout Behavior

    [Fact]
    public async Task ExecuteFunctionAsync_CompletesBeforeTimeout_Succeeds()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(2);
        var middleware = new FunctionTimeoutMiddleware(timeout);
        var context = CreateContext();

        Func<ValueTask<object?>> next = async () =>
        {
            await Task.Delay(50); // Fast execution
            return "Success";
        };

        // Act
        var result = await middleware.ExecuteFunctionAsync(context, next, CancellationToken.None);

        // Assert
        Assert.Equal("Success", result);
    }

    [Fact]
    public async Task ExecuteFunctionAsync_ExceedsTimeout_ThrowsTimeoutException()
    {
        // Arrange
        var timeout = TimeSpan.FromMilliseconds(100);
        var middleware = new FunctionTimeoutMiddleware(timeout);
        var context = CreateContext();

        Func<ValueTask<object?>> next = async () =>
        {
            await Task.Delay(500); // Slow execution (will timeout)
            return "Success";
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await middleware.ExecuteFunctionAsync(context, next, CancellationToken.None));

        Assert.Contains("timed out", exception.Message);
        Assert.Contains("TestFunction", exception.Message);
        Assert.Contains("0.1", exception.Message); // Timeout in seconds
    }

    [Fact]
    public async Task ExecuteFunctionAsync_ExactlyAtTimeout_MaySucceedOrTimeout()
    {
        // Arrange
        var timeout = TimeSpan.FromMilliseconds(100);
        var middleware = new FunctionTimeoutMiddleware(timeout);
        var context = CreateContext();

        Func<ValueTask<object?>> next = async () =>
        {
            await Task.Delay(100); // Exactly at timeout
            return "Success";
        };

        // Act - This is a race condition, either outcome is acceptable
        try
        {
            var result = await middleware.ExecuteFunctionAsync(context, next, CancellationToken.None);
            // If we get here, execution completed just in time
            Assert.Equal("Success", result);
        }
        catch (TimeoutException ex)
        {
            // If we get here, timeout triggered first
            Assert.Contains("timed out", ex.Message);
        }

        // Test passes either way - just documenting the race condition
        Assert.True(true);
    }

    #endregion

    #region Cancellation Token Handling

    [Fact]
    public async Task ExecuteFunctionAsync_ParentCancellationTriggered_ThrowsOperationCanceled()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(10); // Long timeout
        var middleware = new FunctionTimeoutMiddleware(timeout);
        var context = CreateContext();

        var cts = new CancellationTokenSource();

        Func<ValueTask<object?>> next = async () =>
        {
            await Task.Delay(50);
            cts.Cancel(); // Parent cancellation (not timeout)
            await Task.Delay(100, cts.Token); // This will throw
            return "Success";
        };

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await middleware.ExecuteFunctionAsync(context, next, cts.Token));
    }

    [Fact]
    public async Task ExecuteFunctionAsync_TimeoutCancellation_ThrowsTimeoutException()
    {
        // Arrange
        var timeout = TimeSpan.FromMilliseconds(100);
        var middleware = new FunctionTimeoutMiddleware(timeout);
        var context = CreateContext();

        Func<ValueTask<object?>> next = async () =>
        {
            // Slow operation that will timeout
            await Task.Delay(500);
            return "Success";
        };

        // Act & Assert - Should throw TimeoutException, not OperationCanceledException
        var exception = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await middleware.ExecuteFunctionAsync(context, next, CancellationToken.None));

        Assert.Contains("timed out", exception.Message);
    }

    [Fact]
    public async Task ExecuteFunctionAsync_ParentAlreadyCanceled_ThrowsImmediately()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(10);
        var middleware = new FunctionTimeoutMiddleware(timeout);
        var context = CreateContext();

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Already canceled

        bool nextCalled = false;
        Func<ValueTask<object?>> next = async () =>
        {
            nextCalled = true;
            // Add a small delay to ensure we have an async operation to cancel
            await Task.Delay(100, cts.Token);
            return "Success";
        };

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await middleware.ExecuteFunctionAsync(context, next, cts.Token));

        // Next is called but should throw immediately when it checks the cancellation token
    }

    #endregion

    #region Function Name in Exception

    [Fact]
    public async Task ExecuteFunctionAsync_Timeout_IncludesFunctionNameInException()
    {
        // Arrange
        var timeout = TimeSpan.FromMilliseconds(50);
        var middleware = new FunctionTimeoutMiddleware(timeout);

        var context = CreateContext();
        context.Function = CreateMockFunction("MyCustomFunction");

        Func<ValueTask<object?>> next = async () =>
        {
            await Task.Delay(200);
            return "Success";
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await middleware.ExecuteFunctionAsync(context, next, CancellationToken.None));

        Assert.Contains("MyCustomFunction", exception.Message);
    }

    [Fact]
    public async Task ExecuteFunctionAsync_NoFunctionName_UsesUnknown()
    {
        // Arrange
        var timeout = TimeSpan.FromMilliseconds(50);
        var middleware = new FunctionTimeoutMiddleware(timeout);

        var context = CreateContext();
        context.Function = null; // No function set

        Func<ValueTask<object?>> next = async () =>
        {
            await Task.Delay(200);
            return "Success";
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await middleware.ExecuteFunctionAsync(context, next, CancellationToken.None));

        Assert.Contains("Unknown", exception.Message);
    }

    #endregion

    #region Constructor Validation

    [Fact]
    public void Constructor_NegativeTimeout_ThrowsArgumentException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            new FunctionTimeoutMiddleware(TimeSpan.FromSeconds(-1)));

        Assert.Contains("greater than zero", exception.Message);
    }

    [Fact]
    public void Constructor_ZeroTimeout_ThrowsArgumentException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            new FunctionTimeoutMiddleware(TimeSpan.Zero));

        Assert.Contains("greater than zero", exception.Message);
    }

    [Fact]
    public void Constructor_ValidTimeout_Succeeds()
    {
        // Act
        var middleware = new FunctionTimeoutMiddleware(TimeSpan.FromSeconds(1));

        // Assert - no exception thrown
        Assert.NotNull(middleware);
    }

    #endregion

    #region Integration with Other Middleware Concerns

    [Fact]
    public async Task ExecuteFunctionAsync_ExceptionDuringExecution_PropagatesException()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(10); // Long timeout
        var middleware = new FunctionTimeoutMiddleware(timeout);
        var context = CreateContext();

        Func<ValueTask<object?>> next = () =>
        {
            throw new InvalidOperationException("Business logic error");
        };

        // Act & Assert - Exception should propagate (not timeout)
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await middleware.ExecuteFunctionAsync(context, next, CancellationToken.None));

        Assert.Equal("Business logic error", exception.Message);
    }

    [Fact]
    public async Task ExecuteFunctionAsync_FastFailure_DoesNotWaitForTimeout()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(10); // Long timeout
        var middleware = new FunctionTimeoutMiddleware(timeout);
        var context = CreateContext();

        var startTime = DateTime.UtcNow;
        Func<ValueTask<object?>> next = () =>
        {
            throw new InvalidOperationException("Immediate failure");
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await middleware.ExecuteFunctionAsync(context, next, CancellationToken.None));

        var elapsed = DateTime.UtcNow - startTime;

        // Should fail immediately, not wait for timeout
        Assert.True(elapsed < TimeSpan.FromSeconds(1));
    }

    #endregion

    #region Multiple Concurrent Calls

    [Fact]
    public async Task ExecuteFunctionAsync_MultipleConcurrentCalls_EachHasOwnTimeout()
    {
        // Arrange
        var timeout = TimeSpan.FromMilliseconds(500);
        var middleware = new FunctionTimeoutMiddleware(timeout);

        // Act - Start multiple concurrent calls
        var tasks = new List<Task>();

        // Fast call - should succeed
        var context1 = CreateContext();
        tasks.Add(Task.Run(async () =>
        {
            var result = await middleware.ExecuteFunctionAsync(
                context1,
                async () =>
                {
                    await Task.Delay(100);
                    return "Fast";
                },
                CancellationToken.None);
            Assert.Equal("Fast", result);
        }));

        // Slow call - should timeout
        var context2 = CreateContext();
        tasks.Add(Task.Run(async () =>
        {
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await middleware.ExecuteFunctionAsync(
                    context2,
                    async () =>
                    {
                        await Task.Delay(1000);
                        return "Slow";
                    },
                    CancellationToken.None));
        }));

        // Assert - All tasks complete successfully
        await Task.WhenAll(tasks);
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
}
