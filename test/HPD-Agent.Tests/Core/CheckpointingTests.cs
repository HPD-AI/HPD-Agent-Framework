using Microsoft.Extensions.AI;
using Xunit;
using HPD.Agent;
using HPD.Agent;
using HPD.Agent.Tests.Infrastructure;
using System.Text.Json;

namespace HPD.Agent.Tests.Core;

/// <summary>
/// Comprehensive tests for thread-Collapsed durable execution (checkpointing).
/// Tests serialization, persistence, resume semantics, and checkpoint retention modes.
/// </summary>
public class CheckpointingTests : AgentTestBase
{
    //      
    // AGENTLOOPSTATE SERIALIZATION TESTS
    //      

    [Fact]
    public void AgentLoopState_Serialize_ProducesValidJson()
    {
        // Arrange: Create a state with sample data
        var messages = new List<ChatMessage>
        {
            UserMessage("Hello"),
            AssistantMessage("Hi there!")
        };
        var state = AgentLoopState.Initial(messages, "run-123", "conv-456", "TestAgent")
            .NextIteration();

        // Act: Serialize to JSON
        var json = state.Serialize();

        // Assert: Should produce valid JSON
        Assert.NotNull(json);
        Assert.NotEmpty(json);

        // Verify JSON is valid by parsing and deserializing back
        var doc = JsonDocument.Parse(json);
        var restoredState = AgentLoopState.Deserialize(json);

        // Verify the state can be round-tripped
        Assert.Equal(1, restoredState.Iteration); // After NextIteration(), should be 1
        Assert.Equal(1, restoredState.Version);
        Assert.NotNull(restoredState.ETag); // ETag should be generated
    }

    [Fact]
    public void AgentLoopState_Deserialize_RestoresCompleteState()
    {
        // Arrange: Create and serialize a complex state
        var messages = new List<ChatMessage>
        {
            UserMessage("Test message"),
            AssistantMessage("Test response")
        };
        var originalState = AgentLoopState.Initial(messages, "run-123", "conv-456", "TestAgent")
            .NextIteration()
            .NextIteration();

        var json = originalState.Serialize();

        // Act: Deserialize
        var restoredState = AgentLoopState.Deserialize(json);

        // Assert: All properties should match
        Assert.Equal(originalState.Iteration, restoredState.Iteration);
        Assert.Equal(originalState.RunId, restoredState.RunId);
        Assert.Equal(originalState.ConversationId, restoredState.ConversationId);
        Assert.Equal(originalState.AgentName, restoredState.AgentName);
        Assert.Equal(originalState.CurrentMessages.Count, restoredState.CurrentMessages.Count);
        Assert.NotNull(restoredState.ETag);
    }

