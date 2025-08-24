using System;
using System.Collections.Generic;

/// <summary>
/// Represents a single, atomic step in the execution of a workflow.
/// </summary>
public record ExecutionStep(
    string NodeId,
    string NodeKey,
    object InputState,      // A snapshot of TState before execution
    object OutputState,     // A snapshot of TState after execution
    string? EdgeConditionKey,
    string? ConditionResult,
    TimeSpan Duration
);

/// <summary>
/// Immutable workflow context focused on workflow execution state and traceability.
/// </summary>
public record WorkflowContext<TState>(
    TState State,
    string? CurrentNodeId,
    IReadOnlyList<ExecutionStep> Trace,
    AggregatorCollection Aggregators
) where TState : class, new();