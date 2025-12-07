namespace HPD.Agent.Checkpointing.Services;

/// <summary>
/// Extension methods for AgentBuilder to configure checkpointing services.
/// </summary>
/// <remarks>
/// <para>
/// These extensions provide the new layered architecture for checkpointing:
/// <list type="bullet">
/// <item><c>WithCheckpointStore()</c> - Configure the storage backend</item>
/// <item><c>WithDurableExecution()</c> - Configure auto-checkpointing + retention</item>
/// </list>
/// </para>
/// <para>
/// <strong>Example Usage:</strong>
/// <code>
/// var agent = new AgentBuilder()
///     .WithProvider("openai", "gpt-4")
///     .WithCheckpointStore(new JsonCheckpointStore("./checkpoints"))
///     .WithDurableExecution(CheckpointFrequency.PerTurn, RetentionPolicy.FullHistory)
///     .Build();
/// </code>
/// </para>
/// </remarks>
public static class AgentBuilderCheckpointingExtensions
{
    /// <summary>
    /// Configures the checkpoint store for the agent.
    /// This is the storage backend only - use WithDurableExecution to configure features.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="store">The checkpoint store</param>
    /// <returns>The builder for chaining</returns>
    public static AgentBuilder WithCheckpointStore(
        this AgentBuilder builder,
        ICheckpointStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        builder.Config.ThreadStore = store;
        return builder;
    }

    /// <summary>
    /// Configures durable execution with the new service layer.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="frequency">How often to checkpoint</param>
    /// <param name="retention">Retention policy for checkpoints</param>
    /// <param name="enablePendingWrites">Enable pending writes for partial failure recovery</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// This overload creates a DurableExecutionService internally.
    /// The service is available via <c>agent.Config.DurableExecutionService</c>.
    /// </para>
    /// <para>
    /// <strong>Note:</strong> You must call <c>WithCheckpointStore()</c> first to set the storage backend.
    /// </para>
    /// </remarks>
    public static AgentBuilder WithDurableExecution(
        this AgentBuilder builder,
        CheckpointFrequency frequency,
        RetentionPolicy retention,
        bool enablePendingWrites = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(retention);

        var serviceConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = frequency,
            Retention = retention,
            EnablePendingWrites = enablePendingWrites
        };
        builder.Config.DurableExecutionConfig = serviceConfig;

        return builder;
    }

    /// <summary>
    /// Full configuration overload for advanced scenarios.
    /// Configures store and durable execution in one call.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="store">The checkpoint store</param>
    /// <param name="frequency">How often to checkpoint</param>
    /// <param name="retention">Retention policy for checkpoints</param>
    /// <param name="enablePendingWrites">Enable pending writes for partial failure recovery</param>
    /// <returns>The builder for chaining</returns>
    public static AgentBuilder WithCheckpointing(
        this AgentBuilder builder,
        ICheckpointStore store,
        CheckpointFrequency frequency = CheckpointFrequency.PerTurn,
        RetentionPolicy? retention = null,
        bool enablePendingWrites = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        retention ??= RetentionPolicy.LatestOnly;

        return builder
            .WithCheckpointStore(store)
            .WithDurableExecution(frequency, retention, enablePendingWrites);
    }

    /// <summary>
    /// Convenience overload with file-based storage.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="storagePath">Directory to store checkpoint files</param>
    /// <param name="frequency">How often to checkpoint</param>
    /// <param name="retention">Retention policy for checkpoints</param>
    /// <returns>The builder for chaining</returns>
    public static AgentBuilder WithCheckpointing(
        this AgentBuilder builder,
        string storagePath,
        CheckpointFrequency frequency = CheckpointFrequency.PerTurn,
        RetentionPolicy? retention = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        retention ??= RetentionPolicy.LatestOnly;

        var store = new JsonConversationThreadStore(storagePath);

        return builder.WithCheckpointing(store, frequency, retention);
    }
}
