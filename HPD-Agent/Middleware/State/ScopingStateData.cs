// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Collections.Immutable;

// Backward compatibility alias
using CollapsingStateData = HPD.Agent.ContainerMiddlewareState;

namespace HPD.Agent;

/// <summary>
/// State for ContainerMiddleware. Tracks container expansions, instructions, and recovery operations.
/// Handles both Toolkits and Skills uniformly as "containers".
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
/// var state = context.GetMiddlewareState&lt;ContainerMiddlewareState&gt;() ?? new();
/// var isExpanded = state.ExpandedContainers.Contains("FinancialToolkit");
///
/// // Update state
/// context.UpdateMiddlewareState&lt;ContainerMiddlewareState&gt;(s =>
///     s.WithExpandedContainer("FinancialToolkit"));
/// </code>
///
/// <para><b>Lifecycle:</b></para>
/// <para>
/// - ExpandedContainers persist across message turns (session-level state)
/// - ContainersExpandedThisTurn tracked during current turn, cleared at turn end
/// - ActiveContainerInstructions cleared at end of message turn
/// - RecoveredFunctionCalls tracked during turn, cleared at turn end
/// </para>
/// </remarks>
[MiddlewareState]
public sealed record ContainerMiddlewareState
{
    /// <summary>
    /// All expanded containers (Toolkits AND skills) across the entire session.
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
    /// Tracks recovered function calls (hidden items or qualified names).
    /// Maps CallId to recovery info for explanatory messages and history rewriting.
    /// Cleared at end of message turn.
    /// </summary>
    public ImmutableDictionary<string, RecoveryInfo> RecoveredFunctionCalls { get; init; }
        = ImmutableDictionary<string, RecoveryInfo>.Empty;

    /// <summary>
    /// Records a container expansion (Toolkit or skill).
    /// Adds to both session-level ExpandedContainers and turn-level ContainersExpandedThisTurn.
    /// </summary>
    /// <param name="containerName">Name of the container being expanded</param>
    /// <returns>New state with container added to both sets</returns>
    public ContainerMiddlewareState WithExpandedContainer(string containerName)
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
    public ContainerMiddlewareState WithContainerInstructions(string containerName, ContainerInstructionSet instructions)
    {
        return this with
        {
            ActiveContainerInstructions = ActiveContainerInstructions.SetItem(containerName, instructions)
        };
    }

    /// <summary>
    /// Records a recovered function call for later explanatory message injection.
    /// </summary>
    /// <param name="callId">Function call ID</param>
    /// <param name="recovery">Recovery information</param>
    /// <returns>New state with recovery info added</returns>
    public ContainerMiddlewareState WithRecoveredFunction(string callId, RecoveryInfo recovery)
    {
        return this with
        {
            RecoveredFunctionCalls = RecoveredFunctionCalls.SetItem(callId, recovery)
        };
    }

    /// <summary>
    /// Clears all active container instructions (typically at end of message turn).
    /// </summary>
    /// <returns>New state with cleared instructions</returns>
    public ContainerMiddlewareState ClearContainerInstructions()
    {
        return this with
        {
            ActiveContainerInstructions = ImmutableDictionary<string, ContainerInstructionSet>.Empty
        };
    }

    /// <summary>
    /// Clears the turn-level container tracking and recovery info.
    /// Called at end of message turn after cleanup is complete.
    /// </summary>
    /// <returns>New state with cleared turn containers and recovery info</returns>
    public ContainerMiddlewareState ClearTurnContainers()
    {
        return this with
        {
            ContainersExpandedThisTurn = ImmutableHashSet<string>.Empty,
            RecoveredFunctionCalls = ImmutableDictionary<string, RecoveryInfo>.Empty
        };
    }
}

/// <summary>
/// Instruction contexts for a container (Toolkit or skill).
/// Supports dual-injection: function result (ephemeral) + system prompt (persistent).
/// </summary>
public sealed record ContainerInstructionSet(
    string? FunctionResult,
    string? SystemPrompt
);

/// <summary>
/// Information about a recovered function call.
/// Used to track why a function triggered auto-expansion and provide explanatory messages.
/// </summary>
/// <param name="Type">Type of recovery that occurred</param>
/// <param name="ContainerName">Name of the container that was auto-expanded</param>
/// <param name="FunctionName">Original function name that triggered recovery</param>
public sealed record RecoveryInfo(
    RecoveryType Type,
    string ContainerName,
    string FunctionName
);

/// <summary>
/// Type of recovery operation.
/// </summary>
public enum RecoveryType
{
    /// <summary>Hidden item call (e.g., "Add" when container not expanded)</summary>
    HiddenItem,
    /// <summary>Qualified name call (e.g., "MathToolkit:Add", "MathToolkit.Add")</summary>
    QualifiedName,
    /// <summary>Container called with arguments (e.g., "MathToolkit(a: 5, b: 10)" should be "MathToolkit()")</summary>
    ContainerWithArguments
}
