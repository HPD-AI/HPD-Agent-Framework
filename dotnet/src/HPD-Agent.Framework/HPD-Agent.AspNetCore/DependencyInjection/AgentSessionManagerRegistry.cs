using System.Collections.Concurrent;
using HPD.Agent;
using HPD.Agent.AspNetCore.Lifecycle;
using HPD.Agent.Hosting.Configuration;
using HPD.Agent.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HPD.Agent.AspNetCore.DependencyInjection;

/// <summary>
/// Registry for managing multiple named agent session managers.
/// Enables hosting multiple agents at different route prefixes.
/// </summary>
internal sealed class AgentSessionManagerRegistry
{
    private readonly ConcurrentDictionary<string, AgentSessionManager> _managers = new();
    private readonly IServiceProvider _serviceProvider;

    public AgentSessionManagerRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>
    /// Get or create a session manager for the given name.
    /// </summary>
    public AgentSessionManager Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        // Allow empty string for Options.DefaultName

        return _managers.GetOrAdd(name, CreateManager);
    }

    private AgentSessionManager CreateManager(string name)
    {
        var optionsMonitor = _serviceProvider.GetRequiredService<IOptionsMonitor<HPDAgentConfig>>();
        var options = optionsMonitor.Get(name);

        // The hosting layer owns the session store — not the AgentBuilder.
        // This is intentional: the manager needs a store at construction time to serve
        // session/branch endpoints (list, create, delete) before any agent is ever built.
        // AgentBuilder is only invoked lazily on the first stream request, and receives
        // this same store via WithSessionStore() in BuildAgentAsync. The flow is:
        //   HPDAgentConfig.SessionStore → manager._store (immediately, at startup)
        //   manager._store → builder.WithSessionStore() (lazily, per stream request)
        // This is why SessionStore lives on HPDAgentConfig and not on AgentBuilder
        // when using the hosted model — the two are not redundant.
        ISessionStore store = options.SessionStore ?? new InMemorySessionStore();

        // Resolve optional IAgentFactory
        var agentFactory = _serviceProvider.GetService<IAgentFactory>();

        return new AspNetCoreSessionManager(store, optionsMonitor, name, agentFactory);
    }
}
