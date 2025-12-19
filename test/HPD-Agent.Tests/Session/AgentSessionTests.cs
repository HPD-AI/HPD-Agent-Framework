using Microsoft.Extensions.AI;
using Xunit;
using HPD.Agent;
using HPD.Agent;
using HPD.Agent.Tests.Infrastructure;

namespace HPD.Agent.Tests.Session;

/// <summary>
/// Tests for AgentSession, SessionSnapshot, and ExecutionCheckpoint types.
/// Covers message operations, metadata, checkpoint conversion, and serialization.
/// </summary>
public class AgentSessionTests : AgentTestBase
{
    //──────────────────────────────────────────────────────────────────
    // AGENTSESSION - CONSTRUCTION
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void AgentSession_DefaultConstructor_GeneratesId()
    {
        // Arrange & Act
        var session = new AgentSession();

        // Assert
        Assert.NotNull(session.Id);
        Assert.NotEmpty(session.Id);
        Assert.True(Guid.TryParse(session.Id, out _));
    }

    [Fact]
    public void AgentSession_WithId_UsesProvidedId()
    {
        // Arrange & Act
        var session = new AgentSession("custom-session-id");

        // Assert
        Assert.Equal("custom-session-id", session.Id);
    }

    [Fact]
    public void AgentSession_WithId_ThrowsOnNullOrWhitespace()
    {
        // Arrange & Act & Assert
        // ThrowIfNullOrWhiteSpace throws ArgumentNullException for null, ArgumentException for empty/whitespace
        Assert.Throws<ArgumentNullException>(() => new AgentSession(null!));
        Assert.Throws<ArgumentException>(() => new AgentSession(""));
        Assert.Throws<ArgumentException>(() => new AgentSession("   "));
    }

    [Fact]
    public void AgentSession_CreatedAt_SetToNow()
    {
        // Arrange
        var before = DateTime.UtcNow;

        // Act
        var session = new AgentSession();

        // Assert
        var after = DateTime.UtcNow;
        Assert.InRange(session.CreatedAt, before, after);
    }

    //──────────────────────────────────────────────────────────────────
    // AGENTSESSION - MESSAGE OPERATIONS
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void AgentSession_AddMessage_AddsToCollection()
    {
        // Arrange
        var session = new AgentSession();

        // Act
        session.AddMessage(UserMessage("Hello"));
        session.AddMessage(AssistantMessage("Hi!"));

        // Assert
        Assert.Equal(2, session.MessageCount);
        Assert.Equal("Hello", session.Messages[0].Text);
        Assert.Equal("Hi!", session.Messages[1].Text);
    }

    [Fact]
    public void AgentSession_AddMessages_AddsMultiple()
    {
        // Arrange
        var session = new AgentSession();
        var messages = new List<ChatMessage>
        {
            UserMessage("One"),
            AssistantMessage("Two"),
            UserMessage("Three")
        };

        // Act
        session.AddMessages(messages);

        // Assert
        Assert.Equal(3, session.MessageCount);
    }

    [Fact]
    public async Task AgentSession_AddMessageAsync_AddsToCollection()
    {
        // Arrange
        var session = new AgentSession();

        // Act
        await session.AddMessageAsync(UserMessage("Async message"));

        // Assert
        Assert.Single(session.Messages);
        Assert.Equal("Async message", session.Messages[0].Text);
    }

