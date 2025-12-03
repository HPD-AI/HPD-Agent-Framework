using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HPD.Agent.Memory;

/// <summary>
/// Abstract base class for agent plan storage implementations.
/// Provides conversation-scoped plan storage with pluggable backends (in-memory, JSON files, SQL, Redis, etc.).
/// </summary>
public abstract class AgentPlanStore
{
    /// <summary>
    /// Creates a new plan for a conversation. If a plan already exists, it will be replaced.
    /// </summary>
    public abstract Task<AgentPlan> CreatePlanAsync(
        string conversationId,
        string goal,
        string[]? initialSteps = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the plan for a conversation, or null if no plan exists.
    /// </summary>
    public abstract Task<AgentPlan?> GetPlanAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a plan exists for a conversation.
    /// </summary>
    public abstract Task<bool> HasPlanAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the status and notes of a specific step.
    /// </summary>
    public abstract Task<PlanStep?> UpdateStepAsync(
        string conversationId,
        string stepId,
        PlanStepStatus status,
        string? notes = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a context note to a conversation's plan.
    /// </summary>
    public abstract Task AddContextNoteAsync(
        string conversationId,
        string note,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new step to a conversation's plan.
    /// </summary>
    public abstract Task<PlanStep?> AddStepAsync(
        string conversationId,
        string description,
        string? afterStepId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a conversation's plan as complete.
    /// </summary>
    public abstract Task CompletePlanAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears a conversation's plan.
    /// </summary>
    public abstract Task ClearPlanAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a formatted string representation of a conversation's plan for injection into prompts.
    /// </summary>
    public abstract Task<string> BuildPlanPromptAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes the current state of all plans to a snapshot for persistence.
    /// </summary>
    public abstract AgentPlanStoreSnapshot SerializeToSnapshot();

    /// <summary>
    /// Deserializes a snapshot and returns the appropriate store implementation.
    /// </summary>
    public static AgentPlanStore Deserialize(AgentPlanStoreSnapshot snapshot)
    {
        return snapshot.StoreType switch
        {
            AgentPlanStoreType.InMemory => InMemoryAgentPlanStore.FromSnapshot(snapshot),
            AgentPlanStoreType.JsonFile => JsonAgentPlanStore.FromSnapshot(snapshot),
            _ => throw new ArgumentException($"Unknown store type: {snapshot.StoreType}")
        };
    }
}

/// <summary>
/// Defines the types of plan stores available.
/// </summary>
public enum AgentPlanStoreType
{
    /// <summary>In-memory storage (non-persistent)</summary>
    InMemory,

    /// <summary>JSON file-based storage</summary>
    JsonFile,

    /// <summary>Custom implementation</summary>
    Custom
}

/// <summary>
/// Snapshot of plan store state for serialization/deserialization.
/// </summary>
public record AgentPlanStoreSnapshot
{
    /// <summary>The type of store this snapshot represents</summary>
    public required AgentPlanStoreType StoreType { get; init; }

    /// <summary>All plans indexed by conversation ID</summary>
    public required Dictionary<string, AgentPlan> Plans { get; init; }

    /// <summary>Optional store-specific configuration data</summary>
    public Dictionary<string, object>? Configuration { get; init; }
}
