using Microsoft.Extensions.AI;
using Xunit;
using HPD.Agent;
using HPD.Agent.Conversation;
using HPD.Agent.Conversation.Checkpointing;
using HPD_Agent.Tests.Infrastructure;
using System.Text.Json;

namespace HPD_Agent.Tests.Core;

/// <summary>
/// Comprehensive tests for thread-scoped durable execution (checkpointing).
/// Tests serialization, persistence, resume semantics, and checkpoint retention modes.
/// </summary>
public class CheckpointingTests : AgentTestBase
{
    // ═══════════════════════════════════════════════════════
    // AGENTLOOPSTATE SERIALIZATION TESTS
    // ═══════════════════════════════════════════════════════

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
            .NextIteration()
            .WithExpandedPlugin("TestPlugin");

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
            .NextIteration()
            .WithExpandedPlugin("PluginA")
            .WithExpandedPlugin("PluginB")
            .WithFailure();

        var json = originalState.Serialize();

        // Act: Deserialize
        var restoredState = AgentLoopState.Deserialize(json);

        // Assert: All properties should match
        Assert.Equal(originalState.Iteration, restoredState.Iteration);
        Assert.Equal(originalState.ConsecutiveFailures, restoredState.ConsecutiveFailures);
        Assert.Equal(originalState.RunId, restoredState.RunId);
        Assert.Equal(originalState.ConversationId, restoredState.ConversationId);
        Assert.Equal(originalState.AgentName, restoredState.AgentName);
        Assert.Equal(originalState.CurrentMessages.Count, restoredState.CurrentMessages.Count);
        Assert.Equal(originalState.expandedScopedPluginContainers.Count, restoredState.expandedScopedPluginContainers.Count);
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
            "ExpandedPlugins": [],
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

    // ═══════════════════════════════════════════════════════
    // INMEMORYTHREADCHECKPOINTER TESTS - LATESTONLY MODE
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task InMemoryCheckpointer_LatestOnly_SaveAndLoad_RoundTrip()
    {
        // Arrange: Checkpointer in LatestOnly mode
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(),
            "run-123",
            "conv-456",
            "TestAgent");
        thread.ExecutionState = state;

        // Act: Save and load
        await checkpointer.SaveThreadAsync(thread);
        var loadedThread = await checkpointer.LoadThreadAsync(thread.Id);

