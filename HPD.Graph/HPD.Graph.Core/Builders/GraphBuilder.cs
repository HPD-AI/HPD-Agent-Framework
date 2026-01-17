using HPDAgent.Graph.Abstractions.Execution;
using HPDAgent.Graph.Abstractions.Graph;

// Alias for SuspensionOptions to avoid conflicts
using SuspensionOpts = HPDAgent.Graph.Abstractions.Execution.SuspensionOptions;

namespace HPDAgent.Graph.Core.Builders;

/// <summary>
/// Fluent API for programmatically constructing graphs.
/// Provides a chainable interface for adding nodes and edges.
/// </summary>
public class GraphBuilder
{
    private string? _id;
    private string? _name;
    private string _version = "1.0.0";
    private readonly List<Node> _nodes = new();
    private readonly List<Edge> _edges = new();
    private string _entryNodeId = "START";
    private string _exitNodeId = "END";
    private readonly Dictionary<string, string> _metadata = new();
    private int _maxIterations = 10;
    private TimeSpan? _executionTimeout;

    /// <summary>
    /// Creates a new GraphBuilder instance.
    /// </summary>
    public GraphBuilder()
    {
        // Auto-generate ID if not specified
        _id = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Sets the graph ID.
    /// </summary>
    public GraphBuilder WithId(string id)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        return this;
    }

