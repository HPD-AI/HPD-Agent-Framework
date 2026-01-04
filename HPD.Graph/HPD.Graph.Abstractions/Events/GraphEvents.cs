using HPD.Events;
using HPDAgent.Graph.Abstractions.Context;
using HPDAgent.Graph.Abstractions.Execution;

namespace HPDAgent.Graph.Abstractions.Events;

/// <summary>
/// Event emitted when graph execution starts.
/// </summary>
public sealed record GraphExecutionStartedEvent : GraphEvent
{
    /// <summary>
    /// Number of nodes in the graph.
    /// </summary>
    public required int NodeCount { get; init; }

    /// <summary>
    /// Number of execution layers (null if not layer-based).
    /// </summary>
    public int? LayerCount { get; init; }

    /// <summary>
    /// Override Kind to Lifecycle.
    /// </summary>
    public new EventKind Kind { get; init; } = EventKind.Lifecycle;
}

/// <summary>
/// Event emitted when graph execution completes.
/// </summary>
public sealed record GraphExecutionCompletedEvent : GraphEvent
{
    /// <summary>
    /// Total execution duration.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Number of nodes successfully completed.
    /// </summary>
    public int SuccessfulNodes { get; init; }

    /// <summary>
    /// Number of nodes that failed.
    /// </summary>
    public int FailedNodes { get; init; }

    /// <summary>
    /// Number of nodes that were skipped.
    /// </summary>
    public int SkippedNodes { get; init; }

    /// <summary>
    /// Override Kind to Lifecycle.
    /// </summary>
    public new EventKind Kind { get; init; } = EventKind.Lifecycle;
}

/// <summary>
/// Event emitted when a layer starts executing.
/// Only emitted for layer-based execution.
/// </summary>
public sealed record LayerExecutionStartedEvent : GraphEvent
{
    /// <summary>
    /// Layer index (0-based).
    /// </summary>
    public required int LayerIndex { get; init; }

    /// <summary>
    /// Number of nodes in this layer.
    /// </summary>
    public required int NodeCount { get; init; }

    /// <summary>
    /// Override Kind to Lifecycle.
    /// </summary>
    public new EventKind Kind { get; init; } = EventKind.Lifecycle;
}

/// <summary>
/// Event emitted when a layer completes executing.
/// Only emitted for layer-based execution.
/// </summary>
public sealed record LayerExecutionCompletedEvent : GraphEvent
{
    /// <summary>
    /// Layer index (0-based).
    /// </summary>
    public required int LayerIndex { get; init; }

    /// <summary>
    /// Time taken to execute this layer.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Number of nodes successfully completed in this layer.
    /// </summary>
    public int SuccessfulNodes { get; init; }

    /// <summary>
    /// Override Kind to Lifecycle.
    /// </summary>
    public new EventKind Kind { get; init; } = EventKind.Lifecycle;
}

/// <summary>
/// Event emitted when a node starts executing.
/// </summary>
public sealed record NodeExecutionStartedEvent : GraphEvent
{
    /// <summary>
    /// ID of the node that started.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Name of the handler being executed.
    /// </summary>
    public required string HandlerName { get; init; }

    /// <summary>
    /// Layer index (null for nodes outside layer-based execution).
    /// </summary>
    public int? LayerIndex { get; init; }

    /// <summary>
    /// Override Kind to Lifecycle.
    /// </summary>
    public new EventKind Kind { get; init; } = EventKind.Lifecycle;
}

/// <summary>
/// Event emitted when a node completes executing.
/// This is the primary progress tracking event during streaming execution.
/// Based on the PartialResult structure from the original proposal.
/// </summary>
public sealed record NodeExecutionCompletedEvent : GraphEvent
{
    /// <summary>
    /// ID of the node that completed.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Name of the handler that executed.
    /// </summary>
    public required string HandlerName { get; init; }

    /// <summary>
    /// Execution layer index (null for nodes outside layer-based execution).
    /// </summary>
    public int? LayerIndex { get; init; }

