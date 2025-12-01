using Microsoft.Extensions.AI;

namespace HPD.Agent.Middleware;

/// <summary>
/// Unified middleware interface for all agent lifecycle hooks.
/// Implement only the hooks you need - all have default no-op implementations.
/// </summary>
/// <remarks>
/// <para><b>Lifecycle Overview:</b></para>
/// <code>
/// BeforeMessageTurnAsync
///   └─► [LOOP] BeforeIterationAsync
///               └─► LLM Call
///               └─► BeforeToolExecutionAsync
///                     └─► BeforeParallelFunctionsAsync (if parallel execution)
///                     └─► [LOOP] BeforeSequentialFunctionAsync
///                                  └─► Function Execution
///                                  └─► AfterFunctionAsync
///               └─► AfterIterationAsync
///   └─► AfterMessageTurnAsync
/// </code>
///
/// <para><b>Execution Order:</b></para>
/// <para>
/// Before* hooks run in registration order.
/// After* hooks run in reverse registration order (stack unwinding).
/// </para>
///
/// <para><b>Blocking Execution:</b></para>
/// <list type="bullet">
/// <item>Set <c>SkipLLMCall = true</c> in BeforeIteration to skip LLM</item>
/// <item>Set <c>SkipToolExecution = true</c> in BeforeToolExecution to skip ALL tools</item>
/// <item>Set <c>BlockFunctionExecution = true</c> in BeforeFunction to skip ONE function</item>
/// </list>
///
/// <para><b>State Management:</b></para>
/// <para>
/// Use <c>context.State.GetState&lt;T&gt;()</c> to read typed middleware state.
/// Use <c>context.UpdateState&lt;T&gt;(transform)</c> to schedule state updates.
/// Updates are applied after the middleware chain completes.
/// </para>
///
/// <para><b>Bidirectional Events:</b></para>
/// <para>
/// Use <c>context.Emit(event)</c> to send events to external handlers.
/// Use <c>await context.WaitForResponseAsync&lt;T&gt;(requestId)</c> for request/response patterns.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class MyMiddleware : IAgentMiddleware
/// {
///     public Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CancellationToken ct)
///     {
///         Console.WriteLine($"About to call: {context.Function?.Name}");
///         return Task.CompletedTask;
///     }
///
///     public Task AfterFunctionAsync(AgentMiddlewareContext context, CancellationToken ct)
///     {
///         Console.WriteLine($"Result: {context.FunctionResult}");
///         return Task.CompletedTask;
///     }
/// }
/// </code>
/// </example>
public interface IAgentMiddleware
{
    //     
    // MESSAGE TURN LEVEL (run once per user message)
    //     

    /// <summary>
    /// Called BEFORE processing a user message turn.
    /// Use for: RAG injection, memory retrieval, context augmentation.
    /// </summary>
    /// <remarks>
    /// At this point:
    /// <list type="bullet">
    /// <item>UserMessage contains the incoming user message</item>
    /// <item>ConversationHistory contains prior messages</item>
    /// <item>No LLM call has been made yet</item>
    /// </list>
    /// </remarks>
    /// <param name="context">The middleware context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task BeforeMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Called AFTER a message turn completes (all iterations done).
    /// Use for: Memory extraction, analytics, turn-level logging.
    /// </summary>
    /// <remarks>
    /// At this point:
    /// <list type="bullet">
    /// <item>FinalResponse contains the assistant's final response</item>
    /// <item>TurnFunctionCalls contains all functions called during this turn</item>
    /// </list>
    /// </remarks>
    /// <param name="context">The middleware context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task AfterMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    //     
    // ITERATION LEVEL (run once per LLM call within a turn)
    //     

