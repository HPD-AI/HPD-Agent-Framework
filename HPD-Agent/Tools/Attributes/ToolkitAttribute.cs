namespace HPD.Agent;

/// <summary>
/// Marks a class as a toolkit - a collection of related AI functions, skills, and sub-agents.
/// Consolidates the previous [Collapse] attribute functionality with clearer terminology.
/// </summary>
/// <remarks>
/// <para><b>Terminology:</b></para>
/// <list type="bullet">
/// <item><b>Toolkit</b>: A class containing [AIFunction], [Skill], and/or [SubAgent] methods</item>
/// <item><b>Tool</b>: An individual AI function within a toolkit</item>
/// </list>
///
/// <para><b>Usage Patterns:</b></para>
/// <code>
/// // Non-collapsed toolkit (all functions visible)
/// [Toolkit]
/// public class MathToolkit
/// {
///     [AIFunction] public int Add(int a, int b) => a + b;
/// }
///
/// // Collapsed toolkit (functions hidden behind container)
/// [Toolkit("Search operations across web and code")]
/// public class SearchToolkit
/// {
///     [AIFunction] public Task&lt;string&gt; WebSearch(string query) { ... }
/// }
///
/// // With custom name for registry lookup
/// [Toolkit(Name = "math")]
/// public class MathToolkit { ... }
///
/// // Collapsed with dual-context instructions
/// [Toolkit(
///     "Database operations",
///     FunctionResult = "Transaction functions available",
///     SystemPrompt = "Always use transactions for data modifications"
/// )]
/// public class DatabaseToolkit { ... }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ToolkitAttribute : Attribute
{
    /// <summary>
    /// Optional custom name for registry lookup and LLM-visible container name.
    /// If not specified, defaults to the class name (e.g., "MathToolkit").
    /// </summary>
    /// <remarks>
    /// <para>Use cases:</para>
    /// <list type="bullet">
    /// <item>Shorter names: [Toolkit(Name = "math")] instead of "MathToolkit"</item>
    /// <item>Config-friendly: snake_case or kebab-case for serialization</item>
    /// <item>LLM-optimized: Clearer names for the AI to understand</item>
    /// </list>
    ///
    /// <para>The source generator uses EffectiveName (CustomName ?? ClassName) for:</para>
    /// <list type="bullet">
    /// <item>ToolkitRegistry entry name</item>
    /// <item>Generated container function name (when collapsed)</item>
    /// <item>Skill references: "EffectiveName.FunctionName"</item>
    /// </list>
    /// </remarks>
    public string? Name { get; set; }

    /// <summary>
    /// Description shown when toolkit is collapsed into a container.
    /// When a description is provided (via positional argument or this property),
    /// the toolkit CAN be collapsed based on CollapsingConfig.Enabled.
    /// To prevent specific toolkits from collapsing at runtime, use CollapsingConfig.NeverCollapse.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Instructions returned as FUNCTION RESULT when container is activated.
    /// Visible to LLM once, as contextual acknowledgment.
    /// Use for: Status messages, operation lists, dynamic feedback.
    /// </summary>
    public string? FunctionResult { get; set; }

    /// <summary>
    /// Instructions injected into SYSTEM PROMPT persistently after activation.
    /// Visible to LLM on every iteration after container expansion.
    /// Use for: Core rules, safety guidelines, best practices, permanent context.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Simple constructor for non-collapsed toolkits.
    /// Functions are visible immediately without container expansion.
    /// </summary>
    public ToolkitAttribute()
    {
    }

    /// <summary>
    /// Constructor for collapsible toolkits (shorthand).
    /// Providing a description enables collapsing (based on CollapsingConfig.Enabled).
    /// </summary>
    /// <param name="description">Description shown in the container tool</param>
    /// <exception cref="ArgumentNullException">Thrown when description is null</exception>
    public ToolkitAttribute(string description)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    /// <summary>
    /// Constructor for collapsible toolkits with dual-context instruction injection.
    /// </summary>
    /// <param name="description">Brief description of container capabilities</param>
    /// <param name="FunctionResult">Optional instructions returned as function result (ephemeral, one-time)</param>
    /// <param name="SystemPrompt">Optional instructions injected into system prompt (persistent, every iteration)</param>
    /// <exception cref="ArgumentNullException">Thrown when description is null</exception>
    public ToolkitAttribute(
        string description,
        string? FunctionResult = null,
        string? SystemPrompt = null)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
        this.FunctionResult = FunctionResult;
        this.SystemPrompt = SystemPrompt;
    }
}
