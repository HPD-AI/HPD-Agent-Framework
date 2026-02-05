namespace HPD.Agent;

/// <summary>
/// Extension methods for AgentBuilder to configure session persistence.
/// </summary>
/// <remarks>
/// <para>
/// Crash recovery is automatic: if a session store is configured, uncommitted turns
/// are saved after each tool batch and deleted on turn completion.
/// </para>
/// <para>
/// <strong>Example Usage:</strong>
/// <code>
/// // Manual session persistence
/// var agent = new AgentBuilder()
///     .WithSessionStore(store)
///     .Build();
///
/// // Auto session persistence (snapshot after each turn)
/// var agent = new AgentBuilder()
///     .WithSessionStore(store, persistAfterTurn: true)
///     .Build();
/// </code>
/// </para>
/// </remarks>
public static class AgentBuilderSessionExtensions
{
    /// <summary>
    /// Configures the session store for the agent with manual save mode (default).
    /// Crash recovery is automatic when a session store is configured.
    /// </summary>
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
    /// Crash recovery is automatic when a session store is configured.
    /// </summary>
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
    /// Convenience overload with file-based storage.
    /// </summary>
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
}
