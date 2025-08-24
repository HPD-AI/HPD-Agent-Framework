using System.Collections.Generic;

/// <summary>
/// An edge in the workflow graph defining a conditional transition between nodes.
/// </summary>
public record WorkflowEdge(
    string FromNodeId,
    string? ToNodeId, // Can be null for conditional edges
    string ConditionKey, // Changed from Condition
    IReadOnlyDictionary<string, string> RouteMap // New property for routing
);