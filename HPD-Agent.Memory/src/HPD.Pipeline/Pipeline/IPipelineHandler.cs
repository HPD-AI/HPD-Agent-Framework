// Copyright (c) Einstein Essibu. All rights reserved.
// Inspired by Microsoft Kernel Memory, enhanced with modern patterns.

namespace HPD.Pipeline;

/// <summary>
/// Generic pipeline handler interface that works with any pipeline context type.
/// Inspired by Kernel Memory's IPipelineStepHandler but more flexible with generics.
/// Domain-agnostic - works for RAG, video processing, trading, ETL, or any workflow.
/// </summary>
/// <typeparam name="TContext">The pipeline context type (must implement IPipelineContext)</typeparam>
/// <remarks>
/// Handlers are the building blocks of pipelines. Each handler performs one specific task
/// in the pipeline and can modify the context or perform side effects.
///
/// Key differences from Kernel Memory's IPipelineStepHandler:
/// 1. Generic over TContext - works with ingestion, retrieval, or any custom pipeline type
/// 2. Returns PipelineResult instead of tuple - cleaner, more extensible
/// 3. Context contains services - handlers can resolve dependencies dynamically
///
/// Example handler (RAG document extraction):
/// <code>
/// public class ExtractTextHandler : IPipelineHandler&lt;DocumentIngestionContext&gt;
/// {
///     public string StepName => "extract_text";
///
///     public async Task&lt;PipelineResult&gt; HandleAsync(
///         DocumentIngestionContext context,
///         CancellationToken cancellationToken)
///     {
///         try
///         {
///             // Extract text from documents
///             foreach (var doc in context.Documents)
///             {
///                 doc.ExtractedText = await ExtractTextAsync(doc);
///             }
///             return PipelineResult.Success();
///         }
///         catch (Exception ex)
///         {
///             return PipelineResult.Failure(ex.Message, isTransient: true);
///         }
///     }
/// }
/// </code>
///
/// Example handler (Video transcoding):
/// <code>
/// public class TranscodeHandler : IPipelineHandler&lt;VideoProcessingContext&gt;
/// {
///     public string StepName => "transcode";
///
///     public async Task&lt;PipelineResult&gt; HandleAsync(
///         VideoProcessingContext context,
///         CancellationToken cancellationToken)
///     {
///         // Transcode video...
///         return PipelineResult.Success();
///     }
/// }
/// </code>
/// </remarks>
public interface IPipelineHandler<in TContext> where TContext : IPipelineContext
{
    /// <summary>
    /// Unique name of the pipeline step handled by this handler.
    /// Used for routing pipeline execution to the correct handler.
    /// </summary>
    /// <remarks>
    /// This must match the step name in the pipeline's Steps list.
    /// Convention: use lowercase with underscores (e.g., "extract_text", "generate_embeddings")
    /// </remarks>
    string StepName { get; }

    /// <summary>
    /// Handle the pipeline step by processing the context.
    /// </summary>
    /// <param name="context">
    /// The pipeline context containing all data, services, and state for this execution.
    /// Handlers can read from and write to the context.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token for async operations.
    /// </param>
    /// <returns>
    /// A PipelineResult indicating success or failure.
    /// On success, the pipeline continues to the next step.
    /// On transient failure, the pipeline may retry this step.
    /// On fatal failure, the pipeline stops immediately.
    /// </returns>
    /// <remarks>
    /// Handlers SHOULD:
    /// - Be idempotent (safe to run multiple times)
    /// - Use context.Log() to record important events
    /// - Use context.Services to resolve dependencies
    /// - Return transient failures for retryable errors (network timeouts, rate limits)
    /// - Return fatal failures for non-retryable errors (invalid data, missing resources)
    ///
    /// Handlers SHOULD NOT:
    /// - Modify context.Steps, context.CompletedSteps, or context.RemainingSteps
    ///   (the orchestrator manages this)
    /// - Throw exceptions (return PipelineResult.Failure instead)
    /// - Have side effects that aren't tracked in the context
    /// </remarks>
    Task<PipelineResult> HandleAsync(TContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a pipeline handler execution.
/// Inspired by Kernel Memory's ReturnType but more informative.
/// </summary>
public record PipelineResult
{
    /// <summary>
    /// Whether the handler succeeded.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Whether the failure is transient (retryable).
    /// Only meaningful when IsSuccess is false.
    /// </summary>
    public bool IsTransient { get; init; }

    /// <summary>
    /// Optional error message.
    /// Should be set when IsSuccess is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Optional exception details.
    /// For debugging and logging purposes.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Additional metadata about the execution.
    /// Can contain timing, resource usage, or custom metrics.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Create a successful result.
    /// </summary>
    public static PipelineResult Success(IReadOnlyDictionary<string, object>? metadata = null)
        => new()
        {
            IsSuccess = true,
            IsTransient = false,
            Metadata = metadata
        };

    /// <summary>
    /// Create a failure result.
    /// </summary>
    /// <param name="errorMessage">Description of the error</param>
    /// <param name="isTransient">Whether this is a retryable error (network timeout, rate limit, etc.)</param>
    /// <param name="exception">Optional exception for debugging</param>
    /// <param name="metadata">Optional metadata</param>
    public static PipelineResult Failure(
        string errorMessage,
        bool isTransient = false,
        Exception? exception = null,
        IReadOnlyDictionary<string, object>? metadata = null)
        => new()
        {
            IsSuccess = false,
            IsTransient = isTransient,
            ErrorMessage = errorMessage,
            Exception = exception,
            Metadata = metadata
        };

    /// <summary>
    /// Create a fatal (non-retryable) failure result.
    /// </summary>
    public static PipelineResult FatalFailure(
        string errorMessage,
        Exception? exception = null,
        IReadOnlyDictionary<string, object>? metadata = null)
        => Failure(errorMessage, isTransient: false, exception, metadata);

    /// <summary>
    /// Create a transient (retryable) failure result.
    /// </summary>
    public static PipelineResult TransientFailure(
        string errorMessage,
        Exception? exception = null,
        IReadOnlyDictionary<string, object>? metadata = null)
        => Failure(errorMessage, isTransient: true, exception, metadata);
}
