using System;
using System.Collections.Generic;

/// <summary>
/// Fluent builder that makes simple things simple, and complex things possible for creating WorkflowDefinitions.
/// </summary>
public class WorkflowBuilder
{
    private readonly List<WorkflowNode> _nodes = new();
    private readonly List<WorkflowEdge> _edges = new();
    private readonly string _name;
    private string? _startNodeId;

    private WorkflowBuilder(string name)
    {
        _name = name;
    }

    public static WorkflowBuilder Create(string name = "Workflow") => new(name);

    /// <summary>
    /// Defines the start node and adds it to the graph.
    /// </summary>
    /// <param name="nodeId">The unique ID for this node instance in the graph.</param>
    /// <param name="nodeKey">The key to look up the StateNode in the WorkflowRegistry.</param>
    public WorkflowBuilder StartWith(string nodeId, string nodeKey)
    {
        _startNodeId = nodeId;
        return AddNode(nodeId, nodeKey);
    }

    /// <summary>
    /// Adds a node to the workflow.
    /// </summary>
    /// <param name="id">The unique ID for this node instance in the graph.</param>
    /// <param name="nodeKey">The key to look up the StateNode in the WorkflowRegistry.</param>
    public WorkflowBuilder AddNode(string id, string nodeKey)
    {
        _nodes.Add(new WorkflowNode(id, nodeKey));
        return this;
    }

    /// <summary>
    /// Adds an unconditional transition edge from one node to another.
    /// </summary>
    public WorkflowBuilder AddEdge(string fromNodeId, string toNodeId)
    {
        // A direct edge is a conditional edge with a default "true" condition and a single route.
        var routeMap = new Dictionary<string, string> { { "true", toNodeId } };
        _edges.Add(new WorkflowEdge(fromNodeId, null, "true", routeMap));
        return this;
    }

    /// <summary>
    /// Adds a conditional edge that routes to different nodes based on the result of a condition function.
    /// </summary>
    /// <param name="fromNodeId">The starting node ID.</param>
    /// <param name="conditionKey">The key to look up the ConditionFunc in the WorkflowRegistry.</param>
    /// <param name="routeMap">A dictionary mapping the string result of the condition function to the next node ID.</param>
    public WorkflowBuilder AddConditionalEdge(
        string fromNodeId,
        string conditionKey,
        IReadOnlyDictionary<string, string> routeMap)
    {
        _edges.Add(new WorkflowEdge(fromNodeId, null, conditionKey, routeMap));
        return this;
    }

    public WorkflowDefinition Build()
    {
        if (string.IsNullOrEmpty(_startNodeId))
            throw new InvalidOperationException("Workflow must have a start node. Use the StartWith() method.");
        return new WorkflowDefinition(_name, _startNodeId, _nodes, _edges);
    }
}