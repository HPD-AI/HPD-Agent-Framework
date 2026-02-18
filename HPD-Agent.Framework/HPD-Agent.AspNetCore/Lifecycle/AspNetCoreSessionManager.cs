using System.Text.Json;
using HPD.Agent;
using HPD.Agent.Hosting.Configuration;
using HPD.Agent.Hosting.Lifecycle;
using Microsoft.Extensions.Options;

namespace HPD.Agent.AspNetCore.Lifecycle;

/// <summary>
/// ASP.NET Core-specific implementation of AgentSessionManager.
/// Uses IOptionsMonitor for configuration and supports IAgentFactory.
/// </summary>
internal class AspNetCoreSessionManager : AgentSessionManager
{
    private readonly IOptionsMonitor<HPDAgentOptions> _optionsMonitor;
    private readonly string _name;
    private readonly IAgentFactory? _agentFactory;

    internal AspNetCoreSessionManager(
        ISessionStore store,
        IOptionsMonitor<HPDAgentOptions> optionsMonitor,
        string name,
        IAgentFactory? agentFactory = null)
        : base(store)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _agentFactory = agentFactory;
    }

    protected override async Task<Agent> BuildAgentAsync(
        string sessionId, CancellationToken ct)
    {
        var opts = _optionsMonitor.Get(_name);

        // Priority 1: IAgentFactory from DI
        if (_agentFactory != null)
        {
            return await _agentFactory.CreateAgentAsync(sessionId, Store, ct);
        }

        // Priority 2: AgentConfig object
        // Priority 3: AgentConfigPath file
        // Priority 4: Empty builder
        AgentBuilder builder;
        if (opts.AgentConfig != null)
        {
            builder = new AgentBuilder(opts.AgentConfig);
        }
        else if (opts.AgentConfigPath != null)
        {
            var json = await File.ReadAllTextAsync(opts.AgentConfigPath, ct);
            var config = JsonSerializer.Deserialize<AgentConfig>(json)
                ?? throw new InvalidOperationException(
                    $"Failed to deserialize AgentConfig from {opts.AgentConfigPath}");
            builder = new AgentBuilder(config);
        }
        else
        {
            builder = new AgentBuilder();
        }

        builder.WithSessionStore(Store);
        opts.ConfigureAgent?.Invoke(builder);

        return await builder.Build(ct);
    }

    protected override TimeSpan GetIdleTimeout() =>
        _optionsMonitor.Get(_name).AgentIdleTimeout;
}
