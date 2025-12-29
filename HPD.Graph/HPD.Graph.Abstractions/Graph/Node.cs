namespace HPDAgent.Graph.Abstractions.Graph;

/// <summary>
/// A node in the execution graph.
/// Nodes represent units of work (handlers) or control flow (START/END/Router).
/// Immutable after construction.
/// </summary>
public sealed record Node
{
    /// <summary>
    /// Unique node identifier within the graph.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable node name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Node type (Start, End, Handler, Router, SubGraph).
    /// </summary>
    public required NodeType Type { get; init; }

    /// <summary>
    /// Handler name for Handler and Router nodes.
    /// Must match a registered IGraphNodeHandler.HandlerName.
    /// Null for Start/End nodes.
    /// </summary>
    public string? HandlerName { get; init; }

    /// <summary>
    /// Node-specific configuration.
    /// Deserialized into handler-specific config types at runtime.
    /// </summary>
    public IReadOnlyDictionary<string, object> Config { get; init; } =
        new Dictionary<string, object>();

    /// <summary>
    /// Timeout for this node execution.
    /// Null = no timeout (use graph-level default).
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Retry policy for this node.
    /// Null = no retries.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Additional metadata (tags, labels, etc.).
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Enable checkpointing for this node.
    /// If true, node state is saved after execution.
    /// </summary>
    public bool EnableCheckpointing { get; init; } = true;

    /// <summary>
    /// Maximum number of times this node can execute (for loop detection).
    /// Null = unlimited.
    /// </summary>
    public int? MaxExecutions { get; init; }

    /// <summary>
    /// Sub-graph definition (for NodeType.SubGraph).
    /// Either embedded or referenced via SubGraphRef.
    /// </summary>
    public Graph? SubGraph { get; init; }

    /// <summary>
    /// Sub-graph reference (path or URI to graph definition).
    /// Alternative to inline SubGraph - load graph at runtime.
    /// </summary>
    public string? SubGraphRef { get; init; }

    /// <summary>
    /// Maximum number of parallel executions for this node within a layer.
    /// When limit is reached, additional executions wait for available slots.
    /// Null = unbounded parallelism (default).
    /// Use this to prevent resource exhaustion (e.g., database connections, API rate limits).
    /// Note: This limits concurrent executions in a layer, not input buffering.
    /// </summary>
    public int? MaxParallelExecutions { get; init; }

    /// <summary>
    /// Error propagation policy for this node.
    /// Defines how errors from this node affect downstream execution.
    /// Null = use graph-level default (StopGraph).
    /// </summary>
    public Execution.ErrorPropagationPolicy? ErrorPolicy { get; init; }

    /// <summary>
    /// Version identifier for this node's state schema.
    /// Used during checkpoint resume to validate compatibility.
    /// Increment when handler signature or state format changes (breaking changes).
    /// Default: "1.0"
    /// Examples: "1.0", "2.0", "2.1"
    /// </summary>
    public string Version { get; init; } = "1.0";

    // ===== MAP NODE PROPERTIES =====
    //
    // DECISION: Do you need a router?
    //
    // NO ROUTER (Homogeneous - use MapProcessorGraph):
    //   - All items are SAME TYPE and need SAME PROCESSING
    //   - Example: List of emails all need validation → use ONE graph
    //   - Set: MapProcessorGraph property
    //
    // YES ROUTER (Heterogeneous - use MapProcessorGraphs + MapRouterName):
    //   - Items are DIFFERENT TYPES and need DIFFERENT PROCESSING
    //   - Example: Mixed docs (PDF/Image/Video) each need specialized processing
    //   - Set: MapProcessorGraphs dictionary + MapRouterName + optional MapDefaultGraph
    //

    /// <summary>
    /// Processor graph for Map nodes (NodeType.Map).
    /// This graph is executed once per item in the input collection.
    /// Similar to SubGraph but with iteration semantics.
    ///
    /// Use when ALL items are SAME TYPE and need SAME PROCESSING (homogeneous).
    /// Mutually exclusive with MapProcessorGraphs (heterogeneous).
    /// </summary>
    public Graph? MapProcessorGraph { get; init; }

    /// <summary>
    /// Reference to processor graph for Map nodes (path or URI).
    /// Alternative to inline MapProcessorGraph - loaded at runtime.
    /// </summary>
    public string? MapProcessorGraphRef { get; init; }

    /// <summary>
    /// Maximum number of concurrent processor graph executions.
    /// - Null: Unbounded parallelism
    /// - 0: Auto (Environment.ProcessorCount)
    /// - N > 0: Limit to N concurrent executions
    /// Default: 0 (auto)
    /// </summary>
    public int? MaxParallelMapTasks { get; init; }

    /// <summary>
    /// Channel name to read input items from (for Map nodes).
    /// If not specified, reads from "node_output:{previousNodeId}".
    /// Input must be IEnumerable&lt;T&gt;.
    /// </summary>
    public string? MapInputChannel { get; init; }

    /// <summary>
    /// Channel name to write aggregated results to (for Map nodes).
    /// If not specified, writes to "node_output:{nodeId}".
    /// Uses Append channel semantics to preserve all results.
    /// </summary>
    public string? MapOutputChannel { get; init; }

    /// <summary>
    /// Error handling strategy for map processing.
    /// Default: FailFast
    /// </summary>
    public MapErrorMode? MapErrorMode { get; init; }

    /// <summary>
    /// Expected input item type name (optional validation).
    /// Example: "MyApp.Models.TextChunk"
    /// Used for runtime type checking and better error messages.
    /// </summary>
    public string? MapItemType { get; init; }

    /// <summary>
    /// Expected result type name (optional validation).
    /// Example: "System.Single[]"
    /// </summary>
    public string? MapResultType { get; init; }

    // ===== HETEROGENEOUS MAP PROPERTIES (v1.6) =====
    //
    // Use these properties when you have MIXED ITEM TYPES that need DIFFERENT PROCESSING.
    // The router inspects each item and returns a key to select which graph to use.
    //

    /// <summary>
    /// Multiple processor graphs for heterogeneous Map nodes, keyed by routing value.
    /// When specified, MapRouterName determines which graph to use per item.
    /// Example: { "pdf": pdfGraph, "image": imageGraph, "video": videoGraph }
    /// Mutually exclusive with MapProcessorGraph.
    ///
    /// REQUIRES: MapRouterName must be specified (router decides which graph per item)
    /// OPTIONAL: MapDefaultGraph provides fallback for unmatched routing values
    /// </summary>
    public IReadOnlyDictionary<string, Graph>? MapProcessorGraphs { get; init; }

    /// <summary>
    /// Name of the router to use for per-item routing.
    /// Must match an IMapRouter.RouterName registered in DI.
    /// Required when MapProcessorGraphs is specified.
    /// Example: "DocumentTypeRouter"
    /// Resolved from DI at runtime (same pattern as HandlerName).
    /// </summary>
    public string? MapRouterName { get; init; }

    /// <summary>
    /// Default processor graph to use if routing value doesn't match any key in MapProcessorGraphs.
    /// If null and no match found, behavior depends on MapErrorMode:
    /// - FailFast: Throws exception immediately
    /// - ContinueWithNulls: Adds null to results
    /// - ContinueOmitFailures: Skips item silently
    /// </summary>
    public Graph? MapDefaultGraph { get; init; }
}
