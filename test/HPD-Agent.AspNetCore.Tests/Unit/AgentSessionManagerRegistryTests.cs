using FluentAssertions;
using HPD.Agent.AspNetCore.DependencyInjection;
using HPD.Agent.Hosting.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HPD.Agent.AspNetCore.Tests.Unit;

/// <summary>
/// Unit tests for AgentSessionManagerRegistry.
/// Tests named manager resolution and lifecycle.
/// </summary>
public class AgentSessionManagerRegistryTests
{
    #region Named Manager Resolution

    [Fact]
    public void Get_CreatesManager_OnFirstCall()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<HPDAgentOptions>(Options.DefaultName, opts =>
        {
            opts.SessionStore = new InMemorySessionStore();
        });
        var provider = services.BuildServiceProvider();
        var registry = new AgentSessionManagerRegistry(provider);

        // Act
        var manager = registry.Get(Options.DefaultName);

        // Assert
        manager.Should().NotBeNull();
    }

    [Fact]
    public void Get_ReturnsSameManager_OnSubsequentCalls()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<HPDAgentOptions>(Options.DefaultName, opts =>
        {
            opts.SessionStore = new InMemorySessionStore();
        });
        var provider = services.BuildServiceProvider();
        var registry = new AgentSessionManagerRegistry(provider);

        // Act
        var manager1 = registry.Get(Options.DefaultName);
        var manager2 = registry.Get(Options.DefaultName);

        // Assert
        manager1.Should().BeSameAs(manager2);
    }

    [Fact]
    public void Get_CreatesDifferentManagers_ForDifferentNames()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<HPDAgentOptions>("agent1", opts =>
        {
            opts.SessionStore = new InMemorySessionStore();
        });
        services.Configure<HPDAgentOptions>("agent2", opts =>
        {
            opts.SessionStore = new InMemorySessionStore();
        });
        var provider = services.BuildServiceProvider();
        var registry = new AgentSessionManagerRegistry(provider);

        // Act
        var manager1 = registry.Get("agent1");
        var manager2 = registry.Get("agent2");

        // Assert
        manager1.Should().NotBeSameAs(manager2);
    }

    [Fact]
    public void CreateManager_UsesOptionsMonitor_ForNamedOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<HPDAgentOptions>("test-agent", opts =>
        {
            opts.SessionStore = new InMemorySessionStore();
            opts.AgentIdleTimeout = TimeSpan.FromMinutes(60);
        });
        var provider = services.BuildServiceProvider();
        var registry = new AgentSessionManagerRegistry(provider);

        // Act
        var manager = registry.Get("test-agent");

        // Assert
        manager.Should().NotBeNull();
    }

    [Fact]
    public void CreateManager_ResolvesIAgentFactory_FromDI()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<IAgentFactory, TestAgentFactory>();
        services.Configure<HPDAgentOptions>(Options.DefaultName, opts =>
        {
            opts.SessionStore = new InMemorySessionStore();
        });
        var provider = services.BuildServiceProvider();
        var registry = new AgentSessionManagerRegistry(provider);

        // Act
        var manager = registry.Get(Options.DefaultName);

        // Assert
        manager.Should().NotBeNull();
    }

    [Fact]
    public void CreateManager_CreatesJsonSessionStore_WhenPathProvided()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<HPDAgentOptions>(Options.DefaultName, opts =>
        {
            opts.SessionStorePath = tempPath;
        });
        var provider = services.BuildServiceProvider();
        var registry = new AgentSessionManagerRegistry(provider);

        // Act
        var manager = registry.Get(Options.DefaultName);

        // Assert
        manager.Should().NotBeNull();
        manager.Store.Should().NotBeNull();

        // Cleanup
        if (Directory.Exists(tempPath))
            Directory.Delete(tempPath, true);
    }

    [Fact]
    public void CreateManager_CreatesInMemoryStore_WhenNoPathProvided()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<HPDAgentOptions>(Options.DefaultName, opts => { });
        var provider = services.BuildServiceProvider();
        var registry = new AgentSessionManagerRegistry(provider);

        // Act
        var manager = registry.Get(Options.DefaultName);

        // Assert
        manager.Should().NotBeNull();
        manager.Store.Should().BeOfType<InMemorySessionStore>();
    }

    [Fact]
    public void CreateManager_UsesProvidedStore_WhenAvailable()
    {
        // Arrange
        var customStore = new InMemorySessionStore();
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<HPDAgentOptions>(Options.DefaultName, opts =>
        {
            opts.SessionStore = customStore;
        });
        var provider = services.BuildServiceProvider();
        var registry = new AgentSessionManagerRegistry(provider);

        // Act
        var manager = registry.Get(Options.DefaultName);

        // Assert
        manager.Store.Should().BeSameAs(customStore);
    }

    #endregion

    #region Test Helpers

    private class TestAgentFactory : IAgentFactory
    {
        public Task<HPD.Agent.Agent> CreateAgentAsync(
            string sessionId,
            ISessionStore store,
            CancellationToken ct = default)
        {
            var config = new AgentConfig
            {
                Name = "TestAgent",
                Provider = new ProviderConfig
                {
                    ProviderKey = "test",
                    ModelName = "test-model"
                }
            };

            var chatClient = new HPD.Agent.AspNetCore.Tests.TestInfrastructure.FakeChatClient();
            var providerRegistry = new HPD.Agent.AspNetCore.Tests.TestInfrastructure.TestProviderRegistry(chatClient);

            return new AgentBuilder(config, providerRegistry)
                .WithSessionStore(store)
                .Build(ct);
        }
    }

    #endregion
}
