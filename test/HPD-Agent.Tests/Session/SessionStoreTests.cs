using Microsoft.Extensions.AI;
using Xunit;
using HPD.Agent;

using HPD.Agent.Tests.Infrastructure;

namespace HPD.Agent.Tests.Session;

/// <summary>
/// Tests for ISessionStore implementations (InMemorySessionStore, JsonSessionStore).
/// Covers CRUD operations, uncommitted turns, and cleanup.
/// </summary>
public class SessionStoreTests : AgentTestBase
{
    //──────────────────────────────────────────────────────────────────
    // INMEMORY SESSION STORE - BASIC CRUD
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InMemoryStore_SaveAndLoad_RoundTrip()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = new AgentSession("test-session-1");
        session.AddMessage(UserMessage("Hello"));
        session.AddMessage(AssistantMessage("Hi there!"));

        // Act
        await store.SaveSessionAsync(session);
        var loaded = await store.LoadSessionAsync("test-session-1");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("test-session-1", loaded.Id);
        Assert.Equal(2, loaded.MessageCount);
    }

    [Fact]
    public async Task InMemoryStore_LoadNonExistent_ReturnsNull()
    {
        // Arrange
        var store = new InMemorySessionStore();

        // Act
        var result = await store.LoadSessionAsync("non-existent-id");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task InMemoryStore_DeleteSession_RemovesSession()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = CreateSessionWithMessages("session-to-delete");
        await store.SaveSessionAsync(session);

        // Act
        await store.DeleteSessionAsync("session-to-delete");
        var loaded = await store.LoadSessionAsync("session-to-delete");

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task InMemoryStore_ListSessionIds_ReturnsAllSessions()
    {
        // Arrange
        var store = new InMemorySessionStore();
        await store.SaveSessionAsync(CreateSessionWithMessages("session-1"));
        await store.SaveSessionAsync(CreateSessionWithMessages("session-2"));
        await store.SaveSessionAsync(CreateSessionWithMessages("session-3"));

        // Act
        var ids = await store.ListSessionIdsAsync();

        // Assert
        Assert.Equal(3, ids.Count);
        Assert.Contains("session-1", ids);
        Assert.Contains("session-2", ids);
        Assert.Contains("session-3", ids);
    }

    [Fact]
    public async Task InMemoryStore_SaveWithoutExecutionState_SavesSnapshot()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = new AgentSession("empty-session");
        session.AddMessage(UserMessage("Hello"));

        // Act
        await store.SaveSessionAsync(session);
        var loaded = await store.LoadSessionAsync("empty-session");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("empty-session", loaded.Id);
        Assert.Equal(1, loaded.MessageCount);
    }

    [Fact]
    public async Task InMemoryStore_SaveOverwrites_PreviousSnapshot()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = new AgentSession("overwrite-session");
        session.AddMessage(UserMessage("Message 1"));
        await store.SaveSessionAsync(session);

        // Update with new message
        session.AddMessage(AssistantMessage("Response 1"));
        await store.SaveSessionAsync(session);

        // Act
        var loaded = await store.LoadSessionAsync("overwrite-session");

        // Assert: Should have latest state with 2 messages
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.MessageCount);
    }

    //──────────────────────────────────────────────────────────────────
    // INMEMORY SESSION STORE - BACKWARD COMPAT CONSTRUCTOR
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InMemoryStore_BackwardCompatConstructor_Works()
    {
        // The old constructor with enableHistory/enablePendingWrites still compiles
        var store = new InMemorySessionStore(enableHistory: true, enablePendingWrites: true);
        var session = new AgentSession("compat-session");
        session.AddMessage(UserMessage("Hello"));

        await store.SaveSessionAsync(session);
        var loaded = await store.LoadSessionAsync("compat-session");

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.MessageCount);
    }

    //──────────────────────────────────────────────────────────────────
    // INMEMORY SESSION STORE - CLEANUP
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InMemoryStore_DeleteInactiveSessions_DryRun_DoesNotDelete()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = CreateSessionWithMessages("inactive-session");
        await store.SaveSessionAsync(session);
        await Task.Delay(50);

        // Act
        var count = await store.DeleteInactiveSessionsAsync(
            TimeSpan.FromMilliseconds(10), dryRun: true);

        // Assert
        Assert.Equal(1, count);
        Assert.NotNull(await store.LoadSessionAsync("inactive-session"));
    }

    [Fact]
    public async Task InMemoryStore_DeleteInactiveSessions_ActualDelete_RemovesSessions()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = CreateSessionWithMessages("inactive-session");
        await store.SaveSessionAsync(session);
        await Task.Delay(50);

        // Act
        var count = await store.DeleteInactiveSessionsAsync(
            TimeSpan.FromMilliseconds(10), dryRun: false);

        // Assert
        Assert.Equal(1, count);
        Assert.Null(await store.LoadSessionAsync("inactive-session"));
    }

    [Fact]
    public async Task InMemoryStore_DeleteSession_AlsoRemovesUncommittedTurn()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = CreateSessionWithMessages("session-with-turn");
        await store.SaveSessionAsync(session);

        var turn = CreateUncommittedTurn("session-with-turn");
        await store.SaveUncommittedTurnAsync(turn);

        // Act
        await store.DeleteSessionAsync("session-with-turn");

        // Assert
        Assert.Null(await store.LoadSessionAsync("session-with-turn"));
        Assert.Null(await store.LoadUncommittedTurnAsync("session-with-turn"));
    }

    //──────────────────────────────────────────────────────────────────
    // HELPERS
    //──────────────────────────────────────────────────────────────────

    private AgentSession CreateSessionWithMessages(string sessionId)
    {
        var session = new AgentSession(sessionId);
        session.AddMessage(UserMessage("Test message"));
        return session;
    }

    private UncommittedTurn CreateUncommittedTurn(string sessionId)
    {
        return new UncommittedTurn
        {
            SessionId = sessionId,
            BranchId = UncommittedTurn.DefaultBranch,
            TurnMessages = new List<ChatMessage> { UserMessage("test") },
            Iteration = 1,
            CompletedFunctions = System.Collections.Immutable.ImmutableHashSet<string>.Empty,
            MiddlewareState = new MiddlewareState(),
            IsTerminated = false,
            CreatedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow,
        };
    }
}
