/// <summary>
/// A node in the workflow graph representing an agent execution step.
/// </summary>
public record WorkflowNode(
    string Id,
    string NodeKey // Changed from AgentName
);