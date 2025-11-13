namespace HPD_Agent.Skills;

/// <summary>
/// Represents a type-safe skill - a semantic grouping of functions with instructions.
/// Created via SkillFactory.Create() and processed by source generator.
/// </summary>
public class Skill
{
    /// <summary>
    /// Skill name (REQUIRED - becomes AIFunction name shown to agent)
    /// </summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>
    /// Description shown in tool list before activation (REQUIRED - becomes AIFunction description).
    /// This is what the agent sees when browsing available tools.
    /// Example: "Debug file operations" appears as tool description before skill is invoked.
    /// </summary>
    public string Description { get; internal set; } = string.Empty;

    /// <summary>
    /// Inline instructions shown after skill activation (shown in function response).
    /// This is returned to the agent after it invokes the skill.
    /// </summary>
    public string? Instructions { get; internal set; }

    /// <summary>
    /// String-based references to functions or skills in "PluginName.FunctionName" format.
    /// Validated at compile-time by source generator, works with instance methods at runtime.
    /// Example: "FileSystemPlugin.ReadFile"
    /// </summary>
    public string[] References { get; internal set; } = Array.Empty<string>();

    /// <summary>Skill configuration options</summary>
    public SkillOptions Options { get; internal set; } = new();

    // Internal - resolved by source generator during code generation

    /// <summary>
    /// Resolved function references in "PluginName.FunctionName" format
    /// Set by source generator after flattening skill references
    /// </summary>
    internal string[] ResolvedFunctionReferences { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Resolved plugin types that need to be registered
    /// Set by source generator after analyzing all references
    /// </summary>
    internal string[] ResolvedPluginTypes { get; set; } = Array.Empty<string>();
}