    /// <summary>
    /// Called BEFORE each LLM call within a turn.
    /// Use for: Dynamic instruction injection, caching, iteration-aware prompting.
    /// </summary>
    /// <remarks>
    /// <para>At this point:</para>
    /// <list type="bullet">
    /// <item>Messages contains the messages to send to LLM (mutable)</item>
    /// <item>Options contains chat options (mutable)</item>
    /// <item>Iteration indicates which LLM call this is (0-based)</item>
    /// </list>
    ///
    /// <para>Set <c>SkipLLMCall = true</c> in BeforeIterationAsync to skip the LLM call.
    /// Alternatively, override <c>ExecuteLLMCallAsync</c> for full streaming control.</para>
    /// </remarks>
    /// <param name="context">The middleware context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task BeforeIterationAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Executes the LLM call with full streaming control. Override for advanced scenarios.
    /// Use for: Caching, retry logic, response transformation, streaming interception.
    /// </summary>
    /// <remarks>
    /// <para><b>Advanced Hook - Most middleware won't need this!</b></para>
    /// <para>
    /// This hook gives middleware complete control over the LLM call, including:
    /// - Skipping the call entirely (caching)
    /// - Intercepting and modifying the streaming response
    /// - Implementing retry logic with backoff
    /// - Wrapping the call with custom error handling
    /// </para>
    ///
    /// <para><b>Execution Flow:</b></para>
    /// <list type="number">
    /// <item>BeforeIterationAsync runs (all middleware)</item>
    /// <item>ExecuteLLMCallAsync chains execute (registration order, like onion)</item>
    /// <item>Innermost call invokes actual LLM</item>
    /// <item>Response streams back through the chain</item>
    /// <item>AfterIterationAsync runs (all middleware)</item>
    /// </list>
    ///
    /// <para><b>Default Implementation:</b></para>
    /// <para>
    /// By default, this method just calls <c>next()</c> to pass through to the next middleware.
    /// The innermost call (when no more middleware) invokes the actual LLM via AgentTurn.
    /// </para>
    ///
    /// <para><b>Examples:</b></para>
    ///
    /// <para><b>Example 1: Caching</b></para>
    /// <code>
    /// public async IAsyncEnumerable&lt;ChatResponseUpdate&gt; ExecuteLLMCallAsync(
    ///     AgentMiddlewareContext context,
    ///     Func&lt;IAsyncEnumerable&lt;ChatResponseUpdate&gt;&gt; next,
    ///     [EnumeratorCancellation] CancellationToken ct)
    /// {
    ///     var cacheKey = ComputeKey(context.Messages);
    ///
    ///     if (_cache.TryGet(cacheKey, out var cached))
    ///     {
    ///         // Return cached response - NO LLM call!
    ///         yield return cached;
    ///         yield break;
    ///     }
    ///
    ///     // Cache miss - call LLM and cache result
    ///     var response = new List&lt;ChatResponseUpdate&gt;();
    ///     await foreach (var update in next().WithCancellation(ct))
    ///     {
    ///         response.Add(update);
    ///         yield return update;
    ///     }
    ///
    ///     _cache.Set(cacheKey, response);
    /// }
    /// </code>
    ///
    /// <para><b>Example 2: Retry with Backoff</b></para>
    /// <code>
    /// public async IAsyncEnumerable&lt;ChatResponseUpdate&gt; ExecuteLLMCallAsync(
    ///     AgentMiddlewareContext context,
    ///     Func&lt;IAsyncEnumerable&lt;ChatResponseUpdate&gt;&gt; next,
    ///     [EnumeratorCancellation] CancellationToken ct)
    /// {
    ///     for (int attempt = 0; attempt &lt; 3; attempt++)
    ///     {
    ///         Exception? error = null;
    ///
    ///         await foreach (var update in next().WithCancellation(ct))
    ///         {
    ///             try
    ///             {
    ///                 yield return update;
    ///             }
    ///             catch (RateLimitException ex)
    ///             {
    ///                 error = ex;
    ///                 break;
    ///             }
    ///         }
    ///
    ///         if (error == null) break; // Success!
    ///
    ///         if (attempt &lt; 2)
    ///         {
    ///             await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
    ///             // Retry
    ///         }
    ///         else
    ///         {
    ///             throw error; // Exhausted retries
    ///         }
    ///     }
    /// }
    /// </code>
    ///
    /// <para><b>Example 3: Token Counting</b></para>
    /// <code>
    /// public async IAsyncEnumerable&lt;ChatResponseUpdate&gt; ExecuteLLMCallAsync(
    ///     AgentMiddlewareContext context,
    ///     Func&lt;IAsyncEnumerable&lt;ChatResponseUpdate&gt;&gt; next,
    ///     [EnumeratorCancellation] CancellationToken ct)
    /// {
    ///     int tokenCount = 0;
    ///
    ///     await foreach (var update in next().WithCancellation(ct))
    ///     {
    ///         // Count tokens as they stream
    ///         if (update.Contents != null)
    ///         {
    ///             foreach (var content in update.Contents)
    ///             {
    ///                 if (content is TextContent text)
    ///                     tokenCount += EstimateTokens(text.Text);
    ///             }
    ///         }
    ///
    ///         yield return update;
    ///     }
    ///
    ///     context.Emit(new TokenUsageEvent(tokenCount));
    /// }
    /// </code>
    ///
    /// <para><b>Important Notes:</b></para>
    /// <list type="bullet">
    /// <item>You MUST call <c>next()</c> unless you're completely skipping the LLM call</item>
    /// <item>You MUST yield every update from <c>next()</c> to preserve streaming</item>
    /// <item>Use <c>[EnumeratorCancellation]</c> attribute on cancellationToken parameter</item>
    /// <item>Middleware chains execute in REGISTRATION order (first registered = outermost)</item>
    /// <item>For simple skip logic, use <c>SkipLLMCall</c> in BeforeIterationAsync instead</item>
    /// </list>
    /// </remarks>
    /// <param name="context">The middleware context with full agent state</param>
    /// <param name="next">The next middleware in the chain (or actual LLM call)</param>
    /// <param name="cancellationToken">Cancellation token (use [EnumeratorCancellation])</param>
    /// <returns>Streaming LLM response updates</returns>
    IAsyncEnumerable<ChatResponseUpdate> ExecuteLLMCallAsync(
        AgentMiddlewareContext context,
        Func<IAsyncEnumerable<ChatResponseUpdate>> next,
        CancellationToken cancellationToken)
    {
        // Default: pass through to next middleware
        return next();
    }

