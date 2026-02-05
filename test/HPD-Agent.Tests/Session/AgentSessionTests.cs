using Microsoft.Extensions.AI;
using Xunit;
using HPD.Agent;
using HPD.Agent;
using HPD.Agent.Tests.Infrastructure;

namespace HPD.Agent.Tests.Session;

/// <summary>
/// Tests for AgentSession and SessionSnapshot types.
/// Covers message operations, metadata, snapshot conversion, and serialization.
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
        original.ConversationId = "conv_456";

        var snapshot = original.ToSnapshot();

        // Act
        var restored = AgentSession.FromSnapshot(snapshot);

        // Assert
        Assert.Equal("original-session", restored.Id);
        Assert.Single(restored.Messages);
        Assert.Equal("testValue", restored.Metadata["testKey"]);
        Assert.Equal("conv_456", restored.ConversationId);
        Assert.Null(restored.ExecutionState); // Snapshots don't include ExecutionState
    }

    //──────────────────────────────────────────────────────────────────
    // SESSION.STORE PROPERTY
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void AgentSession_Store_DefaultsToNull()
    {
        // Arrange & Act
        var session = new AgentSession();

        // Assert
        Assert.Null(session.Store);
    }

    [Fact]
    public void AgentSession_Store_CanBeSet()
    {
        // Arrange
        var session = new AgentSession();
        var store = new InMemorySessionStore();

        // Act
        session.Store = store;

        // Assert
        Assert.Same(store, session.Store);
    }

    [Fact]
    public void AgentSession_Store_NotSerializedToJson()
    {
        // Arrange
        var session = new AgentSession("test-session");
        session.AddMessage(UserMessage("Hello"));
        session.AddMetadata("key", "value");

        // Set a store reference
        var store = new InMemorySessionStore();
        session.Store = store;

        // Act - Convert to snapshot and serialize
        var snapshot = session.ToSnapshot();
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);

        // Assert - JSON should NOT contain "Store" property
        Assert.DoesNotContain("\"Store\"", json);
        Assert.DoesNotContain("\"store\"", json, StringComparison.OrdinalIgnoreCase);

        // Deserialize and verify
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<SessionSnapshot>(json);
        Assert.NotNull(deserialized);
        Assert.Equal("test-session", deserialized.SessionId);
    }

    [Fact]
    public async Task AgentSession_SaveAsync_ThrowsWhenStoreIsNull()
    {
        // Arrange
        var session = new AgentSession();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.SaveAsync());

        Assert.Contains("no associated store", exception.Message);
    }

    [Fact]
    public async Task AgentSession_SaveAsync_CallsStoreWhenSet()
    {
        // Arrange
        var session = new AgentSession("test-session");
        session.AddMessage(UserMessage("Test"));

        var store = new InMemorySessionStore();
        session.Store = store;

        // Act
        await session.SaveAsync();

        // Assert - Session should be saved to store
        var loaded = await store.LoadSessionAsync("test-session");
        Assert.NotNull(loaded);
        Assert.Single(loaded.Messages);
    }

}
