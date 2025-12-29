using Xunit;
using FluentAssertions;
using HPD.Agent;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// Tests for automatic middleware state persistence via LoadFromSession/SaveToSession.
/// Verifies that states marked with [MiddlewareState(Persistent = true)] are
/// automatically serialized and restored across agent runs.
/// </summary>
public class SessionPersistenceTests
{
    [Fact]
    public void LoadFromSession_WithNullSession_ReturnsEmptyState()
    {
        // Act
        var state = MiddlewareState.LoadFromSession(null);

        // Assert
        state.Should().NotBeNull();
        state.PermissionPersistent.Should().BeNull("no session data to load from");
        state.HistoryReduction.Should().BeNull("no session data to load from");
    }

    [Fact]
    public void LoadFromSession_RestoresPermissionState()
    {
        // Arrange: Create session with permission state
        var session = new AgentSession();
        var permState = new PermissionPersistentStateData()
            .WithPermission("Bash", PermissionChoice.AlwaysAllow)
            .WithPermission("Read", PermissionChoice.AlwaysDeny);

        var middlewareState = new MiddlewareState()
            .WithPermissionPersistent(permState);

        middlewareState.SaveToSession(session);

        // Act: Load from session
        var restored = MiddlewareState.LoadFromSession(session);

        // Assert: Permission state is restored
        restored.PermissionPersistent.Should().NotBeNull();
        restored.PermissionPersistent!.GetPermission("Bash")
            .Should().Be(PermissionChoice.AlwaysAllow);
        restored.PermissionPersistent.GetPermission("Read")
            .Should().Be(PermissionChoice.AlwaysDeny);
        restored.PermissionPersistent.GetPermission("Write")
            .Should().BeNull("not stored");
    }

    [Fact]
    public void LoadFromSession_RestoresHistoryReduction()
    {
        // Arrange: Create session with history reduction state
        var session = new AgentSession();
        // Create enough messages for the test
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>();
        for (int i = 0; i < 100; i++)
        {
            messages.Add(new(Microsoft.Extensions.AI.ChatRole.User, $"message {i}"));
        }

        var reduction = CachedReduction.Create(
            messages: messages,
            summaryContent: "Test summary",
            summarizedUpToIndex: 90,
            targetMessageCount: 100,
            reductionThreshold: 5);

        var hrState = new HistoryReductionStateData().WithReduction(reduction);
        var middlewareState = new MiddlewareState().WithHistoryReduction(hrState);

        middlewareState.SaveToSession(session);

        // Act: Load from session
        var restored = MiddlewareState.LoadFromSession(session);

        // Assert: History reduction is restored
        restored.HistoryReduction.Should().NotBeNull();
        restored.HistoryReduction!.LastReduction.Should().NotBeNull();
        restored.HistoryReduction.LastReduction!.SummarizedUpToIndex.Should().Be(90);
        restored.HistoryReduction.LastReduction.MessageCountAtReduction.Should().Be(100);
        restored.HistoryReduction.LastReduction.SummaryContent.Should().Be("Test summary");
    }

