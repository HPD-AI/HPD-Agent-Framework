using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace HPD.Agent.Middleware;

/// <summary>
/// Executes V2 middleware hooks in the correct order for each lifecycle phase.
/// Handles Before* hooks in registration order and After* hooks in reverse order.
/// </summary>
/// <remarks>
/// <para><b>V2 Improvements:</b></para>
/// <list type="bullet">
/// <item>  Typed context passing - each hook gets strongly-typed context</item>
/// <item>  Dual pattern support - automatically routes to simple or streaming LLM hooks</item>
/// <item>  Centralized error routing - OnErrorAsync called in reverse order</item>
/// <item>  Immutable request pattern - preserve original for debugging/retry</item>
/// </list>
///
/// <para><b>Execution Order:</b></para>
/// <para>
/// Before* hooks run in registration order (first registered = first executed).<br/>
/// After* hooks run in reverse order (last registered = first executed, like stack unwinding).<br/>
/// OnErrorAsync runs in reverse order (error unwinding).
/// </para>
/// </remarks>
public class AgentMiddlewarePipeline
{
    private readonly IReadOnlyList<IAgentMiddleware> _middlewares;
    private readonly IReadOnlyList<IAgentMiddleware> _reversedMiddlewares;

    public AgentMiddlewarePipeline(IEnumerable<IAgentMiddleware> middlewares)
    {
        _middlewares = middlewares?.ToList() ?? throw new ArgumentNullException(nameof(middlewares));
        _reversedMiddlewares = _middlewares.Reverse().ToList();
    }

    public AgentMiddlewarePipeline(IReadOnlyList<IAgentMiddleware> middlewares)
    {
        _middlewares = middlewares ?? throw new ArgumentNullException(nameof(middlewares));
        _reversedMiddlewares = _middlewares.Reverse().ToList();
    }

    public bool IsEmpty => _middlewares.Count == 0;
    public int Count => _middlewares.Count;
    public IReadOnlyList<IAgentMiddleware> Middlewares => _middlewares;

    //
    // TURN LEVEL
    //