    [Fact]
    public void AgentLoopState_Deserialize_NewerVersion_ThrowsVersionException()
    {
        // Arrange: Create JSON with future version number
        var futureVersionJson = """
        {
            "Version": 999,
            "Iteration": 1,
            "ConsecutiveFailures": 0,
            "MessageTurnId": "test",
            "ConversationId": "test",
            "AgentName": "test",
            "CurrentMessages": [],
            "ExpandedToolkits": [],
            "CompletedFunctions": []
        }
        """;

        // Act & Assert: Should throw version exception
        var ex = Assert.Throws<CheckpointVersionTooNewException>(() =>
            AgentLoopState.Deserialize(futureVersionJson));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void AgentLoopState_ValidateConsistency_MatchingMessageCount_Succeeds()
    {
        // Arrange: State with 3 messages
        var messages = new List<ChatMessage>
        {
            UserMessage("1"),
            UserMessage("2"),
            UserMessage("3")
        };
        var state = AgentLoopState.Initial(messages, "run-123", "conv-456", "TestAgent");

        // Act & Assert: Should not throw when message counts match
        state.ValidateConsistency(currentMessageCount: 3, allowStaleCheckpoint: false);
    }

    [Fact]
    public void AgentLoopState_ValidateConsistency_MismatchedMessageCount_ThrowsStaleException()
    {
        // Arrange: State with 3 messages, but conversation now has 5
        var messages = new List<ChatMessage>
        {
            UserMessage("1"),
            UserMessage("2"),
            UserMessage("3")
        };
        var state = AgentLoopState.Initial(messages, "run-123", "conv-456", "TestAgent");

        // Act & Assert: Should throw stale checkpoint exception
        var ex = Assert.Throws<CheckpointStaleException>(() =>
            state.ValidateConsistency(currentMessageCount: 5, allowStaleCheckpoint: false));
        Assert.Contains("5 messages", ex.Message);
        Assert.Contains("3", ex.Message);
    }

    [Fact]
    public void AgentLoopState_ValidateConsistency_AllowStale_DoesNotThrow()
    {
        // Arrange: State with mismatched message count
        var messages = new List<ChatMessage> { UserMessage("1") };
        var state = AgentLoopState.Initial(messages, "run-123", "conv-456", "TestAgent");

        // Act & Assert: Should not throw when allowStaleCheckpoint = true
        state.ValidateConsistency(currentMessageCount: 10, allowStaleCheckpoint: true);
    }

    //      
    // INMEMORYTHREADCHECKPOINTER TESTS - LATESTONLY MODE
    //      

    [Fact]
    public async Task InMemoryCheckpointer_LatestOnly_SaveAndLoad_RoundTrip()
    {
        // Arrange: Checkpointer in LatestOnly mode (history disabled for simpler storage)
        var checkpointer = new InMemorySessionStore(enableHistory: false);
        var thread = new AgentSession();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(),
            "run-123",
            "conv-456",
            "TestAgent");
        thread.ExecutionState = state;

        // Act: Save checkpoint (for crash recovery, use the internal checkpoint method)
        // For LatestOnly mode with history disabled, we store as a SessionCheckpoint
        var checkpoint = thread.ToCheckpoint();
        // Save as checkpoint by storing directly
        await checkpointer.SaveSessionAsync(thread);
        var loadedThread = await checkpointer.LoadSessionAsync(thread.Id);

        // Assert: With SaveSessionAsync, snapshots are saved (no ExecutionState)
        // This is expected - SaveSessionAsync is for lightweight saves
        Assert.NotNull(loadedThread);
        Assert.Equal(1, loadedThread.MessageCount);
        // Note: ExecutionState is null because SaveSessionAsync saves snapshots
        // Use SaveSessionAtCheckpointAsync for full checkpoints with ExecutionState
    }

    [Fact]
    public async Task InMemoryCheckpointer_LatestOnly_Upsert_OverwritesPrevious()
    {
        // Arrange: Checkpointer in LatestOnly mode (history disabled)
        var checkpointer = new InMemorySessionStore(enableHistory: false);
        var thread = new AgentSession();
        await thread.AddMessageAsync(UserMessage("Hello"));

        // Save first snapshot
        await checkpointer.SaveSessionAsync(thread);

        // Act: Add a message and save again
        await thread.AddMessageAsync(AssistantMessage("World"));
        await checkpointer.SaveSessionAsync(thread);

        // Load
        var loadedThread = await checkpointer.LoadSessionAsync(thread.Id);

        // Assert: Should have latest snapshot with 2 messages
        Assert.NotNull(loadedThread);
        Assert.Equal(2, loadedThread.MessageCount);
        // SaveSessionAsync saves snapshots, so ExecutionState is null
        Assert.Null(loadedThread.ExecutionState);
    }

    [Fact]
    public async Task InMemoryCheckpointer_LatestOnly_LoadNonExistent_ReturnsNull()
    {
        // Arrange: Empty checkpointer
        var checkpointer = new InMemorySessionStore();

        // Act: Try to load non-existent thread
        var result = await checkpointer.LoadSessionAsync("non-existent-thread-id");

        // Assert: Should return null
        Assert.Null(result);
    }

