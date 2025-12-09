using Microsoft.Extensions.AI;

namespace HPD.Agent.Middleware;


#region Agnet Middleware Pipeline
/// <summary>
/// Executes middleware hooks in the correct order for each lifecycle phase.
/// Handles Before* hooks in registration order and After* hooks in reverse order.
/// </summary>
/// <remarks>
/// <para><b>Execution Order:</b></para>
/// <para>
/// Before* hooks run in registration order (first registered = first executed).
/// After* hooks run in reverse order (last registered = first executed, like stack unwinding).
/// </para>
///
/// <para><b>Error Handling:</b></para>
/// <para>
/// Exceptions in Before* hooks stop the chain and propagate immediately.
/// After* hooks always run (even if the operation failed), to allow cleanup.
/// </para>
///
/// <para><b>Short-Circuiting:</b></para>
/// <para>
/// Middlewares can set context flags (SkipLLMCall, SkipToolExecution, BlockFunctionExecution)
/// to prevent execution. The pipeline continues to run remaining Before* hooks, but the
/// actual operation is skipped.
/// </para>
/// </remarks>
public class AgentMiddlewarePipeline
{
    private readonly IReadOnlyList<IAgentMiddleware> _middlewares;
    private readonly IReadOnlyList<IAgentMiddleware> _reversedMiddlewares;

    /// <summary>
    /// Creates a new middleware pipeline with the given middlewares.
    /// </summary>
    /// <param name="middlewares">Middlewares in registration order</param>
    public AgentMiddlewarePipeline(IEnumerable<IAgentMiddleware> middlewares)
    {
        _middlewares = middlewares?.ToList() ?? throw new ArgumentNullException(nameof(middlewares));
        _reversedMiddlewares = _middlewares.Reverse().ToList();
    }

    /// <summary>
    /// Creates a new middleware pipeline with the given middlewares.
    /// </summary>
    /// <param name="middlewares">Middlewares in registration order</param>
    public AgentMiddlewarePipeline(IReadOnlyList<IAgentMiddleware> middlewares)
    {
        _middlewares = middlewares ?? throw new ArgumentNullException(nameof(middlewares));
        _reversedMiddlewares = _middlewares.Reverse().ToList();
    }

    /// <summary>
    /// Returns true if there are no middlewares in the pipeline.
    /// </summary>
    public bool IsEmpty => _middlewares.Count == 0;

    /// <summary>
    /// Number of middlewares in the pipeline.
    /// </summary>
    public int Count => _middlewares.Count;

    /// <summary>
    /// Gets all middlewares in the pipeline (registration order).
    /// </summary>
    public IReadOnlyList<IAgentMiddleware> Middlewares => _middlewares;

    //     
    // MESSAGE TURN LEVEL
    //     

    /// <summary>
    /// Executes BeforeMessageTurnAsync on all middlewares in registration order.
    /// </summary>
    public async Task ExecuteBeforeMessageTurnAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await middleware.BeforeMessageTurnAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes AfterMessageTurnAsync on all middlewares in reverse order.
    /// Always runs even if earlier operations failed.
    /// </summary>
    public async Task ExecuteAfterMessageTurnAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        List<Exception>? exceptions = null;

        foreach (var middleware in _reversedMiddlewares)
        {
            try
            {
                // Don't throw on cancellation in After* hooks - allow cleanup
                await middleware.AfterMessageTurnAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Collect exceptions but continue running other After* hooks
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }

        // Throw aggregate if any After* hooks failed
        if (exceptions != null)
        {
            throw new AggregateException(
                "One or more AfterMessageTurn middleware hooks failed",
                exceptions);
        }
    }

    //     
    // ITERATION LEVEL
    //     

    /// <summary>
    /// Executes BeforeIterationAsync on all middlewares in registration order.
    /// </summary>
    public async Task ExecuteBeforeIterationAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await middleware.BeforeIterationAsync(context, cancellationToken).ConfigureAwait(false);