    /// <summary>
    /// Sets the graph name.
    /// </summary>
    public GraphBuilder WithName(string name)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        return this;
    }

    /// <summary>
    /// Sets the graph version.
    /// </summary>
    public GraphBuilder WithVersion(string version)
    {
        _version = version ?? throw new ArgumentNullException(nameof(version));
        return this;
    }

    /// <summary>
    /// Sets the entry node ID (default: "START").
    /// </summary>
    public GraphBuilder WithEntryNode(string entryNodeId)
    {
        _entryNodeId = entryNodeId ?? throw new ArgumentNullException(nameof(entryNodeId));
        return this;
    }

    /// <summary>
    /// Sets the exit node ID (default: "END").
    /// </summary>
    public GraphBuilder WithExitNode(string exitNodeId)
    {
        _exitNodeId = exitNodeId ?? throw new ArgumentNullException(nameof(exitNodeId));
        return this;
    }

    /// <summary>
    /// Sets the maximum iterations for cyclic graphs.
    /// </summary>
    public GraphBuilder WithMaxIterations(int maxIterations)
    {
        _maxIterations = maxIterations;
        return this;
    }

    /// <summary>
    /// Sets the global execution timeout.
    /// </summary>
    public GraphBuilder WithExecutionTimeout(TimeSpan timeout)
    {
        _executionTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Adds metadata to the graph.
    /// </summary>
    public GraphBuilder WithMetadata(string key, string value)
    {
        _metadata[key] = value;
        return this;
    }

    /// <summary>
    /// Adds a node to the graph.
    /// </summary>
    public GraphBuilder AddNode(
        string id,
        string name,
        NodeType type,
        string? handlerName = null,
        Action<NodeBuilder>? configure = null)
    {
        var builder = new NodeBuilder(id, name, type, handlerName);
        configure?.Invoke(builder);
        _nodes.Add(builder.Build());
        return this;
    }

    /// <summary>
    /// Adds a START node to the graph.
    /// </summary>
    public GraphBuilder AddStartNode(string id = "START", string name = "Start")
    {
        _entryNodeId = id;
        _nodes.Add(new Node
        {
            Id = id,
            Name = name,
            Type = NodeType.Start
        });
        return this;
    }

    /// <summary>
    /// Adds an END node to the graph.
    /// </summary>
    public GraphBuilder AddEndNode(string id = "END", string name = "End")
    {
        _exitNodeId = id;
        _nodes.Add(new Node
        {
            Id = id,
            Name = name,
            Type = NodeType.End
        });
        return this;
    }

    /// <summary>
    /// Adds a handler node to the graph.
    /// </summary>
    public GraphBuilder AddHandlerNode(
        string id,
        string name,
        string handlerName,
        Action<NodeBuilder>? configure = null)
    {
        return AddNode(id, name, NodeType.Handler, handlerName, configure);
    }

    /// <summary>
    /// Adds a router node to the graph.
    /// </summary>
    public GraphBuilder AddRouterNode(
        string id,
        string name,
        string handlerName,
        Action<NodeBuilder>? configure = null)
    {
        return AddNode(id, name, NodeType.Router, handlerName, configure);
    }

    /// <summary>
    /// Adds a subgraph node to the graph.
    /// </summary>
    public GraphBuilder AddSubGraphNode(
        string id,
        string name,
        Abstractions.Graph.Graph subGraph,
        Action<NodeBuilder>? configure = null)
    {
        var builder = new NodeBuilder(id, name, NodeType.SubGraph, null);
        builder.WithSubGraph(subGraph);
        configure?.Invoke(builder);
        _nodes.Add(builder.Build());
        return this;
    }

    /// <summary>
    /// Adds an edge between two nodes.
    /// </summary>
    public GraphBuilder AddEdge(
        string from,
        string to,
        Action<EdgeBuilder>? configure = null)
    {
        var builder = new EdgeBuilder(from, to);
        configure?.Invoke(builder);
        _edges.Add(builder.Build());
        return this;
    }

    /// <summary>
    /// Adds an unconditional edge between two nodes.
    /// </summary>
    public GraphBuilder AddEdge(string from, string to)
    {
        return AddEdge(from, to, null);
    }

    // ========================================
    // Upstream State Conditions
    // ========================================

    /// <summary>
    /// Set upstream state condition on all incoming edges to a node.
    /// WARNING: This will replace any existing data-based conditions on those edges.
    /// </summary>
    /// <param name="targetNodeId">Node ID to apply condition to</param>
    /// <param name="upstreamCondition">Upstream condition type</param>
    /// <returns>This builder for chaining</returns>
    /// <exception cref="ArgumentException">If condition is not an upstream condition type</exception>
    /// <exception cref="InvalidOperationException">If node has no incoming edges or edges have conflicting conditions</exception>
    public GraphBuilder WithUpstreamCondition(string targetNodeId, ConditionType upstreamCondition)
    {
        if (upstreamCondition != ConditionType.UpstreamOneSuccess &&
            upstreamCondition != ConditionType.UpstreamAllDone &&
            upstreamCondition != ConditionType.UpstreamAllDoneOneSuccess)
        {
            throw new ArgumentException(
                $"Condition type must be upstream condition, got: {upstreamCondition}",
                nameof(upstreamCondition));
        }

        // Find all edges pointing to targetNodeId
        var incomingEdges = _edges.Where(e => e.To == targetNodeId).ToList();

        if (incomingEdges.Count == 0)
            throw new InvalidOperationException($"Node {targetNodeId} has no incoming edges");

        // Check if any edges already have non-default conditions
        var edgesWithConditions = incomingEdges
            .Where(e => e.Condition != null &&
                        e.Condition.Type != ConditionType.Always)
            .ToList();

        if (edgesWithConditions.Count > 0)
        {
            var edgeList = string.Join(", ", edgesWithConditions.Select(e => $"{e.From} → {e.To}"));
            throw new InvalidOperationException(
                $"Cannot set upstream condition on node {targetNodeId}: " +
                $"The following edges already have conditions: {edgeList}. " +
                "Upstream conditions replace existing edge conditions. " +
                "Remove existing conditions first or use separate nodes for different conditions.");
        }

        // Set condition on all incoming edges
        for (int i = 0; i < _edges.Count; i++)
        {
            var edge = _edges[i];
            if (edge.To == targetNodeId)
            {
                _edges[i] = edge with
                {
                    Condition = new EdgeCondition
                    {
                        Type = upstreamCondition
                    }
                };
            }
        }

        return this;
    }

    /// <summary>
    /// Convenience: Execute if at least one upstream succeeded (parallel fallback).
    /// </summary>
    /// <param name="targetNodeId">Node ID to apply condition to</param>
    /// <returns>This builder for chaining</returns>
    public GraphBuilder RequireOneSuccess(string targetNodeId)
    {
        return WithUpstreamCondition(targetNodeId, ConditionType.UpstreamOneSuccess);
    }

    /// <summary>
    /// Convenience: Execute when all upstreams completed (aggregation).
    /// </summary>
    /// <param name="targetNodeId">Node ID to apply condition to</param>
    /// <returns>This builder for chaining</returns>
    public GraphBuilder RequireAllDone(string targetNodeId)
    {
        return WithUpstreamCondition(targetNodeId, ConditionType.UpstreamAllDone);
    }

    /// <summary>
    /// Convenience: Execute when all done AND at least one succeeded (partial success).
    /// </summary>
    /// <param name="targetNodeId">Node ID to apply condition to</param>
    /// <returns>This builder for chaining</returns>
    public GraphBuilder RequirePartialSuccess(string targetNodeId)
    {
        return WithUpstreamCondition(targetNodeId, ConditionType.UpstreamAllDoneOneSuccess);
    }

    /// <summary>
    /// Builds the graph.
    /// </summary>
    public Abstractions.Graph.Graph Build()
    {
        if (string.IsNullOrWhiteSpace(_name))
            throw new InvalidOperationException("Graph name is required. Call WithName() before Build().");

        // Ensure START and END nodes exist
        if (!_nodes.Any(n => n.Id == _entryNodeId))
        {
            AddStartNode(_entryNodeId);
        }

        if (!_nodes.Any(n => n.Id == _exitNodeId))
        {
            AddEndNode(_exitNodeId);
        }

        return new Abstractions.Graph.Graph
        {
            Id = _id!,
            Name = _name,
            Version = _version,
            Nodes = _nodes,
            Edges = _edges,
            EntryNodeId = _entryNodeId,
            ExitNodeId = _exitNodeId,
            Metadata = _metadata,
            MaxIterations = _maxIterations,
            ExecutionTimeout = _executionTimeout
        };
    }

    /// <summary>
    /// Creates a simple linear graph from a sequence of handler names.
    /// </summary>
    public static Abstractions.Graph.Graph Linear(string graphName, params string[] handlerNames)
    {
        if (handlerNames == null || handlerNames.Length == 0)
            throw new ArgumentException("At least one handler name is required", nameof(handlerNames));

        var builder = new GraphBuilder()
            .WithName(graphName)
            .AddStartNode();

        string previousNodeId = "START";

        for (int i = 0; i < handlerNames.Length; i++)
        {
            var nodeId = $"node_{i + 1}";
            var handlerName = handlerNames[i];

            builder.AddHandlerNode(nodeId, handlerName, handlerName);
            builder.AddEdge(previousNodeId, nodeId);

            previousNodeId = nodeId;
        }

        builder.AddEndNode();
        builder.AddEdge(previousNodeId, "END");

        return builder.Build();
    }
}

