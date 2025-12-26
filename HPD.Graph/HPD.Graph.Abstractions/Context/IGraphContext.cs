using HPDAgent.Graph.Abstractions.Channels;
using HPDAgent.Graph.Abstractions.State;

namespace HPDAgent.Graph.Abstractions.Context;

/// <summary>
/// Execution context for graph execution.
/// Carries state, data, and execution progress through the graph.
/// Generic base for domain-specific contexts.
///
/// DESIGN: Slim context - only cross-cutting concerns.
/// Data flows through HandlerInputs/NodeResult, NOT context properties.
/// </summary>
public interface IGraphContext
{
    // ========================================
    // Execution Identity
    // ========================================

    /// <summary>
    /// Unique execution identifier.
    /// </summary>
    string ExecutionId { get; }

    /// <summary>
    /// The graph being executed.
    /// </summary>
    Graph.Graph Graph { get; }

    // ========================================
    // Execution State (Mutable)
    // ========================================

    /// <summary>
    /// Current node being executed (null if not executing).
    /// </summary>
    string? CurrentNodeId { get; }

    /// <summary>
    /// Nodes that have successfully completed.
    /// </summary>
    IReadOnlySet<string> CompletedNodes { get; }

    /// <summary>
    /// When execution started.
    /// </summary>
    DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Last update timestamp.
    /// </summary>
    DateTimeOffset LastUpdatedAt { get; }

    /// <summary>
    /// Whether execution is complete.
    /// </summary>
    bool IsComplete { get; }

    /// <summary>
    /// Whether execution was cancelled.
    /// </summary>
    bool IsCancelled { get; }

    // ========================================
    // State Management (Unified System)
    // ========================================

    /// <summary>
    /// Channel set for managing state with explicit update semantics.
    /// </summary>
    IGraphChannelSet Channels { get; }

    /// <summary>
    /// Managed context for execution metadata (ephemeral).
    /// </summary>
    IManagedContext Managed { get; }

    /// <summary>
    /// Get a state scope for hierarchical namespace-based state management.
    /// </summary>
    /// <param name="scopeName">Scope name (null for root scope)</param>
    IGraphStateScope GetScope(string? scopeName = null);

    // ========================================
    // Node Execution Tracking
    // ========================================

    /// <summary>
    /// Set the current node being executed.
    /// </summary>
    void SetCurrentNode(string? nodeId);

    /// <summary>
    /// Mark a node as completed.
    /// </summary>
    void MarkNodeComplete(string nodeId);

    /// <summary>
    /// Check if a node is complete.
    /// </summary>
    bool IsNodeComplete(string nodeId);

    /// <summary>
    /// Get execution count for a node (for loop detection).
    /// </summary>
    int GetNodeExecutionCount(string nodeId);

    /// <summary>
    /// Increment execution count for a node.
    /// </summary>
    void IncrementNodeExecutionCount(string nodeId);

    // ========================================
    // Logging & Observability
    // ========================================

    /// <summary>
    /// Log entries for this execution.
    /// </summary>
    IReadOnlyList<GraphLogEntry> LogEntries { get; }

    /// <summary>
    /// Add a log entry.
    /// </summary>
    void Log(string source, string message, LogLevel level = LogLevel.Information,
             string? nodeId = null, Exception? exception = null);

    // ========================================
    // Service Resolution (DI)
    // ========================================

    /// <summary>
    /// Service provider for dependency injection.
    /// </summary>
    IServiceProvider Services { get; }

    // ========================================
    // Context Isolation (For Parallel Execution)
    // ========================================

    /// <summary>
    /// Create an isolated copy of this context for parallel execution.
    /// Shared: Immutable state (ExecutionId, Graph, Services).
    /// Cloned: Mutable collections (CompletedNodes, Channels, Logs).
    /// Reset: Current execution position (CurrentNodeId = null).
    /// </summary>
    IGraphContext CreateIsolatedCopy();

    /// <summary>
    /// Merge state from an isolated context back into this context.
    /// Uses channel semantics (Append = accumulate, LastValue = overwrite).
    /// </summary>
    void MergeFrom(IGraphContext isolatedContext);

    // ========================================
    // Metadata & Tags
    // ========================================

    /// <summary>
    /// Tags for categorization and filtering.
    /// Format: key → list of values.
    /// </summary>
    IDictionary<string, List<string>> Tags { get; }

    /// <summary>
    /// Add a tag.
    /// </summary>
    void AddTag(string key, string value);

    // ========================================
    // Progress Tracking
    // ========================================

    /// <summary>
    /// Current layer index being executed (0-based).
    /// </summary>
    int CurrentLayerIndex { get; set; }

    /// <summary>
    /// Total number of layers in the graph.
    /// </summary>
    int TotalLayers { get; }

    /// <summary>
    /// Overall progress (0.0 to 1.0).
    /// </summary>
    float Progress { get; }
}

/// <summary>
/// Log entry for graph execution.
/// </summary>
public sealed record GraphLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Source { get; init; }
    public required LogLevel Level { get; init; }
    public required string Message { get; init; }
    public string? NodeId { get; init; }
    public Exception? Exception { get; init; }
}

/// <summary>
/// Log level.
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