    /// <summary>
    /// Progress percentage (0.0 to 1.0).
    /// Based on number of completed nodes / total nodes.
    /// </summary>
    public float Progress { get; init; }

    /// <summary>
    /// Outputs from the completed node (null if not included).
    /// Controlled by StreamingOptions.IncludeOutputs.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Outputs { get; init; }

    /// <summary>
    /// Time taken to execute the node.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Execution result status (Success, Failure, Skipped, etc.).
    /// </summary>
    public required NodeExecutionResult Result { get; init; }
}

/// <summary>
/// Event emitted when an edge is traversed during graph execution.
/// Useful for debugging routing decisions.
/// </summary>
public sealed record EdgeTraversedEvent : GraphEvent
{
    /// <summary>
    /// ID of the source node.
    /// </summary>
    public required string FromNodeId { get; init; }

    /// <summary>
    /// ID of the target node.
    /// </summary>
    public required string ToNodeId { get; init; }

    /// <summary>
    /// Whether this edge had a condition that was evaluated.
    /// </summary>
    public bool HasCondition { get; init; }

    /// <summary>
    /// Description of the condition (if any).
    /// </summary>
    public string? ConditionDescription { get; init; }

    /// <summary>
    /// Override Kind to Diagnostic.
    /// </summary>
    public new EventKind Kind { get; init; } = EventKind.Diagnostic;
}

/// <summary>
/// Event emitted when an edge condition is evaluated but fails.
/// Useful for debugging why certain paths weren't taken.
/// </summary>
public sealed record EdgeConditionFailedEvent : GraphEvent
{
    /// <summary>
    /// ID of the source node.
    /// </summary>
    public required string FromNodeId { get; init; }

    /// <summary>
    /// ID of the target node that was NOT taken.
    /// </summary>
    public required string ToNodeId { get; init; }

    /// <summary>
    /// The condition that failed.
    /// </summary>
    public required string ConditionDescription { get; init; }

    /// <summary>
    /// The actual value that was checked.
    /// </summary>
    public string? ActualValue { get; init; }

    /// <summary>
    /// The expected value for the condition.
    /// </summary>
    public string? ExpectedValue { get; init; }

    /// <summary>
    /// Override Kind to Diagnostic.
    /// </summary>
    public new EventKind Kind { get; init; } = EventKind.Diagnostic;
}

/// <summary>
/// Event emitted when a node is skipped due to no incoming edges being traversed.
/// </summary>
public sealed record NodeSkippedEvent : GraphEvent
{
    /// <summary>
    /// ID of the node that was skipped.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Reason for skipping.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// The edges that could have led to this node but didn't.
    /// </summary>
    public IReadOnlyList<string>? PotentialSourceNodes { get; init; }

    /// <summary>
    /// Override Kind to Diagnostic.
    /// </summary>
    public new EventKind Kind { get; init; } = EventKind.Diagnostic;
}

/// <summary>
/// Real-time diagnostic/log event emitted during graph execution.
/// Streamed via EventCoordinator for immediate visibility during debugging.
///
/// Filter by level using numeric comparison:
///   - Trace = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Critical = 5
///   - Example: events.Where(e => (int)e.Level >= (int)LogLevel.Warning)
/// </summary>
public sealed record GraphDiagnosticEvent : GraphEvent
{
    /// <summary>
    /// Log level for filtering.
    /// Use (int)Level for numeric comparisons (Trace=0 ... Critical=5).
    /// </summary>
    public required LogLevel Level { get; init; }

    /// <summary>
    /// Source of the log (e.g., "Orchestrator", "PrepareInputs", node handler name).
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// The log message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Node ID if this log is related to a specific node.
    /// </summary>
    public string? NodeId { get; init; }

    /// <summary>
    /// Exception details if this is an error log.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Additional structured data for debugging.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Data { get; init; }

    /// <summary>
    /// Override Kind to Diagnostic.
    /// </summary>
    public new EventKind Kind { get; init; } = EventKind.Diagnostic;
}