    [Fact]
    public void SaveToSession_PersistsMultipleStates()
    {
        // Arrange: Create multiple persistent states
        var session = new AgentSession();

        var permState = new PermissionPersistentStateData()
            .WithPermission("Bash", PermissionChoice.AlwaysAllow);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, "msg"),
        };

        var reduction = CachedReduction.Create(
            messages: messages,
            summaryContent: "Summary",
            summarizedUpToIndex: 50,
            targetMessageCount: 60,
            reductionThreshold: 10);
        var hrState = new HistoryReductionStateData().WithReduction(reduction);

        var middlewareState = new MiddlewareState()
            .WithPermissionPersistent(permState)
            .WithHistoryReduction(hrState);

        // Act: Save to session
        middlewareState.SaveToSession(session);

        // Load back
        var restored = MiddlewareState.LoadFromSession(session);

        // Assert: Both states are restored
        restored.PermissionPersistent.Should().NotBeNull();
        restored.PermissionPersistent!.GetPermission("Bash")
            .Should().Be(PermissionChoice.AlwaysAllow);

        restored.HistoryReduction.Should().NotBeNull();
        restored.HistoryReduction!.LastReduction!.SummarizedUpToIndex.Should().Be(50);
    }

    [Fact]
    public void SaveToSession_DoesNotPersistTransientStates()
    {
        // Arrange: Create transient batch permission state
        var session = new AgentSession();
        var batchState = new BatchPermissionStateData()
            .RecordApproval("Bash")
            .RecordApproval("Read");

        var middlewareState = new MiddlewareState()
            .WithBatchPermission(batchState);

        // Act: Save to session (batch state should NOT be saved)
        middlewareState.SaveToSession(session);

        // Load back
        var restored = MiddlewareState.LoadFromSession(session);

        // Assert: Batch state is NOT restored (it's transient)
        restored.BatchPermission.Should().BeNull("batch state is transient and should not persist");
    }

    [Fact]
    public void SaveToSession_WithNullState_DoesNotSaveAnything()
    {
        // Arrange
        var session = new AgentSession();
        var middlewareState = new MiddlewareState(); // All states null

        // Act
        middlewareState.SaveToSession(session);

        // Load back
        var restored = MiddlewareState.LoadFromSession(session);

        // Assert: Nothing restored (nothing was saved)
        restored.PermissionPersistent.Should().BeNull();
        restored.HistoryReduction.Should().BeNull();
    }

    [Fact]
    public void SaveToSession_WithNullSession_ThrowsArgumentNullException()
    {
        // Arrange
        var middlewareState = new MiddlewareState();

        // Act & Assert
        Action act = () => middlewareState.SaveToSession(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PermissionState_RoundTrip_PreservesAllData()
    {
        // Arrange: Create complex permission state
        var session = new AgentSession();
        var permState = new PermissionPersistentStateData()
            .WithPermission("Bash", PermissionChoice.AlwaysAllow)
            .WithPermission("Read", PermissionChoice.AlwaysAllow)
            .WithPermission("Write", PermissionChoice.AlwaysDeny)
            .WithPermission("Delete", PermissionChoice.Ask);

        var middlewareState = new MiddlewareState().WithPermissionPersistent(permState);

        // Act: Round-trip through session
        middlewareState.SaveToSession(session);
        var restored = MiddlewareState.LoadFromSession(session);

        // Assert: All permission choices preserved
        restored.PermissionPersistent!.GetPermission("Bash").Should().Be(PermissionChoice.AlwaysAllow);
        restored.PermissionPersistent.GetPermission("Read").Should().Be(PermissionChoice.AlwaysAllow);
        restored.PermissionPersistent.GetPermission("Write").Should().Be(PermissionChoice.AlwaysDeny);
        restored.PermissionPersistent.GetPermission("Delete").Should().Be(PermissionChoice.Ask);
    }

    [Fact]
    public void SessionPersistence_MultipleRoundTrips_PreservesData()
    {
        // Arrange
        var session = new AgentSession();
        var permState1 = new PermissionPersistentStateData()
            .WithPermission("Bash", PermissionChoice.AlwaysAllow);

        // Act: First round-trip
        var state1 = new MiddlewareState().WithPermissionPersistent(permState1);
        state1.SaveToSession(session);
        var restored1 = MiddlewareState.LoadFromSession(session);

        // Add more permissions
        var permState2 = restored1.PermissionPersistent!
            .WithPermission("Read", PermissionChoice.AlwaysDeny);
        var state2 = new MiddlewareState().WithPermissionPersistent(permState2);
        state2.SaveToSession(session);
        var restored2 = MiddlewareState.LoadFromSession(session);

        // Assert: Both permissions present after multiple round-trips
        restored2.PermissionPersistent.Should().NotBeNull();
        restored2.PermissionPersistent!.GetPermission("Bash").Should().Be(PermissionChoice.AlwaysAllow);
        restored2.PermissionPersistent.GetPermission("Read").Should().Be(PermissionChoice.AlwaysDeny);
    }
}
