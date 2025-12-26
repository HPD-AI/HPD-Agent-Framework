using HPDAgent.Graph.Abstractions.Graph;
using HPDAgent.Graph.Abstractions.Validation;
using GraphDefinition = HPDAgent.Graph.Abstractions.Graph.Graph;

namespace HPDAgent.Graph.Core.Validation;

/// <summary>
/// Validates graph structure and configuration.
/// </summary>
public static class GraphValidator
{
    /// <summary>
    /// Validate a graph definition.
    /// </summary>
    public static GraphValidationResult Validate(GraphDefinition graph)
    {
        var errors = new List<GraphValidationError>();
        var warnings = new List<GraphValidationWarning>();

        // Check basic structure
        ValidateBasicStructure(graph, errors);

        // Check for cycles
        ValidateCycles(graph, errors, warnings);

        // Check for unreachable nodes
        ValidateReachability(graph, errors, warnings);

        // Check for orphaned nodes
        ValidateOrphanedNodes(graph, warnings);

        // Check handler names
        ValidateHandlerNames(graph, warnings);

        if (errors.Count > 0)
        {
            return GraphValidationResult.Failure(errors, warnings);
        }

        return GraphValidationResult.Success(warnings);
    }

    private static void ValidateBasicStructure(GraphDefinition graph, List<GraphValidationError> errors)
    {
        // Check for START node
        var startNode = graph.GetNode(graph.EntryNodeId);
        if (startNode == null)
        {
            errors.Add(new GraphValidationError
            {
                Code = "MISSING_START",
                Message = $"Entry node '{graph.EntryNodeId}' not found in graph"
            });
        }
        else if (startNode.Type != NodeType.Start)
        {
            errors.Add(new GraphValidationError
            {
                Code = "INVALID_START",
                Message = $"Entry node '{graph.EntryNodeId}' is not of type Start",
                NodeId = startNode.Id
            });
        }

        // Check for END node
        var endNode = graph.GetNode(graph.ExitNodeId);
        if (endNode == null)
        {
            errors.Add(new GraphValidationError
            {
                Code = "MISSING_END",
                Message = $"Exit node '{graph.ExitNodeId}' not found in graph"
            });
        }
        else if (endNode.Type != NodeType.End)
        {
            errors.Add(new GraphValidationError
            {
                Code = "INVALID_END",
                Message = $"Exit node '{graph.ExitNodeId}' is not of type End",
                NodeId = endNode.Id
            });
        }

        // Check for duplicate node IDs
        var nodeIds = new HashSet<string>();
        foreach (var node in graph.Nodes)
        {
            if (!nodeIds.Add(node.Id))
            {
                errors.Add(new GraphValidationError
                {
                    Code = "DUPLICATE_NODE_ID",
                    Message = $"Duplicate node ID: {node.Id}",
                    NodeId = node.Id
                });
            }
        }

        // Check edge references
        foreach (var edge in graph.Edges)
        {
            if (graph.GetNode(edge.From) == null)
            {
                errors.Add(new GraphValidationError
                {
                    Code = "INVALID_EDGE_FROM",
                    Message = $"Edge references non-existent source node: {edge.From}"
                });
            }

            if (graph.GetNode(edge.To) == null)
            {
                errors.Add(new GraphValidationError
                {
                    Code = "INVALID_EDGE_TO",
                    Message = $"Edge references non-existent target node: {edge.To}"
                });
            }
        }
    }

    private static void ValidateCycles(GraphDefinition graph, List<GraphValidationError> errors, List<GraphValidationWarning> warnings)
    {
        // Simple cycle detection using DFS
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();

        foreach (var node in graph.Nodes.Where(n => n.Type != NodeType.Start && n.Type != NodeType.End))
        {
            if (HasCycle(graph, node.Id, visited, recursionStack))
            {
                warnings.Add(new GraphValidationWarning
                {
                    Code = "CYCLE_DETECTED",
                    Message = $"Cycle detected involving node: {node.Id}. Ensure MaxIterations is set appropriately.",
                    NodeId = node.Id
                });
            }
        }
    }

    private static bool HasCycle(GraphDefinition graph, string nodeId, HashSet<string> visited, HashSet<string> recursionStack)
    {
        if (!visited.Contains(nodeId))
        {
            visited.Add(nodeId);
            recursionStack.Add(nodeId);

            foreach (var edge in graph.GetOutgoingEdges(nodeId))
            {
                if (!visited.Contains(edge.To))
                {
                    if (HasCycle(graph, edge.To, visited, recursionStack))
                    {
                        return true;
                    }
                }
                else if (recursionStack.Contains(edge.To))
                {
                    return true; // Cycle detected
                }
            }
        }

        recursionStack.Remove(nodeId);
        return false;
    }

    private static void ValidateReachability(GraphDefinition graph, List<GraphValidationError> errors, List<GraphValidationWarning> warnings)
    {
        // BFS from START to find all reachable nodes
        var reachable = new HashSet<string>();
        var queue = new Queue<string>();

        queue.Enqueue(graph.EntryNodeId);
        reachable.Add(graph.EntryNodeId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var edge in graph.GetOutgoingEdges(current))
            {
                if (reachable.Add(edge.To))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        // Check if END is reachable
        if (!reachable.Contains(graph.ExitNodeId))
        {
            errors.Add(new GraphValidationError
            {
                Code = "UNREACHABLE_END",
                Message = "End node is not reachable from Start node"
            });
        }

        // Warn about unreachable nodes
        foreach (var node in graph.Nodes.Where(n => n.Type != NodeType.Start && n.Type != NodeType.End))
        {
            if (!reachable.Contains(node.Id))
            {
                warnings.Add(new GraphValidationWarning
                {
                    Code = "UNREACHABLE_NODE",
                    Message = $"Node '{node.Id}' is not reachable from Start",
                    NodeId = node.Id
                });
            }
        }
    }

    private static void ValidateOrphanedNodes(GraphDefinition graph, List<GraphValidationWarning> warnings)
    {
        foreach (var node in graph.Nodes.Where(n => n.Type != NodeType.Start && n.Type != NodeType.End))
        {
            var incomingCount = graph.GetIncomingEdges(node.Id).Count;
            var outgoingCount = graph.GetOutgoingEdges(node.Id).Count;

            if (incomingCount == 0 && outgoingCount == 0)
            {
                warnings.Add(new GraphValidationWarning
                {
                    Code = "ORPHANED_NODE",
                    Message = $"Node '{node.Id}' has no incoming or outgoing edges",
                    NodeId = node.Id
                });
            }
        }
    }

    private static void ValidateHandlerNames(GraphDefinition graph, List<GraphValidationWarning> warnings)
    {
        foreach (var node in graph.Nodes.Where(n => n.Type == NodeType.Handler || n.Type == NodeType.Router))
        {
            if (string.IsNullOrWhiteSpace(node.HandlerName))
            {
                warnings.Add(new GraphValidationWarning
                {
                    Code = "MISSING_HANDLER_NAME",
                    Message = $"Node '{node.Id}' is type {node.Type} but has no handler name",
                    NodeId = node.Id
                });
            }
        }
    }
}
