// Copyright (c) Einstein Essibu. All rights reserved.
// Inspired by Microsoft Kernel Memory, enhanced with modern patterns.

using System.Diagnostics.CodeAnalysis;

namespace HPD.Pipeline;

/// <summary>
/// Log level for pipeline log entries.
/// </summary>
public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Base context for all pipeline executions.
/// Provides shared infrastructure for data passing, service access, and state management.
/// Inspired by Kernel Memory's DataPipeline but more generic and extensible.
/// Domain-agnostic - works for RAG, video processing, trading, ETL, or any workflow.
/// </summary>
public interface IPipelineContext
{
    /// <summary>
    /// Unique identifier for this pipeline execution instance.
    /// </summary>
    string PipelineId { get; }

    /// <summary>
    /// Pipeline execution ID. Changes on each execution of the same pipeline.
    /// Used for tracking multiple runs of the same logical pipeline.
    /// </summary>
    string ExecutionId { get; }

    /// <summary>
    /// Index/collection/namespace where data is stored or retrieved from.
    /// </summary>
    string Index { get; }

    /// <summary>
    /// List of all steps in this pipeline.
    /// Steps can be sequential (single handler) or parallel (multiple handlers).
    /// </summary>
    IReadOnlyList<PipelineStep> Steps { get; }

    /// <summary>
    /// Steps that have been completed.
    /// </summary>
    IReadOnlyList<PipelineStep> CompletedSteps { get; }

    /// <summary>
    /// Steps remaining to execute.
    /// </summary>
    IReadOnlyList<PipelineStep> RemainingSteps { get; }

    /// <summary>
    /// Get the current step being executed (first of RemainingSteps).
    /// Returns null if pipeline is complete.
    /// </summary>
    PipelineStep? CurrentStep { get; }

    /// <summary>
    /// Check if the current step is a parallel step.
    /// </summary>
    bool IsCurrentStepParallel { get; }

    /// <summary>
    /// Get all handler names in the current step.
    /// For sequential steps, returns single handler.
    /// For parallel steps, returns all handlers in the group.
    /// </summary>
    IReadOnlyList<string> CurrentHandlerNames { get; }

    /// <summary>
    /// Get the current step index (0-based).
    /// </summary>
    int CurrentStepIndex { get; }

    /// <summary>
    /// Get total number of steps in the pipeline.
    /// </summary>
    int TotalSteps { get; }

    /// <summary>
    /// Get pipeline progress as percentage (0.0 to 1.0).
    /// </summary>
    float Progress { get; }

    /// <summary>
    /// Whether the pipeline has completed all steps.
    /// </summary>
    [MemberNotNullWhen(false, nameof(RemainingSteps))]
    bool IsComplete { get; }

    /// <summary>
    /// Creation timestamp.
    /// </summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Last update timestamp.
    /// </summary>
    DateTimeOffset LastUpdatedAt { get; }

    /// <summary>
    /// Shared data dictionary for passing information between handlers.
    /// Handlers can store/retrieve arbitrary data here.
    /// </summary>
    IDictionary<string, object> Data { get; }

    /// <summary>
    /// Service provider for accessing registered services.
    /// Handlers can use this to resolve dependencies dynamically.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Tags for organizing and filtering pipelines/documents.
    /// Similar to Kernel Memory's TagCollection.
    /// </summary>
    IDictionary<string, List<string>> Tags { get; }

    /// <summary>
    /// Optional log entries for tracking important events during pipeline execution.
    /// Useful for debugging and user feedback.
    /// </summary>
    IList<PipelineLogEntry> LogEntries { get; }

    /// <summary>
    /// Add a log entry for this pipeline execution.
    /// </summary>
    void Log(string source, string message, LogLevel level = LogLevel.Information);

    /// <summary>
    /// Get typed data from the context.
    /// </summary>
    T? GetData<T>(string key) where T : class;

    /// <summary>
    /// Set typed data in the context.
    /// </summary>
    void SetData<T>(string key, T value) where T : class;

    /// <summary>
    /// Check if data exists.
    /// </summary>
    bool HasData(string key);

