namespace HPDAgent.Graph.Abstractions.Graph;

/// <summary>
/// Directed graph definition.
/// Supports both DAG (acyclic) and cyclic graphs with iteration limits.
/// Immutable after construction.
/// </summary>
public sealed record Graph
{
    /// <summary>
    /// Unique graph identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable graph name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Graph version (semantic versioning recommended).
    /// </summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// All nodes in the graph.
    /// </summary>
    public required IReadOnlyList<Node> Nodes { get; init; }

    /// <summary>
    /// All edges in the graph.
    /// </summary>
    public required IReadOnlyList<Edge> Edges { get; init; }

    /// <summary>
    /// Entry node ID (typically "START").
    /// </summary>
    public required string EntryNodeId { get; init; }

    /// <summary>
    /// Exit node ID (typically "END").
    /// </summary>
    public required string ExitNodeId { get; init; }

    /// <summary>
    /// Additional metadata (author, description, tags, etc.).
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Maximum iterations for cyclic graphs (cycle protection).
    /// </summary>
    public int MaxIterations { get; init; } = 10;

    /// <summary>
    /// Global execution timeout for the entire graph.
    /// Null = no timeout.
    /// </summary>
    public TimeSpan? ExecutionTimeout { get; init; }

    /// <summary>
    /// Get a node by ID.
    /// </summary>
    public Node? GetNode(string nodeId)
    {
        return Nodes.FirstOrDefault(n => n.Id == nodeId);
    }

    /// <summary>
    /// Get all incoming edges for a node.
    /// </summary>
    public IReadOnlyList<Edge> GetIncomingEdges(string nodeId)
    {
        return Edges.Where(e => e.To == nodeId).ToList();
    }

    /// <summary>
    /// Get all outgoing edges for a node.
    /// </summary>
    public IReadOnlyList<Edge> GetOutgoingEdges(string nodeId)
    {
        return Edges.Where(e => e.From == nodeId).ToList();
    }

    /// <summary>
    /// Get all edges in the graph.
    /// </summary>
    public IReadOnlyList<Edge> GetAllEdges()
    {
        return Edges;
    }

    /// <summary>
    /// Get execution layers using topological sorting (Kahn's algorithm).
    /// Nodes in the same layer can execute in parallel.
    /// </summary>
    /// <returns>List of execution layers in order</returns>
    public IReadOnlyList<Orchestration.ExecutionLayer> GetExecutionLayers()
    {
        var layers = new List<Orchestration.ExecutionLayer>();
        var inDegree = new Dictionary<string, int>();

        // Build adjacency list once for O(E) instead of O(V·E)
        var outgoingEdges = new Dictionary<string, List<Edge>>();

        // Initialize structures
        foreach (var node in Nodes.Where(n => n.Type != NodeType.Start && n.Type != NodeType.End))
        {
            inDegree[node.Id] = 0;
            outgoingEdges[node.Id] = new List<Edge>();
        }

        // Build adjacency list and count in-degrees in one pass O(E)
        foreach (var edge in Edges)
        {
            // Skip edges involving START/END nodes
            var fromIsExcluded = Nodes.FirstOrDefault(n => n.Id == edge.From)?.Type is NodeType.Start or NodeType.End;
            var toIsExcluded = Nodes.FirstOrDefault(n => n.Id == edge.To)?.Type is NodeType.Start or NodeType.End;

            if (fromIsExcluded == true || toIsExcluded == true)
                continue;

            if (inDegree.ContainsKey(edge.To))
            {
                inDegree[edge.To]++;
            }

            if (outgoingEdges.ContainsKey(edge.From))
            {
                outgoingEdges[edge.From].Add(edge);
            }
        }

        int level = 0;

        while (inDegree.Count > 0)
        {
            // Find all nodes with in-degree 0 (ready to execute)
            var ready = inDegree.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key).ToList();

            if (ready.Count == 0)
            {
                // Cycle detected or unreachable nodes
                break;
            }

            // Create layer for nodes that are ready
            layers.Add(new Orchestration.ExecutionLayer
            {
                Level = level,
                NodeIds = ready
            });

            // Remove processed nodes and update in-degrees
            foreach (var nodeId in ready)
            {
                inDegree.Remove(nodeId);

                // Decrease in-degree for downstream nodes using adjacency list
                if (outgoingEdges.TryGetValue(nodeId, out var edges))
                {
                    foreach (var edge in edges)
                    {
                        if (inDegree.ContainsKey(edge.To))
                        {
                            inDegree[edge.To]--;
                        }
                    }
                }
            }

            level++;
        }

        return layers;
    }
}