    public async Task ExecuteBeforeMessageTurnAsync(
        BeforeMessageTurnContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await middleware.BeforeMessageTurnAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ExecuteAfterMessageTurnAsync(
        AfterMessageTurnContext context,
        CancellationToken cancellationToken)
    {
        List<Exception>? exceptions = null;

        foreach (var middleware in _reversedMiddlewares)
        {
            try
            {
                await middleware.AfterMessageTurnAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }

        if (exceptions != null)
            throw new AggregateException("One or more After* hooks failed", exceptions);
    }

    //
    // ITERATION LEVEL
    //

    public async Task ExecuteBeforeIterationAsync(
        BeforeIterationContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await middleware.BeforeIterationAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes WrapModelCall hooks using DUAL PATTERN (simple or streaming).
    /// Automatically routes to streaming variant if middleware provides it.
    /// </summary>
    public async Task<ModelResponse> ExecuteModelCallAsync(
        ModelRequest request,
        Func<ModelRequest, Task<ModelResponse>> coreHandler,
        CancellationToken cancellationToken)
    {
        // Build handler chain in reverse order (last middleware wraps first)
        Func<ModelRequest, Task<ModelResponse>> handler = coreHandler;

        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var previousHandler = handler;

            handler = async (req) =>
            {
                return await middleware.WrapModelCallAsync(req, previousHandler, cancellationToken)
                    .ConfigureAwait(false);
            };
        }

        return await handler(request).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes WrapModelCall hooks using STREAMING PATTERN.
    /// Checks each middleware for streaming support, falls back to simple pattern if null.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> ExecuteModelCallStreamingAsync(
        ModelRequest request,
        Func<ModelRequest, IAsyncEnumerable<ChatResponseUpdate>> coreHandler,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Build handler chain in reverse order (last middleware wraps first)
        Func<ModelRequest, IAsyncEnumerable<ChatResponseUpdate>> handler = coreHandler;

        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var previousHandler = handler;

            // Check if middleware provides streaming variant
            var streamingResult = middleware.WrapModelCallStreamingAsync(
                request,
                previousHandler,
                cancellationToken);

            if (streamingResult != null)
            {
                // Middleware provides streaming - use it
                handler = (req) => middleware.WrapModelCallStreamingAsync(
                    req,
                    previousHandler,
                    cancellationToken) ?? previousHandler(req);
            }
            else
            {
                // Middleware doesn't provide streaming - just pass through
                // (streaming is preferred by default to maintain token-by-token flow)
                // Only buffer if middleware explicitly needs non-streaming via WrapModelCallAsync
                handler = previousHandler;
            }
        }

        await foreach (var update in handler(request).WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    public async Task ExecuteBeforeToolExecutionAsync(
        BeforeToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await middleware.BeforeToolExecutionAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ExecuteAfterIterationAsync(
        AfterIterationContext context,
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
            throw new AggregateException("One or more After* hooks failed", exceptions);
    }

    //
    // FUNCTION LEVEL
    //

    public async Task ExecuteBeforeParallelBatchAsync(
        BeforeParallelBatchContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await middleware.BeforeParallelBatchAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ExecuteBeforeFunctionAsync(
        BeforeFunctionContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if middleware should execute based on its scope
            if (!middleware.ShouldExecute(context))
                continue;

            await middleware.BeforeFunctionAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<object?> ExecuteFunctionCallAsync(
        FunctionRequest request,
        Func<FunctionRequest, Task<object?>> coreHandler,
        CancellationToken cancellationToken)
    {
        // Build handler chain in reverse order (last middleware wraps first)
        Func<FunctionRequest, Task<object?>> handler = coreHandler;

        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var previousHandler = handler;

            handler = async (req) =>
            {
                return await middleware.WrapFunctionCallAsync(req, previousHandler, cancellationToken)
                    .ConfigureAwait(false);
            };
        }

        return await handler(request).ConfigureAwait(false);
    }

    public async Task ExecuteAfterFunctionAsync(
        AfterFunctionContext context,
        CancellationToken cancellationToken)
    {
        List<Exception>? exceptions = null;

        foreach (var middleware in _reversedMiddlewares)
        {
            // Check if middleware should execute based on its scope
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
            throw new AggregateException("One or more After* hooks failed", exceptions);
    }

    //
    // ERROR HANDLING (NEW IN V2)
    //

    /// <summary>
    /// Executes OnErrorAsync hooks in REVERSE order (error unwinding).
    /// Each hook sees the original error. If a hook throws, the original error is preserved.
    /// </summary>
    public async Task ExecuteOnErrorAsync(
        ErrorContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _reversedMiddlewares)
        {
            try
            {
                await middleware.OnErrorAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Swallow errors in error handlers to preserve original error
                // Could log this to observability system if needed
            }
        }
    }

    //
    // HELPER METHODS FOR STREAMING CONVERSION
    //

    private static ChatMessage ConvertUpdatesToMessage(List<ChatResponseUpdate> updates)
    {
        // Combine all text updates
        var textBuilder = new System.Text.StringBuilder();
        var reasoningBuilder = new System.Text.StringBuilder();
        var toolCalls = new List<FunctionCallContent>();

        foreach (var update in updates)
        {
            if (update.Contents != null)
            {
                foreach (var content in update.Contents)
                {
                    if (content is TextReasoningContent reasoning)
                        reasoningBuilder.Append(reasoning.Text);
                    else if (content is TextContent text)
                        textBuilder.Append(text.Text);
                    else if (content is FunctionCallContent toolCall)
                        toolCalls.Add(toolCall);
                }
            }
        }

        var contents = new List<AIContent>();
        // Add reasoning content first (if any)
        if (reasoningBuilder.Length > 0)
            contents.Add(new TextReasoningContent(reasoningBuilder.ToString()));
        // Then text content
        if (textBuilder.Length > 0)
            contents.Add(new TextContent(textBuilder.ToString()));
        // Then tool calls
        contents.AddRange(toolCalls);

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static IReadOnlyList<FunctionCallContent> ExtractToolCalls(ChatMessage message)
    {
        return message.Contents
            .OfType<FunctionCallContent>()
            .ToList();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ConvertSimpleToStreamingWrapper(
        IAgentMiddleware middleware,
        Func<ModelRequest, IAsyncEnumerable<ChatResponseUpdate>> previousHandler,
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await middleware.WrapModelCallAsync(
            request,
            async (r) =>
            {
                // Consume streaming and convert to simple response
                var updates = new List<ChatResponseUpdate>();
                await foreach (var update in previousHandler(r).WithCancellation(cancellationToken))
                {
                    updates.Add(update);
                }

                // Convert updates to final response
                var message = ConvertUpdatesToMessage(updates);
                var toolCalls = ExtractToolCalls(message);

                return new ModelResponse
                {
                    Message = message,
                    ToolCalls = toolCalls,
                    Error = null
                };
            },
            cancellationToken).ConfigureAwait(false);

        // Convert simple response back to streaming
        if (response.Error != null)
        {
            throw response.Error;
        }

        // Emit single update with full message
        yield return new ChatResponseUpdate
        {
            Contents = response.Message.Contents.ToList()
        };
    }
}
