using HPD.Agent;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using Xunit;
using static HPD.Agent.Tests.Middleware.V2.MiddlewareTestHelpers;

namespace HPD.Agent.Tests.Middleware.V2;

/// <summary>
/// Tests for AgentMiddlewarePipeline V2 features - dual pattern, OnErrorAsync routing, typed contexts.
/// </summary>
public class PipelineV2Tests
{
    /// <summary>
    /// Tests that ExecuteBeforeIteration calls middleware in forward registration order.
    /// </summary>
    [Fact]
    public async Task ExecuteBeforeIteration_CallsInOrder()
    {
        // Arrange
        var callOrder = new List<string>();
        var middleware1 = new TestMiddleware("M1", callOrder);
        var middleware2 = new TestMiddleware("M2", callOrder);

        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { middleware1, middleware2 });
        var context = CreateBeforeIterationContext();

        // Act
        await pipeline.ExecuteBeforeIterationAsync(context, CancellationToken.None);

        // Assert - forward order
        Assert.Equal(new[] { "M1.Before", "M2.Before" }, callOrder);
    }

    /// <summary>
    /// Tests that ExecuteAfterIteration calls middleware in reverse registration order.
    /// </summary>
    [Fact]
    public async Task ExecuteAfterIteration_CallsInReverseOrder()
    {
        // Arrange
        var callOrder = new List<string>();
        var middleware1 = new TestMiddleware("M1", callOrder);
        var middleware2 = new TestMiddleware("M2", callOrder);

        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { middleware1, middleware2 });
        var context = CreateAfterIterationContext();

        // Act
        await pipeline.ExecuteAfterIterationAsync(context, CancellationToken.None);

        // Assert - reverse order (stack unwinding)
        Assert.Equal(new[] { "M2.After", "M1.After" }, callOrder);
    }

    /// <summary>
    /// Tests that ExecuteOnError calls middleware in reverse registration order for error unwinding.
    /// </summary>
    [Fact]
    public async Task ExecuteOnError_CallsInReverseOrder()
    {
        // Arrange
        var callOrder = new List<string>();
        var middleware1 = new TestMiddleware("M1", callOrder);
        var middleware2 = new TestMiddleware("M2", callOrder);

        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { middleware1, middleware2 });
        var context = CreateErrorContext();

        // Act
        await pipeline.ExecuteOnErrorAsync(context, CancellationToken.None);

        // Assert - reverse order (error unwinding)
        Assert.Equal(new[] { "M2.OnError", "M1.OnError" }, callOrder);
    }

    /// <summary>
    /// Tests that WrapModelCall builds a proper middleware chain using the simple pattern.
    /// </summary>
    [Fact]
    public async Task WrapModelCall_SimplePattern_BuildsChain()
    {
        // Arrange
        var middleware = new RetryMiddleware (maxRetries: 2);
        var pipeline = new AgentMiddlewarePipeline(new[] { middleware });

        var request = CreateModelRequest();
        var callCount = 0;

        // Handler that fails once, then succeeds
        Task<ModelResponse> Handler(ModelRequest req)
        {
            callCount++;
            if (callCount == 1)
                throw new HttpRequestException("Transient error");

            return Task.FromResult(new ModelResponse
            {
                Message = new ChatMessage(ChatRole.Assistant, "Success"),
                ToolCalls = Array.Empty<FunctionCallContent>(),
                Error = null
            });
        }

        // Act
        var response = await pipeline.ExecuteModelCallAsync(request, Handler, CancellationToken.None);

        // Assert - retry worked!
        Assert.Equal(2, callCount);
        Assert.True(response.IsSuccess);
        Assert.Equal("Success", response.Message.Text);
    }

    /// <summary>
    /// Tests that WrapFunctionCall builds a proper middleware chain for function execution.
    /// </summary>
    [Fact]
    public async Task WrapFunctionCall_BuildsChain()
    {
        // Arrange
        var middleware = new FunctionRetryMiddleware (maxRetries: 3);
        var pipeline = new AgentMiddlewarePipeline(new[] { middleware });

        var request = CreateFunctionRequest();
        var callCount = 0;

        // Handler that fails twice, then succeeds
        Task<object?> Handler(FunctionRequest req)
        {
            callCount++;
            if (callCount < 3)
                throw new InvalidOperationException($"Attempt {callCount}");

            return Task.FromResult<object?>("Success");
        }

        // Act
        var result = await pipeline.ExecuteFunctionCallAsync(request, Handler, CancellationToken.None);

        // Assert
        Assert.Equal(3, callCount);
        Assert.Equal("Success", result);
    }

    /// <summary>
    /// Tests that state updates are immediately visible to next middleware in the chain.
    /// </summary>
    [Fact]
    public async Task StateUpdates_ImmediatelyVisibleToNextMiddleware()
    {
        // Arrange
        var middleware1 = new StateWriterMiddleware();
        var middleware2 = new StateReaderMiddleware();

        var pipeline = new AgentMiddlewarePipeline(new IAgentMiddleware[] { middleware1, middleware2 });
        var context = CreateBeforeIterationContext();

        // Act
        await pipeline.ExecuteBeforeIterationAsync(context, CancellationToken.None);

        // Assert - M2 saw M1's state update immediately
        Assert.Equal(42, context.State.Iteration);
        Assert.True(StateReaderMiddleware.SawUpdatedState);
    }

    // Helper middleware

    /// <summary>
    /// Test middleware for tracking call order.
    /// </summary>
    public class TestMiddleware : IAgentMiddleware
    {
        private readonly string _name;
        private readonly List<string> _callOrder;

        /// <summary>
        /// Initializes a new instance of the TestMiddleware class.
        /// </summary>
        public TestMiddleware(string name, List<string> callOrder)
        {
            _name = name;
            _callOrder = callOrder;
        }

        /// <inheritdoc />
        public Task BeforeIterationAsync(BeforeIterationContext context, CancellationToken ct)
        {
            _callOrder.Add($"{_name}.Before");
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task AfterIterationAsync(AfterIterationContext context, CancellationToken ct)
        {
            _callOrder.Add($"{_name}.After");
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task OnErrorAsync(ErrorContext context, CancellationToken ct)
        {
            _callOrder.Add($"{_name}.OnError");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Test middleware that retries model calls.
    /// </summary>
    public class RetryMiddleware  : IAgentMiddleware
    {
        private readonly int _maxRetries;

        /// <summary>
        /// Initializes a new instance of the RetryMiddleware class.
        /// </summary>
        public RetryMiddleware (int maxRetries) => _maxRetries = maxRetries;

        /// <inheritdoc />
        public async Task<ModelResponse> WrapModelCallAsync(
            ModelRequest request,
            Func<ModelRequest, Task<ModelResponse>> handler,
            CancellationToken ct)
        {
            for (int i = 0; i < _maxRetries; i++)
            {
                try { return await handler(request); }
                catch when (i < _maxRetries - 1) { }
            }
            return await handler(request);
        }
    }

    /// <summary>
    /// Test middleware that retries function calls.
    /// </summary>
    public class FunctionRetryMiddleware  : IAgentMiddleware
    {
        private readonly int _maxRetries;

        /// <summary>
        /// Initializes a new instance of the FunctionRetryMiddleware class.
        /// </summary>
        public FunctionRetryMiddleware (int maxRetries) => _maxRetries = maxRetries;

        /// <inheritdoc />
        public async Task<object?> WrapFunctionCallAsync(
            FunctionRequest request,
            Func<FunctionRequest, Task<object?>> handler,
            CancellationToken ct)
        {
            for (int i = 0; i < _maxRetries; i++)
            {
                try { return await handler(request); }
                catch when (i < _maxRetries - 1) { }
            }
            return await handler(request);
        }
    }

    /// <summary>
    /// Test middleware that writes state updates.
    /// </summary>
    public class StateWriterMiddleware : IAgentMiddleware
    {
        /// <inheritdoc />
        public Task BeforeIterationAsync(BeforeIterationContext context, CancellationToken ct)
        {
            // Update state
            context.UpdateState(s => s with { Iteration = 42 });
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Test middleware that reads state to verify immediate updates.
    /// </summary>
    public class StateReaderMiddleware : IAgentMiddleware
    {
        /// <summary>
        /// Gets a value indicating whether this middleware saw the updated state.
        /// </summary>
        public static bool SawUpdatedState { get; private set; }

        /// <inheritdoc />
        public Task BeforeIterationAsync(BeforeIterationContext context, CancellationToken ct)
        {
            // Check if we see M1's update immediately
            SawUpdatedState = context.State.Iteration == 42;
            return Task.CompletedTask;
        }
    }

    // Helpers

    private static BeforeIterationContext CreateBeforeIterationContext()
    {
        var state = AgentLoopState.Initial(
            new List<ChatMessage>(),
            "run123",
            "conv123",
            "TestAgent");

        var agentContext = new AgentContext(
            "TestAgent",
            "conv123",
            state,
            new BidirectionalEventCoordinator(),
            CancellationToken.None);

        return agentContext.AsBeforeIteration(
            0,
            new List<ChatMessage>(),
            new ChatOptions(),
            new AgentRunOptions());
    }

    private static AfterIterationContext CreateAfterIterationContext()
    {
        var state = AgentLoopState.Initial(
            new List<ChatMessage>(),
            "run123",
            "conv123",
            "TestAgent");

        var agentContext = new AgentContext(
            "TestAgent",
            "conv123",
            state,
            new BidirectionalEventCoordinator(),
            CancellationToken.None);

        return agentContext.AsAfterIteration(0, Array.Empty<FunctionResultContent>(), new AgentRunOptions());
    }

    private static ErrorContext CreateErrorContext()
    {
        var state = AgentLoopState.Initial(
            new List<ChatMessage>(),
            "run123",
            "conv123",
            "TestAgent");

        var agentContext = new AgentContext(
            "TestAgent",
            "conv123",
            state,
            new BidirectionalEventCoordinator(),
            CancellationToken.None);

        return agentContext.AsError(
            new InvalidOperationException("Test"),
            ErrorSource.ToolCall,
            0);
    }

    private static ModelRequest CreateModelRequest()
    {
        var state = AgentLoopState.Initial(
            new List<ChatMessage>(),
            "run123",
            "conv123",
            "TestAgent");

        return new ModelRequest
        {
            Model = new TestChatClient(),
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions(),
            State = state,
            Iteration = 0
        };
    }

    private static FunctionRequest CreateFunctionRequest()
    {
        var state = AgentLoopState.Initial(
            new List<ChatMessage>(),
            "run123",
            "conv123",
            "TestAgent");

        var function = AIFunctionFactory.Create(() => "test", "TestFunc");

        return new FunctionRequest
        {
            Function = function,
            CallId = "call1",
            Arguments = new Dictionary<string, object?>(),
            State = state
        };
    }

    /// <summary>
    /// Test implementation of IChatClient for middleware testing.
    /// </summary>
    public class TestChatClient : IChatClient
    {
        /// <inheritdoc />
        public ChatClientMetadata Metadata => new("test");

        /// <inheritdoc />
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Test")));
        }

        /// <inheritdoc />
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        /// <inheritdoc />
        public TService? GetService<TService>(object? serviceKey = null) where TService : class => null;

        /// <inheritdoc />
        public void Dispose() => GC.SuppressFinalize(this);
    }
}
