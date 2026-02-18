using HPD.Agent;

namespace HPD.Agent.Hosting.Configuration;

/// <summary>
/// Configuration options for hosting an HPD Agent.
/// Used by both AspNetCore and MAUI hosting platforms.
/// </summary>
public class HPDAgentOptions
{
    /// <summary>
    /// Pre-configured session store instance.
    /// Takes priority over SessionStorePath.
    /// </summary>
    public ISessionStore? SessionStore { get; set; }

    /// <summary>
    /// Directory path for JsonSessionStore.
    /// Ignored if SessionStore is set.
    /// </summary>
    public string? SessionStorePath { get; set; }

    /// <summary>
    /// Serializable agent configuration.
    /// If set, seeds the AgentBuilder before ConfigureAgent runs.
    /// Because AgentConfig is JSON-serializable, it can be loaded from files,
    /// databases, or API payloads — enabling no-code agent definition.
    /// Takes priority over AgentConfigPath.
    /// </summary>
    public AgentConfig? AgentConfig { get; set; }

    /// <summary>
    /// Path to a JSON file containing an AgentConfig.
    /// Loaded once per agent build. Ignored if AgentConfig is set.
    /// </summary>
    public string? AgentConfigPath { get; set; }

    /// <summary>
    /// Callback to configure the AgentBuilder for each new session.
    /// Called after AgentConfig/AgentConfigPath are applied.
    /// Use this for runtime-only concerns (compiled type references, DI services).
    /// </summary>
    /// <remarks>
    /// The AgentBuilder is pre-configured with the registered ISessionStore
    /// and any AgentConfig/AgentConfigPath settings. Use this callback to
    /// layer on runtime-only concerns that JSON can't express (compiled type
    /// references, DI-resolved services, etc.).
    /// </remarks>
    public Action<AgentBuilder>? ConfigureAgent { get; set; }

    /// <summary>
    /// How long an agent can sit idle before eviction from the in-memory cache.
    /// Only agents that are not actively streaming are eligible for eviction.
    /// Default: 30 minutes.
    /// </summary>
    public TimeSpan AgentIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
