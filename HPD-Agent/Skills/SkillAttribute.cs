/// <summary>
/// Marks a method as a skill for source generator detection.
/// Required for explicit intent and preventing false positives with helper methods.
/// Skills are automatically containers - they group related functions together.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class SkillAttribute : Attribute
{
}
