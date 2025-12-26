using HPDAgent.Graph.Abstractions.Execution;
using HPDAgent.Graph.Abstractions.Handlers;
using HPDAgent.Graph.Core.Context;

namespace HPD.Graph.Tests.Helpers;

/// <summary>
/// Simple test handler that always succeeds.
/// </summary>
public class SuccessHandler : IGraphNodeHandler<GraphContext>
{
    public string HandlerName => "SuccessHandler";

    public Task<NodeExecutionResult> ExecuteAsync(GraphContext context, HandlerInputs inputs, CancellationToken cancellationToken = default)
    {
        var output = inputs.TryGet<string>("input", out var value) ? value : "success";
        return Task.FromResult<NodeExecutionResult>(new NodeExecutionResult.Success(
            Outputs: new Dictionary<string, object> { ["output"] = output },
            Duration: TimeSpan.FromMilliseconds(10)
        ));
    }
}

/// <summary>
/// Test handler that always fails.
/// </summary>
public class FailureHandler : IGraphNodeHandler<GraphContext>
{
    public string HandlerName => "FailureHandler";

    public Task<NodeExecutionResult> ExecuteAsync(GraphContext context, HandlerInputs inputs, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<NodeExecutionResult>(new NodeExecutionResult.Failure(
            Exception: new InvalidOperationException("Test failure"),
            Severity: ErrorSeverity.Fatal,
            IsTransient: false,
            Duration: TimeSpan.FromMilliseconds(5)
        ));
    }
}

/// <summary>
/// Test handler that fails transiently (can be retried).
/// </summary>
public class TransientFailureHandler : IGraphNodeHandler<GraphContext>
{
    public string HandlerName => "TransientFailureHandler";

    private int _attempts = 0;
    private readonly int _failuresBeforeSuccess;

    public TransientFailureHandler(int failuresBeforeSuccess = 2)
    {
        _failuresBeforeSuccess = failuresBeforeSuccess;
    }

    public Task<NodeExecutionResult> ExecuteAsync(GraphContext context, HandlerInputs inputs, CancellationToken cancellationToken = default)
    {
        _attempts++;

        if (_attempts <= _failuresBeforeSuccess)
        {
            return Task.FromResult<NodeExecutionResult>(new NodeExecutionResult.Failure(
                Exception: new InvalidOperationException($"Transient failure (attempt {_attempts})"),
                Severity: ErrorSeverity.Transient,
                IsTransient: true,
                Duration: TimeSpan.FromMilliseconds(5)
            ));
        }

        return Task.FromResult<NodeExecutionResult>(new NodeExecutionResult.Success(
            Outputs: new Dictionary<string, object> { ["attempts"] = _attempts },
            Duration: TimeSpan.FromMilliseconds(10)
        ));
    }
}

/// <summary>
/// Test handler that suspends for human input.
/// </summary>
public class SuspendingHandler : IGraphNodeHandler<GraphContext>
{
    public string HandlerName => "SuspendingHandler";

    public Task<NodeExecutionResult> ExecuteAsync(GraphContext context, HandlerInputs inputs, CancellationToken cancellationToken = default)
    {
        var token = Guid.NewGuid().ToString();
        return Task.FromResult<NodeExecutionResult>(new NodeExecutionResult.Suspended(
            SuspendToken: token,
            Message: "Waiting for human approval"
        ));
    }
}

/// <summary>
/// Test handler that delays execution.
/// </summary>
public class DelayHandler : IGraphNodeHandler<GraphContext>
{
    public string HandlerName => "DelayHandler";

    private readonly TimeSpan _delay;

    public DelayHandler(TimeSpan delay)
    {
        _delay = delay;
    }

    public async Task<NodeExecutionResult> ExecuteAsync(GraphContext context, HandlerInputs inputs, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_delay, cancellationToken);
        return new NodeExecutionResult.Success(
            Outputs: new Dictionary<string, object> { ["delayed"] = true },
            Duration: _delay
        );
    }
}

/// <summary>
/// Test handler that echoes input to output.
/// </summary>
public class EchoHandler : IGraphNodeHandler<GraphContext>
{
    public string HandlerName => "EchoHandler";

    public Task<NodeExecutionResult> ExecuteAsync(GraphContext context, HandlerInputs inputs, CancellationToken cancellationToken = default)
    {
        var outputs = new Dictionary<string, object>();
        foreach (var (key, value) in inputs.GetAll())
        {
            outputs[key] = value;
        }

        return Task.FromResult<NodeExecutionResult>(new NodeExecutionResult.Success(
            Outputs: outputs,
            Duration: TimeSpan.FromMilliseconds(1)
        ));
    }
}

/// <summary>
/// Test handler that increments a counter.
/// </summary>
public class CounterHandler : IGraphNodeHandler<GraphContext>
{
    public string HandlerName => "CounterHandler";

    public Task<NodeExecutionResult> ExecuteAsync(GraphContext context, HandlerInputs inputs, CancellationToken cancellationToken = default)
    {
        var current = inputs.TryGet<int>("count", out var value) ? value : 0;
        return Task.FromResult<NodeExecutionResult>(new NodeExecutionResult.Success(
            Outputs: new Dictionary<string, object> { ["count"] = current + 1 },
            Duration: TimeSpan.FromMilliseconds(1)
        ));
    }
}