    [Fact]
    public async Task InMemoryCheckpointer_LatestOnly_DeleteThread_RemovesCheckpoint()
    {
        // Arrange: Checkpointer with saved thread
        var checkpointer = new InMemorySessionStore();
        var thread = new AgentSession();
        await thread.AddMessageAsync(UserMessage("Hello"));
        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(),
            "run-123",
            "conv-456",
            "TestAgent");
        thread.ExecutionState = state;
        await checkpointer.SaveSessionAsync(thread);

        // Act: Delete thread
        await checkpointer.DeleteSessionAsync(thread.Id);

        // Assert: Should no longer exist
        var loadedThread = await checkpointer.LoadSessionAsync(thread.Id);
        Assert.Null(loadedThread);
    }

    [Fact]
    public async Task InMemoryCheckpointer_LatestOnly_ListThreadIds_ReturnsAllThreads()
    {
        // Arrange: Checkpointer with multiple threads
        var checkpointer = new InMemorySessionStore();

        var thread1 = new AgentSession();
        await thread1.AddMessageAsync(UserMessage("Hello"));
        thread1.ExecutionState = AgentLoopState.Initial(
            await thread1.GetMessagesAsync(), "run-1", "conv-1", "Agent1");
        await checkpointer.SaveSessionAsync(thread1);

        var thread2 = new AgentSession();
        await thread2.AddMessageAsync(UserMessage("World"));
        thread2.ExecutionState = AgentLoopState.Initial(
            await thread2.GetMessagesAsync(), "run-2", "conv-2", "Agent2");
        await checkpointer.SaveSessionAsync(thread2);

        // Act: List all thread IDs
        var threadIds = await checkpointer.ListSessionIdsAsync();

        // Assert: Should contain both threads
        Assert.Equal(2, threadIds.Count);
        Assert.Contains(thread1.Id, threadIds);
        Assert.Contains(thread2.Id, threadIds);
    }

    //      
    // INMEMORYTHREADCHECKPOINTER TESTS - FULLHISTORY MODE
    //      

    [Fact]
    public async Task InMemoryCheckpointer_FullHistory_SaveMultiple_PreservesHistory()
    {
        // Arrange: Checkpointer in FullHistory mode
        var checkpointer = new InMemorySessionStore();
        var thread = new AgentSession();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state1 = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent");
        thread.ExecutionState = state1;

        // Use SaveSessionAtCheckpointAsync for full checkpoints with ExecutionState
        var checkpoint1Id = Guid.NewGuid().ToString();
        var metadata1 = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = state1.Iteration,
            MessageIndex = thread.MessageCount
        };
        await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpoint1Id, metadata1);

        // Act: Save again with different iteration
        var state2 = state1.NextIteration();
        thread.ExecutionState = state2;
        var checkpoint2Id = Guid.NewGuid().ToString();
        var metadata2 = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = state2.Iteration,
            MessageIndex = thread.MessageCount
        };
        await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpoint2Id, metadata2);

        var state3 = state2.NextIteration();
        thread.ExecutionState = state3;
        var checkpoint3Id = Guid.NewGuid().ToString();
        var metadata3 = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = state3.Iteration,
            MessageIndex = thread.MessageCount
        };
        await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpoint3Id, metadata3);

        // Assert: Should have 3 checkpoints in history (no root with SaveSessionAtCheckpointAsync)
        var history = await checkpointer.GetCheckpointManifestAsync(thread.Id);
        var checkpoints = history.Where(c => !c.IsSnapshot).ToList();
        Assert.Equal(3, checkpoints.Count);
        // Manifest entries have Step field instead of full State
        // Order is newest first: [iter2, iter1, iter0]
        Assert.Equal(2, checkpoints[0].Step); // Newest first
        Assert.Equal(1, checkpoints[1].Step);
        Assert.Equal(0, checkpoints[2].Step);
    }

    [Fact]
    public async Task InMemoryCheckpointer_FullHistory_LoadThreadAtCheckpoint_RestoresSpecificCheckpoint()
    {
        // Arrange: Checkpointer with multiple checkpoints
        var checkpointer = new InMemorySessionStore();
        var thread = new AgentSession();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state1 = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent");
        thread.ExecutionState = state1;

        // Save first checkpoint using SaveSessionAtCheckpointAsync
        var checkpoint1Id = Guid.NewGuid().ToString();
        var metadata1 = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = state1.Iteration,
            MessageIndex = thread.MessageCount
        };
        await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpoint1Id, metadata1);

        var state2 = state1.NextIteration();
        thread.ExecutionState = state2;
        var checkpoint2Id = Guid.NewGuid().ToString();
        var metadata2 = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = state2.Iteration,
            MessageIndex = thread.MessageCount
        };
        await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpoint2Id, metadata2);

        // Act: Load specific checkpoint (first one)
        var loadedThread = await checkpointer.LoadSessionAtCheckpointAsync(thread.Id, checkpoint1Id);

        // Assert: Should restore first checkpoint state
        Assert.NotNull(loadedThread);
        Assert.NotNull(loadedThread.ExecutionState);
        Assert.Equal(0, loadedThread.ExecutionState.Iteration);
        var CollapsingState = loadedThread.ExecutionState.MiddlewareState.Collapsing;
        Assert.True(CollapsingState == null || CollapsingState.ExpandedContainers.Count == 0);
    }

    [Fact]
    public async Task InMemoryCheckpointer_FullHistory_PruneCheckpoints_KeepsLatestN()
    {
        // Arrange: Checkpointer with 5 checkpoints
        var checkpointer = new InMemorySessionStore();
        var thread = new AgentSession();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent");

        for (int i = 0; i < 5; i++)
        {
            thread.ExecutionState = state;
            var checkpointId = Guid.NewGuid().ToString();
            var metadata = new CheckpointMetadata
            {
                Source = CheckpointSource.Loop,
                Step = state.Iteration,
                MessageIndex = thread.MessageCount
            };
            await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpointId, metadata);
            state = state.NextIteration();
        }

        // Act: Prune to keep only 4 latest checkpoints
        await checkpointer.PruneCheckpointsAsync(thread.Id, keepLatest: 4);

        // Assert: Should have 4 checkpoints (newest 4: iter4, iter3, iter2, iter1)
        var history = await checkpointer.GetCheckpointManifestAsync(thread.Id);
        var checkpoints = history.Where(c => !c.IsSnapshot).ToList();
        Assert.Equal(4, checkpoints.Count);
        Assert.Equal(4, checkpoints[0].Step); // Newest
        Assert.Equal(3, checkpoints[1].Step);
        Assert.Equal(2, checkpoints[2].Step);
        Assert.Equal(1, checkpoints[3].Step);
    }

    [Fact]
    public async Task InMemoryCheckpointer_FullHistory_GetCheckpointHistory_WithLimit_ReturnsLimitedResults()
    {
        // Arrange: Checkpointer with 10 checkpoints
        var checkpointer = new InMemorySessionStore();
        var thread = new AgentSession();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent");

        for (int i = 0; i < 10; i++)
        {
            thread.ExecutionState = state;
            var checkpointId = Guid.NewGuid().ToString();
            var metadata = new CheckpointMetadata
            {
                Source = CheckpointSource.Loop,
                Step = state.Iteration,
                MessageIndex = thread.MessageCount
            };
            await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpointId, metadata);
            state = state.NextIteration();
        }

        // Act: Get only 5 entries (limit applies to all entries)
        var history = await checkpointer.GetCheckpointManifestAsync(thread.Id, limit: 5);
        var checkpoints = history.Where(c => !c.IsSnapshot).ToList();

        // Assert: limit=5 returns 5 total entries
        Assert.Equal(5, history.Count);
        // All 5 should be checkpoints (since we only saved checkpoints)
        Assert.Equal(5, checkpoints.Count);
        Assert.Equal(9, checkpoints[0].Step); // Newest checkpoint first
    }

    [Fact]
    public async Task InMemoryCheckpointer_FullHistory_GetCheckpointHistory_WithBefore_FiltersOlderCheckpoints()
    {
        // Arrange: Checkpointer with checkpoints at different times
        var checkpointer = new InMemorySessionStore();
        var thread = new AgentSession();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent");

        // Save 3 checkpoints
        for (int i = 0; i < 3; i++)
        {
            thread.ExecutionState = state;
            var checkpointId = Guid.NewGuid().ToString();
            var metadata = new CheckpointMetadata
            {
                Source = CheckpointSource.Loop,
                Step = state.Iteration,
                MessageIndex = thread.MessageCount
            };
            await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpointId, metadata);
            state = state.NextIteration();
            await Task.Delay(10); // Small delay to ensure different timestamps
        }

        var cutoffTime = DateTime.UtcNow;
        await Task.Delay(10);

        // Save 2 more checkpoints after cutoff
        for (int i = 0; i < 2; i++)
        {
            thread.ExecutionState = state;
            var checkpointId = Guid.NewGuid().ToString();
            var metadata = new CheckpointMetadata
            {
                Source = CheckpointSource.Loop,
                Step = state.Iteration,
                MessageIndex = thread.MessageCount
            };
            await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpointId, metadata);
            state = state.NextIteration();
        }

        // Act: Get all checkpoints and filter to before cutoff
        var allHistory = await checkpointer.GetCheckpointManifestAsync(thread.Id);
        var history = allHistory.Where(cp => cp.CreatedAt < cutoffTime).ToList();

        // Assert: Should return only checkpoints created before cutoff (3 checkpoints)
        Assert.True(history.Count >= 3);
        Assert.All(history, cp => Assert.True(cp.CreatedAt < cutoffTime));
    }

    //      
    // CONVERSATIONTHREAD SERIALIZATION WITH EXECUTIONSTATE
    //      


    //      
    // CHECKPOINT METADATA TESTS
    //      

    [Fact]
    public void CheckpointMetadata_DefaultValues_AreCorrect()
    {
        // Arrange & Act: Create metadata with default constructor
        var metadata = new CheckpointMetadata();

        // Assert: Should have default values
        Assert.Equal(CheckpointSource.Input, metadata.Source);
        Assert.Equal(0, metadata.Step); // Default value for int is 0
        Assert.Null(metadata.ParentCheckpointId);
    }

    [Fact]
    public void CheckpointMetadata_LoopSource_HasCorrectProperties()
    {
        // Arrange & Act: Create metadata for loop checkpoint
        var metadata = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = 5
        };

        // Assert: Should have loop source and step number
        Assert.Equal(CheckpointSource.Loop, metadata.Source);
        Assert.Equal(5, metadata.Step);
    }

    //      
    // CHECKPOINT CLEANUP TESTS
    //      

    [Fact]
    public async Task InMemoryCheckpointer_DeleteOlderThan_RemovesOldCheckpoints()
    {
        // Arrange: Checkpointer with old and new checkpoints
        var checkpointer = new InMemorySessionStore();

        var oldThread = new AgentSession();
        await oldThread.AddMessageAsync(UserMessage("Old"));
        oldThread.ExecutionState = AgentLoopState.Initial(
            await oldThread.GetMessagesAsync(), "run-old", "conv-old", "Agent");
        await checkpointer.SaveSessionAsync(oldThread);

        // Wait a bit
        await Task.Delay(50);
        var cutoff = DateTime.UtcNow;
        await Task.Delay(50);

        var newThread = new AgentSession();
        await newThread.AddMessageAsync(UserMessage("New"));
        newThread.ExecutionState = AgentLoopState.Initial(
            await newThread.GetMessagesAsync(), "run-new", "conv-new", "Agent");
        await checkpointer.SaveSessionAsync(newThread);

        // Act: Delete checkpoints older than cutoff
        await checkpointer.DeleteOlderThanAsync(cutoff);

        // Assert: Old thread should be deleted, new thread should remain
        var oldLoaded = await checkpointer.LoadSessionAsync(oldThread.Id);
        var newLoaded = await checkpointer.LoadSessionAsync(newThread.Id);

        Assert.Null(oldLoaded);
        Assert.NotNull(newLoaded);
    }

    [Fact]
    public async Task InMemoryCheckpointer_DeleteInactiveThreads_DryRun_DoesNotDelete()
    {
        // Arrange: Checkpointer with inactive thread
        var checkpointer = new InMemorySessionStore();
        var thread = new AgentSession();
        await thread.AddMessageAsync(UserMessage("Hello"));
        thread.ExecutionState = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "Agent");
        await checkpointer.SaveSessionAsync(thread);

        await Task.Delay(50); // Make it "old"

        // Act: Dry run deletion
        var count = await checkpointer.DeleteInactiveSessionsAsync(
            TimeSpan.FromMilliseconds(10),
            dryRun: true);

        // Assert: Should report 1 thread but not actually delete it
        Assert.Equal(1, count);
        var loaded = await checkpointer.LoadSessionAsync(thread.Id);
        Assert.NotNull(loaded); // Should still exist
    }

    [Fact]
    public async Task InMemoryCheckpointer_DeleteInactiveThreads_ActualDelete_RemovesThreads()
    {
        // Arrange: Checkpointer with inactive thread
        var checkpointer = new InMemorySessionStore();
        var thread = new AgentSession();
        await thread.AddMessageAsync(UserMessage("Hello"));
        thread.ExecutionState = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "Agent");
        await checkpointer.SaveSessionAsync(thread);

        await Task.Delay(50); // Make it "old"

        // Act: Actual deletion
        var count = await checkpointer.DeleteInactiveSessionsAsync(
            TimeSpan.FromMilliseconds(10),
            dryRun: false);

        // Assert: Should delete the thread
        Assert.Equal(1, count);
        var loaded = await checkpointer.LoadSessionAsync(thread.Id);
        Assert.Null(loaded); // Should be deleted
    }

    //      
    // PENDING WRITES TESTS
    //      

    [Fact]
    public async Task PendingWrites_SaveAndLoad_RoundTrip()
    {
        // Arrange: Checkpointer and pending writes
        var checkpointer = new InMemorySessionStore();
        var threadId = "thread-123";
        var checkpointId = "checkpoint-456";

        var writes = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-1",
                FunctionName = "GetWeather",
                ResultJson = "{\"temp\": 72}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                SessionId = threadId
            },
            new PendingWrite
            {
                CallId = "call-2",
                FunctionName = "GetNews",
                ResultJson = "{\"headline\": \"Breaking news\"}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                SessionId = threadId
            }
        };

        // Act: Save and load
        await checkpointer.SavePendingWritesAsync(threadId, checkpointId, writes);
        var loadedWrites = await checkpointer.LoadPendingWritesAsync(threadId, checkpointId);

        // Assert: Should restore all pending writes
        Assert.Equal(2, loadedWrites.Count);
        Assert.Equal("call-1", loadedWrites[0].CallId);
        Assert.Equal("GetWeather", loadedWrites[0].FunctionName);
        Assert.Equal("{\"temp\": 72}", loadedWrites[0].ResultJson);
        Assert.Equal("call-2", loadedWrites[1].CallId);
        Assert.Equal("GetNews", loadedWrites[1].FunctionName);
    }

    [Fact]
    public async Task PendingWrites_LoadNonExistent_ReturnsEmptyList()
    {
        // Arrange: Empty checkpointer
        var checkpointer = new InMemorySessionStore();

        // Act: Try to load non-existent pending writes
        var result = await checkpointer.LoadPendingWritesAsync("thread-123", "checkpoint-456");

        // Assert: Should return empty list
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task PendingWrites_Delete_RemovesWrites()
    {
        // Arrange: Checkpointer with saved pending writes
        var checkpointer = new InMemorySessionStore();
        var threadId = "thread-123";
        var checkpointId = "checkpoint-456";

        var writes = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-1",
                FunctionName = "TestFunction",
                ResultJson = "{}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                SessionId = threadId
            }
        };

        await checkpointer.SavePendingWritesAsync(threadId, checkpointId, writes);

        // Act: Delete pending writes
        await checkpointer.DeletePendingWritesAsync(threadId, checkpointId);

        // Assert: Should return empty list after deletion
        var loaded = await checkpointer.LoadPendingWritesAsync(threadId, checkpointId);
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task PendingWrites_SaveMultipleTimes_AppendsWrites()
    {
        // Arrange: Checkpointer
        var checkpointer = new InMemorySessionStore();
        var threadId = "thread-123";
        var checkpointId = "checkpoint-456";

        var writes1 = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-1",
                FunctionName = "Function1",
                ResultJson = "{}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                SessionId = threadId
            }
        };

        var writes2 = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-2",
                FunctionName = "Function2",
                ResultJson = "{}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                SessionId = threadId
            }
        };

        // Act: Save twice to same checkpoint
        await checkpointer.SavePendingWritesAsync(threadId, checkpointId, writes1);
        await checkpointer.SavePendingWritesAsync(threadId, checkpointId, writes2);

        // Load
        var loadedWrites = await checkpointer.LoadPendingWritesAsync(threadId, checkpointId);

        // Assert: Should have both writes appended
        Assert.Equal(2, loadedWrites.Count);
        Assert.Contains(loadedWrites, w => w.CallId == "call-1");
        Assert.Contains(loadedWrites, w => w.CallId == "call-2");
    }

    [Fact]
    public async Task PendingWrites_DifferentCheckpoints_AreIsolated()
    {
        // Arrange: Checkpointer with writes for different checkpoints
        var checkpointer = new InMemorySessionStore();
        var threadId = "thread-123";
        var checkpoint1 = "checkpoint-1";
        var checkpoint2 = "checkpoint-2";

        var writes1 = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-1",
                FunctionName = "Function1",
                ResultJson = "{}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                SessionId = threadId
            }
        };

        var writes2 = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-2",
                FunctionName = "Function2",
                ResultJson = "{}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 2,
                SessionId = threadId
            }
        };

        // Act: Save to different checkpoints
        await checkpointer.SavePendingWritesAsync(threadId, checkpoint1, writes1);
        await checkpointer.SavePendingWritesAsync(threadId, checkpoint2, writes2);

        // Load both
        var loaded1 = await checkpointer.LoadPendingWritesAsync(threadId, checkpoint1);
        var loaded2 = await checkpointer.LoadPendingWritesAsync(threadId, checkpoint2);

        // Assert: Each checkpoint should have its own writes
        Assert.Single(loaded1);
        Assert.Equal("call-1", loaded1[0].CallId);

        Assert.Single(loaded2);
        Assert.Equal("call-2", loaded2[0].CallId);
    }

    [Fact]
    public async Task PendingWrites_DifferentThreads_AreIsolated()
    {
        // Arrange: Checkpointer with writes for different threads
        var checkpointer = new InMemorySessionStore();
        var thread1 = "thread-1";
        var thread2 = "thread-2";
        var checkpointId = "checkpoint-123";

        var writes1 = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-1",
                FunctionName = "Function1",
                ResultJson = "{}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                SessionId = thread1
            }
        };

        var writes2 = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-2",
                FunctionName = "Function2",
                ResultJson = "{}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                SessionId = thread2
            }
        };

        // Act: Save to different threads
        await checkpointer.SavePendingWritesAsync(thread1, checkpointId, writes1);
        await checkpointer.SavePendingWritesAsync(thread2, checkpointId, writes2);

        // Load both
        var loaded1 = await checkpointer.LoadPendingWritesAsync(thread1, checkpointId);
        var loaded2 = await checkpointer.LoadPendingWritesAsync(thread2, checkpointId);

        // Assert: Each thread should have its own writes
        Assert.Single(loaded1);
        Assert.Equal("call-1", loaded1[0].CallId);
        Assert.Equal(thread1, loaded1[0].SessionId);

        Assert.Single(loaded2);
        Assert.Equal("call-2", loaded2[0].CallId);
        Assert.Equal(thread2, loaded2[0].SessionId);
    }

    [Fact]
    public async Task PendingWrites_LoadReturnsCopy_ModificationsDoNotAffectStorage()
    {
        // Arrange: Checkpointer with saved pending writes
        var checkpointer = new InMemorySessionStore();
        var threadId = "thread-123";
        var checkpointId = "checkpoint-456";

        var writes = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-1",
                FunctionName = "TestFunction",
                ResultJson = "{}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                SessionId = threadId
            }
        };

        await checkpointer.SavePendingWritesAsync(threadId, checkpointId, writes);

        // Act: Load and modify the list
        var loaded1 = await checkpointer.LoadPendingWritesAsync(threadId, checkpointId);
        loaded1.Clear(); // Modify the returned list

        // Load again
        var loaded2 = await checkpointer.LoadPendingWritesAsync(threadId, checkpointId);

        // Assert: Second load should still have the original data
        Assert.Single(loaded2);
        Assert.Equal("call-1", loaded2[0].CallId);
    }

    [Fact]
    public async Task LoadThreadAtCheckpoint_RestoresMessagesFromState()
    {
        // Arrange: Create a conversation with multiple checkpoints
        var checkpointer = new InMemorySessionStore();
        var thread = new AgentSession();

        // First exchange
        await thread.AddMessageAsync(UserMessage("Message 1"));
        await thread.AddMessageAsync(AssistantMessage("Response 1"));

        var state1 = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-1", "conv-1", "TestAgent");
        thread.ExecutionState = state1;
        var checkpoint1Id = Guid.NewGuid().ToString();
        var metadata1 = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = state1.Iteration,
            MessageIndex = thread.MessageCount
        };
        await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpoint1Id, metadata1);

        // Second exchange
        await thread.AddMessageAsync(UserMessage("Message 2"));
        await thread.AddMessageAsync(AssistantMessage("Response 2"));

        var state2 = state1.NextIteration()
            .WithMessages(await thread.GetMessagesAsync());
        thread.ExecutionState = state2;
        var checkpoint2Id = Guid.NewGuid().ToString();
        var metadata2 = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = state2.Iteration,
            MessageIndex = thread.MessageCount
        };
        await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpoint2Id, metadata2);

        // Act: Load thread at earlier checkpoint (first one with 2 messages)
        var loadedThread = await checkpointer.LoadSessionAtCheckpointAsync(
            thread.Id,
            checkpoint1Id);

        // Assert: Should have only 2 messages
        Assert.NotNull(loadedThread);
        Assert.Equal(2, loadedThread.MessageCount);
        Assert.Equal("Message 1", loadedThread.Messages[0].Text);
        Assert.Equal("Response 1", loadedThread.Messages[1].Text);
    }

    [Fact]
    public async Task CheckpointMessageIndex_MatchesThreadMessageCount()
    {
        // Arrange: Create checkpoints at different message counts
        var checkpointer = new InMemorySessionStore();
        var thread = new AgentSession();

        // Add 2 messages
        await thread.AddMessageAsync(UserMessage("Hello"));
        await thread.AddMessageAsync(AssistantMessage("Hi!"));

        var state1 = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-1", "conv-1", "TestAgent");
        thread.ExecutionState = state1;
        var checkpoint1Id = Guid.NewGuid().ToString();
        var metadata1 = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = state1.Iteration,
            MessageIndex = thread.MessageCount
        };
        await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpoint1Id, metadata1);

        // Add 2 more messages
        await thread.AddMessageAsync(UserMessage("How are you?"));
        await thread.AddMessageAsync(AssistantMessage("I'm good!"));

        var state2 = state1.NextIteration()
            .WithMessages(await thread.GetMessagesAsync());
        thread.ExecutionState = state2;
        var checkpoint2Id = Guid.NewGuid().ToString();
        var metadata2 = new CheckpointMetadata
        {
            Source = CheckpointSource.Loop,
            Step = state2.Iteration,
            MessageIndex = thread.MessageCount
        };
        await checkpointer.SaveSessionAtCheckpointAsync(thread, checkpoint2Id, metadata2);

        // Act: Get checkpoint history
        var history = await checkpointer.GetCheckpointManifestAsync(thread.Id);

        // Assert: Check message indices
        var messageIndices = history.Select(c => c.MessageIndex).OrderBy(i => i).ToList();
        Assert.Contains(2, messageIndices);  // After first exchange
        Assert.Contains(4, messageIndices);  // After second exchange
    }
}
