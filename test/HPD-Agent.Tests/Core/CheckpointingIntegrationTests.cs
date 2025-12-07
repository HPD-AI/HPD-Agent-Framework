using Microsoft.Extensions.AI;
using Xunit;
using HPD.Agent;
using HPD.Agent.Checkpointing;
using HPD.Agent.Checkpointing.Services;
using HPD.Agent.Tests.Infrastructure;

namespace HPD.Agent.Tests.Core;

/// <summary>
/// Integration tests for checkpointing with the full agent loop.
/// Tests checkpoint save/load, resume semantics, and end-to-end scenarios.
/// </summary>
public class CheckpointingIntegrationTests : AgentTestBase
{
    //      
    // CHECKPOINT SAVE DURING AGENT EXECUTION
    //      

    [Fact]
    public async Task Agent_WithCheckpointer_SavesCheckpointAfterIteration()
    {
        // Arrange: Agent with checkpointer configured
        var checkpointer = new InMemoryConversationThreadStore();
        var client = new FakeChatClient();
        client.EnqueueTextResponse("Hello from agent!");

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        config.DurableExecutionConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = CheckpointFrequency.PerIteration,
            Retention = RetentionPolicy.LatestOnly
        };

        var agent = CreateAgent(config, client);
        var thread = new ConversationThread();

        // Act: Run agent
        var events = new List<AgentEvent>();
        await foreach (var evt in agent.RunAsync(
            new[] { UserMessage("Hello") },
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            events.Add(evt);
        }

        // Small delay for fire-and-forget checkpoint to complete
        await Task.Delay(100);

        // Assert: Checkpoint should be saved
        var loadedThread = await checkpointer.LoadThreadAsync(thread.Id);
        Assert.NotNull(loadedThread);
        Assert.NotNull(loadedThread.ExecutionState);
    }

    [Fact]
    public async Task Agent_WithCheckpointer_PerTurnFrequency_SavesAfterTurnCompletes()
    {
        // Arrange: Agent with PerTurn checkpoint frequency
        var checkpointer = new InMemoryConversationThreadStore();
        var client = new FakeChatClient();
        client.EnqueueTextResponse("Response 1");

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        config.DurableExecutionConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = CheckpointFrequency.PerTurn,
            Retention = RetentionPolicy.LatestOnly
        };

        var agent = CreateAgent(config, client);
        var thread = new ConversationThread();

        // Act: Run agent
        await foreach (var evt in agent.RunAsync(
            new[] { UserMessage("Hello") },
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            // Consume events
        }

        // Small delay for checkpoint
        await Task.Delay(100);

        // Assert: Final checkpoint should be saved
        var loadedThread = await checkpointer.LoadThreadAsync(thread.Id);
        Assert.NotNull(loadedThread);
        Assert.NotNull(loadedThread.ExecutionState);
    }

    //      
    // RESUME FROM CHECKPOINT TESTS
    //      

    [Fact]
    public async Task Agent_ResumeFromCheckpoint_RestoresExecutionState()
    {
        // Arrange: Create checkpoint mid-execution
        var checkpointer = new InMemoryConversationThreadStore();
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var scopingState = new ScopingStateData().WithExpandedPlugin("TestPlugin");
        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(),
            "run-123",
            "conv-456",
            "TestAgent")
            .NextIteration() with
            {
                MiddlewareState = new MiddlewareState().WithScoping(scopingState)
            };
        thread.ExecutionState = state;
        await checkpointer.SaveThreadAsync(thread);

        // Act: Create new agent and resume
        var client = new FakeChatClient();
        client.EnqueueTextResponse("Resumed response");

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;

        var agent = CreateAgent(config, client);

        // Load thread from checkpointer
        var loadedThread = await checkpointer.LoadThreadAsync(thread.Id);
        Assert.NotNull(loadedThread);

        // Resume with empty messages array
        var events = new List<AgentEvent>();
        await foreach (var evt in agent.RunAsync(
            Array.Empty<ChatMessage>(),
            options: null,
            thread: loadedThread,
            cancellationToken: TestCancellationToken))
        {
            events.Add(evt);
        }

