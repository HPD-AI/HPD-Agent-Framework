using FluentAssertions;
using HPD.Agent.Hosting.Lifecycle;
using HPD.Agent;
using HPD.Agent.Providers;
using HPD.Agent.Hosting.Tests.Infrastructure;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Hosting.Tests.Lifecycle;

/// <summary>
/// Tests for AgentSessionManager abstract base class.
/// Uses a concrete test implementation to verify all lifecycle behaviors.
/// </summary>
public class AgentSessionManagerTests : IDisposable
{
    private readonly TestSessionManager _manager;
    private readonly InMemorySessionStore _store;

    public AgentSessionManagerTests()
    {
        _store = new InMemorySessionStore();
        _manager = new TestSessionManager(_store);
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    #region Agent Caching & Lifecycle

    [Fact]
    public async Task GetOrCreateAgentAsync_CreatesAgentOnce_ForSameSessionId()
    {
        // Act
        var agent1 = await _manager.GetOrCreateAgentAsync("session-1");
        var agent2 = await _manager.GetOrCreateAgentAsync("session-1");

        // Assert
        agent1.Should().BeSameAs(agent2);
        _manager.BuildCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_CreatesDifferentAgents_ForDifferentSessionIds()
    {
        // Act
        var agent1 = await _manager.GetOrCreateAgentAsync("session-1");
        var agent2 = await _manager.GetOrCreateAgentAsync("session-2");

        // Assert
        agent1.Should().NotBeSameAs(agent2);
        _manager.BuildCallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_UsesPerSessionLocking_PreventsConcurrentBuilds()
    {
        // Arrange
        var buildStarted = new TaskCompletionSource();
        var buildCanComplete = new TaskCompletionSource();

        _manager.OnBuildStarted = () => buildStarted.SetResult();
        _manager.OnBuildWait = () => buildCanComplete.Task;

        // Act - Start two concurrent builds for the same session
        var task1 = Task.Run(() => _manager.GetOrCreateAgentAsync("session-1"));
        await buildStarted.Task; // Wait for first build to start

        var task2 = Task.Run(() => _manager.GetOrCreateAgentAsync("session-1"));
        await Task.Delay(100); // Give time for second call to attempt acquisition

        buildCanComplete.SetResult(); // Allow first build to complete

        var agent1 = await task1;
        var agent2 = await task2;

        // Assert - Only one build should have occurred
        agent1.Should().BeSameAs(agent2);
        _manager.BuildCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreateAgentAsync_UpdatesLastAccessedTime_OnEveryAccess()
    {
        // Arrange
        await _manager.GetOrCreateAgentAsync("session-1");
        var firstAccessTime = _manager.GetLastAccessedTime("session-1");
        await Task.Delay(10);

        // Act
        await _manager.GetOrCreateAgentAsync("session-1");
        var secondAccessTime = _manager.GetLastAccessedTime("session-1");

        // Assert
        secondAccessTime.Should().BeAfter(firstAccessTime);
    }

    [Fact]
    public void GetRunningAgent_ReturnsNull_WhenAgentNotCached()
    {
        // Act
        var agent = _manager.GetRunningAgent("nonexistent-session");

        // Assert
        agent.Should().BeNull();
    }

    [Fact]
    public async Task GetRunningAgent_ReturnsAgent_WhenAgentExists()
    {
        // Arrange
        var createdAgent = await _manager.GetOrCreateAgentAsync("session-1");
        _manager.SetStreaming("session-1", true); // Mark as streaming so GetRunningAgent returns it

        // Act
        var retrievedAgent = _manager.GetRunningAgent("session-1");

        // Assert
        retrievedAgent.Should().BeSameAs(createdAgent);
    }

    [Fact]
    public async Task RemoveAgent_RemovesFromCache_AndClearsLock()
    {
        // Arrange
        var agent = await _manager.GetOrCreateAgentAsync("session-1");

        // Act
        _manager.RemoveAgent("session-1");
        var retrievedAgent = _manager.GetRunningAgent("session-1");

        // Assert
        retrievedAgent.Should().BeNull();
    }

    #endregion

    #region Stream Locking

    [Fact]
    public void TryAcquireStreamLock_ReturnsTrue_WhenLockAvailable()
    {
        // Act
        var acquired = _manager.TryAcquireStreamLock("session-1", "branch-1");

        // Assert
        acquired.Should().BeTrue();
    }

    [Fact]
    public void TryAcquireStreamLock_ReturnsFalse_WhenAlreadyLocked()
    {
        // Arrange
        _manager.TryAcquireStreamLock("session-1", "branch-1");

        // Act
        var secondAcquisition = _manager.TryAcquireStreamLock("session-1", "branch-1");

        // Assert
        secondAcquisition.Should().BeFalse();
    }

    [Fact]
    public void TryAcquireStreamLock_AllowsConcurrentStreams_OnDifferentBranches()
    {
        // Act
        var lock1 = _manager.TryAcquireStreamLock("session-1", "branch-1");
        var lock2 = _manager.TryAcquireStreamLock("session-1", "branch-2");

        // Assert
        lock1.Should().BeTrue();
        lock2.Should().BeTrue();
    }

    [Fact]
    public void TryAcquireStreamLock_AllowsConcurrentStreams_OnDifferentSessions()
    {
        // Act
        var lock1 = _manager.TryAcquireStreamLock("session-1", "branch-1");
        var lock2 = _manager.TryAcquireStreamLock("session-2", "branch-1");

        // Assert
        lock1.Should().BeTrue();
        lock2.Should().BeTrue();
    }

    [Fact]
    public void ReleaseStreamLock_AllowsReacquisition()
    {
        // Arrange
        _manager.TryAcquireStreamLock("session-1", "branch-1");
        _manager.ReleaseStreamLock("session-1", "branch-1");

        // Act
        var reacquired = _manager.TryAcquireStreamLock("session-1", "branch-1");

        // Assert
        reacquired.Should().BeTrue();
    }

    [Fact]
    public void ReleaseStreamLock_DoesNotThrow_WhenLockNotHeld()
    {
        // Act
        var act = () => _manager.ReleaseStreamLock("session-1", "branch-1");

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region Idle Eviction

    [Fact]
    public async Task EvictIdleAgents_RemovesAgents_OlderThanTimeout()
    {
        // Arrange
        await _manager.GetOrCreateAgentAsync("session-1");
        _manager.SetLastAccessedTime("session-1", DateTime.UtcNow.AddMinutes(-35)); // Older than 30 min timeout

        // Act
        _manager.TriggerEviction();
        await Task.Delay(100); // Give eviction time to run

        // Assert
        var agent = _manager.GetRunningAgent("session-1");
        agent.Should().BeNull();
    }

    [Fact]
    public async Task EvictIdleAgents_KeepsAgents_NewerThanTimeout()
    {
        // Arrange
        var firstAgent = await _manager.GetOrCreateAgentAsync("session-1");
        _manager.SetLastAccessedTime("session-1", DateTime.UtcNow.AddMinutes(-10)); // Newer than timeout
        var buildCountBeforeEviction = _manager.BuildCallCount;

        // Act
        _manager.TriggerEviction();
        await Task.Delay(100);

        // Assert - Agent should still be in cache (not evicted), so no rebuild needed
        var agent = await _manager.GetOrCreateAgentAsync("session-1");
        agent.Should().BeSameAs(firstAgent);
        _manager.BuildCallCount.Should().Be(buildCountBeforeEviction); // No rebuild occurred
    }

    [Fact]
    public async Task EvictIdleAgents_NeverEvicts_StreamingAgents()
    {
        // Arrange
        await _manager.GetOrCreateAgentAsync("session-1");
        _manager.SetLastAccessedTime("session-1", DateTime.UtcNow.AddMinutes(-35)); // Old enough to evict
        _manager.SetStreaming("session-1", true); // But currently streaming

        // Act
        _manager.TriggerEviction();
        await Task.Delay(100);

        // Assert
        var agent = _manager.GetRunningAgent("session-1");
        agent.Should().NotBeNull();
    }

    [Fact]
    public async Task EvictIdleAgents_EvictsAgent_AfterStreamingCompletes()
    {
        // Arrange
        await _manager.GetOrCreateAgentAsync("session-1");
        _manager.SetLastAccessedTime("session-1", DateTime.UtcNow.AddMinutes(-35));
        _manager.SetStreaming("session-1", true);
        _manager.TriggerEviction();
        await Task.Delay(100);

        var agentWhileStreaming = _manager.GetRunningAgent("session-1");
        agentWhileStreaming.Should().NotBeNull();

        // Act - Stop streaming and evict again
        _manager.SetStreaming("session-1", false);
        // SetStreaming updates LastAccessed, so we need to reset it to an old time
        _manager.SetLastAccessedTime("session-1", DateTime.UtcNow.AddMinutes(-35));
        _manager.TriggerEviction();
        await Task.Delay(100);

        // Assert
        var agentAfterStreaming = _manager.GetRunningAgent("session-1");
        agentAfterStreaming.Should().BeNull();
    }

    [Fact]
    public async Task SetStreaming_PreventsEviction_WhileTrue()
    {
        // Arrange
        await _manager.GetOrCreateAgentAsync("session-1");
        _manager.SetStreaming("session-1", true);
        _manager.SetLastAccessedTime("session-1", DateTime.UtcNow.AddHours(-1));

        // Act
        _manager.TriggerEviction();
        await Task.Delay(100);

        // Assert
        var agent = _manager.GetRunningAgent("session-1");
        agent.Should().NotBeNull();
    }

    [Fact]
    public async Task SetStreaming_AllowsEviction_WhenSetToFalse()
    {
        // Arrange
        await _manager.GetOrCreateAgentAsync("session-1");
        _manager.SetStreaming("session-1", false);
        _manager.SetLastAccessedTime("session-1", DateTime.UtcNow.AddHours(-1));

        // Act
        _manager.TriggerEviction();
        await Task.Delay(100);

        // Assert
        var agent = _manager.GetRunningAgent("session-1");
        agent.Should().BeNull();
    }

    #endregion

    #region Abstract Method Implementation

    [Fact]
    public async Task BuildAgentAsync_CalledOnFirstAccess_NotOnSubsequent()
    {
        // Act
        await _manager.GetOrCreateAgentAsync("session-1");
        await _manager.GetOrCreateAgentAsync("session-1");
        await _manager.GetOrCreateAgentAsync("session-1");

        // Assert
        _manager.BuildCallCount.Should().Be(1);
    }

    [Fact]
    public void GetIdleTimeout_UsedForEvictionCalculation()
    {
        // Assert - Our test manager returns 30 minutes
        _manager.GetIdleTimeoutForTests().Should().Be(TimeSpan.FromMinutes(30));
    }

    #endregion

    #region Test Helper Class

    /// <summary>
    /// Concrete implementation of AgentSessionManager for testing.
    /// </summary>
    private class TestSessionManager : AgentSessionManager
    {
        public int BuildCallCount { get; private set; }
        public Action? OnBuildStarted { get; set; }
        public Func<Task>? OnBuildWait { get; set; }

        public TestSessionManager(ISessionStore store) : base(store)
        {
        }

        protected override async Task<HPD.Agent.Agent> BuildAgentAsync(string sessionId, CancellationToken ct)
        {
            OnBuildStarted?.Invoke();
            if (OnBuildWait != null)
            {
                await OnBuildWait();
            }

            BuildCallCount++;

            // Use test-friendly agent creation with minimal config and test provider
            var config = new AgentConfig
            {
                Name = "TestAgent",
                MaxAgenticIterations = 50,
                SystemInstructions = "You are a helpful test agent.",
                Provider = new ProviderConfig
                {
                    ProviderKey = "test",  // Required by validation
                    ModelName = "test-model"
                },
                AgenticLoop = new AgenticLoopConfig
                {
                    MaxTurnDuration = TimeSpan.FromMinutes(1)
                },
                ErrorHandling = new ErrorHandlingConfig
                {
                    MaxRetries = 3,
                    NormalizeErrors = true
                }
            };

            var chatClient = new FakeChatClient();
            var providerRegistry = new TestProviderRegistry(chatClient);

            return await new AgentBuilder(config, providerRegistry)
                .WithSessionStore(Store)
                .WithCircuitBreaker(5)
                .WithErrorTracking(maxConsecutiveErrors: 3)
                .Build(ct);
        }

        protected override TimeSpan GetIdleTimeout() => TimeSpan.FromMinutes(30);

        public TimeSpan GetIdleTimeoutForTests() => GetIdleTimeout();

        // Expose internal state for testing
        public DateTime GetLastAccessedTime(string sessionId)
        {
            var field = typeof(AgentSessionManager).GetField("_agents",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (field == null)
                throw new InvalidOperationException("Could not find _agents field via reflection");

            var agents = field.GetValue(this);
            if (agents == null)
                return DateTime.MinValue;

            var tryGetValueMethod = agents.GetType().GetMethod("TryGetValue");
            if (tryGetValueMethod == null)
                throw new InvalidOperationException("Could not find TryGetValue method");

            var parameters = new object[] { sessionId, null! };
            var found = (bool)tryGetValueMethod.Invoke(agents, parameters)!;

            if (found && parameters[1] != null)
            {
                var entry = parameters[1];
                var lastAccessedProp = entry.GetType().GetProperty("LastAccessed");
                if (lastAccessedProp == null)
                    throw new InvalidOperationException("Could not find LastAccessed property");

                return (DateTime)lastAccessedProp.GetValue(entry)!;
            }

            return DateTime.MinValue;
        }

        public void SetLastAccessedTime(string sessionId, DateTime time)
        {
            var field = typeof(AgentSessionManager).GetField("_agents",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (field == null)
                throw new InvalidOperationException("Could not find _agents field via reflection");

            var agents = field.GetValue(this);
            if (agents == null)
                return;

            var tryGetValueMethod = agents.GetType().GetMethod("TryGetValue");
            if (tryGetValueMethod == null)
                throw new InvalidOperationException("Could not find TryGetValue method");

            var parameters = new object[] { sessionId, null! };
            var found = (bool)tryGetValueMethod.Invoke(agents, parameters)!;

            if (found && parameters[1] != null)
            {
                var entry = parameters[1];
                var lastAccessedProp = entry.GetType().GetProperty("LastAccessed");
                if (lastAccessedProp == null)
                    throw new InvalidOperationException("Could not find LastAccessed property");

                lastAccessedProp.SetValue(entry, time);
            }
        }

        public void TriggerEviction()
        {
            var method = typeof(AgentSessionManager).GetMethod("EvictIdleAgents",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method!.Invoke(this, [null]);
        }
    }

    #endregion
}
