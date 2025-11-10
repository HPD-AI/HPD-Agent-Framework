using HPD_Agent.Skills;
using HPD.Agent;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Extension methods for configuring skills for the AgentBuilder.
/// Skills enable semantic grouping of functions from multiple plugins (M:N relationships).
/// </summary>
public static class AgentBuilderSkillExtensions
{
    /// <summary>
    /// Configures skills for the agent using a fluent configuration API.
    /// Skills enable semantic grouping of functions from multiple plugins (M:N relationships).
    /// IMPORTANT: Requires plugin scoping to be enabled (Config.PluginScoping.Enabled = true).
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="configure">Configuration action for defining skills</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// var agent = AgentBuilder.Create()
    ///     .WithOpenAI(apiKey, "gpt-4")
    ///     .WithPlugin<FileSystemPlugin>()
    ///     .WithPlugin<DebugPlugin>()
    ///     .WithSkills(skills => {
    ///         skills.DefineSkill("Debugging", "Debugging and troubleshooting",
    ///             functionRefs: new[] { "FileSystemPlugin.ReadFile", "DebugPlugin.GetStackTrace" },
    ///             instructionDocuments: new[] { "debugging-protocol.md" });
    ///
    ///         skills.DefineSkill("FileManagement", "File operations",
    ///             functionRefs: new[] { "FileSystemPlugin.ReadFile", "FileSystemPlugin.WriteFile" },
    ///             instructionDocuments: new[] { "file-safety.md" });
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithSkills(this AgentBuilder builder, Action<SkillConfigurator> configure)
    {
        var configurator = new SkillConfigurator();
        configure(configurator);

        // Store skills in the builder for later use in Build()
        builder._skillDefinitions = configurator.GetSkills();

        return builder;
    }

    /// <summary>
    /// Configures skills using pre-defined SkillDefinition objects.
    /// Useful when loading skills from configuration files or databases.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="skills">Array of skill definitions</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// var skills = LoadSkillsFromConfig();
    /// builder.WithSkills(skills);
    /// </code>
    /// </example>
    public static AgentBuilder WithSkills(this AgentBuilder builder, params SkillDefinition[] skills)
    {
        builder._skillDefinitions = new List<SkillDefinition>(skills);
        return builder;
    }

    /// <summary>
    /// Configures skills using a list of pre-defined SkillDefinition objects.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="skills">List of skill definitions</param>
    /// <returns>The builder for method chaining</returns>
    public static AgentBuilder WithSkills(this AgentBuilder builder, List<SkillDefinition> skills)
    {
        builder._skillDefinitions = skills;
        return builder;
    }

    /// <summary>
    /// Adds a single skill to the agent (allows mixing reusable and custom skills).
    /// Can be called multiple times to add multiple skills.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="skill">The skill definition to add</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// // Mix reusable and custom skills
    /// var agent = AgentBuilder.Create()
    ///     .WithPlugin<FileSystemPlugin>()
    ///     .WithPlugin<DebugPlugin>()
    ///     .WithSkill(CommonSkills.DebuggingSkill)  // Reusable
    ///     .WithSkill(new SkillDefinition {          // Custom
    ///         Name = "CustomSkill",
    ///         Description = "...",
    ///         FunctionReferences = new[] { "..." }
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithSkill(this AgentBuilder builder, SkillDefinition skill)
    {
        builder._skillDefinitions ??= new List<SkillDefinition>();
        builder._skillDefinitions.Add(skill);
        return builder;
    }

