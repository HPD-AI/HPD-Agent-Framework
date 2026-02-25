using FluentAssertions;
using HPD.Agent.Hosting.Configuration;
using HPD.Agent.Maui;
using HPD.Agent.Maui.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HPD.Agent.Maui.Tests.Unit;

/// <summary>
/// Unit tests for MauiSessionManager.
/// Tests agent building logic with MAUI-specific configuration.
/// </summary>
public class MauiSessionManagerTests : IDisposable
{
    private readonly InMemorySessionStore _store;
    private readonly OptionsMonitorWrapper _optionsMonitor;

    public MauiSessionManagerTests()
    {
        _store = new InMemorySessionStore();
        _optionsMonitor = new OptionsMonitorWrapper();
    }

    public void Dispose()
    {
        // Cleanup
    }

    #region Agent Building

    [Fact]
    public async Task BuildAgentAsync_UsesIAgentFactory_WhenRegistered()
    {
        // Arrange
        var factory = new TestAgentFactory();
        var manager = new MauiSessionManager(_store, _optionsMonitor, Options.DefaultName, factory);

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
            Name = "MAUI Agent",
            SystemInstructions = "Mobile agent",
            Provider = new ProviderConfig
            {
                ProviderKey = "test",
                ModelName = "test-model"
            }
        };
        _optionsMonitor.CurrentValue.AgentConfig = config;
        _optionsMonitor.CurrentValue.ConfigureAgent = builder =>
        {
            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);
            // Replace builder's provider registry with test one (via reflection)
            var field = typeof(AgentBuilder).GetField("_providerRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(builder, providerRegistry);
        };

        var manager = new MauiSessionManager(_store, _optionsMonitor, Options.DefaultName, null);

        // Act
        var agent = await manager.GetOrCreateAgentAsync("session-1");

        // Assert
        agent.Should().NotBeNull();
    }

    [Fact]
    public async Task BuildAgentAsync_LoadsAgentConfigFromPath_WhenProvided()
    {
        // Arrange - Create temp config file
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var config = new AgentConfig
        {
            Name = "File Agent",
            Provider = new ProviderConfig
            {
                ProviderKey = "test",
                ModelName = "test-model"
            }
        };
        await File.WriteAllTextAsync(tempPath, System.Text.Json.JsonSerializer.Serialize(config));

        _optionsMonitor.CurrentValue.AgentConfigPath = tempPath;
        _optionsMonitor.CurrentValue.ConfigureAgent = builder =>
        {
            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);
            // Replace builder's provider registry with test one (via reflection)
            var field = typeof(AgentBuilder).GetField("_providerRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(builder, providerRegistry);
        };

        var manager = new MauiSessionManager(_store, _optionsMonitor, Options.DefaultName, null);

        // Act
        var agent = await manager.GetOrCreateAgentAsync("session-1");

        // Assert
        agent.Should().NotBeNull();

        // Cleanup
        File.Delete(tempPath);
    }

    [Fact]
    public async Task BuildAgentAsync_DeserializesAgentConfig_Correctly()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var config = new AgentConfig
        {
            Name = "Configured Agent",
            SystemInstructions = "Test instructions",
            MaxAgenticIterations = 10,
            Provider = new ProviderConfig
            {
                ProviderKey = "test",
                ModelName = "test-model"
            }
        };
        await File.WriteAllTextAsync(tempPath, System.Text.Json.JsonSerializer.Serialize(config));

        _optionsMonitor.CurrentValue.AgentConfigPath = tempPath;
        _optionsMonitor.CurrentValue.ConfigureAgent = builder =>
        {
            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);
            // Replace builder's provider registry with test one (via reflection)
            var field = typeof(AgentBuilder).GetField("_providerRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(builder, providerRegistry);
        };

        var manager = new MauiSessionManager(_store, _optionsMonitor, Options.DefaultName, null);

        // Act
        var agent = await manager.GetOrCreateAgentAsync("session-1");

        // Assert
        agent.Should().NotBeNull();

        // Cleanup
        File.Delete(tempPath);
    }

    [Fact]
    public async Task BuildAgentAsync_ThrowsException_WhenConfigFileInvalid()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        await File.WriteAllTextAsync(tempPath, "{ invalid json");

        _optionsMonitor.CurrentValue.AgentConfigPath = tempPath;
        var manager = new MauiSessionManager(_store, _optionsMonitor, Options.DefaultName, null);

        // Act & Assert
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(async () =>
            await manager.GetOrCreateAgentAsync("session-1"));

        // Cleanup
        File.Delete(tempPath);
    }

    [Fact]
    public async Task BuildAgentAsync_CallsConfigureAgent_AfterConfig()
    {
        // Arrange
        var called = false;
        _optionsMonitor.CurrentValue.AgentConfig = new AgentConfig
        {
            Name = "Test Agent",
            Provider = new ProviderConfig
            {
                ProviderKey = "test",
                ModelName = "test-model"
            }
        };
        _optionsMonitor.CurrentValue.ConfigureAgent = builder =>
        {
            called = true;
            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);
            // Replace builder's provider registry with test one (via reflection)
            var field = typeof(AgentBuilder).GetField("_providerRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(builder, providerRegistry);
        };
        var manager = new MauiSessionManager(_store, _optionsMonitor, Options.DefaultName, null);

        // Act
        await manager.GetOrCreateAgentAsync("session-1");

        // Assert
        called.Should().BeTrue();
    }

    #endregion

    #region Test Helpers

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
                .BuildAsync(ct);
        }
    }

    private class OptionsMonitorWrapper : IOptionsMonitor<HPDAgentConfig>
    {
        public HPDAgentConfig CurrentValue { get; } = new HPDAgentConfig();
        public HPDAgentConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<HPDAgentConfig, string?> listener) => null;
    }

    #endregion
}
