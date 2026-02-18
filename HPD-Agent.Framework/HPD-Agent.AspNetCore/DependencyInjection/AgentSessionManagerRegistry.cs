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
        var optionsMonitor = _serviceProvider.GetRequiredService<IOptionsMonitor<HPDAgentOptions>>();
        var options = optionsMonitor.Get(name);

        // Resolve session store
        ISessionStore store;
        if (options.SessionStore != null)
        {
            store = options.SessionStore;
        }
        else if (options.SessionStorePath != null)
        {
            store = new JsonSessionStore(options.SessionStorePath);
        }
        else
        {
            // Default to InMemorySessionStore if neither is provided
            store = new InMemorySessionStore();
        }

        // Resolve optional IAgentFactory
        var agentFactory = _serviceProvider.GetService<IAgentFactory>();

        return new AspNetCoreSessionManager(store, optionsMonitor, name, agentFactory);
    }
}
