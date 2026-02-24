using HPD.Agent;
using HPD.Agent.Hosting.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HPD.Agent.Maui;

/// <summary>
/// Extension methods for registering HPD Agent bridge services in MAUI applications.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the HPD Agent bridge for MAUI HybridWebView integration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback for agent options.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// This registers:
    /// - MauiSessionManager as a singleton
    /// - ISessionStore (either provided via options or InMemorySessionStore by default)
    /// - HPDAgentConfig configuration
    ///
    /// Example usage:
    /// <code>
    /// builder.Services.AddHPDAgentBridge(options =>
    /// {
    ///     options.AgentConfig = new AgentConfig
    ///     {
    ///         Name = "My Agent",
    ///         Provider = new ProviderConfig { ProviderKey = "anthropic", ModelName = "claude-sonnet-4-5" }
    ///     };
    ///     options.SessionStore = new JsonSessionStore(Path.Combine(FileSystem.AppDataDirectory, "sessions"));
    /// });
    /// </code>
    /// </remarks>
    public static IServiceCollection AddHPDAgentBridge(
        this IServiceCollection services,
        Action<HPDAgentConfig>? configure = null)
        => services.AddHPDAgentBridge(Options.DefaultName, configure);

    /// <summary>
    /// Registers a named HPD Agent bridge for MAUI HybridWebView integration.
    /// Use this overload to register multiple agents with different configurations.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name for this agent configuration.</param>
    /// <param name="configure">Optional configuration callback for agent options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHPDAgentBridge(
        this IServiceCollection services,
        string name,
        Action<HPDAgentConfig>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Register named options (each agent name gets its own HPDAgentConfig)
        if (configure != null)
            services.Configure(name, configure);

        // Register the session manager as singleton
        // Use a factory to resolve the correct ISessionStore based on options
        services.TryAddSingleton<MauiSessionManager>(sp =>
        {
            var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<HPDAgentConfig>>();
            var opts = optionsMonitor.Get(name);

            // Determine session store
            ISessionStore store = opts.SessionStore ?? new InMemorySessionStore();

            // Try to resolve IAgentFactory from DI (optional)
            var agentFactory = sp.GetService<IAgentFactory>();

            return new MauiSessionManager(store, optionsMonitor, name, agentFactory);
        });

        return services;
    }
}