    [Fact]
    public async Task AgentSession_GetMessagesAsync_ReturnsAllMessages()
    {
        // Arrange
        var session = new AgentSession();
        session.AddMessage(UserMessage("One"));
        session.AddMessage(AssistantMessage("Two"));

        // Act
        var messages = await session.GetMessagesAsync();

        // Assert
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void AgentSession_Clear_RemovesAllMessages()
    {
        // Arrange
        var session = new AgentSession();
        session.AddMessage(UserMessage("Hello"));
        session.AddMetadata("key", "value");

        // Act
        session.Clear();

        // Assert
        Assert.Empty(session.Messages);
        Assert.Empty(session.Metadata);
    }

    [Fact]
    public void AgentSession_AddMessage_UpdatesLastActivity()
    {
        // Arrange
        var session = new AgentSession();
        var initialActivity = session.LastActivity;
        System.Threading.Thread.Sleep(10);

        // Act
        session.AddMessage(UserMessage("Hello"));

        // Assert
        Assert.True(session.LastActivity > initialActivity);
    }

    //──────────────────────────────────────────────────────────────────
    // AGENTSESSION - METADATA
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void AgentSession_AddMetadata_StoresValue()
    {
        // Arrange
        var session = new AgentSession();

        // Act
        session.AddMetadata("customKey", "customValue");

        // Assert
        Assert.Equal("customValue", session.Metadata["customKey"]);
    }

    [Fact]
    public void AgentSession_DisplayName_GetSet()
    {
        // Arrange
        var session = new AgentSession();

        // Act
        session.DisplayName = "My Custom Name";

        // Assert
        Assert.Equal("My Custom Name", session.DisplayName);
    }

    [Fact]
    public void AgentSession_GetDisplayName_FromFirstUserMessage()
    {
        // Arrange
        var session = new AgentSession();
        session.AddMessage(UserMessage("Hello, how are you today?"));

        // Act
        var displayName = session.GetDisplayName(maxLength: 15);

        // Assert
        Assert.Equal("Hello, how a...", displayName);
    }

    [Fact]
    public void AgentSession_GetDisplayName_FallsBackToDefault()
    {
        // Arrange
        var session = new AgentSession();

        // Act
        var displayName = session.GetDisplayName();

        // Assert
        Assert.Equal("New Conversation", displayName);
    }

    [Fact]
    public void AgentSession_GetDisplayName_PreferExplicitName()
    {
        // Arrange
        var session = new AgentSession();
        session.AddMessage(UserMessage("Some user message"));
        session.DisplayName = "Custom Name";

        // Act
        var displayName = session.GetDisplayName();

        // Assert
        Assert.Equal("Custom Name", displayName);
    }

    //──────────────────────────────────────────────────────────────────
    // AGENTSESSION - SERVICE THREAD ID
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void AgentSession_ServiceThreadId_GetSet()
    {
        // Arrange
        var session = new AgentSession();

        // Act
        session.ServiceThreadId = "thread_abc123";

        // Assert
        Assert.Equal("thread_abc123", session.ServiceThreadId);
    }

    [Fact]
    public void AgentSession_ServiceThreadId_NullOrWhitespace_SetsNull()
    {
        // Arrange
        var session = new AgentSession();
        session.ServiceThreadId = "thread_abc123";

        // Act
        session.ServiceThreadId = "";

        // Assert
        Assert.Null(session.ServiceThreadId);
    }

    //──────────────────────────────────────────────────────────────────
    // AGENTSESSION - EXECUTION STATE
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void AgentSession_ExecutionState_GetSet()
    {
        // Arrange
        var session = new AgentSession();
        var state = AgentLoopState.Initial(
            new List<ChatMessage>(), "run-123", "conv-456", "TestAgent");

        // Act
        session.ExecutionState = state;

        // Assert
        Assert.NotNull(session.ExecutionState);
        Assert.Equal("run-123", session.ExecutionState.RunId);
    }

    //──────────────────────────────────────────────────────────────────
    // AGENTSESSION - EXECUTION CHECKPOINT CONVERSION (NEW API)
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void AgentSession_ToExecutionCheckpoint_CreatesCheckpoint()
    {
        // Arrange
        var session = new AgentSession("session-123");
        session.AddMessage(UserMessage("Hello"));
        session.AddMetadata("key", "value");
        session.ExecutionState = AgentLoopState.Initial(
            session.Messages.ToList(), "run-123", "conv-456", "TestAgent");

        // Act
        var checkpoint = session.ToExecutionCheckpoint();

        // Assert
        Assert.Equal("session-123", checkpoint.SessionId);
        Assert.NotNull(checkpoint.ExecutionCheckpointId);
        Assert.NotNull(checkpoint.ExecutionState);
        Assert.Equal("run-123", checkpoint.ExecutionState.RunId);
        // Messages are inside ExecutionState.CurrentMessages (no duplication)
        Assert.Single(checkpoint.ExecutionState.CurrentMessages);
    }

    [Fact]
    public void AgentSession_ToExecutionCheckpoint_ThrowsWithoutExecutionState()
    {
        // Arrange
        var session = new AgentSession();
        session.AddMessage(UserMessage("Hello"));
        // Note: No ExecutionState set

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => session.ToExecutionCheckpoint());
    }

