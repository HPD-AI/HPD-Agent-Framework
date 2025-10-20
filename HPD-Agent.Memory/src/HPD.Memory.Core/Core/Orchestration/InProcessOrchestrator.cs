// Copyright (c) Einstein Essibu. All rights reserved.
// In-process (synchronous) pipeline orchestrator.
// Inspired by Kernel Memory's InProcessPipelineOrchestrator but generic and cleaner.

using HPDAgent.Memory.Abstractions.Pipeline;
using Microsoft.Extensions.Logging;

namespace HPDAgent.Memory.Core.Orchestration;

/// <summary>
/// In-process pipeline orchestrator that executes handlers synchronously in sequence.
/// Inspired by Kernel Memory's InProcessPipelineOrchestrator but improved:
/// - Generic over TContext (works with any pipeline type)
/// - Cleaner separation of concerns (no file I/O, no service exposure)
/// - Better error handling (rich PipelineResult instead of enum)
/// - Standard DI patterns (no special service provider handling)
/// </summary>
/// <typeparam name="TContext">Pipeline context type</typeparam>
public class InProcessOrchestrator<TContext> : IPipelineOrchestrator<TContext>
    where TContext : IPipelineContext
{
    private readonly Dictionary<string, IPipelineHandler<TContext>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<InProcessOrchestrator<TContext>> _logger;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    /// <summary>
    /// Create a new in-process orchestrator.
    /// </summary>
    /// <param name="logger">Logger for orchestration events</param>
    public InProcessOrchestrator(ILogger<InProcessOrchestrator<TContext>> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> HandlerNames => _handlers.Keys.OrderBy(x => x).ToList();

    /// <inheritdoc />
    public Task AddHandlerAsync(IPipelineHandler<TContext> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (string.IsNullOrWhiteSpace(handler.StepName))
        {
            throw new ArgumentException("Handler step name cannot be empty", nameof(handler));
        }

        if (!_handlers.TryAdd(handler.StepName, handler))
        {
            throw new ArgumentException($"Handler for step '{handler.StepName}' is already registered");
        }

        _logger.LogInformation("Registered handler '{StepName}' ({HandlerType})",
            handler.StepName, handler.GetType().Name);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task TryAddHandlerAsync(IPipelineHandler<TContext> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (string.IsNullOrWhiteSpace(handler.StepName))
        {
            _logger.LogWarning("Attempted to add handler with empty step name, ignoring");
            return Task.CompletedTask;
        }

        if (_handlers.ContainsKey(handler.StepName))
        {
            _logger.LogDebug("Handler '{StepName}' already registered, skipping", handler.StepName);
            return Task.CompletedTask;
        }

        try
        {
            _handlers.Add(handler.StepName, handler);
            _logger.LogInformation("Registered handler '{StepName}' ({HandlerType})",
                handler.StepName, handler.GetType().Name);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to add handler '{StepName}', ignoring", handler.StepName);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<TContext> ExecuteAsync(TContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        _logger.LogInformation(
            "Starting pipeline execution: {PipelineId} in index {Index} with {StepCount} steps",
            context.PipelineId, context.Index, context.Steps.Count);

        try
        {
            // Remaining steps should already be initialized by the context builder
            // Nothing to do here

            // Execute pipeline steps
            while (!context.IsComplete)
            {
                // Check for cancellation
                if (_cancellationTokenSource.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Pipeline execution was cancelled");
                }

                var currentStep = context.CurrentStep;
                if (currentStep == null)
                {
                    break; // No more steps
                }

                if (currentStep.IsParallel)
                {
                    // Execute parallel step
                    await ExecuteParallelStepAsync(context, (ParallelStep)currentStep, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Execute sequential step
                    await ExecuteSequentialStepAsync(context, (SequentialStep)currentStep, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Move to next step after successful execution
                context.MoveToNextStep();
            }

            _logger.LogInformation(
                "Pipeline {PipelineId} completed successfully. Executed {StepCount} steps.",
                context.PipelineId, context.CompletedSteps.Count);

            return context;
        }
        catch (Exception ex) when (ex is not PipelineException and not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Unexpected error during pipeline {PipelineId} execution",
                context.PipelineId);

            throw new PipelineException(
                $"Unexpected error: {ex.Message}",
                ex,
                isTransient: false);
        }
    }

    /// <inheritdoc />
    public Task<TContext?> ReadPipelineStatusAsync(
        string index,
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        // In-process orchestrator doesn't persist state
        // This is only for distributed orchestrators
        _logger.LogWarning(
            "ReadPipelineStatusAsync called on in-process orchestrator. " +
            "In-process orchestrators don't persist state. Returning null.");

        return Task.FromResult<TContext?>(default);
    }

    /// <inheritdoc />
    public Task<bool> IsCompletedAsync(
        string index,
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        // In-process orchestrator doesn't track completion externally
        _logger.LogWarning(
            "IsCompletedAsync called on in-process orchestrator. " +
            "In-process orchestrators execute synchronously. Returning false.");

        return Task.FromResult(false);
    }

    /// <summary>
    /// Execute a sequential step (single handler).
    /// </summary>
    private async Task ExecuteSequentialStepAsync(
        TContext context,
        SequentialStep step,
        CancellationToken cancellationToken)
    {
        var handlerName = step.HandlerName;

        _logger.LogDebug(
            "Executing sequential step '{StepName}' for pipeline {PipelineId} ({Completed}/{Total})",
            handlerName, context.PipelineId, context.CurrentStepIndex, context.TotalSteps);

        // Find handler
        if (!_handlers.TryGetValue(handlerName, out var handler))
        {
            var message = $"No handler registered for step '{handlerName}'. " +
                          $"Available handlers: {string.Join(", ", _handlers.Keys)}";
            _logger.LogError(message);
            throw new PipelineException(message, isTransient: false, stepName: handlerName);
        }

        // Execute handler
        PipelineResult result;
        try
        {
            result = await handler.HandleAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Handler '{StepName}' threw exception for pipeline {PipelineId}",
                handlerName, context.PipelineId);

            throw new PipelineException(
                $"Handler '{handlerName}' threw exception: {ex.Message}",
                ex,
                isTransient: false,
                stepName: handlerName);
        }

        // Process result
        if (!result.IsSuccess)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown error";
            _logger.LogError(
                "Handler '{StepName}' failed for pipeline {PipelineId}: {Error} (Transient: {IsTransient})",
                handlerName, context.PipelineId, errorMsg, result.IsTransient);

            throw new PipelineException(
                $"Handler '{handlerName}' failed: {errorMsg}",
                result.Exception ?? new Exception(errorMsg),
                isTransient: result.IsTransient,
                stepName: handlerName);
        }

        _logger.LogInformation(
            "Handler '{StepName}' completed successfully for pipeline {PipelineId}",
            handlerName, context.PipelineId);

        // Log metadata if present
        if (result.Metadata != null && result.Metadata.Count > 0)
        {
            _logger.LogDebug(
                "Handler '{StepName}' metadata: {Metadata}",
                handlerName, result.Metadata);
        }
    }

    /// <summary>
    /// Execute a parallel step (multiple handlers with isolation).
    /// </summary>
    private async Task ExecuteParallelStepAsync(
        TContext context,
        ParallelStep step,
        CancellationToken cancellationToken)
    {
        var handlerNames = step.HandlerNames;

        _logger.LogInformation(
            "Executing parallel step with {HandlerCount} handlers for pipeline {PipelineId}: {Handlers}",
            handlerNames.Count, context.PipelineId, string.Join(", ", handlerNames));

        // Find all handlers
        var handlers = new List<IPipelineHandler<TContext>>();
        foreach (var handlerName in handlerNames)
        {
            if (!_handlers.TryGetValue(handlerName, out var handler))
            {
                var message = $"No handler registered for parallel step handler '{handlerName}'. " +
                              $"Available handlers: {string.Join(", ", _handlers.Keys)}";
                _logger.LogError(message);
                throw new PipelineException(message, isTransient: false, stepName: handlerName);
            }
            handlers.Add(handler);
        }

        // Create isolated contexts for each handler
        var isolatedContexts = handlers.Select(_ => context.CreateIsolatedCopy()).ToList();

        _logger.LogDebug(
            "Created {Count} isolated contexts for parallel execution",
            isolatedContexts.Count);

        // Execute all handlers in parallel with their isolated contexts
        var tasks = new List<Task<(string HandlerName, TContext Context, PipelineResult Result, Exception? Exception)>>();

        for (int i = 0; i < handlers.Count; i++)
        {
            var handler = handlers[i];
            var isolatedContext = (TContext)isolatedContexts[i];
            var handlerName = handlerNames[i];

            tasks.Add(ExecuteHandlerWithIsolationAsync(handler, handlerName, isolatedContext, cancellationToken));
        }

        // Wait for all handlers to complete
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Check for failures (all-or-nothing policy)
        var failures = results.Where(r => r.Exception != null || !r.Result.IsSuccess).ToList();
        if (failures.Any())
        {
            var failedHandlers = string.Join(", ", failures.Select(f => f.HandlerName));
            var firstFailure = failures.First();

            _logger.LogError(
                "Parallel step failed: {FailedCount}/{TotalCount} handlers failed ({FailedHandlers})",
                failures.Count, handlers.Count, failedHandlers);

            var errorMsg = firstFailure.Exception?.Message ?? firstFailure.Result.ErrorMessage ?? "Unknown error";
            throw new PipelineException(
                $"Parallel step failed: {failures.Count} handler(s) failed. First failure: {errorMsg}",
                firstFailure.Exception ?? firstFailure.Result.Exception ?? new Exception(errorMsg),
                isTransient: firstFailure.Result.IsTransient,
                stepName: $"Parallel({failedHandlers})");
        }

        // All handlers succeeded - merge results back into main context
        _logger.LogDebug("Merging {Count} isolated contexts back into main context", results.Length);

        foreach (var (handlerName, isolatedContext, result, _) in results)
        {
            context.MergeFrom(isolatedContext);
            context.MarkHandlerComplete(handlerName);

            _logger.LogInformation(
                "Handler '{HandlerName}' completed successfully in parallel step",
                handlerName);

            // Log metadata if present
            if (result.Metadata != null && result.Metadata.Count > 0)
            {
                _logger.LogDebug(
                    "Handler '{HandlerName}' metadata: {Metadata}",
                    handlerName, result.Metadata);
            }
        }

        _logger.LogInformation(
            "Parallel step completed successfully: {HandlerCount} handlers executed",
            handlers.Count);
    }

    /// <summary>
    /// Execute a single handler with isolated context, capturing all exceptions.
    /// </summary>
    private async Task<(string HandlerName, TContext Context, PipelineResult Result, Exception? Exception)>
        ExecuteHandlerWithIsolationAsync(
            IPipelineHandler<TContext> handler,
            string handlerName,
            TContext isolatedContext,
            CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug(
                "Executing handler '{HandlerName}' with isolated context",
                handlerName);

            var result = await handler.HandleAsync(isolatedContext, cancellationToken)
                .ConfigureAwait(false);

            return (handlerName, isolatedContext, result, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Handler '{HandlerName}' threw exception in parallel step",
                handlerName);

            // Return failure result with exception
            var failureResult = PipelineResult.Failure(
                $"Handler threw exception: {ex.Message}",
                isTransient: false,
                exception: ex);

            return (handlerName, isolatedContext, failureResult, ex);
        }
    }

    /// <inheritdoc />
    public Task CancelPipelineAsync(
        string index,
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Cancelling pipeline {PipelineId} in index {Index}",
            pipelineId, index);

        _cancellationTokenSource.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAllPipelinesAsync()
    {
        _logger.LogInformation("Stopping all pipelines");
        _cancellationTokenSource.Cancel();
        return Task.CompletedTask;
    }


    /// <summary>
    /// Dispose resources.
    /// </summary>
    public void Dispose()
    {
        _cancellationTokenSource.Dispose();
    }
}