/// <summary>
/// Builder for individual nodes.
/// </summary>
public class NodeBuilder
{
    private readonly string _id;
    private readonly string _name;
    private readonly NodeType _type;
    private readonly string? _handlerName;
    private readonly Dictionary<string, object> _config = new();
    private TimeSpan? _timeout;
    private RetryPolicy? _retryPolicy;
    private readonly Dictionary<string, string> _metadata = new();
    private bool _enableCheckpointing = true;
    private int? _maxExecutions;
    private Abstractions.Graph.Graph? _subGraph;
    private string? _subGraphRef;
    private int? _maxInputBufferSize;
    private ErrorPropagationPolicy? _errorPolicy;
    private SuspensionOpts? _suspensionOptions;

    internal NodeBuilder(string id, string name, NodeType type, string? handlerName)
    {
        _id = id;
        _name = name;
        _type = type;
        _handlerName = handlerName;
    }

    /// <summary>
    /// Adds a configuration key-value pair.
    /// </summary>
    public NodeBuilder WithConfig(string key, object value)
    {
        _config[key] = value;
        return this;
    }

    /// <summary>
    /// Sets the node timeout.
    /// </summary>
    public NodeBuilder WithTimeout(TimeSpan timeout)
    {
        _timeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets the retry policy.
    /// </summary>
    public NodeBuilder WithRetryPolicy(RetryPolicy policy)
    {
        _retryPolicy = policy;
        return this;
    }

    /// <summary>
    /// Adds metadata.
    /// </summary>
    public NodeBuilder WithMetadata(string key, string value)
    {
        _metadata[key] = value;
        return this;
    }

    /// <summary>
    /// Enables or disables checkpointing.
    /// </summary>
    public NodeBuilder WithCheckpointing(bool enabled)
    {
        _enableCheckpointing = enabled;
        return this;
    }

    /// <summary>
    /// Sets the maximum execution count.
    /// </summary>
    public NodeBuilder WithMaxExecutions(int maxExecutions)
    {
        _maxExecutions = maxExecutions;
        return this;
    }

    /// <summary>
    /// Sets the subgraph for SubGraph nodes.
    /// </summary>
    public NodeBuilder WithSubGraph(Abstractions.Graph.Graph subGraph)
    {
        _subGraph = subGraph;
        return this;
    }

    /// <summary>
    /// Sets the subgraph reference for SubGraph nodes.
    /// </summary>
    public NodeBuilder WithSubGraphRef(string subGraphRef)
    {
        _subGraphRef = subGraphRef;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of parallel executions for this node.
    /// </summary>
    public NodeBuilder WithMaxParallelExecutions(int maxParallelExecutions)
    {
        _maxInputBufferSize = maxParallelExecutions;
        return this;
    }

    /// <summary>
    /// Sets the error propagation policy.
    /// </summary>
    public NodeBuilder WithErrorPolicy(ErrorPropagationPolicy policy)
    {
        _errorPolicy = policy;
        return this;
    }

    /// <summary>
    /// Sets the suspension options for this node.
    /// Controls behavior when handler returns Suspended result.
    /// </summary>
    public NodeBuilder WithSuspensionOptions(SuspensionOpts options)
    {
        _suspensionOptions = options;
        return this;
    }

    /// <summary>
    /// Configures active wait timeout for suspension.
    /// Convenience method for common case.
    /// </summary>
    /// <param name="timeout">How long to wait for approval response.</param>
    public NodeBuilder WithActiveWait(TimeSpan timeout)
    {
        _suspensionOptions = new SuspensionOpts { ActiveWaitTimeout = timeout };
        return this;
    }

    /// <summary>
    /// Configures immediate suspend (no waiting).
    /// Use when approval may take hours/days and caller will resume from checkpoint.
    /// </summary>
    public NodeBuilder WithImmediateSuspend()
    {
        _suspensionOptions = SuspensionOpts.ImmediateSuspend;
        return this;
    }

    internal Node Build()
    {
        return new Node
        {
            Id = _id,
            Name = _name,
            Type = _type,
            HandlerName = _handlerName,
            Config = _config,
            Timeout = _timeout,
            RetryPolicy = _retryPolicy,
            Metadata = _metadata,
            EnableCheckpointing = _enableCheckpointing,
            MaxExecutions = _maxExecutions,
            SubGraph = _subGraph,
            SubGraphRef = _subGraphRef,
            MaxParallelExecutions = _maxInputBufferSize,
            ErrorPolicy = _errorPolicy,
            SuspensionOptions = _suspensionOptions
        };
    }
}

/// <summary>
/// Builder for individual edges.
/// </summary>
public class EdgeBuilder
{
    private readonly string _from;
    private readonly string _to;
    private EdgeCondition? _condition;
    private readonly Dictionary<string, string> _metadata = new();

    internal EdgeBuilder(string from, string to)
    {
        _from = from;
        _to = to;
    }

    /// <summary>
    /// Sets the edge condition.
    /// </summary>
    public EdgeBuilder WithCondition(EdgeCondition condition)
    {
        _condition = condition;
        return this;
    }

    /// <summary>
    /// Adds metadata.
    /// </summary>
    public EdgeBuilder WithMetadata(string key, string value)
    {
        _metadata[key] = value;
        return this;
    }

    internal Edge Build()
    {
        return new Edge
        {
            From = _from,
            To = _to,
            Condition = _condition,
            Metadata = _metadata
        };
    }
}