            // If middleware signaled to skip LLM, we still run remaining Before* hooks
            // but the actual LLM call will be skipped
        }
    }

    /// <summary>
    /// Executes the LLM call through the middleware pipeline with full streaming control.
    /// Middleware chains execute in REVERSE order (last registered = outermost layer).
    /// </summary>
    /// <remarks>
    /// <para><b>Onion Architecture:</b></para>
    /// <para>
    /// Middlewares wrap each other in reverse registration order:
    /// - Last registered middleware wraps everything (outermost)
    /// - First registered middleware is closest to the actual LLM call (innermost)
    /// </para>
    ///
    /// <para><b>Example Flow:</b></para>
    /// <code>
    /// // Registration order: [Logging, Caching, Retry]
    /// // Execution order: Retry → Caching → Logging → LLM
    ///
    /// Retry.ExecuteLLMCallAsync(next: () =>
    ///   Caching.ExecuteLLMCallAsync(next: () =>
    ///     Logging.ExecuteLLMCallAsync(next: () =>
    ///       actualLLMCall()
    ///     )
    ///   )
    /// )
    /// </code>
    ///
    /// <para><b>Streaming Behavior:</b></para>
    /// <para>
    /// Each middleware can intercept, transform, or cache the streaming response.
    /// Updates flow back through the chain in reverse order.
    /// </para>
    /// </remarks>
    /// <param name="context">The middleware context with full agent state</param>
    /// <param name="innerCall">The innermost operation (actual LLM call)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Streaming LLM response after passing through all middleware</returns>
    public IAsyncEnumerable<ChatResponseUpdate> ExecuteLLMCallAsync(
        AgentMiddlewareContext context,
        Func<IAsyncEnumerable<ChatResponseUpdate>> innerCall,
        CancellationToken cancellationToken)
    {
        // Build the pipeline chain in registration order (first registered = outermost)
        // Loop backwards so that first registered middleware wraps everything
        Func<IAsyncEnumerable<ChatResponseUpdate>> pipeline = innerCall;

        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var currentPipeline = pipeline;

            // Wrap the current pipeline with this middleware's ExecuteLLMCallAsync
            pipeline = () => middleware.ExecuteLLMCallAsync(context, currentPipeline, cancellationToken);
        }

        // Execute the outermost middleware (which will call the next, and so on)
        return pipeline();
    }

    /// <summary>
    /// Executes BeforeToolExecutionAsync on all middlewares in registration order.
    /// </summary>
    public async Task ExecuteBeforeToolExecutionAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await middleware.BeforeToolExecutionAsync(context, cancellationToken).ConfigureAwait(false);

            // If middleware signaled to skip tool execution, we still run remaining Before* hooks
            // but the actual tool execution will be skipped
        }
    }

    /// <summary>
    /// Executes AfterIterationAsync on all middlewares in reverse order.
    /// Always runs even if tool execution failed.
    /// </summary>
    public async Task ExecuteAfterIterationAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        List<Exception>? exceptions = null;

        foreach (var middleware in _reversedMiddlewares)
        {
            try
            {
                await middleware.AfterIterationAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }

        if (exceptions != null)
        {
            throw new AggregateException(
                "One or more AfterIteration middleware hooks failed",
                exceptions);
        }
    }

    //     
    // FUNCTION LEVEL
    //     

    /// <summary>
    /// Executes BeforeParallelFunctionsAsync on all middlewares in registration order.
    /// Called ONCE before multiple functions execute in parallel.
    /// Allows middlewares to handle batch operations (e.g., batch permission checking).
    /// </summary>
    public async Task ExecuteBeforeParallelFunctionsAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await middleware.BeforeParallelFunctionsAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes BeforeSequentialFunctionAsync on all middlewares in registration order.
    /// Filters middlewares by Collapse - only executes those that apply to the current function context.
    /// </summary>
    /// <returns>True if function should execute, false if blocked by middleware</returns>
    public async Task<bool> ExecuteBeforeSequentialFunctionAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            // Check if middleware should execute based on its Collapse
            if (!middleware.ShouldExecute(context))
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            await middleware.BeforeSequentialFunctionAsync(context, cancellationToken).ConfigureAwait(false);

            // If any middleware blocks execution, stop the chain immediately
            // This is different from iteration-level hooks where we continue
            // the chain but skip the operation. For permissions, we want to
            // stop as soon as one middleware denies.
            if (context.BlockFunctionExecution)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Executes the function call through the middleware pipeline with full control.
    /// Middleware chains execute in REVERSE order (last registered = outermost layer).
    /// </summary>
    /// <remarks>
    /// <para><b>Onion Architecture:</b></para>
    /// <para>
    /// Middlewares wrap each other in reverse registration order:
    /// - Last registered middleware wraps everything (outermost)
    /// - First registered middleware is closest to the actual function (innermost)
    /// </para>
    ///
    /// <para><b>Example Flow:</b></para>
    /// <code>
    /// // Registration order: [Permissions, Retry, Telemetry]
    /// // Execution order: Telemetry → Retry → Permissions → Function
    ///
    /// Telemetry.ExecuteFunctionAsync(next: () =>
    ///   Retry.ExecuteFunctionAsync(next: () =>
    ///     Permissions.ExecuteFunctionAsync(next: () =>
    ///       actualFunctionExecution()
    ///     )
    ///   )
    /// )
    /// </code>
    /// </remarks>
    /// <param name="context">The middleware context with function info</param>
    /// <param name="innerCall">The innermost operation (actual function execution)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function result after passing through all middleware</returns>
    public ValueTask<object?> ExecuteFunctionAsync(
        AgentMiddlewareContext context,
        Func<ValueTask<object?>> innerCall,
        CancellationToken cancellationToken)
    {
        // Build the pipeline chain in registration order (first registered = outermost)
        // Loop backwards so that first registered middleware wraps everything
        Func<ValueTask<object?>> pipeline = innerCall;

        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];

            // Check if middleware should execute based on Collapse
            if (!middleware.ShouldExecute(context))
                continue;

            var currentPipeline = pipeline;

            // Wrap the current pipeline with this middleware's ExecuteFunctionAsync
            pipeline = () => middleware.ExecuteFunctionAsync(context, currentPipeline, cancellationToken);
        }

        // Execute the outermost middleware (which will call the next, and so on)
        return pipeline();
    }

    /// <summary>
    /// Executes AfterFunctionAsync on all middlewares in reverse order.
    /// Filters middlewares by Collapse - only executes those that apply to the current function context.
    /// Always runs even if function execution failed.
    /// </summary>
    public async Task ExecuteAfterFunctionAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        List<Exception>? exceptions = null;

        foreach (var middleware in _reversedMiddlewares)
        {
            // Check if middleware should execute based on its Collapse
            if (!middleware.ShouldExecute(context))
                continue;

            try
            {
                await middleware.AfterFunctionAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }

        if (exceptions != null)
        {
            throw new AggregateException(
                "One or more AfterFunction middleware hooks failed",
                exceptions);
        }
    }

    //     
    // CONVENIENCE METHODS
    //     

    /// <summary>
    /// Wraps a function execution with Before/After hooks.
    /// Handles BlockFunctionExecution and exception propagation.
    /// </summary>
    /// <param name="context">The middleware context (must have function info set)</param>
    /// <param name="executeFunction">The function to execute if not blocked</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function result (either from execution or from middleware blocking)</returns>
    public async Task<object?> ExecuteFunctionWithHooksAsync(
        AgentMiddlewareContext context,
        Func<Task<object?>> executeFunction,
        CancellationToken cancellationToken)
    {
        // Run Before* hooks
        var shouldExecute = await ExecuteBeforeSequentialFunctionAsync(context, cancellationToken)
            .ConfigureAwait(false);

        if (shouldExecute)
        {
            // Execute the actual function
            // Note: Exception handling is delegated to middleware (e.g., ErrorFormattingMiddleware)
            // or to the Agent.cs FormatErrorForLLM() fallback.
            // This ensures consistent, security-aware error formatting.
            context.FunctionResult = await executeFunction().ConfigureAwait(false);
        }
        // If blocked, FunctionResult should already be set by the blocking middleware

        // Run After* hooks (always, even if blocked or failed)
        await ExecuteAfterFunctionAsync(context, cancellationToken).ConfigureAwait(false);

        return context.FunctionResult;
    }

    /// <summary>
    /// Wraps an iteration with Before/After hooks.
    /// Handles SkipLLMCall, SkipToolExecution, and exception propagation.
    /// </summary>
    /// <param name="context">The middleware context</param>
    /// <param name="executeLLMCall">The LLM call to execute if not skipped</param>
    /// <param name="executeTools">The tool execution to run if not skipped</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task ExecuteIterationWithHooksAsync(
        AgentMiddlewareContext context,
        Func<Task> executeLLMCall,
        Func<Task> executeTools,
        CancellationToken cancellationToken)
    {
        // BeforeIteration
        await ExecuteBeforeIterationAsync(context, cancellationToken).ConfigureAwait(false);

        // LLM Call (unless skipped)
        if (!context.SkipLLMCall)
        {
            try
            {
                await executeLLMCall().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.IterationException = ex;
            }
        }

        // BeforeToolExecution (only if LLM succeeded and has tool calls)
        if (context.IterationException == null && context.ToolCalls.Count > 0)
        {
            await ExecuteBeforeToolExecutionAsync(context, cancellationToken).ConfigureAwait(false);

            // Tool Execution (unless skipped)
            if (!context.SkipToolExecution)
            {
                await executeTools().ConfigureAwait(false);
            }
        }

        // AfterIteration (always runs)
        await ExecuteAfterIterationAsync(context, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Extension methods for building middleware pipelines from AgentBuilder.
/// </summary>
public static class AgentMiddlewarePipelineExtensions
{
    /// <summary>
    /// Creates a middleware pipeline from a list of middlewares.
    /// </summary>
    public static AgentMiddlewarePipeline ToPipeline(this IEnumerable<IAgentMiddleware> middlewares)
        => new AgentMiddlewarePipeline(middlewares);

    /// <summary>
    /// Creates a middleware pipeline from a list of middlewares.
    /// </summary>
    public static AgentMiddlewarePipeline ToPipeline(this IReadOnlyList<IAgentMiddleware> middlewares)
        => new AgentMiddlewarePipeline(middlewares);
}
    
#endregion