    /// <summary>
    /// Check if a specific handler has already processed this context.
    /// Used for idempotency - handlers can skip work if already done.
    /// Inspired by Kernel Memory's AlreadyProcessedBy pattern.
    /// </summary>
    /// <param name="handlerName">Name of the handler to check</param>
    /// <param name="subStep">Optional sub-step identifier for handlers that run multiple passes</param>
    /// <returns>True if the handler (and optional sub-step) has already processed this context</returns>
    bool AlreadyProcessedBy(string handlerName, string? subStep = null);

    /// <summary>
    /// Mark this context as processed by a specific handler.
    /// Used for idempotency tracking and retry safety.
    /// </summary>
    /// <param name="handlerName">Name of the handler that processed this context</param>
    /// <param name="subStep">Optional sub-step identifier for handlers that run multiple passes</param>
    void MarkProcessedBy(string handlerName, string? subStep = null);

    /// <summary>
    /// Get list of all handlers (and sub-steps) that have processed this context.
    /// Useful for debugging and status tracking.
    /// </summary>
    IReadOnlyList<string> GetProcessedHandlers();

    /// <summary>
    /// Move pipeline to next step. Removes first item from RemainingSteps and adds to CompletedSteps.
    /// Called by orchestrator after successful step execution.
    /// </summary>
    void MoveToNextStep();

    // ========================================
    // Parallel Execution Support
    // ========================================

    /// <summary>
    /// Mark a specific handler as complete in the current parallel step.
    /// Only used during parallel execution to track which handlers have finished.
    /// Orchestrator calls this after each parallel handler completes successfully.
    /// </summary>
    /// <param name="handlerName">Name of the handler that completed</param>
    void MarkHandlerComplete(string handlerName);

    /// <summary>
    /// Check if a specific handler has completed in the current parallel step.
    /// Only relevant during parallel execution.
    /// </summary>
    /// <param name="handlerName">Name of the handler to check</param>
    /// <returns>True if the handler has completed in the current step</returns>
    bool IsHandlerComplete(string handlerName);

    /// <summary>
    /// Get list of handlers that have completed in the current parallel step.
    /// Only relevant during parallel execution. Returns empty list for sequential steps.
    /// </summary>
    IReadOnlyList<string> GetCompletedHandlersInCurrentStep();

    // ========================================
    // Context Isolation (for Parallel Safety)
    // ========================================

    /// <summary>
    /// Check if this context is an isolated copy created for parallel execution.
    /// Isolated contexts are used to prevent race conditions when handlers run concurrently.
    /// </summary>
    bool IsIsolated { get; }

    /// <summary>
    /// Create an isolated copy of this context for parallel execution.
    /// The isolated copy has its own mutable state (Files, Data, Tags) but shares
    /// immutable references (Services, Steps).
    ///
    /// This enforces safety: each parallel handler gets its own context copy,
    /// preventing race conditions. Changes are merged back after all handlers complete.
    /// </summary>
    /// <returns>Isolated copy of the context</returns>
    /// <exception cref="InvalidOperationException">If called on already isolated context</exception>
    IPipelineContext CreateIsolatedCopy();

    /// <summary>
    /// Merge changes from an isolated context back into this (main) context.
    /// Called by orchestrator after parallel handlers complete.
    ///
    /// Merge strategy:
    /// - Files: Union (new files added, existing files updated)
    /// - Data: Union (new keys added, existing keys overwritten)
    /// - Tags: Union (new tags added, existing tags merged)
    /// - ProcessedBy: Union (all processed handlers tracked)
    /// </summary>
    /// <param name="isolatedContext">Isolated context to merge from</param>
    /// <exception cref="InvalidOperationException">If called on isolated context or with non-isolated context</exception>
    void MergeFrom(IPipelineContext isolatedContext);
}

/// <summary>
/// Log entry for pipeline execution.
/// Similar to Kernel Memory's PipelineLogEntry.
/// </summary>
public record PipelineLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }
    public LogLevel Level { get; init; } = LogLevel.Information;
}
