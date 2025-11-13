using System;

/// <summary>
/// Marks a container class (plugin or skill) for scoping. When scoped, the container's functions are hidden
/// until the container is explicitly expanded by the agent.
/// This reduces token consumption and cognitive load by organizing functions hierarchically.
/// 
/// This attribute is universal - it applies to any container type (plugins, skills, or future types).
/// </summary>
/// <example>
/// <code>
/// // Plugin scoping - groups AI functions
/// [Scope("Search operations across web, code, and documentation")]
/// public class SearchPlugin
/// {
///     [AIFunction]
///     [AIDescription("Search the web for information")]
///     public async Task&lt;string&gt; WebSearch(string query) { ... }
/// }
///
/// // Skill scoping - groups related skills
/// [Scope("Financial analysis workflows combining multiple analysis techniques")]
/// public class FinancialAnalysisSkills
/// {
///     [Skill]
///     public Skill QuickLiquidityAnalysis(...) { ... }
/// }
///
/// // With post-expansion instructions
/// [Scope(
///     description: "Database operations",
///     postExpansionInstructions: @"
///         Transaction workflow:
///         1. BeginTransaction
///         2. Execute operations
///         3. CommitTransaction or RollbackTransaction
///     "
/// )]
/// public class DatabasePlugin { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ScopeAttribute : Attribute
{
    /// <summary>
    /// Description of the container shown in the scoped function.
    /// This helps the agent understand when to expand this container.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Optional instructions provided to the agent after container expansion.
    /// Use this to provide best practices, workflow guidance, safety warnings,
    /// or performance tips that are specific to this container.
    /// These instructions only consume tokens when the container is actually expanded.
    /// </summary>
    public string? PostExpansionInstructions { get; }

    /// <summary>
    /// Initializes a new instance of the ScopeAttribute with the specified description.
    /// </summary>
    /// <param name="description">Brief description of container capabilities (e.g., "Search operations", "Financial analysis")</param>
    /// <exception cref="ArgumentNullException">Thrown when description is null</exception>
    public ScopeAttribute(string description)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
        PostExpansionInstructions = null;
    }

    /// <summary>
    /// Initializes a new instance of the ScopeAttribute with description and post-expansion instructions.
    /// </summary>
    /// <param name="description">Brief description of container capabilities</param>
    /// <param name="postExpansionInstructions">Optional instructions shown to the agent after container expansion</param>
    /// <exception cref="ArgumentNullException">Thrown when description is null</exception>
    public ScopeAttribute(string description, string? postExpansionInstructions)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
        PostExpansionInstructions = postExpansionInstructions;
    }
}
