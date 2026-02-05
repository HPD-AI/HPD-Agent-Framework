using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;
using HPD.Agent;
using HPD.Agent.Tests.Infrastructure;

namespace HPD.Agent.Tests.Session;

/// <summary>
/// Tests for UncommittedTurn: CRUD on InMemorySessionStore and JsonSessionStore,
/// JSON round-trip serialization, and cleanup behavior.
/// </summary>
public class UncommittedTurnTests : AgentTestBase
{
    private static UncommittedTurn CreateTestTurn(string sessionId = "session-1")
    {
        return new UncommittedTurn
        {
            SessionId = sessionId,
            BranchId = UncommittedTurn.DefaultBranch,
            TurnMessages = new List<ChatMessage>
            {
                new(ChatRole.User, "Hello"),
                new(ChatRole.Assistant, "Hi there!")
            },
            Iteration = 2,
            CompletedFunctions = ImmutableHashSet.Create("func1", "func2"),
            MiddlewareState = new MiddlewareState(),
            IsTerminated = false,
            TerminationReason = null,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastUpdatedAt = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)
        };
    }

    //──────────────────────────────────────────────────────────────────
    // INMEMORY SESSION STORE
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InMemoryStore_SaveAndLoadUncommittedTurn_RoundTrip()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var turn = CreateTestTurn();

        // Act
        await store.SaveUncommittedTurnAsync(turn);
        var loaded = await store.LoadUncommittedTurnAsync("session-1");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("session-1", loaded.SessionId);
        Assert.Equal(UncommittedTurn.DefaultBranch, loaded.BranchId);
        Assert.Equal(2, loaded.TurnMessages.Count);
        Assert.Equal(2, loaded.Iteration);
        Assert.Contains("func1", loaded.CompletedFunctions);
        Assert.Contains("func2", loaded.CompletedFunctions);
        Assert.False(loaded.IsTerminated);
    }

    [Fact]
    public async Task InMemoryStore_LoadUncommittedTurn_NoTurn_ReturnsNull()
    {
        // Arrange
        var store = new InMemorySessionStore();

        // Act
        var result = await store.LoadUncommittedTurnAsync("nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task InMemoryStore_DeleteUncommittedTurn_ReturnsNull()
    {
        // Arrange
        var store = new InMemorySessionStore();
        await store.SaveUncommittedTurnAsync(CreateTestTurn());

        // Act
        await store.DeleteUncommittedTurnAsync("session-1");
        var loaded = await store.LoadUncommittedTurnAsync("session-1");

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task InMemoryStore_SaveUncommittedTurn_OverwritesPrevious()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var turn1 = CreateTestTurn() with { Iteration = 1 };
        var turn2 = CreateTestTurn() with { Iteration = 5 };

        // Act
        await store.SaveUncommittedTurnAsync(turn1);
        await store.SaveUncommittedTurnAsync(turn2);
        var loaded = await store.LoadUncommittedTurnAsync("session-1");

        // Assert — should be the latest
        Assert.NotNull(loaded);
        Assert.Equal(5, loaded.Iteration);
    }

    [Fact]
    public async Task InMemoryStore_DeleteSession_AlsoDeletesUncommittedTurn()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = new AgentSession("session-1");
        session.AddMessage(UserMessage("Hello"));
        await store.SaveSessionAsync(session);
        await store.SaveUncommittedTurnAsync(CreateTestTurn());

        // Act
        await store.DeleteSessionAsync("session-1");
        var loaded = await store.LoadUncommittedTurnAsync("session-1");

        // Assert
        Assert.Null(loaded);
    }

    //──────────────────────────────────────────────────────────────────
    // JSON SESSION STORE
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task JsonStore_SaveAndLoadUncommittedTurn_RoundTrip()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), $"uncommitted-test-{Guid.NewGuid()}");
        try
        {
            var store = new JsonSessionStore(tempPath);
            var turn = CreateTestTurn();

            // Act
            await store.SaveUncommittedTurnAsync(turn);
            var loaded = await store.LoadUncommittedTurnAsync("session-1");

            // Assert
            Assert.NotNull(loaded);
            Assert.Equal("session-1", loaded.SessionId);
            Assert.Equal(UncommittedTurn.DefaultBranch, loaded.BranchId);
            Assert.Equal(2, loaded.TurnMessages.Count);
            Assert.Equal(2, loaded.Iteration);
            Assert.False(loaded.IsTerminated);
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    [Fact]
    public async Task JsonStore_LoadUncommittedTurn_NoTurn_ReturnsNull()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), $"uncommitted-test-{Guid.NewGuid()}");
        try
        {
            var store = new JsonSessionStore(tempPath);

            // Act
            var result = await store.LoadUncommittedTurnAsync("nonexistent");

            // Assert
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    [Fact]
    public async Task JsonStore_DeleteUncommittedTurn_ReturnsNull()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), $"uncommitted-test-{Guid.NewGuid()}");
        try
        {
            var store = new JsonSessionStore(tempPath);
            await store.SaveUncommittedTurnAsync(CreateTestTurn());

            // Act
            await store.DeleteUncommittedTurnAsync("session-1");
            var loaded = await store.LoadUncommittedTurnAsync("session-1");

            // Assert
            Assert.Null(loaded);
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    [Fact]
    public async Task JsonStore_DeleteSession_AlsoDeletesUncommittedTurn()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), $"uncommitted-test-{Guid.NewGuid()}");
        try
        {
            var store = new JsonSessionStore(tempPath);
            var session = new AgentSession("session-1");
            session.AddMessage(UserMessage("Hello"));
            await store.SaveSessionAsync(session);
            await store.SaveUncommittedTurnAsync(CreateTestTurn());

            // Act
            await store.DeleteSessionAsync("session-1");
            var loaded = await store.LoadUncommittedTurnAsync("session-1");

            // Assert
            Assert.Null(loaded);
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    [Fact]
    public async Task JsonStore_UncommittedTurnFile_IsCreatedAtSessionLevel()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), $"uncommitted-test-{Guid.NewGuid()}");
        try
        {
            var store = new JsonSessionStore(tempPath);
            await store.SaveUncommittedTurnAsync(CreateTestTurn());

            // Assert — file should be at sessions/{sessionId}/uncommitted.json
            var expectedPath = Path.Combine(tempPath, "session-1", "uncommitted.json");
            Assert.True(File.Exists(expectedPath));
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    //──────────────────────────────────────────────────────────────────
    // JSON SERIALIZATION
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void UncommittedTurn_JsonSerialization_RoundTrip()
    {
        // Arrange
        var turn = CreateTestTurn();

        // Act
        var json = JsonSerializer.Serialize(turn, SessionJsonContext.CombinedOptions);
        var deserialized = JsonSerializer.Deserialize<UncommittedTurn>(json, SessionJsonContext.CombinedOptions);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(turn.SessionId, deserialized.SessionId);
        Assert.Equal(turn.BranchId, deserialized.BranchId);
        Assert.Equal(turn.TurnMessages.Count, deserialized.TurnMessages.Count);
        Assert.Equal(turn.Iteration, deserialized.Iteration);
        Assert.Equal(turn.IsTerminated, deserialized.IsTerminated);
        Assert.Equal(turn.Version, deserialized.Version);
    }

    [Fact]
    public void UncommittedTurn_DefaultBranch_IsMain()
    {
        Assert.Equal("main", UncommittedTurn.DefaultBranch);
    }
}
