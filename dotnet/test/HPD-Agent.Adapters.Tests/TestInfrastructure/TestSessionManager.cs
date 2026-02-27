using HPD.Agent;
using HPD.Agent.Hosting.Lifecycle;

namespace HPD.Agent.Adapters.Tests.TestInfrastructure;

/// <summary>
/// Minimal concrete subclass of AgentSessionManager for unit tests.
/// BuildAgentAsync is never called by PlatformSessionMapper tests — it is here
/// only to satisfy the abstract contract.
/// </summary>
internal sealed class TestSessionManager : AgentSessionManager
{
    public TestSessionManager(ISessionStore store) : base(store) { }

    protected override Task<Agent> BuildAgentAsync(string sessionId, CancellationToken ct)
        => throw new NotSupportedException("BuildAgentAsync is not used in adapter mapper tests.");

    protected override TimeSpan GetIdleTimeout() => TimeSpan.FromMinutes(5);
}
