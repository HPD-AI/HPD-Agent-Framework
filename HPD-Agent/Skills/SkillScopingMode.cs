namespace HPD_Agent.Skills;

/// <summary>
/// Defines how a skill handles function visibility.
/// </summary>
public enum SkillScopingMode
{
    /// <summary>
    /// Functions remain visible, skill provides instructions only (default).
    /// Use for general-purpose skills where discoverability is important.
    /// The skill container serves as an instruction delivery mechanism.
    /// </summary>
    InstructionOnly,

    /// <summary>
    /// Functions hidden until skill expanded (token efficient).
    /// Use for highly specialized workflows where functions should only be
    /// visible when the agent explicitly needs this skill.
    /// </summary>
    Scoped
}
