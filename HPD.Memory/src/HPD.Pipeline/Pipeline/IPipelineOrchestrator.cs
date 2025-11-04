// Copyright (c) Einstein Essibu. All rights reserved.
// Inspired by Microsoft Kernel Memory, enhanced with modern patterns.

namespace HPD.Pipeline;

/// <summary>
/// Generic pipeline orchestrator that executes pipeline handlers in sequence.
/// Inspired by Kernel Memory's IPipelineOrchestrator but made generic and more flexible.
/// Domain-agnostic - works for RAG, video processing, trading, ETL, or any workflow.
/// </summary>
/// <typeparam name="TContext">The pipeline context type (must implement IPipelineContext)</typeparam>
/// <remarks>
/// The orchestrator is responsible for:
/// 1. Managing pipeline execution (running handlers in order)
/// 2. Tracking pipeline state (steps completed, remaining)
/// 3. Handling errors and retries
/// 4. Persisting pipeline state (for distributed execution)
///
/// Key differences from Kernel Memory:
/// 1. Generic over TContext - supports ingestion, retrieval, or any custom pipeline
/// 2. Simpler interface - focused on core orchestration
/// 3. Separated concerns - state management, queuing, etc. handled by implementations
///
/// Two main implementations:
/// - InProcessOrchestrator: Synchronous, in-memory execution
/// - DistributedOrchestrator: Asynchronous, queue-based execution
/// </remarks>
public interface IPipelineOrchestrator<TContext> where TContext : IPipelineContext
{
    /// <summary>
    /// List of handler names registered with this orchestrator.
    /// Useful for debugging and validation.
    /// </summary>
    IReadOnlyList<string> HandlerNames { get; }

    /// <summary>
    /// Register a handler for a specific pipeline step.
    /// Throws if a handler with the same step name already exists.
    /// </summary>
    /// <param name="handler">The handler to register</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <exception cref="ArgumentException">If handler with same step name already registered</exception>
    Task AddHandlerAsync(IPipelineHandler<TContext> handler, CancellationToken cancellationToken = default);

    /// <summary>
    /// Try to register a handler, silently ignoring if already registered.
    /// Useful for optional handlers or when building pipelines programmatically.
    /// </summary>
    /// <param name="handler">The handler to register</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task TryAddHandlerAsync(IPipelineHandler<TContext> handler, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute the pipeline by running all remaining steps in sequence.
    /// </summary>
    /// <param name="context">
    /// The pipeline context containing all data, state, and configuration.
    /// The context is modified as the pipeline executes.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The final context after all steps complete</returns>
    /// <exception cref="PipelineException">If pipeline execution fails</exception>
    /// <remarks>
    /// For in-process orchestrators, this runs all steps synchronously.
    /// For distributed orchestrators, this enqueues the first step and returns.
    /// The pipeline continues asynchronously via message queue handlers.
    /// </remarks>
    Task<TContext> ExecuteAsync(TContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read pipeline status from storage (for distributed orchestrators).
    /// Returns null if pipeline not found.
    /// </summary>
    /// <param name="index">Index/collection name</param>
    /// <param name="pipelineId">Pipeline identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<TContext?> ReadPipelineStatusAsync(
        string index,
        string pipelineId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a pipeline execution has completed.
    /// Useful for polling pipeline status.
    /// </summary>
    /// <param name="index">Index/collection name</param>
    /// <param name="pipelineId">Pipeline identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<bool> IsCompletedAsync(
        string index,
        string pipelineId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a running pipeline.
    /// For in-process orchestrators, sets a cancellation flag.
    /// For distributed orchestrators, marks pipeline as cancelled in storage.
    /// </summary>
    /// <param name="index">Index/collection name</param>
    /// <param name="pipelineId">Pipeline identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CancelPipelineAsync(
        string index,
        string pipelineId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop all pipelines and clean up resources.
    /// For distributed orchestrators, stops listening to message queues.
    /// </summary>
    Task StopAllPipelinesAsync();
}

/// <summary>
/// Exception thrown when pipeline execution fails.
/// </summary>
public class PipelineException : Exception
{
    /// <summary>
    /// Whether this is a transient (retryable) error.
    /// </summary>
    public bool IsTransient { get; }

    /// <summary>
    /// The pipeline step that failed.
    /// </summary>
    public string? StepName { get; }

    public PipelineException(string message, bool isTransient = false, string? stepName = null)
        : base(message)
    {
        IsTransient = isTransient;
        StepName = stepName;
    }

    public PipelineException(string message, Exception innerException, bool isTransient = false, string? stepName = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
        StepName = stepName;
    }
}

/// <summary>
/// Exception thrown when pipeline is not found in storage.
/// </summary>
public class PipelineNotFoundException : PipelineException
{
    public PipelineNotFoundException(string index, string pipelineId)
        : base($"Pipeline '{pipelineId}' not found in index '{index}'", isTransient: false)
    {
    }
}

/// <summary>
/// Exception thrown when pipeline data is invalid or corrupted.
/// </summary>
public class InvalidPipelineDataException : PipelineException
{
    public InvalidPipelineDataException(string message, Exception? innerException = null)
        : base(message, innerException ?? new InvalidOperationException(message), isTransient: false)
    {
    }
}
