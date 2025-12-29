using HPD.Events;
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
