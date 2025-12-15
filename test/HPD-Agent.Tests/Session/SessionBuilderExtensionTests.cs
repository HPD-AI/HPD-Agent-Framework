using Xunit;
using HPD.Agent;
using HPD.Agent.Session;

using HPD.Agent.Tests.Infrastructure;

namespace HPD.Agent.Tests.Session;

/// <summary>
/// Tests for AgentBuilderSessionExtensions.WithSessionStore() overloads.
/// Verifies correct configuration of SessionStore and SessionStoreOptions.
/// </summary>
public class SessionBuilderExtensionTests : AgentTestBase
{
    //──────────────────────────────────────────────────────────────────
    // WithSessionStore(ISessionStore) - Manual Save Mode
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void WithSessionStore_StoreOnly_SetsManualSave()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var builder = new AgentBuilder();

        // Act
        builder.WithSessionStore(store);

        // Assert
        Assert.Same(store, builder.Config.SessionStore);
        Assert.NotNull(builder.Config.SessionStoreOptions);
        Assert.False(builder.Config.SessionStoreOptions.PersistAfterTurn);
    }

    //──────────────────────────────────────────────────────────────────
    // WithSessionStore(ISessionStore, bool persistAfterTurn)
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void WithSessionStore_WithPersistAfterTurnTrue_SetsPersistAfterTurn()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var builder = new AgentBuilder();

        // Act
        builder.WithSessionStore(store, persistAfterTurn: true);

        // Assert
        Assert.Same(store, builder.Config.SessionStore);
        Assert.True(builder.Config.SessionStoreOptions?.PersistAfterTurn);
    }

    [Fact]
    public void WithSessionStore_WithPersistAfterTurnFalse_SetsManualSave()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var builder = new AgentBuilder();

        // Act
        builder.WithSessionStore(store, persistAfterTurn: false);

        // Assert
        Assert.Same(store, builder.Config.SessionStore);
        Assert.False(builder.Config.SessionStoreOptions?.PersistAfterTurn);
    }

    //──────────────────────────────────────────────────────────────────
    // WithSessionStore(ISessionStore, CheckpointFrequency, RetentionPolicy?)
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void WithSessionStore_WithFrequency_EnablesAutoSave()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var builder = new AgentBuilder();

        // Act
        builder.WithSessionStore(store, CheckpointFrequency.PerTurn);

        // Assert
        Assert.Same(store, builder.Config.SessionStore);
        Assert.True(builder.Config.SessionStoreOptions?.PersistAfterTurn);
        Assert.Equal(CheckpointFrequency.PerTurn, builder.Config.SessionStoreOptions?.Frequency);
    }

    [Fact]
    public void WithSessionStore_WithFrequencyPerIteration_SetsCorrectFrequency()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var builder = new AgentBuilder();

        // Act
        builder.WithSessionStore(store, CheckpointFrequency.PerIteration);

        // Assert
        Assert.Equal(CheckpointFrequency.PerIteration, builder.Config.SessionStoreOptions?.Frequency);
    }

    [Fact]
    public void WithSessionStore_WithFrequencyManual_SetsCorrectFrequency()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var builder = new AgentBuilder();

        // Act
        builder.WithSessionStore(store, CheckpointFrequency.Manual);

        // Assert
        Assert.Equal(CheckpointFrequency.Manual, builder.Config.SessionStoreOptions?.Frequency);
        Assert.True(builder.Config.SessionStoreOptions?.PersistAfterTurn); // Still enables persist for this overload
    }

    [Fact]
    public void WithSessionStore_WithFrequencyAndRetention_SetsRetention()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var builder = new AgentBuilder();

        // Act
        builder.WithSessionStore(store, CheckpointFrequency.PerTurn, RetentionPolicy.LastN(5));

        // Assert
        var retention = builder.Config.SessionStoreOptions?.Retention;
        Assert.NotNull(retention);
        Assert.IsType<RetentionPolicy.LastNPolicy>(retention);
    }

    [Fact]
    public void WithSessionStore_WithFrequencyNoRetention_DefaultsToLastN3()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var builder = new AgentBuilder();

        // Act
        builder.WithSessionStore(store, CheckpointFrequency.PerTurn);

        // Assert
        var retention = builder.Config.SessionStoreOptions?.Retention;
        Assert.NotNull(retention);
        Assert.IsType<RetentionPolicy.LastNPolicy>(retention);
        // Verify it's LastN(3)
        var lastNPolicy = (RetentionPolicy.LastNPolicy)retention;
        Assert.Equal(3, lastNPolicy.N);
    }

    //──────────────────────────────────────────────────────────────────
    // WithSessionStore(ISessionStore, Action<SessionStoreOptions>)
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void WithSessionStore_WithConfigureAction_AllowsFullConfiguration()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var builder = new AgentBuilder();

        // Act
        builder.WithSessionStore(store, options =>
        {
            options.PersistAfterTurn = true;
            options.Frequency = CheckpointFrequency.PerIteration;
            options.Retention = RetentionPolicy.FullHistory;
            options.EnablePendingWrites = true;
        });

        // Assert
        var opts = builder.Config.SessionStoreOptions;
        Assert.NotNull(opts);
        Assert.True(opts.PersistAfterTurn);
        Assert.Equal(CheckpointFrequency.PerIteration, opts.Frequency);
        Assert.Same(RetentionPolicy.FullHistory, opts.Retention);
        Assert.True(opts.EnablePendingWrites);
    }

    //──────────────────────────────────────────────────────────────────
    // WithSessionStore(string storagePath, bool persistAfterTurn)
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void WithSessionStore_WithPath_CreatesJsonSessionStore()
    {
        // Arrange
        var builder = new AgentBuilder();
        var tempPath = Path.Combine(Path.GetTempPath(), $"session-test-{Guid.NewGuid()}");

        try
        {
            // Act
            builder.WithSessionStore(tempPath, persistAfterTurn: true);

            // Assert
            Assert.NotNull(builder.Config.SessionStore);
            Assert.IsType<JsonSessionStore>(builder.Config.SessionStore);
            Assert.True(builder.Config.SessionStoreOptions?.PersistAfterTurn);
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    [Fact]
    public void WithSessionStore_WithPathDefaultPersistAfterTurn_SetsManualSave()
    {
        // Arrange
        var builder = new AgentBuilder();
        var tempPath = Path.Combine(Path.GetTempPath(), $"session-test-{Guid.NewGuid()}");

        try
        {
            // Act
            builder.WithSessionStore(tempPath); // persistAfterTurn defaults to false

            // Assert
            Assert.False(builder.Config.SessionStoreOptions?.PersistAfterTurn);
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    //──────────────────────────────────────────────────────────────────
    // WithSessionStore(string storagePath, CheckpointFrequency, RetentionPolicy?)
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void WithSessionStore_WithPathAndFrequency_CreatesJsonSessionStore()
    {
        // Arrange
        var builder = new AgentBuilder();
        var tempPath = Path.Combine(Path.GetTempPath(), $"session-test-{Guid.NewGuid()}");

        try
        {
            // Act
            builder.WithSessionStore(tempPath, CheckpointFrequency.PerTurn, RetentionPolicy.LastN(10));

            // Assert
            Assert.NotNull(builder.Config.SessionStore);
            Assert.IsType<JsonSessionStore>(builder.Config.SessionStore);
            Assert.True(builder.Config.SessionStoreOptions?.PersistAfterTurn);
            Assert.Equal(CheckpointFrequency.PerTurn, builder.Config.SessionStoreOptions?.Frequency);
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    //──────────────────────────────────────────────────────────────────
    // RETENTION POLICY TESTS
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void RetentionPolicy_LatestOnly_IsSingleton()
    {
        // Arrange & Act
        var policy1 = RetentionPolicy.LatestOnly;
        var policy2 = RetentionPolicy.LatestOnly;

        // Assert
        Assert.Same(policy1, policy2);
    }

    [Fact]
    public void RetentionPolicy_FullHistory_IsSingleton()
    {
        // Arrange & Act
        var policy1 = RetentionPolicy.FullHistory;
        var policy2 = RetentionPolicy.FullHistory;

        // Assert
        Assert.Same(policy1, policy2);
    }

    [Fact]
    public void RetentionPolicy_LastN_CreatesNewInstance()
    {
        // Arrange & Act
        var policy1 = RetentionPolicy.LastN(5);
        var policy2 = RetentionPolicy.LastN(5);

        // Assert
        Assert.NotSame(policy1, policy2);
        Assert.Equal(policy1, policy2); // Should be equal by value
    }

    [Fact]
    public void RetentionPolicy_TimeBased_CreatesNewInstance()
    {
        // Arrange & Act
        var policy1 = RetentionPolicy.TimeBased(TimeSpan.FromDays(30));
        var policy2 = RetentionPolicy.TimeBased(TimeSpan.FromDays(30));

        // Assert
        Assert.NotSame(policy1, policy2);
        Assert.Equal(policy1, policy2); // Should be equal by value
    }

    //──────────────────────────────────────────────────────────────────
    // SESSION STORE OPTIONS DEFAULTS
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void SessionStoreOptions_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var options = new SessionStoreOptions();

        // Assert
        Assert.False(options.PersistAfterTurn);
        Assert.Equal(CheckpointFrequency.PerTurn, options.Frequency);
        Assert.IsType<RetentionPolicy.LastNPolicy>(options.Retention);
        Assert.False(options.EnablePendingWrites);
    }

    //──────────────────────────────────────────────────────────────────
    // NULL ARGUMENT CHECKS
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void WithSessionStore_NullBuilder_Throws()
    {
        // Arrange
        AgentBuilder builder = null!;
        var store = new InMemorySessionStore();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => builder.WithSessionStore(store));
    }

    [Fact]
    public void WithSessionStore_NullStore_Throws()
    {
        // Arrange
        var builder = new AgentBuilder();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => builder.WithSessionStore((ISessionStore)null!));
    }

    [Fact]
    public void WithSessionStore_NullPath_Throws()
    {
        // Arrange
        var builder = new AgentBuilder();

        // Act & Assert
        // ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        Assert.Throws<ArgumentNullException>(() => builder.WithSessionStore((string)null!));
    }

    [Fact]
    public void WithSessionStore_EmptyPath_Throws()
    {
        // Arrange
        var builder = new AgentBuilder();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.WithSessionStore(""));
    }

    //──────────────────────────────────────────────────────────────────
    // FLUENT API CHAINING
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void WithSessionStore_ReturnsBuilder_ForChaining()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var builder = new AgentBuilder();

        // Act
        var result = builder.WithSessionStore(store);

        // Assert
        Assert.Same(builder, result);
    }

    [Fact]
    public void WithSessionStore_CanChainMultipleCalls()
    {
        // Arrange
        var builder = new AgentBuilder();
        var store = new InMemorySessionStore();

        // Act - Chain multiple builder methods
        var result = builder
            .WithSessionStore(store, persistAfterTurn: true)
            .WithName("TestAgent")
            .WithMaxFunctionCallTurns(50);

        // Assert
        Assert.Same(builder, result);
        Assert.Equal("TestAgent", builder.Config.Name);
        Assert.Equal(50, builder.Config.MaxAgenticIterations);
        Assert.Same(store, builder.Config.SessionStore);
    }
}
