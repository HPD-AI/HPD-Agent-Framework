namespace HPD.Agent.SourceGenerator.Capabilities;

/// <summary>
/// Defines the types of capabilities that can be registered with the agent.
/// </summary>
internal enum CapabilityType
{
    /// <summary>
    /// A standard function capability that performs a specific operation.
    /// Decorated with [AIFunction] attribute.
    /// </summary>
    Function,

    /// <summary>
    /// A skill capability that groups related functions together.
    /// Decorated with [Skill] attribute. Skills are containers that expand to their constituent functions.
    /// </summary>
    Skill,

    /// <summary>
    /// A sub-agent capability that delegates to another agent.
    /// Decorated with [SubAgent] attribute. SubAgents are wrappers (not containers).
    /// </summary>
    SubAgent,

    /// <summary>
    /// A multi-agent workflow capability that orchestrates multiple agents.
    /// Decorated with [MultiAgent] attribute. MultiAgents are containers that execute graph workflows.
    /// </summary>
    MultiAgent
}
