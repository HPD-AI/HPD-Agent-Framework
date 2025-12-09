// <summary>
/// Factory for creating Skill objects with type-safe function references
/// Mirrors AIFunctionFactory.Create() pattern from Microsoft.Extensions.AI
/// </summary>
public static class SkillFactory
{
    /// <summary>
    /// Creates a skill with type-safe function/skill references
    /// </summary>
    /// <param name="name">Skill name (REQUIRED - becomes AIFunction name)</param>
    /// <param name="description">Description shown before activation (REQUIRED - becomes AIFunction description shown to agent in tools list)</param>
    /// <param name="instructions">Instructions shown after activation</param>
    /// <param name="references">Function or skill references in "PluginName.FunctionName" format</param>
    /// <returns>Skill object processed by source generator</returns>
    public static Skill Create(
        string name,
        string description,
        string instructions,
        params string[] references)
    {
        return Create(name, description, instructions, null, references);
    }

    /// <summary>
    /// Creates a skill with type-safe function/skill references and options
    /// </summary>
    /// <param name="name">Skill name (REQUIRED - becomes AIFunction name)</param>
    /// <param name="description">Description shown before activation (REQUIRED - becomes AIFunction description shown to agent in tools list)</param>
    /// <param name="instructions">Instructions shown after activation (REQUIRED - fallback when document store not available)</param>
    /// <param name="options">Skill configuration options (Collapsing, documents, etc.)</param>
    /// <param name="references">Function or skill references in "PluginName.FunctionName" format</param>
    /// <returns>Skill object processed by source generator</returns>
    public static Skill Create(
        string name,
        string description,
        string instructions,
        SkillOptions? options,
        params string[] references)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Skill name cannot be empty", nameof(name));

        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Skill description cannot be empty", nameof(description));

        if (string.IsNullOrWhiteSpace(instructions))
            throw new ArgumentException(
                "Skill instructions cannot be empty. Instructions are required as they serve as the fallback " +
                "when document store is not configured. Provide at least a brief step-by-step workflow.",
                nameof(instructions));

        return new Skill
        {
            Name = name,
            Description = description,
            Instructions = instructions,
            References = references ?? Array.Empty<string>(),
            Options = options ?? new SkillOptions()
        };
    }
}
