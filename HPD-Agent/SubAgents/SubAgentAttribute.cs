/// <summary>
/// Marks a method as a sub-agent for source generator detection.
/// Sub-agents are callable agents that can be invoked as tools/functions by parent agents.
/// </summary>
/// <remarks>
/// Pattern: [SubAgent] public SubAgent MethodName() => SubAgentFactory.Create(...)
/// The source generator will:
/// 1. Detect methods marked with [SubAgent]
/// 2. Extract the AgentConfig from the method body
/// 3. Generate AIFunction wrapper that builds and invokes the sub-agent
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class SubAgentAttribute : Attribute
{
    /// <summary>
    /// Optional category for grouping sub-agents (e.g., "Domain Experts", "Specialists")
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Optional priority hint for sub-agent ordering (higher = more prominent)
    /// </summary>
    public int Priority { get; set; } = 0;
}
