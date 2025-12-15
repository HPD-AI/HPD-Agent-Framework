using System;

/// <summary>
/// Marks a plugin class as Collapse. When Collapse, the plugin's functions are hidden
/// until the container is explicitly expanded by the agent.
/// This reduces token consumption and cognitive load by organizing functions hierarchically.
///
/// This attribute is universal - it applies to any container type (plugins, skills, or future types).
/// </summary>
/// <example>
/// <code>
/// // Plugin collapsing - groups AI functions
/// [Collapse("Search operations across web, code, and documentation")]
/// public class SearchPlugin
/// {
///     [AIFunction]
///     [AIDescription("Search the web for information")]
///     public async Task&lt;string&gt; WebSearch(string query) { ... }
/// }
///
/// // Skill collapsing - groups related skills
/// [Collapse("Financial analysis workflows combining multiple analysis techniques")]
/// public class FinancialAnalysisSkills
/// {
///     [Skill]
///     public Skill QuickLiquidityAnalysis(...) { ... }
/// }
///
/// // With dual-context instructions
/// [Collapse(
///     description: "Database operations",
///     FunctionResult: "Transaction functions available: BeginTransaction, CommitTransaction, RollbackTransaction",
///     SystemPrompt: @"
///         CRITICAL: Always follow this transaction workflow:
///         1. BeginTransaction
///         2. Execute operations
///         3. CommitTransaction (success) OR RollbackTransaction (failure)
///     "
/// )]
/// public class DatabasePlugin { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CollapseAttribute : Attribute
{
    /// <summary>
    /// Description of the container shown in the Collapse function.
    /// This helps the agent understand when to expand this container.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Instructions returned as FUNCTION RESULT when container is activated.
    /// Visible to LLM once, as contextual acknowledgment.
    /// Use for: Status messages, operation lists, dynamic feedback.
    /// </summary>
    public string? FunctionResult { get; }

    /// <summary>
    /// Instructions injected into SYSTEM PROMPT persistently after activation.
    /// Visible to LLM on every iteration after container expansion.
    /// Use for: Core rules, safety guidelines, best practices, permanent context.
    /// </summary>
    public string? SystemPrompt { get; }

    /// <summary>
    /// Initializes a new instance of the CollapseAttribute with the specified description.
    /// </summary>
    /// <param name="description">Brief description of container capabilities (e.g., "Search operations", "Financial analysis")</param>
    /// <exception cref="ArgumentNullException">Thrown when description is null</exception>
    public CollapseAttribute(string description)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
        FunctionResult = null;
        SystemPrompt = null;
    }

    /// <summary>
    /// Initializes a new instance of the CollapseAttribute with dual-context instruction injection.
    /// </summary>
    /// <param name="description">Brief description of container capabilities (e.g., "Search operations", "Financial analysis")</param>
    /// <param name="FunctionResult">Optional instructions returned as function result (ephemeral, one-time)</param>
    /// <param name="SystemPrompt">Optional instructions injected into system prompt (persistent, every iteration)</param>
    /// <exception cref="ArgumentNullException">Thrown when description is null</exception>
    public CollapseAttribute(
        string description,
        string? FunctionResult = null,
        string? SystemPrompt = null)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
        this.FunctionResult = FunctionResult;
        this.SystemPrompt = SystemPrompt;
    }
}