    [Fact]
    public void AgentSession_ToExecutionCheckpoint_WithCustomId()
    {
        // Arrange
        var session = new AgentSession("session-123");
        session.ExecutionState = AgentLoopState.Initial(
            new List<ChatMessage>(), "run-123", "conv-456", "TestAgent");

        // Act
        var checkpoint = session.ToExecutionCheckpoint("custom-checkpoint-id");

        // Assert
        Assert.Equal("custom-checkpoint-id", checkpoint.ExecutionCheckpointId);
    }

    [Fact]
    public void AgentSession_FromExecutionCheckpoint_RestoresSession()
    {
        // Arrange
        var original = new AgentSession("original-session");
        original.AddMessage(UserMessage("Test message"));
        original.ExecutionState = AgentLoopState.Initial(
            original.Messages.ToList(), "run-123", "conv-456", "TestAgent");

        var checkpoint = original.ToExecutionCheckpoint();

        // Act
        var restored = AgentSession.FromExecutionCheckpoint(checkpoint);

        // Assert
        Assert.Equal("original-session", restored.Id);
        Assert.Single(restored.Messages); // Restored from ExecutionState.CurrentMessages
        Assert.NotNull(restored.ExecutionState);
        Assert.Equal("run-123", restored.ExecutionState.RunId);
    }

    //──────────────────────────────────────────────────────────────────
    // AGENTSESSION - SNAPSHOT CONVERSION
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void AgentSession_ToSnapshot_CreatesSnapshot()
    {
        // Arrange
        var session = new AgentSession("session-123");
        session.AddMessage(UserMessage("Hello"));
        session.AddMetadata("key", "value");

        // Act
        var snapshot = session.ToSnapshot();

        // Assert
        Assert.Equal("session-123", snapshot.SessionId);
        Assert.Single(snapshot.Messages);
        Assert.Equal("value", snapshot.Metadata["key"]);
    }

    [Fact]
    public void AgentSession_ToSnapshot_WorksWithoutExecutionState()
    {
        // Arrange
        var session = new AgentSession();
        session.AddMessage(UserMessage("Hello"));
        // Note: No ExecutionState set - this is the normal case after turn completes

        // Act
        var snapshot = session.ToSnapshot();

        // Assert
        Assert.NotNull(snapshot);
        Assert.Single(snapshot.Messages);
    }

    [Fact]
    public void AgentSession_FromSnapshot_RestoresSession()
    {
        // Arrange
        var original = new AgentSession("original-session");
        original.AddMessage(UserMessage("Test message"));
        original.AddMetadata("testKey", "testValue");
        original.ServiceThreadId = "thread_123";
        original.ConversationId = "conv_456";

        var snapshot = original.ToSnapshot();

        // Act
        var restored = AgentSession.FromSnapshot(snapshot);

        // Assert
        Assert.Equal("original-session", restored.Id);
        Assert.Single(restored.Messages);
        Assert.Equal("testValue", restored.Metadata["testKey"]);
        Assert.Equal("thread_123", restored.ServiceThreadId);
        Assert.Equal("conv_456", restored.ConversationId);
        Assert.Null(restored.ExecutionState); // Snapshots don't include ExecutionState
    }

