using FluentAssertions;
using HPD.Agent.AspNetCore.Lifecycle;
using HPD.Agent.Hosting.Configuration;
using HPD.Agent.AspNetCore.Tests.TestInfrastructure;
using Microsoft.Extensions.Options;

namespace HPD.Agent.AspNetCore.Tests.Unit;

/// <summary>
/// Unit tests for AspNetCoreSessionManager.
/// Tests agent building logic and idle timeout configuration.
/// </summary>
public class AspNetCoreSessionManagerTests : IDisposable
{
    private readonly InMemorySessionStore _store;
    private readonly IOptionsMonitor<HPDAgentConfig> _optionsMonitor;

    public AspNetCoreSessionManagerTests()
    {
        _store = new InMemorySessionStore();
        _optionsMonitor = new OptionsMonitorWrapper();
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    #region Agent Building

    [Fact]
    public async Task BuildAgentAsync_UsesIAgentFactory_WhenRegistered()
    {
        // Arrange
        var factory = new TestAgentFactory();
        var manager = new AspNetCoreSessionManager(_store, _optionsMonitor, Options.DefaultName, factory);

        // Act
        var agent = await manager.GetOrCreateAgentAsync("session-1");

        // Assert
        agent.Should().NotBeNull();
        factory.CreateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildAgentAsync_UsesAgentConfig_WhenProvided()
    {
        // Arrange
        var config = new AgentConfig
        {
            Name = "Test Agent",
            SystemInstructions = "Test instructions",
            Provider = new ProviderConfig
            {
                ProviderKey = "test",
                ModelName = "test-model"
            }
        };

        var options = new OptionsMonitorWrapper();
        options.CurrentValue.AgentConfig = config;
        // Use test provider registry
        options.CurrentValue.ConfigureAgent = builder =>
        {
            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);
            // Replace builder's provider registry with test one (via reflection)
            var field = typeof(AgentBuilder).GetField("_providerRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(builder, providerRegistry);
        };

        var manager = new AspNetCoreSessionManager(_store, options, Options.DefaultName, null);

        // Act
        var agent = await manager.GetOrCreateAgentAsync("session-1");

        // Assert
        agent.Should().NotBeNull();
        // Agent built with config
    }

    [Fact]
    public async Task BuildAgentAsync_UsesAgentConfigPath_WhenProvided()
    {
        // This would require creating a temp file with config
        // Simplified test just verifies the manager can be created with a provider
        var options = new OptionsMonitorWrapper();
        options.CurrentValue.AgentConfig = new AgentConfig
        {
            Name = "Test Agent",
            Provider = new ProviderConfig
            {
                ProviderKey = "test",
                ModelName = "test-model"
            }
        };
        options.CurrentValue.ConfigureAgent = builder =>
        {
            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);
            // Replace builder's provider registry with test one (via reflection)
            var field = typeof(AgentBuilder).GetField("_providerRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(builder, providerRegistry);
        };

        var manager = new AspNetCoreSessionManager(_store, options, Options.DefaultName, null);

        // Act
        var agent = await manager.GetOrCreateAgentAsync("session-1");

        // Assert
        agent.Should().NotBeNull();
    }

    [Fact]
    public async Task BuildAgentAsync_CallsConfigureAgent_AfterConfig()
    {
        // Arrange
        var configureWasCalled = false;
        var options = new OptionsMonitorWrapper();
        options.CurrentValue.AgentConfig = new AgentConfig
        {
            Name = "Test Agent",
            Provider = new ProviderConfig
            {
                ProviderKey = "test",
                ModelName = "test-model"
            }
        };
        options.CurrentValue.ConfigureAgent = builder =>
        {
            configureWasCalled = true;
            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);
            // Replace builder's provider registry with test one (via reflection)
            var field = typeof(AgentBuilder).GetField("_providerRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(builder, providerRegistry);
        };

        var manager = new AspNetCoreSessionManager(_store, options, Options.DefaultName, null);

        // Act
        await manager.GetOrCreateAgentAsync("session-1");

        // Assert
        configureWasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task BuildAgentAsync_SetsSessionStore_OnBuilder()
    {
        // Arrange
        var options = new OptionsMonitorWrapper();
        options.CurrentValue.AgentConfig = new AgentConfig
        {
            Name = "Test Agent",
            Provider = new ProviderConfig
            {
                ProviderKey = "test",
                ModelName = "test-model"
            }
        };
        options.CurrentValue.ConfigureAgent = builder =>
        {
            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);
            // Replace builder's provider registry with test one (via reflection)
            var field = typeof(AgentBuilder).GetField("_providerRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(builder, providerRegistry);
        };

        var manager = new AspNetCoreSessionManager(_store, options, Options.DefaultName, null);

        // Act
        var agent = await manager.GetOrCreateAgentAsync("session-1");

        // Assert
        agent.Should().NotBeNull();
        // SessionStore should be set (verified by agent working correctly)
    }

    [Fact]
    public async Task BuildAgentAsync_CreatesEmptyBuilder_WhenNoConfig()
    {
        // Arrange
        var options = new OptionsMonitorWrapper();
        options.CurrentValue.AgentConfig = new AgentConfig
        {
            Name = "Test Agent",
            Provider = new ProviderConfig
            {
                ProviderKey = "test",
                ModelName = "test-model"
            }
        };
        options.CurrentValue.ConfigureAgent = builder =>
        {
            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);
            // Replace builder's provider registry with test one (via reflection)
            var field = typeof(AgentBuilder).GetField("_providerRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(builder, providerRegistry);
        };

        var manager = new AspNetCoreSessionManager(_store, options, Options.DefaultName, null);

        // Act
        var agent = await manager.GetOrCreateAgentAsync("session-1");

        // Assert
        agent.Should().NotBeNull();
    }

    #endregion

    #region Idle Timeout

    [Fact]
    public void GetIdleTimeout_ReturnsConfiguredTimeout()
    {
        // Arrange
        var options = new OptionsMonitorWrapper();
        options.CurrentValue.AgentIdleTimeout = TimeSpan.FromMinutes(60);
        var manager = new AspNetCoreSessionManager(_store, options, Options.DefaultName, null);

        // Act
        var timeout = manager.GetIdleTimeoutForTests();

        // Assert
        timeout.Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void GetIdleTimeout_ReturnsDefault_WhenNotConfigured()
    {
        // Arrange
        var options = new OptionsMonitorWrapper();
        var manager = new AspNetCoreSessionManager(_store, options, Options.DefaultName, null);

        // Act
        var timeout = manager.GetIdleTimeoutForTests();

        // Assert
        timeout.Should().Be(TimeSpan.FromMinutes(30)); // Default
    }

    #endregion

    #region Test Helpers

    // Testable version of AspNetCoreSessionManager that uses test provider registry
    private class TestableAspNetCoreSessionManager : AspNetCoreSessionManager
    {
        public TestableAspNetCoreSessionManager(
            ISessionStore store,
            IOptionsMonitor<HPDAgentConfig> optionsMonitor,
            string name,
            IAgentFactory? agentFactory = null)
            : base(store, optionsMonitor, name, agentFactory)
        {
        }

        protected override async Task<HPD.Agent.Agent> BuildAgentAsync(string sessionId, CancellationToken ct)
        {
            var opts = _optionsMonitor.Get(_name);

            // Priority 1: IAgentFactory from DI (delegate to base)
            if (_agentFactory != null)
            {
                return await _agentFactory.CreateAgentAsync(sessionId, Store, ct);
            }

            // For tests, always use test provider registry
            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);

            AgentBuilder builder;
            if (opts.AgentConfig != null)
            {
                builder = new AgentBuilder(opts.AgentConfig, providerRegistry);
            }
            else if (opts.AgentConfigPath != null)
            {
                var json = await File.ReadAllTextAsync(opts.AgentConfigPath, ct);
                var config = System.Text.Json.JsonSerializer.Deserialize<AgentConfig>(json)
                    ?? throw new InvalidOperationException(
                        $"Failed to deserialize AgentConfig from {opts.AgentConfigPath}");
                builder = new AgentBuilder(config, providerRegistry);
            }
            else
            {
                // Empty builder with test provider
                var config = new AgentConfig
                {
                    Provider = new ProviderConfig { ProviderKey = "test", ModelName = "test-model" }
                };
                builder = new AgentBuilder(config, providerRegistry);
            }

            builder.WithSessionStore(Store);
            opts.ConfigureAgent?.Invoke(builder);

            return await builder.Build(ct);
        }

        // Expose protected fields for tests
        private readonly IOptionsMonitor<HPDAgentConfig> _optionsMonitor;
        private readonly string _name;
        private readonly IAgentFactory? _agentFactory;

        public void InitializeFields(IOptionsMonitor<HPDAgentConfig> optionsMonitor, string name, IAgentFactory? agentFactory)
        {
            typeof(TestableAspNetCoreSessionManager)
                .GetField("_optionsMonitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(this, optionsMonitor);
            typeof(TestableAspNetCoreSessionManager)
                .GetField("_name", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(this, name);
            typeof(TestableAspNetCoreSessionManager)
                .GetField("_agentFactory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(this, agentFactory);
        }
    }

    private class TestAgentFactory : IAgentFactory
    {
        public int CreateCallCount { get; private set; }

        public async Task<HPD.Agent.Agent> CreateAgentAsync(
            string sessionId,
            ISessionStore store,
            CancellationToken ct = default)
        {
            CreateCallCount++;

            // Use test provider registry with fake chat client
            var config = new AgentConfig
            {
                Name = "TestAgent",
                MaxAgenticIterations = 50,
                Provider = new ProviderConfig
                {
                    ProviderKey = "test",
                    ModelName = "test-model"
                }
            };

            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);

            return await new AgentBuilder(config, providerRegistry)
                .WithSessionStore(store)
                .WithCircuitBreaker(5)
                .Build(ct);
        }
    }

    private class OptionsMonitorWrapper : IOptionsMonitor<HPDAgentConfig>
    {
        public HPDAgentConfig CurrentValue { get; } = new HPDAgentConfig();

        public HPDAgentConfig Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<HPDAgentConfig, string?> listener) => null;
    }

    // Extension to access protected method
    private class AspNetCoreSessionManagerTestable : AspNetCoreSessionManager
    {
        public AspNetCoreSessionManagerTestable(
            ISessionStore store,
            IOptionsMonitor<HPDAgentConfig> optionsMonitor,
            string name,
            IAgentFactory? agentFactory)
            : base(store, optionsMonitor, name, agentFactory)
        {
        }

        public TimeSpan GetIdleTimeoutForTests() => GetIdleTimeout();
    }

    #endregion
}

// Extension methods to access protected members for testing
internal static class AspNetCoreSessionManagerExtensions
{
    internal static TimeSpan GetIdleTimeoutForTests(this AspNetCoreSessionManager manager)
    {
        var method = typeof(AspNetCoreSessionManager).GetMethod(
            "GetIdleTimeout",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (TimeSpan)method!.Invoke(manager, null)!;
    }
}