    /// <summary>
    /// Called AFTER LLM returns but BEFORE any tools execute.
    /// Use for: Circuit breaker, batch validation, pre-execution guards.
    /// </summary>
    /// <remarks>
    /// <para>At this point:</para>
    /// <list type="bullet">
    /// <item>Response is populated with LLM output</item>
    /// <item>ToolCalls contains pending function calls</item>
    /// </list>
    ///
    /// <para>Set <c>SkipToolExecution = true</c> to prevent ALL tools from running.
    /// When skipping, set Response with an appropriate message.</para>
    /// </remarks>
    /// <param name="context">The middleware context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task BeforeToolExecutionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Called AFTER all tools complete for this iteration.
    /// Use for: Error tracking, result analysis, state updates.
    /// </summary>
    /// <remarks>
    /// At this point:
    /// <list type="bullet">
    /// <item>ToolResults contains all function execution outcomes</item>
    /// <item>Can inspect for errors, update state, emit events</item>
    /// </list>
    /// </remarks>
    /// <param name="context">The middleware context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task AfterIterationAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    //     
    // FUNCTION LEVEL (run once per function call)
    //     

    /// <summary>
    /// Called BEFORE a batch of functions executes in parallel.
    /// Use for: Batch permission checking, resource reservation, parallel execution guards.
    /// </summary>
    /// <remarks>
    /// <para><b>Purpose:</b></para>
    /// <para>
    /// This hook is called ONCE when multiple functions are about to execute in parallel,
    /// allowing middleware to handle them as a batch instead of individually.
    /// This is critical for scenarios like permission systems where you want to request
    /// approval for all functions at once, rather than showing duplicate permission prompts.
    /// </para>
    ///
    /// <para><b>Execution Flow:</b></para>
    /// <list type="number">
    /// <item>System detects parallel function execution</item>
    /// <item>BeforeParallelFunctionsAsync called ONCE with all functions</item>
    /// <item>Middleware can populate shared state (e.g., batch permission approvals)</item>
    /// <item>BeforeSequentialFunctionAsync called for each function (can check shared state)</item>
    /// <item>Functions execute in parallel</item>
    /// <item>AfterFunctionAsync called for each function</item>
    /// </list>
    ///
    /// <para><b>At this point:</b></para>
    /// <list type="bullet">
    /// <item>ParallelFunctions contains all functions about to execute in parallel</item>
    /// <item>Can inspect function names, descriptions, arguments</item>
    /// <item>Can populate middleware state to be checked in BeforeSequentialFunctionAsync</item>
    /// <item>Can emit batch events (e.g., batch permission request)</item>
    /// </list>
    ///
    /// <para><b>Common Use Cases:</b></para>
    /// <list type="bullet">
    /// <item><b>Batch Permissions:</b> Request approval for all functions at once</item>
    /// <item><b>Resource Allocation:</b> Reserve resources needed for parallel execution</item>
    /// <item><b>Validation:</b> Check if parallel execution is allowed</item>
    /// <item><b>Optimization:</b> Pre-fetch data needed by multiple functions</item>
    /// </list>
    ///
    /// <para><b>State Management Pattern:</b></para>
    /// <code>
    /// public async Task BeforeParallelFunctionsAsync(context, ct)
    /// {
    ///     // Request approval for ALL functions at once
    ///     var approvals = await RequestBatchApproval(context.ParallelFunctions);
    ///
    ///     // Populate state with approvals
    ///     var batchState = new BatchState { Approvals = approvals };
    ///     context.UpdateState(s => s.WithBatchState(batchState));
    /// }
    ///
    /// public async Task BeforeSequentialFunctionAsync(context, ct)
    /// {
    ///     // Check state populated by batch hook
    ///     var batchState = context.State.MiddlewareState.BatchState;
    ///     if (!batchState.Approvals.Contains(context.Function.Name))
    ///     {
    ///         context.BlockFunctionExecution = true;
    ///         context.FunctionResult = "Permission denied";
    ///     }
    /// }
    /// </code>
    ///
    /// <para><b>Important Notes:</b></para>
    /// <list type="bullet">
    /// <item>Only called for parallel execution - NOT for single/sequential functions</item>
    /// <item>BeforeSequentialFunctionAsync is STILL called for each function after this</item>
    /// <item>Use context.UpdateState() to share information with BeforeSequentialFunctionAsync</item>
    /// <item>If you block execution, do it in BeforeSequentialFunctionAsync, not here</item>
    /// </list>
    /// </remarks>
    /// <param name="context">The middleware context with ParallelFunctions populated</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task BeforeParallelFunctionsAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Called BEFORE a specific function executes.
    /// Use for: Permission checking, argument validation, per-function guards.
    /// </summary>
    /// <remarks>
    /// <para>At this point:</para>
    /// <list type="bullet">
    /// <item>Function contains the AIFunction being called</item>
    /// <item>FunctionCallId identifies this specific invocation</item>
    /// <item>FunctionArguments contains the arguments</item>
    /// </list>
    ///
    /// <para>Set <c>BlockFunctionExecution = true</c> to prevent THIS function from running.
    /// When blocking, set FunctionResult to provide the result without execution.</para>
    /// </remarks>
    /// <param name="context">The middleware context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Executes the function call with full control over execution.
    /// Override for advanced scenarios like retry, caching, timeout, transformation.
    /// </summary>
    /// <remarks>
    /// <para><b>Advanced Hook - Most middleware won't need this!</b></para>
    /// <para>
    /// This hook gives middleware complete control over function execution, including:
    /// - Implementing retry logic with backoff
    /// - Caching function results
    /// - Transforming arguments or results
    /// - Wrapping with timeout, telemetry, etc.
    /// </para>
    ///
    /// <para><b>Execution Flow:</b></para>
    /// <list type="number">
    /// <item>BeforeSequentialFunctionAsync runs (all middleware)</item>
    /// <item>ExecuteFunctionAsync chains execute (registration order, like onion)</item>
    /// <item>Innermost call invokes actual function</item>
    /// <item>Result bubbles back through the chain</item>
    /// <item>AfterFunctionAsync runs (all middleware)</item>
    /// </list>
    ///
    /// <para><b>Default Implementation:</b></para>
    /// <para>
    /// By default, this method just calls <c>next()</c> to pass through to the next middleware.
    /// The innermost call (when no more middleware) invokes the actual function.
    /// </para>
    ///
    /// <para><b>Onion Architecture:</b></para>
    /// <para>
    /// Middlewares wrap each other in registration order:
    /// - First registered middleware wraps everything (outermost)
    /// - Last registered middleware is closest to the actual function (innermost)
    /// </para>
    ///
    /// <para><b>Examples:</b></para>
    ///
    /// <para><b>Example 1: Retry with Backoff</b></para>
    /// <code>
    /// public async ValueTask&lt;object?&gt; ExecuteFunctionAsync(
    ///     AgentMiddlewareContext context,
    ///     Func&lt;ValueTask&lt;object?&gt;&gt; next,
    ///     CancellationToken ct)
    /// {
    ///     for (int attempt = 0; attempt &lt; 3; attempt++)
    ///     {
    ///         try
    ///         {
    ///             return await next(); // Call next middleware or function
    ///         }
    ///         catch (HttpRequestException) when (attempt &lt; 2)
    ///         {
    ///             await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
    ///         }
    ///     }
    /// }
    /// </code>
    ///
    /// <para><b>Example 2: Caching</b></para>
    /// <code>
    /// public async ValueTask&lt;object?&gt; ExecuteFunctionAsync(
    ///     AgentMiddlewareContext context,
    ///     Func&lt;ValueTask&lt;object?&gt;&gt; next,
    ///     CancellationToken ct)
    /// {
    ///     var cacheKey = GetCacheKey(context.Function.Name, context.FunctionArguments);
    ///
    ///     if (_cache.TryGet(cacheKey, out var cached))
    ///         return cached; // Cache hit - skip execution
    ///
    ///     var result = await next(); // Cache miss - execute
    ///     _cache.Set(cacheKey, result);
    ///     return result;
    /// }
    /// </code>
    ///
    /// <para><b>Example 3: Timeout</b></para>
    /// <code>
    /// public async ValueTask&lt;object?&gt; ExecuteFunctionAsync(
    ///     AgentMiddlewareContext context,
    ///     Func&lt;ValueTask&lt;object?&gt;&gt; next,
    ///     CancellationToken ct)
    /// {
    ///     using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    ///     cts.CancelAfter(TimeSpan.FromSeconds(30));
    ///
    ///     try
    ///     {
    ///         return await next();
    ///     }
    ///     catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    ///     {
    ///         throw new TimeoutException("Function timed out");
    ///     }
    /// }
    /// </code>
    ///
    /// <para><b>Important Notes:</b></para>
    /// <list type="bullet">
    /// <item>You MUST call <c>next()</c> unless you're completely skipping execution (e.g., cache hit)</item>
    /// <item>Middleware chains execute in REGISTRATION order (first registered = outermost)</item>
    /// <item>For simple guards, use BeforeSequentialFunctionAsync instead</item>
    /// <item>For simple result transformation, use AfterFunctionAsync instead</item>
    /// </list>
    /// </remarks>
    /// <param name="context">The middleware context with function info</param>
    /// <param name="next">The next middleware in the chain (or actual function execution)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function result</returns>
    ValueTask<object?> ExecuteFunctionAsync(
        AgentMiddlewareContext context,
        Func<ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        // Default: pass through to next middleware
        return next();
    }

    /// <summary>
    /// Called AFTER a specific function completes.
    /// Use for: Result transformation, per-function logging, telemetry.
    /// </summary>
    /// <remarks>
    /// At this point:
    /// <list type="bullet">
    /// <item>FunctionResult contains the execution result (can be modified)</item>
    /// <item>FunctionException contains any error (if failed)</item>
    /// </list>
    /// </remarks>
    /// <param name="context">The middleware context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task AfterFunctionAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