    //──────────────────────────────────────────────────────────────────
    // AGENTSESSION - LEGACY CHECKPOINT CONVERSION (DEPRECATED)
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void AgentSession_ToCheckpoint_Legacy_StillWorks()
    {
        // Arrange
        var session = new AgentSession("session-123");
        session.AddMessage(UserMessage("Hello"));
        session.AddMetadata("key", "value");
        session.ExecutionState = AgentLoopState.Initial(
            session.Messages.ToList(), "run-123", "conv-456", "TestAgent");

        // Act
        #pragma warning disable CS0618 // Suppress obsolete warning for testing
        var checkpoint = session.ToCheckpoint();
        #pragma warning restore CS0618

        // Assert
        Assert.Equal("session-123", checkpoint.SessionId);
        Assert.Single(checkpoint.Messages);
        Assert.Equal("value", checkpoint.Metadata["key"]);
        Assert.NotNull(checkpoint.ExecutionState);
    }

    [Fact]
    public void AgentSession_FromCheckpoint_Legacy_RestoresSession()
    {
        // Arrange
        var original = new AgentSession("original-session");
        original.AddMessage(UserMessage("Test message"));
        original.AddMetadata("testKey", "testValue");
        original.ServiceThreadId = "thread_123";
        original.ConversationId = "conv_456";
        original.ExecutionState = AgentLoopState.Initial(
            original.Messages.ToList(), "run-123", "conv-456", "TestAgent");

        #pragma warning disable CS0618 // Suppress obsolete warning for testing
        var checkpoint = original.ToCheckpoint();

        // Act
        var restored = AgentSession.FromCheckpoint(checkpoint);
        #pragma warning restore CS0618

        // Assert
        Assert.Equal("original-session", restored.Id);
        Assert.Single(restored.Messages);
        Assert.NotNull(restored.ExecutionState);
    }

    //──────────────────────────────────────────────────────────────────
    // EXECUTIONCHECKPOINT
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExecutionCheckpoint_RequiredProperties_AreSet()
    {
        // Arrange & Act
        var checkpoint = new ExecutionCheckpoint
        {
            SessionId = "session-123",
            ExecutionCheckpointId = "checkpoint-456",
            ExecutionState = AgentLoopState.Initial(
                new List<ChatMessage> { UserMessage("Hello") }, "run-123", "conv-456", "TestAgent"),
            CreatedAt = DateTime.UtcNow
        };

        // Assert
        Assert.Equal("session-123", checkpoint.SessionId);
        Assert.Equal("checkpoint-456", checkpoint.ExecutionCheckpointId);
        Assert.Single(checkpoint.ExecutionState.CurrentMessages); // Messages inside ExecutionState
    }

    [Fact]
    public void ExecutionCheckpoint_Version_HasDefaultValue()
    {
        // Arrange & Act
        var checkpoint = new ExecutionCheckpoint
        {
            SessionId = "session-123",
            ExecutionCheckpointId = "checkpoint-456",
            ExecutionState = AgentLoopState.Initial(
                new List<ChatMessage>(), "run-123", "conv-456", "TestAgent"),
            CreatedAt = DateTime.UtcNow
        };

        // Assert
        Assert.Equal(1, checkpoint.Version);
    }

    [Fact]
    public void ExecutionCheckpoint_NoMessageDuplication()
    {
        // Arrange
        var messages = new List<ChatMessage> { UserMessage("Hello"), AssistantMessage("Hi!") };
        var state = AgentLoopState.Initial(messages, "run-123", "conv-456", "TestAgent");

        // Act
        var checkpoint = new ExecutionCheckpoint
        {
            SessionId = "session-123",
            ExecutionCheckpointId = "checkpoint-456",
            ExecutionState = state,
            CreatedAt = DateTime.UtcNow
        };

        // Assert: Messages exist ONLY in ExecutionState.CurrentMessages
        // There's no separate Messages property on ExecutionCheckpoint
        Assert.Equal(2, checkpoint.ExecutionState.CurrentMessages.Count);
    }

}