        // Assert: Should restore thread with execution state
        Assert.NotNull(loadedThread);
        Assert.NotNull(loadedThread.ExecutionState);
        Assert.Equal(state.Iteration, loadedThread.ExecutionState.Iteration);
        Assert.Equal(state.AgentName, loadedThread.ExecutionState.AgentName);
    }

    [Fact]
    public async Task InMemoryCheckpointer_LatestOnly_Upsert_OverwritesPrevious()
    {
        // Arrange: Checkpointer with existing checkpoint
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state1 = AgentLoopState.Initial(
            await thread.GetMessagesAsync(),
            "run-123",
            "conv-456",
            "TestAgent");
        thread.ExecutionState = state1;
        await checkpointer.SaveThreadAsync(thread);

        // Act: Save again with updated state (iteration 2)
        var state2 = state1.NextIteration();
        thread.ExecutionState = state2;
        await checkpointer.SaveThreadAsync(thread);

        // Load
        var loadedThread = await checkpointer.LoadThreadAsync(thread.Id);

        // Assert: Should have latest state (iteration 2)
        Assert.NotNull(loadedThread);
        Assert.NotNull(loadedThread.ExecutionState);
        Assert.Equal(1, loadedThread.ExecutionState.Iteration); // NextIteration makes it 1
    }

    [Fact]
    public async Task InMemoryCheckpointer_LatestOnly_LoadNonExistent_ReturnsNull()
    {
        // Arrange: Empty checkpointer
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);

        // Act: Try to load non-existent thread
        var result = await checkpointer.LoadThreadAsync("non-existent-thread-id");

        // Assert: Should return null
        Assert.Null(result);
    }

    [Fact]
    public async Task InMemoryCheckpointer_LatestOnly_DeleteThread_RemovesCheckpoint()
    {
        // Arrange: Checkpointer with saved thread
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));
        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(),
            "run-123",
            "conv-456",
            "TestAgent");
        thread.ExecutionState = state;
        await checkpointer.SaveThreadAsync(thread);

        // Act: Delete thread
        await checkpointer.DeleteThreadAsync(thread.Id);

        // Assert: Should no longer exist
        var loadedThread = await checkpointer.LoadThreadAsync(thread.Id);
        Assert.Null(loadedThread);
    }

    [Fact]
    public async Task InMemoryCheckpointer_LatestOnly_ListThreadIds_ReturnsAllThreads()
    {
        // Arrange: Checkpointer with multiple threads
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);

        var thread1 = new ConversationThread();
        await thread1.AddMessageAsync(UserMessage("Hello"));
        thread1.ExecutionState = AgentLoopState.Initial(
            await thread1.GetMessagesAsync(), "run-1", "conv-1", "Agent1");
        await checkpointer.SaveThreadAsync(thread1);

        var thread2 = new ConversationThread();
        await thread2.AddMessageAsync(UserMessage("World"));
        thread2.ExecutionState = AgentLoopState.Initial(
            await thread2.GetMessagesAsync(), "run-2", "conv-2", "Agent2");
        await checkpointer.SaveThreadAsync(thread2);

        // Act: List all thread IDs
        var threadIds = await checkpointer.ListThreadIdsAsync();

        // Assert: Should contain both threads
        Assert.Equal(2, threadIds.Count);
        Assert.Contains(thread1.Id, threadIds);
        Assert.Contains(thread2.Id, threadIds);
    }

    // ═══════════════════════════════════════════════════════
    // INMEMORYTHREADCHECKPOINTER TESTS - FULLHISTORY MODE
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task InMemoryCheckpointer_FullHistory_SaveMultiple_PreservesHistory()
    {
        // Arrange: Checkpointer in FullHistory mode
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.FullHistory);
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state1 = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent");
        thread.ExecutionState = state1;
        await checkpointer.SaveThreadAsync(thread);

        // Act: Save again with different iteration
        var state2 = state1.NextIteration();
        thread.ExecutionState = state2;
        await checkpointer.SaveThreadAsync(thread);

        var state3 = state2.NextIteration();
        thread.ExecutionState = state3;
        await checkpointer.SaveThreadAsync(thread);

        // Assert: Should have 3 checkpoints in history
        var history = await checkpointer.GetCheckpointHistoryAsync(thread.Id);
        Assert.Equal(3, history.Count);
        Assert.Equal(2, history[0].State.Iteration); // Newest first
        Assert.Equal(1, history[1].State.Iteration);
        Assert.Equal(0, history[2].State.Iteration);
    }

    [Fact]
    public async Task InMemoryCheckpointer_FullHistory_LoadThreadAtCheckpoint_RestoresSpecificCheckpoint()
    {
        // Arrange: Checkpointer with multiple checkpoints
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.FullHistory);
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state1 = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent");
        thread.ExecutionState = state1;
        await checkpointer.SaveThreadAsync(thread);

        var state2 = state1.NextIteration().WithExpandedPlugin("PluginA");
        thread.ExecutionState = state2;
        await checkpointer.SaveThreadAsync(thread);

        // Get the checkpoint ID of the first checkpoint
        var history = await checkpointer.GetCheckpointHistoryAsync(thread.Id);
        var firstCheckpointId = history[1].CheckpointId; // Second in list (newest first)

        // Act: Load specific checkpoint
        var loadedThread = await checkpointer.LoadThreadAtCheckpointAsync(thread.Id, firstCheckpointId);

        // Assert: Should restore first checkpoint state
        Assert.NotNull(loadedThread);
        Assert.NotNull(loadedThread.ExecutionState);
        Assert.Equal(0, loadedThread.ExecutionState.Iteration);
        Assert.Empty(loadedThread.ExecutionState.expandedScopedPluginContainers);
    }

    [Fact]
    public async Task InMemoryCheckpointer_FullHistory_PruneCheckpoints_KeepsLatestN()
    {
        // Arrange: Checkpointer with 5 checkpoints
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.FullHistory);
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent");

        for (int i = 0; i < 5; i++)
        {
            thread.ExecutionState = state;
            await checkpointer.SaveThreadAsync(thread);
            state = state.NextIteration();
        }

        // Act: Prune to keep only 3 latest checkpoints
        await checkpointer.PruneCheckpointsAsync(thread.Id, keepLatest: 3);

        // Assert: Should have only 3 checkpoints
        var history = await checkpointer.GetCheckpointHistoryAsync(thread.Id);
        Assert.Equal(3, history.Count);
        Assert.Equal(4, history[0].State.Iteration); // Newest
        Assert.Equal(3, history[1].State.Iteration);
        Assert.Equal(2, history[2].State.Iteration);
    }

    [Fact]
    public async Task InMemoryCheckpointer_FullHistory_GetCheckpointHistory_WithLimit_ReturnsLimitedResults()
    {
        // Arrange: Checkpointer with 10 checkpoints
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.FullHistory);
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent");

        for (int i = 0; i < 10; i++)
        {
            thread.ExecutionState = state;
            await checkpointer.SaveThreadAsync(thread);
            state = state.NextIteration();
        }

        // Act: Get only 5 checkpoints
        var history = await checkpointer.GetCheckpointHistoryAsync(thread.Id, limit: 5);

        // Assert: Should return only 5 (newest)
        Assert.Equal(5, history.Count);
        Assert.Equal(9, history[0].State.Iteration);
        Assert.Equal(5, history[4].State.Iteration);
    }

    [Fact]
    public async Task InMemoryCheckpointer_FullHistory_GetCheckpointHistory_WithBefore_FiltersOlderCheckpoints()
    {
        // Arrange: Checkpointer with checkpoints at different times
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.FullHistory);
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var now = DateTime.UtcNow;
        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent");

        // Save 3 checkpoints
        for (int i = 0; i < 3; i++)
        {
            thread.ExecutionState = state;
            await checkpointer.SaveThreadAsync(thread);
            state = state.NextIteration();
            await Task.Delay(10); // Small delay to ensure different timestamps
        }

        var cutoffTime = DateTime.UtcNow;
        await Task.Delay(10);

        // Save 2 more checkpoints after cutoff
        for (int i = 0; i < 2; i++)
        {
            thread.ExecutionState = state;
            await checkpointer.SaveThreadAsync(thread);
            state = state.NextIteration();
        }

        // Act: Get checkpoints before cutoff
        var history = await checkpointer.GetCheckpointHistoryAsync(thread.Id, before: cutoffTime);

        // Assert: Should return only checkpoints created before cutoff (3 checkpoints)
        Assert.True(history.Count >= 3);
        Assert.All(history, cp => Assert.True(cp.CreatedAt < cutoffTime));
    }

    // ═══════════════════════════════════════════════════════
    // CONVERSATIONTHREAD SERIALIZATION WITH EXECUTIONSTATE
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task ConversationThread_Serialize_IncludesExecutionState()
    {
        // Arrange: Thread with execution state
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent")
            .NextIteration();
        thread.ExecutionState = state;

        // Act: Serialize
        var snapshot = thread.Serialize(null);

        // Assert: Should include ExecutionStateJson
        var snapshotJson = JsonSerializer.Serialize(snapshot);
        var doc = JsonDocument.Parse(snapshotJson);
        Assert.True(doc.RootElement.TryGetProperty("ExecutionStateJson", out var stateJson));
        Assert.NotEqual(JsonValueKind.Null, stateJson.ValueKind);
    }

    [Fact]
    public async Task ConversationThread_Deserialize_RestoresExecutionState()
    {
        // Arrange: Thread with execution state
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));
        await thread.AddMessageAsync(AssistantMessage("Hi"));

        var originalState = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "TestAgent")
            .NextIteration()
            .WithExpandedPlugin("TestPlugin");
        thread.ExecutionState = originalState;

        // Serialize
        var snapshot = thread.Serialize(null);
        var snapshotJson = JsonSerializer.Serialize(snapshot);
        var snapshotDeserialized = JsonSerializer.Deserialize<ConversationThreadSnapshot>(snapshotJson);

        // Act: Deserialize
        var restoredThread = ConversationThread.Deserialize(snapshotDeserialized!, null);

        // Assert: Should restore execution state
        Assert.NotNull(restoredThread.ExecutionState);
        Assert.Equal(originalState.Iteration, restoredThread.ExecutionState.Iteration);
        Assert.Equal(originalState.AgentName, restoredThread.ExecutionState.AgentName);
        Assert.Equal(originalState.expandedScopedPluginContainers.Count, restoredThread.ExecutionState.expandedScopedPluginContainers.Count);
    }

    // ═══════════════════════════════════════════════════════
    // CHECKPOINT METADATA TESTS
    // ═══════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════
    // CHECKPOINT CLEANUP TESTS
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task InMemoryCheckpointer_DeleteOlderThan_RemovesOldCheckpoints()
    {
        // Arrange: Checkpointer with old and new checkpoints
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);

        var oldThread = new ConversationThread();
        await oldThread.AddMessageAsync(UserMessage("Old"));
        oldThread.ExecutionState = AgentLoopState.Initial(
            await oldThread.GetMessagesAsync(), "run-old", "conv-old", "Agent");
        await checkpointer.SaveThreadAsync(oldThread);

        // Wait a bit
        await Task.Delay(50);
        var cutoff = DateTime.UtcNow;
        await Task.Delay(50);

        var newThread = new ConversationThread();
        await newThread.AddMessageAsync(UserMessage("New"));
        newThread.ExecutionState = AgentLoopState.Initial(
            await newThread.GetMessagesAsync(), "run-new", "conv-new", "Agent");
        await checkpointer.SaveThreadAsync(newThread);

        // Act: Delete checkpoints older than cutoff
        await checkpointer.DeleteOlderThanAsync(cutoff);

        // Assert: Old thread should be deleted, new thread should remain
        var oldLoaded = await checkpointer.LoadThreadAsync(oldThread.Id);
        var newLoaded = await checkpointer.LoadThreadAsync(newThread.Id);

        Assert.Null(oldLoaded);
        Assert.NotNull(newLoaded);
    }

    [Fact]
    public async Task InMemoryCheckpointer_DeleteInactiveThreads_DryRun_DoesNotDelete()
    {
        // Arrange: Checkpointer with inactive thread
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));
        thread.ExecutionState = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "Agent");
        await checkpointer.SaveThreadAsync(thread);

        await Task.Delay(50); // Make it "old"

        // Act: Dry run deletion
        var count = await checkpointer.DeleteInactiveThreadsAsync(
            TimeSpan.FromMilliseconds(10),
            dryRun: true);

        // Assert: Should report 1 thread but not actually delete it
        Assert.Equal(1, count);
        var loaded = await checkpointer.LoadThreadAsync(thread.Id);
        Assert.NotNull(loaded); // Should still exist
    }

    [Fact]
    public async Task InMemoryCheckpointer_DeleteInactiveThreads_ActualDelete_RemovesThreads()
    {
        // Arrange: Checkpointer with inactive thread
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));
        thread.ExecutionState = AgentLoopState.Initial(
            await thread.GetMessagesAsync(), "run-123", "conv-456", "Agent");
        await checkpointer.SaveThreadAsync(thread);

        await Task.Delay(50); // Make it "old"

        // Act: Actual deletion
        var count = await checkpointer.DeleteInactiveThreadsAsync(
            TimeSpan.FromMilliseconds(10),
            dryRun: false);

        // Assert: Should delete the thread
        Assert.Equal(1, count);
        var loaded = await checkpointer.LoadThreadAsync(thread.Id);
        Assert.Null(loaded); // Should be deleted
    }

    // ═══════════════════════════════════════════════════════
    // PENDING WRITES TESTS
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task PendingWrites_SaveAndLoad_RoundTrip()
    {
        // Arrange: Checkpointer and pending writes
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);
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
                ThreadId = threadId
            },
            new PendingWrite
            {
                CallId = "call-2",
                FunctionName = "GetNews",
                ResultJson = "{\"headline\": \"Breaking news\"}",
                CompletedAt = DateTime.UtcNow,
                Iteration = 1,
                ThreadId = threadId
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
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);

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
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);
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
                ThreadId = threadId
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
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);
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
                ThreadId = threadId
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
                ThreadId = threadId
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
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);
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
                ThreadId = threadId
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
                ThreadId = threadId
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
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);
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
                ThreadId = thread1
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
                ThreadId = thread2
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
        Assert.Equal(thread1, loaded1[0].ThreadId);

        Assert.Single(loaded2);
        Assert.Equal("call-2", loaded2[0].CallId);
        Assert.Equal(thread2, loaded2[0].ThreadId);
    }

    [Fact]
    public async Task PendingWrites_LoadReturnsCopy_ModificationsDoNotAffectStorage()
    {
        // Arrange: Checkpointer with saved pending writes
        var checkpointer = new InMemoryThreadCheckpointer(CheckpointRetentionMode.LatestOnly);
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
                ThreadId = threadId
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
}
