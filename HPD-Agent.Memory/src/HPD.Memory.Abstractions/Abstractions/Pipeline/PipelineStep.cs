// Copyright (c) Einstein Essibu. All rights reserved.
// Pipeline step types for sequential and parallel execution.

namespace HPDAgent.Memory.Abstractions.Pipeline;

/// <summary>
/// Base type for pipeline steps. Sealed hierarchy for pattern matching.
/// Steps can be either sequential (one handler) or parallel (multiple handlers).
/// </summary>
public abstract record PipelineStep
{
    /// <summary>
    /// Get all handler names in this step.
    /// </summary>
    public abstract IReadOnlyList<string> GetHandlerNames();

    /// <summary>
    /// Check if this step represents parallel execution.
    /// </summary>
    public abstract bool IsParallel { get; }
}

/// <summary>
/// Sequential step - executes a single handler.
/// </summary>
public sealed record SequentialStep : PipelineStep
{
    /// <summary>
    /// Name of the handler to execute.
    /// </summary>
    public required string HandlerName { get; init; }

    public override IReadOnlyList<string> GetHandlerNames() => new[] { HandlerName };

    public override bool IsParallel => false;

    public override string ToString() => HandlerName;
}

/// <summary>
/// Parallel step - executes multiple handlers concurrently with isolation.
/// Each handler gets an isolated copy of the context to prevent race conditions.
/// Results are merged back after all handlers complete.
/// If any handler fails, the entire step fails.
/// </summary>
public sealed record ParallelStep : PipelineStep
{
    /// <summary>
    /// Names of handlers to execute in parallel.
    /// </summary>
    public required IReadOnlyList<string> HandlerNames { get; init; }

    /// <summary>
    /// Maximum number of handlers to run concurrently.
    /// If null, no limit (all handlers run at once).
    /// Useful for throttling expensive operations like API calls.
    /// </summary>
    public int? MaxConcurrency { get; init; }

    public override IReadOnlyList<string> GetHandlerNames() => HandlerNames;

    public override bool IsParallel => true;

    /// <summary>
    /// Create a parallel step with handler names.
    /// </summary>
    public ParallelStep(params string[] handlerNames)
        : this((IReadOnlyList<string>)handlerNames)
    {
    }

    /// <summary>
    /// Create a parallel step with handler names.
    /// </summary>
    public ParallelStep(IReadOnlyList<string> handlerNames)
    {
        HandlerNames = handlerNames;
    }

    public override string ToString() =>
        $"Parallel({string.Join(", ", HandlerNames)})";
}
