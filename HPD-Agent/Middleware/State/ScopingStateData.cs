// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Collections.Immutable;

namespace HPD.Agent;

/// <summary>
/// State for tool Collapsing middleware. Tracks which plugin and skill containers
/// have been expanded during the current message turn.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b></para>
/// <para>
/// This state is immutable and flows through the context.
/// It is NOT stored in middleware instance fields, preserving thread safety
/// for concurrent RunAsync() calls.
/// </para>
///
/// <para><b>Usage:</b></para>
/// <code>
/// // Read state
/// var CollapsingState = context.State.MiddlewareState.Collapsing ?? new();
/// var isExpanded = CollapsingState.ExpandedPlugins.Contains("FinancialPlugin");
///
/// // Update state
/// context.UpdateState(s => s with
/// {
///     MiddlewareState = s.MiddlewareState.WithCollapsing(
///         CollapsingState.WithExpandedPlugin("FinancialPlugin"))
/// });
/// </code>
///
/// <para><b>Lifecycle:</b></para>
/// <para>
/// - ExpandedPlugins and ExpandedSkills persist within a message turn
/// - ActiveSkillInstructions cleared at end of message turn
/// </para>
/// </remarks>
[MiddlewareState]
public sealed record CollapsingStateData
{
    /// <summary>
    /// Plugin containers that have been expanded this turn.
    /// Plugins in this set have their member functions visible.
    /// </summary>
    public ImmutableHashSet<string> ExpandedPlugins { get; init; }
        = ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Skill containers that have been expanded this turn.
    /// Skills in this set have their member functions visible.
    /// </summary>
    public ImmutableHashSet<string> ExpandedSkills { get; init; }
        = ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Active skill instructions for prompt injection.
    /// Maps skill name to its instruction text.
    /// Cleared at end of message turn.
    /// </summary>
    public ImmutableDictionary<string, string> ActiveSkillInstructions { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Records a plugin expansion.
    /// </summary>
    /// <param name="pluginName">Name of the plugin being expanded</param>
    /// <returns>New state with plugin added to expanded set</returns>
    public CollapsingStateData WithExpandedPlugin(string pluginName)
    {
        return this with
        {
            ExpandedPlugins = ExpandedPlugins.Add(pluginName)
        };
    }

    /// <summary>
    /// Records a skill expansion.
    /// </summary>
    /// <param name="skillName">Name of the skill being expanded</param>
    /// <returns>New state with skill added to expanded set</returns>
    public CollapsingStateData WithExpandedSkill(string skillName)
    {
        return this with
        {
            ExpandedSkills = ExpandedSkills.Add(skillName)
        };
    }

    /// <summary>
    /// Adds or updates skill instructions.
    /// </summary>
    /// <param name="skillName">Name of the skill</param>
    /// <param name="instructions">Instruction text to inject</param>
    /// <returns>New state with updated instructions</returns>
    public CollapsingStateData WithSkillInstructions(string skillName, string instructions)
    {
        return this with
        {
            ActiveSkillInstructions = ActiveSkillInstructions.SetItem(skillName, instructions)
        };
    }

    /// <summary>
    /// Clears all active skill instructions (typically at end of message turn).
    /// </summary>
    /// <returns>New state with cleared instructions</returns>
    public CollapsingStateData ClearSkillInstructions()
    {
        return this with
        {
            ActiveSkillInstructions = ImmutableDictionary<string, string>.Empty
        };
    }
}
