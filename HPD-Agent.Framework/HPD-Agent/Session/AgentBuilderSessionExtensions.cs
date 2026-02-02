namespace HPD.Agent;

/// <summary>
/// Extension methods for AgentBuilder to configure session persistence.
/// </summary>
/// <remarks>
/// <para>
/// These extensions provide a clean API for configuring two distinct persistence concerns:
/// <list type="bullet">
/// <item><strong>Session Persistence:</strong> Snapshots for conversation history (after turn completes)</item>
/// <item><strong>Durable Execution:</strong> Checkpoints for crash recovery (during execution)</item>
/// </list>
/// </para>
/// <para>
/// <strong>Example Usage:</strong>
/// <code>
/// // Option A: Manual session persistence
/// var agent = new AgentBuilder()
///     .WithSessionStore(store)
///     .Build();
/// await agent.RunAsync("Hello", session);
/// await agent.SaveSessionAsync(session);  // User's responsibility
///
/// // Option B: Auto session persistence (snapshot after each turn)
/// var agent = new AgentBuilder()
///     .WithSessionStore(store, persistAfterTurn: true)
///     .Build();
/// await agent.RunAsync("Hello", "session-123");  // Auto-saves snapshot after turn
///
/// // Option C: Durable execution (crash recovery) - INDEPENDENT
/// var agent = new AgentBuilder()
///     .WithSessionStore(store)
///     .WithDurableExecution(CheckpointFrequency.PerIteration, RetentionPolicy.LastN(5))
///     .Build();
/// // Saves execution checkpoints during iterations for crash recovery
///
/// // Option D: Both features combined
/// var agent = new AgentBuilder()
///     .WithSessionStore(store, persistAfterTurn: true)
///     .WithDurableExecution(CheckpointFrequency.PerIteration)
///     .Build();
/// // During execution: saves ExecutionCheckpoints (crash recovery)
/// // After turn completes: saves SessionSnapshot (conversation persistence)
/// </code>
/// </para>
/// </remarks>
public static class AgentBuilderSessionExtensions
{
    /// <summary>
    /// Configures the session store for the agent with manual save mode (default).
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="store">The session store</param>
    /// <returns>The builder for chaining</returns>
    public static AgentBuilder WithSessionStore(
        this AgentBuilder builder,
        ISessionStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        builder.Config.SessionStore = store;
        builder.Config.SessionStoreOptions = new SessionStoreOptions { PersistAfterTurn = false };
        return builder;
    }

    /// <summary>
    /// Configures the session store for the agent with optional auto-persistence.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="store">The session store</param>
    /// <param name="persistAfterTurn">Whether to automatically save snapshot after each turn</param>
    /// <returns>The builder for chaining</returns>
    public static AgentBuilder WithSessionStore(
        this AgentBuilder builder,
        ISessionStore store,
        bool persistAfterTurn)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        builder.Config.SessionStore = store;
        builder.Config.SessionStoreOptions = new SessionStoreOptions { PersistAfterTurn = persistAfterTurn };
        return builder;
    }

    /// <summary>
    /// Configures the session store for the agent with full options control.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="store">The session store</param>
    /// <param name="configure">Action to configure session store options</param>
    /// <returns>The builder for chaining</returns>
    public static AgentBuilder WithSessionStore(
        this AgentBuilder builder,
        ISessionStore store,
        Action<SessionStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SessionStoreOptions();
        configure(options);

        builder.Config.SessionStore = store;
        builder.Config.SessionStoreOptions = options;
        return builder;
    }

    /// <summary>
    /// Configures durable execution (crash recovery) with checkpointing.
    /// This is INDEPENDENT of session persistence (snapshots).
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="frequency">How often to create execution checkpoints</param>
    /// <param name="retention">How many checkpoints to retain (default: LastN(3))</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// <b>Frequency options:</b>
    /// <list type="bullet">
    /// <item><c>PerTurn</c> - Checkpoint after each message turn (recommended, balanced)</item>
    /// <item><c>PerIteration</c> - Checkpoint after each LLM call (higher overhead, for long agents)</item>
    /// <item><c>Manual</c> - Only checkpoint when explicitly requested</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Retention options:</b>
    /// <list type="bullet">
    /// <item><c>RetentionPolicy.LatestOnly</c> - Keep only the latest checkpoint</item>
    /// <item><c>RetentionPolicy.LastN(n)</c> - Keep the last N checkpoints</item>
    /// <item><c>RetentionPolicy.FullHistory</c> - Keep all checkpoints (time-travel debugging)</item>
    /// <item><c>RetentionPolicy.TimeBased(duration)</c> - Keep checkpoints from the last duration</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static AgentBuilder WithDurableExecution(
        this AgentBuilder builder,
        CheckpointFrequency frequency,
        RetentionPolicy? retention = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Config.DurableExecutionConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = frequency,
            Retention = retention ?? RetentionPolicy.LastN(3)
        };
        return builder;
    }

    /// <summary>
    /// Configures durable execution with full options control.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="configure">Action to configure durable execution options</param>
    /// <returns>The builder for chaining</returns>
    public static AgentBuilder WithDurableExecution(
        this AgentBuilder builder,
        Action<DurableExecutionConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var config = new DurableExecutionConfig { Enabled = true };
        configure(config);

        builder.Config.DurableExecutionConfig = config;
        return builder;
    }

    /// <summary>
    /// Convenience overload with file-based storage.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="storagePath">Directory to store session files</param>
    /// <param name="persistAfterTurn">Whether to automatically save snapshot after each turn</param>
    /// <returns>The builder for chaining</returns>
    public static AgentBuilder WithSessionStore(
        this AgentBuilder builder,
        string storagePath,
        bool persistAfterTurn = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        var store = new JsonSessionStore(storagePath);
        return builder.WithSessionStore(store, persistAfterTurn);
    }

    // ═══════════════════════════════════════════════════════════════════
    // LEGACY METHODS (Deprecated - for backward compatibility)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// [DEPRECATED] Use WithSessionStore + WithDurableExecution instead.
    /// </summary>
    [Obsolete("Use WithSessionStore(store, persistAfterTurn) + WithDurableExecution(frequency, retention) instead")]
    public static AgentBuilder WithSessionStore(
        this AgentBuilder builder,
        ISessionStore store,
        CheckpointFrequency frequency,
        RetentionPolicy? retention = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        // For backward compatibility: enable both session persistence and durable execution
        builder.Config.SessionStore = store;
        builder.Config.SessionStoreOptions = new SessionStoreOptions
        {
            PersistAfterTurn = true,  // Auto-save enabled when specifying frequency (old behavior)
            Frequency = frequency,
            Retention = retention ?? RetentionPolicy.LastN(3)
        };
        builder.Config.DurableExecutionConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = frequency,
            Retention = retention ?? RetentionPolicy.LastN(3)
        };
        return builder;
    }

    /// <summary>
    /// [DEPRECATED] Use WithSessionStore(storagePath, persistAfterTurn) + WithDurableExecution instead.
    /// </summary>
    [Obsolete("Use WithSessionStore(storagePath, persistAfterTurn) + WithDurableExecution(frequency, retention) instead")]
    public static AgentBuilder WithSessionStore(
        this AgentBuilder builder,
        string storagePath,
        CheckpointFrequency frequency,
        RetentionPolicy? retention = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        var store = new JsonSessionStore(storagePath);
        #pragma warning disable CS0618 // Suppress obsolete warning for backward compat
        return builder.WithSessionStore(store, frequency, retention);
        #pragma warning restore CS0618
    }
}
