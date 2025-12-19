using Microsoft.Extensions.AI;
using Xunit;
using HPD.Agent;
using HPD.Agent;

using HPD.Agent.Tests.Infrastructure;

namespace HPD.Agent.Tests.Session;

/// <summary>
/// Tests for ISessionStore implementations (InMemorySessionStore, JsonSessionStore).
/// Covers CRUD operations, checkpoint history, pending writes, and cleanup.
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
        // Note: SaveSessionAsync saves snapshots (no ExecutionState required)

        // Act
        await store.SaveSessionAsync(session);
        var loaded = await store.LoadSessionAsync("test-session-1");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("test-session-1", loaded.Id);
        Assert.Null(loaded.ExecutionState); // Snapshots don't include ExecutionState
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
        var session = CreateSessionWithState("session-to-delete");
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
        await store.SaveSessionAsync(CreateSessionWithState("session-1"));
        await store.SaveSessionAsync(CreateSessionWithState("session-2"));
        await store.SaveSessionAsync(CreateSessionWithState("session-3"));

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
        // Note: No ExecutionState set - this is the normal case after a turn completes

        // Act
        await store.SaveSessionAsync(session);
        var loaded = await store.LoadSessionAsync("empty-session");

        // Assert
        Assert.NotNull(loaded); // Should be saved as snapshot
        Assert.Equal("empty-session", loaded.Id);
        Assert.Null(loaded.ExecutionState); // No ExecutionState in snapshots
        Assert.Equal(1, loaded.MessageCount); // Messages are preserved
    }

    //──────────────────────────────────────────────────────────────────
    // INMEMORY SESSION STORE - EXECUTION CHECKPOINTS (NEW API)
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InMemoryStore_SaveCheckpointAsync_SavesExecutionCheckpoint()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = new AgentSession("session-1");
        session.AddMessage(UserMessage("Hello"));
        session.ExecutionState = AgentLoopState.Initial(
            session.Messages.ToList(), "run-1", "session-1", "TestAgent");

        var checkpoint = session.ToExecutionCheckpoint("checkpoint-1");
        var metadata = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = 0,
            MessageIndex = 1
        };

        // Act
        await store.SaveCheckpointAsync(checkpoint, metadata);
        var loaded = await store.LoadCheckpointAsync("session-1");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("session-1", loaded.SessionId);
        Assert.Equal("checkpoint-1", loaded.ExecutionCheckpointId);
        Assert.Single(loaded.ExecutionState.CurrentMessages);
    }

    [Fact]
    public async Task InMemoryStore_LoadCheckpointAsync_ReturnsLatest()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = new AgentSession("session-1");

        // Save first checkpoint
        session.AddMessage(UserMessage("Message 1"));
        session.ExecutionState = AgentLoopState.Initial(
            session.Messages.ToList(), "run-1", "session-1", "TestAgent");
        var checkpoint1 = session.ToExecutionCheckpoint("checkpoint-1");
        await store.SaveCheckpointAsync(checkpoint1, new CheckpointMetadata { Source = CheckpointSource.Loop, Step = 0, MessageIndex = 1 });

        // Save second checkpoint
        session.AddMessage(AssistantMessage("Response 1"));
        session.ExecutionState = session.ExecutionState.WithMessages(session.Messages.ToList());
        var checkpoint2 = session.ToExecutionCheckpoint("checkpoint-2");
        await store.SaveCheckpointAsync(checkpoint2, new CheckpointMetadata { Source = CheckpointSource.Loop, Step = 1, MessageIndex = 2 });

        // Act
        var loaded = await store.LoadCheckpointAsync("session-1");

        // Assert: Should return latest (checkpoint-2)
        Assert.NotNull(loaded);
        Assert.Equal("checkpoint-2", loaded.ExecutionCheckpointId);
        Assert.Equal(2, loaded.ExecutionState.CurrentMessages.Count);
    }

    [Fact]
    public async Task InMemoryStore_DeleteAllCheckpointsAsync_RemovesAllCheckpoints()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = new AgentSession("session-1");
        session.ExecutionState = AgentLoopState.Initial(
            new List<ChatMessage>(), "run-1", "session-1", "TestAgent");

        await store.SaveCheckpointAsync(
            session.ToExecutionCheckpoint("cp-1"),
            new CheckpointMetadata { Source = CheckpointSource.Loop, Step = 0, MessageIndex = 0 });
        await store.SaveCheckpointAsync(
            session.ToExecutionCheckpoint("cp-2"),
            new CheckpointMetadata { Source = CheckpointSource.Loop, Step = 1, MessageIndex = 0 });

        // Act
        await store.DeleteAllCheckpointsAsync("session-1");
        var loaded = await store.LoadCheckpointAsync("session-1");

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task InMemoryStore_LoadCheckpointAtAsync_ReturnsSpecificCheckpoint()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = new AgentSession("session-1");

        session.AddMessage(UserMessage("Message 1"));
        session.ExecutionState = AgentLoopState.Initial(
            session.Messages.ToList(), "run-1", "session-1", "TestAgent");
        await store.SaveCheckpointAsync(
            session.ToExecutionCheckpoint("cp-1"),
            new CheckpointMetadata { Source = CheckpointSource.Loop, Step = 0, MessageIndex = 1 });

        session.AddMessage(AssistantMessage("Response 1"));
        session.ExecutionState = session.ExecutionState.WithMessages(session.Messages.ToList());
        await store.SaveCheckpointAsync(
            session.ToExecutionCheckpoint("cp-2"),
            new CheckpointMetadata { Source = CheckpointSource.Loop, Step = 1, MessageIndex = 2 });

        // Act: Load the first checkpoint specifically
        var loaded = await store.LoadCheckpointAtAsync("session-1", "cp-1");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("cp-1", loaded.ExecutionCheckpointId);
        Assert.Single(loaded.ExecutionState.CurrentMessages);
    }

    //──────────────────────────────────────────────────────────────────
    // INMEMORY SESSION STORE - HISTORY MODE
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void InMemoryStore_WithHistory_SupportsHistory()
    {
        // Arrange & Act
        var store = new InMemorySessionStore(enableHistory: true);

        // Assert
        Assert.True(store.SupportsHistory);
    }

    [Fact]
    public void InMemoryStore_WithoutHistory_DoesNotSupportHistory()
    {
        // Arrange & Act
        var store = new InMemorySessionStore(enableHistory: false);

        // Assert
        Assert.False(store.SupportsHistory);
    }

    [Fact]
    public async Task InMemoryStore_History_PruneCheckpoints_KeepsLatestN()
    {
        // Arrange
        var store = new InMemorySessionStore(enableHistory: true);
        var session = new AgentSession("prune-session");

        // Create 5 execution checkpoints
        var state = AgentLoopState.Initial(
            new List<ChatMessage>(), "run-1", "conv-1", "TestAgent");

        for (int i = 0; i < 5; i++)
        {
            session.AddMessage(UserMessage($"Message {i}"));
            state = state.NextIteration().WithMessages(session.Messages.ToList());
            session.ExecutionState = state;

            var checkpoint = session.ToExecutionCheckpoint($"cp-{i}");
            await store.SaveCheckpointAsync(checkpoint,
                new CheckpointMetadata { Source = CheckpointSource.Loop, Step = i, MessageIndex = i + 1 });
        }

        // Act: Prune to keep 3
        await store.PruneCheckpointsAsync("prune-session", keepLatest: 3);

        // Assert
        var manifest = await store.GetCheckpointManifestAsync("prune-session");
        Assert.Equal(3, manifest.Count);
    }

    //──────────────────────────────────────────────────────────────────
    // INMEMORY SESSION STORE - PENDING WRITES
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void InMemoryStore_WithPendingWrites_SupportsPendingWrites()
    {
        // Arrange & Act
        var store = new InMemorySessionStore(enablePendingWrites: true);

        // Assert
        Assert.True(store.SupportsPendingWrites);
    }

    [Fact]
    public async Task InMemoryStore_PendingWrites_SaveAndLoad_RoundTrip()
    {
        // Arrange
        var store = new InMemorySessionStore(enablePendingWrites: true);
        var writes = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-1",
                FunctionName = "GetWeather",
                ResultJson = "{\"temp\": 72}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                SessionId = "session-1"
            }
        };

        // Act
        await store.SavePendingWritesAsync("session-1", "checkpoint-1", writes);
        var loaded = await store.LoadPendingWritesAsync("session-1", "checkpoint-1");

        // Assert
        Assert.Single(loaded);
        Assert.Equal("call-1", loaded[0].CallId);
        Assert.Equal("GetWeather", loaded[0].FunctionName);
    }

    [Fact]
    public async Task InMemoryStore_PendingWrites_Delete_RemovesWrites()
    {
        // Arrange
        var store = new InMemorySessionStore(enablePendingWrites: true);
        var writes = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-1",
                FunctionName = "Test",
                ResultJson = "{}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                SessionId = "session-1"
            }
        };
        await store.SavePendingWritesAsync("session-1", "checkpoint-1", writes);

        // Act
        await store.DeletePendingWritesAsync("session-1", "checkpoint-1");
        var loaded = await store.LoadPendingWritesAsync("session-1", "checkpoint-1");

        // Assert
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task InMemoryStore_PendingWrites_DifferentCheckpoints_AreIsolated()
    {
        // Arrange
        var store = new InMemorySessionStore(enablePendingWrites: true);

        var writes1 = new List<PendingWrite>
        {
            new PendingWrite { CallId = "call-1", FunctionName = "Func1", ResultJson = "{}",
                CompletedAt = DateTime.UtcNow, Iteration = 1, SessionId = "session-1" }
        };
        var writes2 = new List<PendingWrite>
        {
            new PendingWrite { CallId = "call-2", FunctionName = "Func2", ResultJson = "{}",
                CompletedAt = DateTime.UtcNow, Iteration = 2, SessionId = "session-1" }
        };

        // Act
        await store.SavePendingWritesAsync("session-1", "checkpoint-1", writes1);
        await store.SavePendingWritesAsync("session-1", "checkpoint-2", writes2);

        var loaded1 = await store.LoadPendingWritesAsync("session-1", "checkpoint-1");
        var loaded2 = await store.LoadPendingWritesAsync("session-1", "checkpoint-2");

        // Assert
        Assert.Single(loaded1);
        Assert.Equal("call-1", loaded1[0].CallId);
        Assert.Single(loaded2);
        Assert.Equal("call-2", loaded2[0].CallId);
    }

    //──────────────────────────────────────────────────────────────────
    // INMEMORY SESSION STORE - CLEANUP
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InMemoryStore_DeleteOlderThan_RemovesOldCheckpoints()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var oldSession = CreateSessionWithState("old-session");
        await store.SaveSessionAsync(oldSession);

        await Task.Delay(50);
        var cutoff = DateTime.UtcNow;
        await Task.Delay(50);

        var newSession = CreateSessionWithState("new-session");
        await store.SaveSessionAsync(newSession);

        // Act
        await store.DeleteOlderThanAsync(cutoff);

        // Assert
        Assert.Null(await store.LoadSessionAsync("old-session"));
        Assert.NotNull(await store.LoadSessionAsync("new-session"));
    }

    [Fact]
    public async Task InMemoryStore_DeleteInactiveSessions_DryRun_DoesNotDelete()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var session = CreateSessionWithState("inactive-session");
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
        var session = CreateSessionWithState("inactive-session");
        await store.SaveSessionAsync(session);
        await Task.Delay(50);

        // Act
        var count = await store.DeleteInactiveSessionsAsync(
            TimeSpan.FromMilliseconds(10), dryRun: false);

        // Assert
        Assert.Equal(1, count);
        Assert.Null(await store.LoadSessionAsync("inactive-session"));
    }

    //──────────────────────────────────────────────────────────────────
    // INMEMORY SESSION STORE - NO HISTORY MODE
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InMemoryStore_NoHistory_OverwritesPreviousState()
    {
        // Arrange
        var store = new InMemorySessionStore(enableHistory: false);
        var session = new AgentSession("no-history-session");
        session.AddMessage(UserMessage("Message 1"));

        // SaveSessionAsync saves snapshots (no ExecutionState required)
        await store.SaveSessionAsync(session);

        // Update with new message
        session.AddMessage(AssistantMessage("Response 1"));
        await store.SaveSessionAsync(session);

        // Act
        var loaded = await store.LoadSessionAsync("no-history-session");

        // Assert: Should have latest state with 2 messages (snapshot overwrites previous)
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.MessageCount);
        Assert.Null(loaded.ExecutionState); // Snapshots don't include ExecutionState
    }

    [Fact]
    public async Task InMemoryStore_NoHistory_GetCheckpointManifest_ReturnsEmpty()
    {
        // Arrange
        var store = new InMemorySessionStore(enableHistory: false);
        var session = CreateSessionWithState("no-history-session");
        await store.SaveSessionAsync(session);

        // Act
        var manifest = await store.GetCheckpointManifestAsync("no-history-session");

        // Assert
        Assert.Empty(manifest);
    }

    //──────────────────────────────────────────────────────────────────
    // HELPERS
    //──────────────────────────────────────────────────────────────────

    private AgentSession CreateSessionWithState(string sessionId)
    {
        var session = new AgentSession(sessionId);
        session.AddMessage(UserMessage("Test message"));

        var state = AgentLoopState.Initial(
            session.Messages.ToList(),
            "run-123",
            sessionId,
            "TestAgent");
        session.ExecutionState = state;

        return session;
    }
}
