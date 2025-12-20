// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Collections.Immutable;

namespace HPD.Agent;

/// <summary>
/// State for tool Collapsing middleware. Tracks which containers (plugins and skills)
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
/// var isExpanded = CollapsingState.ExpandedContainers.Contains("FinancialPlugin");
///
/// // Update state
/// context.UpdateState(s => s with
/// {
///     MiddlewareState = s.MiddlewareState.WithCollapsing(
///         CollapsingState.WithExpandedContainer("FinancialPlugin"))
/// });
/// </code>
///
/// <para><b>Lifecycle:</b></para>
/// <para>
/// - ExpandedContainers persist across message turns (session-level state)
/// - ContainersExpandedThisTurn tracked during current turn, cleared at turn end
/// - ActiveContainerInstructions cleared at end of message turn
/// </para>
/// </remarks>
[MiddlewareState]
public sealed record CollapsingStateData
{
    /// <summary>
    /// All expanded containers (plugins AND skills) across the entire session.
    /// Containers in this set have their member functions visible.
    /// Persists across message turns.
    /// </summary>
    public ImmutableHashSet<string> ExpandedContainers { get; init; }
        = ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Containers expanded during the CURRENT message turn only.
    /// Used for cleanup in AfterMessageTurnAsync to remove container calls from TurnHistory.
    /// Cleared at end of each message turn.
    /// </summary>
    public ImmutableHashSet<string> ContainersExpandedThisTurn { get; init; }
        = ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Active container instructions for prompt injection.
    /// Maps container name to its instruction contexts (FunctionResult + SystemPrompt).
    /// Cleared at end of message turn.
    /// </summary>
    public ImmutableDictionary<string, ContainerInstructionSet> ActiveContainerInstructions { get; init; }
        = ImmutableDictionary<string, ContainerInstructionSet>.Empty;

    /// <summary>
    /// Records a container expansion (plugin or skill).
    /// Adds to both session-level ExpandedContainers and turn-level ContainersExpandedThisTurn.
    /// </summary>
    /// <param name="containerName">Name of the container being expanded</param>
    /// <returns>New state with container added to both sets</returns>
    public CollapsingStateData WithExpandedContainer(string containerName)
    {
        return this with
        {
            ExpandedContainers = ExpandedContainers.Add(containerName),
            ContainersExpandedThisTurn = ContainersExpandedThisTurn.Add(containerName)
        };
    }

    /// <summary>
    /// Adds or updates container instructions.
    /// </summary>
    /// <param name="containerName">Name of the container</param>
    /// <param name="instructions">Instruction contexts to inject</param>
    /// <returns>New state with updated instructions</returns>
    public CollapsingStateData WithContainerInstructions(string containerName, ContainerInstructionSet instructions)
    {
        return this with
        {
            ActiveContainerInstructions = ActiveContainerInstructions.SetItem(containerName, instructions)
        };
    }

    /// <summary>
    /// Clears all active container instructions (typically at end of message turn).
    /// </summary>
    /// <returns>New state with cleared instructions</returns>
    public CollapsingStateData ClearContainerInstructions()
    {
        return this with
        {
            ActiveContainerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
        };
    }

    /// <summary>
    /// Clears the turn-level container tracking.
    /// Called at end of message turn after cleanup is complete.
    /// </summary>
    /// <returns>New state with cleared turn containers</returns>
    public CollapsingStateData ClearTurnContainers()
    {
        return this with
        {
            ContainersExpandedThisTurn = ImmutableHashSet<string>.Empty
        };
    }
}

/// <summary>
/// Instruction contexts for a container (plugin or skill).
/// Supports dual-injection: function result (ephemeral) + system prompt (persistent).
/// </summary>
public sealed record ContainerInstructionSet(
    string? FunctionResult,
    string? SystemPrompt
);
