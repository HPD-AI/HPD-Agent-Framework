using Microsoft.Extensions.DependencyInjection;

namespace HPD.Agent.Checkpointing.Services;

/// <summary>
/// Extension methods for registering checkpointing services with DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds checkpointing services to the DI container.
    /// Both services share the same ICheckpointStore instance.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configuration callback</param>
    /// <returns>The service collection for chaining</returns>
    /// <example>
    /// <code>
    /// services.AddCheckpointing(opts =>
    /// {
    ///     opts.Store = new JsonCheckpointStore("/path/to/checkpoints");
    ///
    ///     opts.DurableExecution.Enabled = true;
    ///     opts.DurableExecution.Frequency = CheckpointFrequency.PerTurn;
    ///     opts.DurableExecution.Retention = RetentionPolicy.FullHistory;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCheckpointing(
        this IServiceCollection services,
        Action<CheckpointingOptions> configure)
    {
        var options = new CheckpointingOptions();
        configure(options);

        if (options.Store == null)
            throw new InvalidOperationException("CheckpointingOptions.Store must be set");

        // Register the store (singleton - shared by both services)
        services.AddSingleton<ICheckpointStore>(options.Store);

        // Register DurableExecutionService if enabled
        if (options.DurableExecution.Enabled)
        {
            services.AddSingleton(options.DurableExecution);
            services.AddSingleton<DurableExecution>();
        }

        return services;
    }

    /// <summary>
    /// Adds checkpointing services with file-based storage.
    /// Convenience overload that creates a JsonConversationThreadStore.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="storagePath">Directory to store checkpoint files</param>
    /// <param name="configure">Optional additional configuration</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddCheckpointing(
        this IServiceCollection services,
        string storagePath,
        Action<CheckpointingOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        return services.AddCheckpointing(opts =>
        {
            // Set default options
            opts.Store = new JsonConversationThreadStore(storagePath);
            opts.DurableExecution.Enabled = true;

            // Allow caller to override
            configure?.Invoke(opts);
        });
    }

    /// <summary>
    /// Adds checkpointing services with in-memory storage.
    /// Useful for development and testing.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional additional configuration</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddInMemoryCheckpointing(
        this IServiceCollection services,
        Action<CheckpointingOptions>? configure = null)
    {
        return services.AddCheckpointing(opts =>
        {
            // Set default options
            opts.Store = new InMemoryConversationThreadStore();
            opts.DurableExecution.Enabled = true;

            // Allow caller to override
            configure?.Invoke(opts);
        });
    }
}

/// <summary>
/// Options for configuring checkpointing services.
/// </summary>
public class CheckpointingOptions
{
    /// <summary>
    /// The checkpoint store (required).
    /// This is the storage backend shared by both services.
    /// </summary>
    public ICheckpointStore? Store { get; set; }

    /// <summary>
    /// Configuration for DurableExecutionService.
    /// </summary>
    public DurableExecutionConfig DurableExecution { get; set; } = new();
}