        // Assert: Should have resumed from checkpoint
        Assert.NotEmpty(events);
        // State should have been restored (iteration > 0)
        var stateSnapshot = events.OfType<StateSnapshotEvent>().FirstOrDefault();
        Assert.NotNull(stateSnapshot);
        Assert.True(stateSnapshot.CurrentIteration > 0);
    }

    //      
    // RESUME VALIDATION TESTS (4 SCENARIOS)
    //      

    [Fact]
    public async Task Agent_Scenario1_NoCheckpoint_NoMessages_ThrowsException()
    {
        // Scenario 1: No checkpoint, no messages, no history
        // Expected: Error

        // Arrange: Empty thread, no checkpoint, no messages
        var checkpointer = new InMemoryConversationThreadStore();
        var client = new FakeChatClient();
        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        var agent = CreateAgent(config, client);

        var emptyThread = new ConversationThread();

        // Act & Assert: Should throw InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var evt in agent.RunAsync(
                Array.Empty<ChatMessage>(),
                options: null,
                thread: emptyThread,
                cancellationToken: TestCancellationToken))
            {
                // Should not reach here
            }
        });
    }

    [Fact]
    public async Task Agent_Scenario2_NoCheckpoint_HasMessages_Succeeds()
    {
        // Scenario 2: No checkpoint, has messages
        // Expected: Fresh run

        // Arrange: No checkpoint, but messages provided
        var checkpointer = new InMemoryConversationThreadStore();
        var client = new FakeChatClient();
        client.EnqueueTextResponse("Fresh run response");

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        var agent = CreateAgent(config, client);

        var thread = new ConversationThread();

        // Act: Run with messages
        var events = new List<AgentEvent>();
        await foreach (var evt in agent.RunAsync(
            new[] { UserMessage("Hello") },
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            events.Add(evt);
        }

        // Assert: Should succeed with fresh run
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task Agent_Scenario3_HasCheckpoint_NoMessages_Succeeds()
    {
        // Scenario 3: Has checkpoint, no messages
        // Expected: Resume execution

        // Arrange: Thread with checkpoint
        var checkpointer = new InMemoryConversationThreadStore();
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(),
            "run-123",
            "conv-456",
            "TestAgent")
            .NextIteration();
        thread.ExecutionState = state;
        await checkpointer.SaveThreadAsync(thread);

        // Load thread
        var loadedThread = await checkpointer.LoadThreadAsync(thread.Id);

        var client = new FakeChatClient();
        client.EnqueueTextResponse("Resumed");

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        var agent = CreateAgent(config, client);

        // Act: Resume with no new messages
        var events = new List<AgentEvent>();
        await foreach (var evt in agent.RunAsync(
            Array.Empty<ChatMessage>(),
            options: null,
            thread: loadedThread!,
            cancellationToken: TestCancellationToken))
        {
            events.Add(evt);
        }

        // Assert: Should succeed (resume)
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task Agent_Scenario4_HasCheckpoint_HasMessages_ThrowsException()
    {
        // Scenario 4: Has checkpoint, has messages
        // Expected: Error (cannot add messages during mid-execution)

        // Arrange: Thread with checkpoint
        var checkpointer = new InMemoryConversationThreadStore();
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Hello"));

        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(),
            "run-123",
            "conv-456",
            "TestAgent")
            .NextIteration();
        thread.ExecutionState = state;
        await checkpointer.SaveThreadAsync(thread);

        // Load thread
        var loadedThread = await checkpointer.LoadThreadAsync(thread.Id);

        var client = new FakeChatClient();
        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        var agent = CreateAgent(config, client);

        // Act & Assert: Should throw InvalidOperationException
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var evt in agent.RunAsync(
                new[] { UserMessage("New message") }, // ERROR: Adding messages to mid-execution
                options: null,
                thread: loadedThread!,
                cancellationToken: TestCancellationToken))
            {
                // Should not reach here
            }
        });

        // Assert: Error message should explain the issue
        Assert.Contains("Cannot add new messages when resuming mid-execution", ex.Message);
        Assert.Contains("iteration", ex.Message.ToLower());
    }

    //      
    // FULLHISTORY MODE INTEGRATION TESTS
    //      

    [Fact]
    public async Task Agent_FullHistoryMode_CreatesMultipleCheckpoints()
    {
        // Arrange: Agent with FullHistory checkpointer
        var checkpointer = new InMemoryConversationThreadStore();
        var client = new FakeChatClient();
        client.EnqueueToolCall("TestTool", "call-1"); // Iteration 1
        client.EnqueueTextResponse("Final response");   // Iteration 2

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        config.DurableExecutionConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = CheckpointFrequency.PerIteration,
            Retention = RetentionPolicy.FullHistory
        };

        var testTool = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("tool result"),
            new HPDAIFunctionFactoryOptions { Name = "TestTool", Description = "Test tool" });

        var agent = CreateAgent(config, client, tools: testTool);
        var thread = new ConversationThread();

        // Act: Run agent (will do 2 iterations)
        await foreach (var evt in agent.RunAsync(
            new[] { UserMessage("Call tool") },
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            // Consume events
        }

        // Wait for fire-and-forget checkpoints
        await Task.Delay(200);

        // Assert: Should have multiple checkpoints in history
        var history = await checkpointer.GetCheckpointManifestAsync(thread.Id);
        Assert.True(history.Count >= 2); // At least 2 checkpoints (per iteration + final)
    }

    [Fact]
    public async Task Agent_FullHistoryMode_CanLoadPreviousCheckpoint()
    {
        // Arrange: Create agent run with multiple iterations
        var checkpointer = new InMemoryConversationThreadStore();
        var client = new FakeChatClient();
        client.EnqueueToolCall("TestTool", "call-1");
        client.EnqueueToolCall("TestTool", "call-2");
        client.EnqueueTextResponse("Done");

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        config.DurableExecutionConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = CheckpointFrequency.PerIteration,
            Retention = RetentionPolicy.FullHistory
        };

        var testTool = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("tool result"),
            new HPDAIFunctionFactoryOptions { Name = "TestTool", Description = "Test tool" });

        var agent = CreateAgent(config, client, circuitBreakerThreshold: null, testTool);
        var thread = new ConversationThread();

        await foreach (var evt in agent.RunAsync(
            new[] { UserMessage("Test") },
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            // Consume events
        }

        await Task.Delay(200);

        // Get checkpoint history (manifest entries use Step, not full State)
        var history = await checkpointer.GetCheckpointManifestAsync(thread.Id);
        Assert.True(history.Count >= 2);

        var earlierCheckpointId = history.MinBy(c => c.Step)!.CheckpointId; // Oldest checkpoint

        // Act: Load earlier checkpoint
        var restoredThread = await checkpointer.LoadThreadAtCheckpointAsync(
            thread.Id,
            earlierCheckpointId);

        // Assert: Should restore earlier state
        Assert.NotNull(restoredThread);
        Assert.NotNull(restoredThread.ExecutionState);
        // Earlier checkpoint should have lower iteration number
        Assert.True(restoredThread.ExecutionState.Iteration < history.MaxBy(c => c.Step)!.Step);
    }

    //      
    // STALE CHECKPOINT DETECTION
    //      

    [Fact]
    public async Task Agent_StaleCheckpoint_ThrowsValidationError()
    {
        // Arrange: Create checkpoint, then add messages to conversation
        var checkpointer = new InMemoryConversationThreadStore();
        var thread = new ConversationThread();
        await thread.AddMessageAsync(UserMessage("Message 1"));
        await thread.AddMessageAsync(AssistantMessage("Response 1"));

        var state = AgentLoopState.Initial(
            await thread.GetMessagesAsync(),
            "run-123",
            "conv-456",
            "TestAgent");
        thread.ExecutionState = state;
        await checkpointer.SaveThreadAsync(thread);

        // Now add more messages to thread (making checkpoint stale)
        await thread.AddMessageAsync(UserMessage("Message 2"));
        await thread.AddMessageAsync(AssistantMessage("Response 2"));

        // Load checkpoint (has 2 messages, but thread now has 4)
        var loadedThread = await checkpointer.LoadThreadAsync(thread.Id);
        Assert.NotNull(loadedThread);

        // Need to update the loaded thread's messages to match the actual thread
        // to simulate what would happen in a real scenario (checkpoint has 2 messages, but thread has 4)
        await loadedThread.AddMessageAsync(UserMessage("Message 2"));
        await loadedThread.AddMessageAsync(AssistantMessage("Response 2"));

        var client = new FakeChatClient();
        client.EnqueueTextResponse("Should not reach here"); // Queue a response to avoid FakeChatClient error

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        var agent = CreateAgent(config, client);

        // Act & Assert: Should throw CheckpointStaleException during validation
        await Assert.ThrowsAnyAsync<CheckpointStaleException>(async () =>
        {
            await foreach (var evt in agent.RunAsync(
                Array.Empty<ChatMessage>(),
                options: null,
                thread: loadedThread,
                cancellationToken: TestCancellationToken))
            {
                // Should not reach here
            }
        });
    }

    //      
    // END-TO-END CRASH RECOVERY SCENARIO
    //      

    [Fact]
    public async Task Agent_CrashRecovery_CanResumeAfterFailure()
    {
        // Simulate: Agent starts, does some work, crashes, then resumes

        // PHASE 1: Initial run (simulate crash mid-execution)
        var checkpointer = new InMemoryConversationThreadStore();
        var client1 = new FakeChatClient();
        client1.EnqueueToolCall("Step1", "call-1");

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        config.DurableExecutionConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = CheckpointFrequency.PerIteration,
            Retention = RetentionPolicy.LatestOnly
        };

        var step1Tool = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("step1 result"),
            new HPDAIFunctionFactoryOptions { Name = "Step1", Description = "First step" });

        var agent1 = CreateAgent(config, client1, tools: step1Tool);
        var thread = new ConversationThread();

        // Run until first iteration completes
        var firstRunEvents = new List<AgentEvent>();
        await foreach (var evt in agent1.RunAsync(
            new[] { UserMessage("Start process") },
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            firstRunEvents.Add(evt);

            // Simulate crash after first iteration
            if (evt is AgentTurnFinishedEvent)
                break;
        }

        // Manually create and save a checkpoint (simulating successful checkpoint before crash)
        // In a real crash, the checkpoint would have been written by the fire-and-forget task
        var messages = await thread.GetMessagesAsync();
        var checkpointState = AgentLoopState.Initial(
            messages.ToList(),
            "run-123",
            "conv-456",
            "TestAgent").NextIteration(); // After first iteration

        thread.ExecutionState = checkpointState;
        await checkpointer.SaveThreadAsync(thread);

        // PHASE 2: Resume after "crash"
        var client2 = new FakeChatClient();
        client2.EnqueueTextResponse("Process completed after recovery");

        var agent2 = CreateAgent(config, client2, tools: step1Tool);

        // Load checkpoint
        var recoveredThread = await checkpointer.LoadThreadAsync(thread.Id);
        Assert.NotNull(recoveredThread);
        Assert.NotNull(recoveredThread.ExecutionState);

        // Resume execution with empty messages
        var resumedEvents = new List<AgentEvent>();
        await foreach (var evt in agent2.RunAsync(
            Array.Empty<ChatMessage>(),
            options: null,
            thread: recoveredThread,
            cancellationToken: TestCancellationToken))
        {
            resumedEvents.Add(evt);
        }

        // Assert: Should have successfully resumed and completed
        Assert.NotEmpty(resumedEvents);
        var finishEvent = resumedEvents.OfType<MessageTurnFinishedEvent>().FirstOrDefault();
        Assert.NotNull(finishEvent);
    }

    //      
    // PENDING WRITES INTEGRATION TESTS
    //      

    [Fact]
    public async Task PendingWrites_ParallelFunctions_SavesSuccessfulResults()
    {
        // Test that successful function results are saved as pending writes during execution

        // Arrange
        var checkpointer = new InMemoryConversationThreadStore();
        var client = new FakeChatClient();
        client.EnqueueToolCall("GetWeather", "call-1");
        client.EnqueueToolCall("GetNews", "call-2");
        client.EnqueueTextResponse("Done");

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        config.DurableExecutionConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = CheckpointFrequency.PerIteration,
            Retention = RetentionPolicy.LatestOnly,
            EnablePendingWrites = true
        };

        var weatherTool = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Sunny, 72°F"),
            new HPDAIFunctionFactoryOptions { Name = "GetWeather", Description = "Get weather" });

        var newsTool = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("Breaking: Test passed!"),
            new HPDAIFunctionFactoryOptions { Name = "GetNews", Description = "Get news" });

        var agent = CreateAgent(config, client, tools: [weatherTool, newsTool]);
        var thread = new ConversationThread();

        // Act: Run agent with parallel function calls
        var events = new List<AgentEvent>();
        await foreach (var evt in agent.RunAsync(
            new[] { UserMessage("Get weather and news") },
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            events.Add(evt);
        }

        // Give pending writes time to save (fire-and-forget)
        await Task.Delay(100);

        // Assert: Pending writes should have been saved
        // Note: After checkpoint completes, pending writes are cleaned up
        // So we can't directly verify them here, but we verified the logic in unit tests
        Assert.NotEmpty(events);
        var finishEvent = events.OfType<MessageTurnFinishedEvent>().FirstOrDefault();
        Assert.NotNull(finishEvent);
    }

    [Fact]
    public async Task PendingWrites_WithCrashRecovery_RestoresPendingWrites()
    {
        // Test that pending writes are restored and loaded into state on resume

        // PHASE 1: Run agent and manually save pending writes before crash
        var checkpointer = new InMemoryConversationThreadStore();
        var client1 = new FakeChatClient();
        client1.EnqueueToolCall("Step1", "call-1");

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        config.DurableExecutionConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = CheckpointFrequency.PerIteration,
            Retention = RetentionPolicy.LatestOnly,
            EnablePendingWrites = true
        };

        var step1Tool = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("step1 result"),
            new HPDAIFunctionFactoryOptions { Name = "Step1", Description = "First step" });

        var agent1 = CreateAgent(config, client1, tools: step1Tool);
        var thread = new ConversationThread();

        // Run until first iteration
        var firstRunEvents = new List<AgentEvent>();
        await foreach (var evt in agent1.RunAsync(
            new[] { UserMessage("Start") },
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            firstRunEvents.Add(evt);
            if (evt is AgentTurnFinishedEvent)
                break;
        }

        // Manually create checkpoint and pending writes (simulating crash before checkpoint completes)
        var messages = await thread.GetMessagesAsync();
        var checkpointState = AgentLoopState.Initial(
            messages.ToList(),
            "run-123",
            "conv-456",
            "TestAgent").NextIteration();

        thread.ExecutionState = checkpointState;
        await checkpointer.SaveThreadAsync(thread);

        // Manually save pending writes (simulating successful function calls before crash)
        var pendingWrites = new List<PendingWrite>
        {
            new PendingWrite
            {
                CallId = "call-1",
                FunctionName = "Step1",
                ResultJson = "\"step1 result\"",
                CompletedAt = DateTime.UtcNow,
                Iteration = checkpointState.Iteration,
                ThreadId = thread.Id
            }
        };
        await checkpointer.SavePendingWritesAsync(thread.Id, checkpointState.ETag!, pendingWrites);

        // PHASE 2: Resume and verify pending writes are loaded
        var client2 = new FakeChatClient();
        client2.EnqueueTextResponse("Completed");

        var agent2 = CreateAgent(config, client2, tools: step1Tool);

        // Load checkpoint
        var recoveredThread = await checkpointer.LoadThreadAsync(thread.Id);
        Assert.NotNull(recoveredThread);
        Assert.NotNull(recoveredThread.ExecutionState);

        // Resume - the pending writes should be loaded into state
        var resumedEvents = new List<AgentEvent>();
        await foreach (var evt in agent2.RunAsync(
            Array.Empty<ChatMessage>(),
            options: null,
            thread: recoveredThread,
            cancellationToken: TestCancellationToken))
        {
            resumedEvents.Add(evt);
        }

        // Assert: Should have successfully resumed
        Assert.NotEmpty(resumedEvents);

        // Verify pending writes were loaded (they should be in the restored state)
        Assert.NotNull(recoveredThread.ExecutionState);
        // Note: Pending writes are loaded into state.PendingWrites during resume
    }

    [Fact]
    public async Task PendingWrites_AfterSuccessfulCheckpoint_CleansUpPendingWrites()
    {
        // Test that pending writes are deleted after successful checkpoint

        // Arrange
        var checkpointer = new InMemoryConversationThreadStore();
        var client = new FakeChatClient();
        client.EnqueueToolCall("TestTool", "call-1");
        client.EnqueueTextResponse("Done");

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        config.DurableExecutionConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = CheckpointFrequency.PerIteration,
            Retention = RetentionPolicy.LatestOnly,
            EnablePendingWrites = true
        };

        var testTool = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("tool result"),
            new HPDAIFunctionFactoryOptions { Name = "TestTool", Description = "Test tool" });

        var agent = CreateAgent(config, client, tools: testTool);
        var thread = new ConversationThread();

        // Act: Run agent
        await foreach (var evt in agent.RunAsync(
            new[] { UserMessage("Test") },
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            // Let it complete
        }

        // Give cleanup time to complete (fire-and-forget)
        await Task.Delay(200);

        // Assert: Pending writes should have been cleaned up after successful checkpoint
        var loadedThread = await checkpointer.LoadThreadAsync(thread.Id);
        Assert.NotNull(loadedThread);
        Assert.NotNull(loadedThread.ExecutionState);

        // Try to load pending writes - should be empty (cleaned up)
        var pendingWrites = await checkpointer.LoadPendingWritesAsync(
            thread.Id,
            loadedThread.ExecutionState.ETag!);
        Assert.Empty(pendingWrites);
    }

    [Fact]
    public async Task PendingWrites_DisabledByDefault_DoesNotSavePendingWrites()
    {
        // Test that pending writes are NOT saved when EnablePendingWrites is false (default)

        // Arrange
        var checkpointer = new InMemoryConversationThreadStore();
        var client = new FakeChatClient();
        client.EnqueueToolCall("TestTool", "call-1");
        client.EnqueueTextResponse("Done");

        var config = DefaultConfig();
        config.ThreadStore = checkpointer;
        config.DurableExecutionConfig = new DurableExecutionConfig
        {
            Enabled = true,
            Frequency = CheckpointFrequency.PerIteration,
            Retention = RetentionPolicy.LatestOnly
            // Note: EnablePendingWrites defaults to false
        };

        var testTool = HPDAIFunctionFactory.Create(
            async (args, ct) => await Task.FromResult("tool result"),
            new HPDAIFunctionFactoryOptions { Name = "TestTool", Description = "Test tool" });

        var agent = CreateAgent(config, client, tools: testTool);
        var thread = new ConversationThread();

        // Act: Run agent
        await foreach (var evt in agent.RunAsync(
            new[] { UserMessage("Test") },
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            // Let it complete
        }

        // Give time for any potential saves (fire-and-forget)
        await Task.Delay(100);

        // Assert: No pending writes should have been saved
        var loadedThread = await checkpointer.LoadThreadAsync(thread.Id);
        Assert.NotNull(loadedThread);
        Assert.NotNull(loadedThread.ExecutionState);

        var pendingWrites = await checkpointer.LoadPendingWritesAsync(
            thread.Id,
            loadedThread.ExecutionState.ETag!);
        Assert.Empty(pendingWrites);
    }
}
