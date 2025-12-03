using HPD.Agent;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// Tests for LoggingMiddleware - the unified logging middleware that replaces
/// LoggingAIFunctionMiddleware, FunctionLoggingMiddleware, and PromptLoggingAgentMiddleware.
/// </summary>
public class LoggingMiddlewareTests
{
    //     
    // CONFIGURATION TESTS
    //     

    [Fact]
    public void DefaultOptions_HasReasonableDefaults()
    {
        // Arrange & Act
        var options = LoggingMiddlewareOptions.Default;

        // Assert
        Assert.True(options.LogMessageTurn);
        Assert.False(options.LogIteration);
        Assert.True(options.LogFunction);
        Assert.True(options.IncludeTiming);
        Assert.True(options.IncludeArguments);
        Assert.True(options.IncludeResults);
        Assert.True(options.IncludeInstructions);
        Assert.Equal(1000, options.MaxStringLength);
        Assert.Equal("[HPD-Agent]", options.LogPrefix);
    }

    [Fact]
    public void MinimalOptions_LogsMinimalInfo()
    {
        // Arrange & Act
        var options = LoggingMiddlewareOptions.Minimal;

        // Assert
        Assert.False(options.LogMessageTurn);
        Assert.False(options.LogIteration);
        Assert.True(options.LogFunction);
        Assert.False(options.IncludeArguments);
        Assert.False(options.IncludeResults);
        Assert.True(options.IncludeTiming);
    }

    [Fact]
    public void VerboseOptions_LogsEverything()
    {
        // Arrange & Act
        var options = LoggingMiddlewareOptions.Verbose;

        // Assert
        Assert.True(options.LogMessageTurn);
        Assert.True(options.LogIteration);
        Assert.True(options.LogFunction);
        Assert.True(options.IncludeArguments);
        Assert.True(options.IncludeResults);
        Assert.True(options.IncludeTiming);
        Assert.True(options.IncludeInstructions);
        Assert.Equal(0, options.MaxStringLength); // Unlimited
    }

    //     
    // MESSAGE TURN TESTS
    //     

