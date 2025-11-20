using HPD_Agent.Memory.Agent.PlanMode;

namespace HPD_Agent.Memory.Agent.PlanMode;

/// <summary>
/// Configuration options for Plan Mode.
/// </summary>
public class PlanModeOptions
{
    /// <summary>
    /// Whether plan mode is enabled.
    /// Default is true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Custom storage implementation for plans.
    /// If not provided, defaults to InMemoryAgentPlanStore (non-persistent).
    /// Use JsonAgentPlanStore for file-based persistence.
    /// </summary>
    public AgentPlanStore? Store { get; set; }

    /// <summary>
    /// Storage directory for JSON file-based storage (when using default JsonAgentPlanStore).
    /// Default is "./agent-plans"
    /// </summary>
    public string StorageDirectory { get; set; } = "./agent-plans";

    /// <summary>
    /// Whether to persist plans to disk (creates JsonAgentPlanStore).
    /// Default is false (uses InMemoryAgentPlanStore).
    /// </summary>
    public bool EnablePersistence { get; set; } = false;

    /// <summary>
    /// Custom instructions to add to system prompt explaining plan mode usage.
    /// If null, no additional instructions are added (plan injection handles it).
    /// </summary>
    public string? CustomInstructions { get; set; }

    /// <summary>
    /// Sets a custom store implementation.
    /// </summary>
    public PlanModeOptions WithStore(AgentPlanStore store)
    {
        Store = store;
        return this;
    }

    /// <summary>
    /// Enables or disables plan mode.
    /// </summary>
    public PlanModeOptions WithEnabled(bool enabled)
    {
        Enabled = enabled;
        return this;
    }

    /// <summary>
    /// Sets the storage directory for file-based persistence.
    /// Only applies if EnablePersistence is true or if using JsonAgentPlanStore.
    /// </summary>
    public PlanModeOptions WithStorageDirectory(string directory)
    {
        StorageDirectory = directory;
        return this;
    }

    /// <summary>
    /// Enables file-based persistence (uses JsonAgentPlanStore).
    /// </summary>
    public PlanModeOptions WithPersistence(bool enable = true)
    {
        EnablePersistence = enable;
        return this;
    }

    /// <summary>
    /// Sets custom instructions for plan mode usage.
    /// </summary>
    public PlanModeOptions WithCustomInstructions(string instructions)
    {
        CustomInstructions = instructions;
        return this;
    }
}