    /// <summary>
    /// Enables "Skills-Only Mode" where ONLY functions referenced by skills are visible.
    /// All plugin containers and unreferenced functions are hidden.
    /// Skills become the exclusive interface for accessing functions.
    ///
    /// Use this for pure skill-based interfaces where:
    /// - Plugins must be registered (for validation), but won't be visible
    /// - Functions only accessible through skill expansion
    /// - Skills provide the ONLY interface to the agent
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="enabled">Whether to enable skills-only mode (default: true)</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// var agent = AgentBuilder.Create()
    ///     .WithOpenAI(apiKey, "gpt-4")
    ///     .WithPlugin<FileSystemPlugin>()      // Must register (for validation)
    ///     .WithPlugin<DatabasePlugin>()        // Must register (for validation)
    ///     .WithSkill(new SkillDefinition {
    ///         Name = "Debugging",
    ///         FunctionReferences = new[] { "ReadFile", "ExecuteSQL" }
    ///     })
    ///     .EnableSkillsOnlyMode()  // ← Only Debugging skill visible!
    ///     .Build();
    ///
    /// // Agent will see:
    /// //   ✅ Debugging (skill container - ONLY interface)
    /// //   ❌ ReadFile (hidden until skill expanded)
    /// //   ❌ WriteFile (hidden - not referenced by any skill)
    /// //   ❌ ExecuteSQL (hidden until skill expanded)
    /// //   ❌ All other functions (hidden - not referenced)
    /// </code>
    /// </example>
    public static AgentBuilder EnableSkillsOnlyMode(this AgentBuilder builder, bool enabled = true)
    {
        // Ensure PluginScoping config exists
        if (builder.Config.PluginScoping == null)
        {
            builder.Config.PluginScoping = new PluginScopingConfig();
        }

        // Enable plugin scoping if not already enabled (required for skills)
        if (enabled && !builder.Config.PluginScoping.Enabled)
        {
            builder.Config.PluginScoping.Enabled = true;
        }

        // Set SkillsOnlyMode flag
        builder.Config.PluginScoping.SkillsOnlyMode = enabled;

        return builder;
    }
}

/// <summary>
/// Fluent configurator for defining skills during agent builder configuration.
/// </summary>
public class SkillConfigurator
{
    private readonly List<SkillDefinition> _skills = new();

    /// <summary>
    /// Defines a new skill with function references and optional instructions.
    /// </summary>
    /// <param name="name">Skill name (used as container function name)</param>
    /// <param name="description">Description of skill capabilities</param>
    /// <param name="functionRefs">Function references in format "PluginName.FunctionName" or "FunctionName"</param>
    /// <param name="instructions">Optional inline post-expansion instructions</param>
    /// <param name="instructionDocuments">Optional paths to instruction document files</param>
    /// <param name="baseDirectory">Base directory for instruction documents (default: "skills/documents/")</param>
    /// <returns>This configurator for method chaining</returns>
    /// <example>
    /// <code>
    /// configurator.DefineSkill(
    ///     name: "Debugging",
    ///     description: "Debugging and troubleshooting capabilities",
    ///     functionRefs: new[] { "FileSystemPlugin.ReadFile", "DebugPlugin.GetStackTrace" },
    ///     instructions: "Always read error logs first.",
    ///     instructionDocuments: new[] { "debugging-protocol.md", "troubleshooting.md" }
    /// );
    /// </code>
    /// </example>
    public SkillConfigurator DefineSkill(
        string name,
        string description,
        string[] functionRefs,
        string? instructions = null,
        string[]? instructionDocuments = null,
        string? baseDirectory = null)
    {
        var skill = new SkillDefinition
        {
            Name = name,
            Description = description,
            FunctionReferences = functionRefs,
            PostExpansionInstructions = instructions,
            PostExpansionInstructionDocuments = instructionDocuments,
            InstructionDocumentBaseDirectory = baseDirectory ?? "skills/documents/"
        };

        _skills.Add(skill);
        return this;
    }

    /// <summary>
    /// Adds a pre-configured SkillDefinition to the configurator.
    /// </summary>
    /// <param name="skill">The skill definition to add</param>
    /// <returns>This configurator for method chaining</returns>
    public SkillConfigurator AddSkill(SkillDefinition skill)
    {
        _skills.Add(skill);
        return this;
    }

    /// <summary>
    /// Gets all configured skills.
    /// </summary>
    internal List<SkillDefinition> GetSkills() => _skills;
}