    [Fact]
    public async Task BeforeMessageTurn_WhenEnabled_LogsContext()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogMessageTurn = true,
            LogFunction = false
        });

        var context = CreateContext();
        context.Options = new ChatOptions { Instructions = "Test instructions" };

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        Assert.NotEmpty(logOutput);
        Assert.Contains(logOutput, s => s.Contains("MESSAGE TURN"));
        Assert.Contains(logOutput, s => s.Contains("TestAgent"));
    }

    [Fact]
    public async Task BeforeMessageTurn_WhenDisabled_DoesNotLog()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogMessageTurn = false
        });

        var context = CreateContext();

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        Assert.Empty(logOutput);
    }

    [Fact]
    public async Task AfterMessageTurn_WhenEnabled_LogsCompletion()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogMessageTurn = true
        });

        var context = CreateContext();
        context.Response = new ChatMessage(ChatRole.Assistant, "Test response");

        // Act
        await middleware.AfterMessageTurnAsync(context, CancellationToken.None);

        // Assert
        Assert.NotEmpty(logOutput);
        Assert.Contains(logOutput, s => s.Contains("END"));
    }

    //     
    // ITERATION TESTS
    //     

    [Fact]
    public async Task BeforeIteration_WhenEnabled_LogsIterationInfo()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogIteration = true,
            LogMessageTurn = false,
            LogFunction = false
        });

        var context = CreateContext();
        context.Iteration = 2;

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.NotEmpty(logOutput);
        Assert.Contains(logOutput, s => s.Contains("ITERATION"));
    }

    [Fact]
    public async Task BeforeIteration_WhenDisabled_DoesNotLog()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogIteration = false
        });

        var context = CreateContext();

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Empty(logOutput);
    }

    [Fact]
    public async Task AfterIteration_WhenEnabled_LogsToolResults()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogIteration = true
        });

        var context = CreateContext();
        context.ToolResults = new[]
        {
            new FunctionResultContent("call1", "Success"),
            new FunctionResultContent("call2", "Also success")
        };

        // Act
        await middleware.AfterIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.NotEmpty(logOutput);
        Assert.Contains(logOutput, s => s.Contains("ITERATION END"));
    }

    //     
    // FUNCTION TESTS
    //     

    [Fact]
    public async Task BeforeFunction_WhenEnabled_LogsFunctionInfo()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogFunction = true,
            IncludeArguments = true
        });

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc", "Test function");
        context.FunctionCallId = "call-123";
        context.FunctionArguments = new Dictionary<string, object?> { ["param1"] = "value1" };

        // Act
        await middleware.BeforeSequentialFunctionAsync(context, CancellationToken.None);

        // Assert
        Assert.NotEmpty(logOutput);
        Assert.Contains(logOutput, s => s.Contains("[PRE]"));
        Assert.Contains(logOutput, s => s.Contains("TestFunc"));
    }

    [Fact]
    public async Task BeforeFunction_WithoutArguments_OmitsArgs()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogFunction = true,
            IncludeArguments = false
        });

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc");
        context.FunctionArguments = new Dictionary<string, object?> { ["secret"] = "password123" };

        // Act
        await middleware.BeforeSequentialFunctionAsync(context, CancellationToken.None);

        // Assert
        Assert.NotEmpty(logOutput);
        Assert.DoesNotContain(logOutput, s => s.Contains("password123"));
    }

    [Fact]
    public async Task AfterFunction_Success_LogsResultAndTiming()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogFunction = true,
            IncludeTiming = true,
            IncludeResults = true
        });

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc");
        context.FunctionCallId = "call-123";
        context.FunctionResult = "Success result";

        // Simulate BeforeFunction to start timer
        await middleware.BeforeSequentialFunctionAsync(context, CancellationToken.None);
        logOutput.Clear();

        // Act
        await middleware.AfterFunctionAsync(context, CancellationToken.None);

        // Assert
        Assert.NotEmpty(logOutput);
        Assert.Contains(logOutput, s => s.Contains("[POST]"));
        Assert.Contains(logOutput, s => s.Contains("OK"));
        Assert.Contains(logOutput, s => s.Contains("ms")); // Timing
    }

    [Fact]
    public async Task AfterFunction_Error_LogsError()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogFunction = true
        });

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc");
        context.FunctionCallId = "call-123";
        context.FunctionException = new InvalidOperationException("Something went wrong");

        // Act
        await middleware.AfterFunctionAsync(context, CancellationToken.None);

        // Assert
        Assert.NotEmpty(logOutput);
        Assert.Contains(logOutput, s => s.Contains("FAILED"));
        Assert.Contains(logOutput, s => s.Contains("Something went wrong"));
    }

    [Fact]
    public async Task AfterFunction_WithoutResults_OmitsResult()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogFunction = true,
            IncludeResults = false
        });

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc");
        context.FunctionResult = "Secret result that should not be logged";

        // Act
        await middleware.AfterFunctionAsync(context, CancellationToken.None);

        // Assert
        Assert.NotEmpty(logOutput);
        Assert.DoesNotContain(logOutput, s => s.Contains("Secret result"));
    }

    //     
    // STRING TRUNCATION TESTS
    //     

    [Fact]
    public async Task LogsWithMaxStringLength_TruncatesLongStrings()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogFunction = true,
            IncludeResults = true,
            MaxStringLength = 20
        });

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc");
        context.FunctionResult = "This is a very long result that should be truncated";

        // Act
        await middleware.AfterFunctionAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains(logOutput, s => s.Contains("..."));
        Assert.DoesNotContain(logOutput, s => s.Contains("truncated")); // End of string should be cut
    }

    [Fact]
    public async Task LogsWithUnlimitedLength_DoesNotTruncate()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, LoggingMiddlewareOptions.Verbose);

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc");
        context.FunctionResult = "This is a very long result that should NOT be truncated because MaxStringLength is 0";

        // Act
        await middleware.AfterFunctionAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains(logOutput, s => s.Contains("should NOT be truncated"));
    }

    //     
    // CUSTOM PREFIX TESTS
    //     

    [Fact]
    public async Task LogsWithCustomPrefix_UsesPrefix()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogFunction = true,
            LogPrefix = "[MyCustomApp]"
        });

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc");

        // Act
        await middleware.BeforeSequentialFunctionAsync(context, CancellationToken.None);

        // Assert
        Assert.Contains(logOutput, s => s.Contains("[MyCustomApp]"));
    }

    //     
    // TIMING/STOPWATCH TESTS (ported from IterationLoggingFilterTests)
    //     

    [Fact]
    public async Task FunctionTiming_MeasuresExecutionTime()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogFunction = true,
            IncludeTiming = true
        });

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc");
        context.FunctionCallId = "call-123";
        var delayMs = 50;

        // Act
        var startTime = DateTime.UtcNow;
        await middleware.BeforeSequentialFunctionAsync(context, CancellationToken.None);
        await Task.Delay(delayMs); // Simulate work
        context.FunctionResult = "Done";
        await middleware.AfterFunctionAsync(context, CancellationToken.None);
        var endTime = DateTime.UtcNow;

        // Assert
        var actualDuration = (endTime - startTime).TotalMilliseconds;
        // Allow 5ms tolerance for Task.Delay imprecision (can complete slightly early)
        Assert.True(actualDuration >= delayMs - 5, $"Expected at least {delayMs - 5}ms, got {actualDuration}ms");

        // Verify timing was logged
        Assert.Contains(logOutput, s => s.Contains("ms"));
        Assert.Contains(logOutput, s => s.Contains("[POST]"));
    }

    [Fact]
    public async Task FunctionTiming_StartsStopwatch_InBeforePhase()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogFunction = true,
            IncludeTiming = true
        });

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc");
        context.FunctionCallId = "call-123";

        // Act - Before
        await middleware.BeforeSequentialFunctionAsync(context, CancellationToken.None);

        // Simulate work
        await Task.Delay(20);

        // Simulate function completion
        context.FunctionResult = "Done";

        // Act - After
        await middleware.AfterFunctionAsync(context, CancellationToken.None);

        // Assert - Stopwatch should have measured time (check for timing in log)
        Assert.Contains(logOutput, s => s.Contains("ms"));
    }

    [Fact]
    public async Task FunctionTiming_WithoutTiming_OmitsTiming()
    {
        // Arrange
        var logOutput = new List<string>();
        var loggerFactory = CreateTestLoggerFactory(logOutput);
        var middleware = new LoggingMiddleware(loggerFactory, new LoggingMiddlewareOptions
        {
            LogFunction = true,
            IncludeTiming = false  // Timing disabled
        });

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc");
        context.FunctionResult = "Done";

        // Act
        await middleware.BeforeSequentialFunctionAsync(context, CancellationToken.None);
        await middleware.AfterFunctionAsync(context, CancellationToken.None);

        // Assert - No timing should be logged
        Assert.DoesNotContain(logOutput, s => s.Contains("ms"));
    }

    //     
    // NO LOGGER TESTS
    //     

    [Fact]
    public async Task WithoutLogger_DoesNotThrow()
    {
        // Arrange - no logger factory
        var middleware = new LoggingMiddleware(null);

        var context = CreateContext();
        context.Function = AIFunctionFactory.Create(() => "test", "TestFunc");

        // Act & Assert - should not throw
        await middleware.BeforeSequentialFunctionAsync(context, CancellationToken.None);
        await middleware.AfterFunctionAsync(context, CancellationToken.None);
    }

    //     
    // HELPER METHODS
    //     

    private static AgentMiddlewareContext CreateContext()
    {
        var context = new AgentMiddlewareContext
        {
            AgentName = "TestAgent",
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions(),
            ConversationId = "test-conversation",
            CancellationToken = CancellationToken.None
        };
        context.SetOriginalState(AgentLoopState.Initial(
            messages: Array.Empty<ChatMessage>(),
            runId: "test-run",
            conversationId: "test-conversation",
            agentName: "TestAgent"));
        return context;
    }

    private static ILoggerFactory CreateTestLoggerFactory(List<string> output)
    {
        return new TestLoggerFactory(output);
    }

    /// <summary>
    /// Simple logger factory for capturing log output in tests.
    /// </summary>
    private class TestLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _output;

        public TestLoggerFactory(List<string> output)
        {
            _output = output;
        }

        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        public ILogger CreateLogger(string categoryName)
        {
            return new TestLogger(_output);
        }
    }

    private class TestLogger : ILogger
    {
        private readonly List<string> _output;

        public TestLogger(List<string> output)
        {
            _output = output;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _output.Add(formatter(state, exception));
        }
    }
}
