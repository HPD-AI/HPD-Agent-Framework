namespace HPD_Agent.Skills;

/// <summary>
/// Marks a method as a skill for source generator detection.
/// Required for explicit intent and preventing false positives with helper methods.
/// Skills are automatically containers - they group related functions together.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class SkillAttribute : Attribute
{
    /// <summary>
    /// Optional category for grouping skills (e.g., "Debugging", "FileManagement")
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Optional priority hint for skill ordering (higher = more prominent)
    /// </summary>
    public int Priority { get; set; } = 0;
}
