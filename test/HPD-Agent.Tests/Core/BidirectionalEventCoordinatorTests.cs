using HPD.Agent;
using HPD.Agent.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Xunit;

namespace HPD.Agent.Tests.Core;

/// <summary>
/// Tests for BidirectionalEventCoordinator cycle detection and event bubbling.
/// </summary>
public class BidirectionalEventCoordinatorTests
{
    [Fact]
    public void SetParent_WithNullParent_ThrowsArgumentNullException()
    {
        // Arrange
        var coordinator = new BidirectionalEventCoordinator();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => coordinator.SetParent(null!));
    }

    [Fact]
    public void SetParent_WithSelfReference_ThrowsInvalidOperationException()
    {
        // Arrange
        var coordinator = new BidirectionalEventCoordinator();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => coordinator.SetParent(coordinator));
        Assert.Contains("Cannot set coordinator as its own parent", ex.Message);
        Assert.Contains("infinite loop", ex.Message);
    }

    [Fact]
    public void SetParent_WithTwoNodeCycle_ThrowsInvalidOperationException()
    {
        // Arrange
        var coordinatorA = new BidirectionalEventCoordinator();
        var coordinatorB = new BidirectionalEventCoordinator();

        coordinatorA.SetParent(coordinatorB);

        // Act & Assert
        // Trying to set B's parent to A would create cycle: A -> B -> A
        var ex = Assert.Throws<InvalidOperationException>(() => coordinatorB.SetParent(coordinatorA));
        Assert.Contains("Cycle detected", ex.Message);
        Assert.Contains("infinite loop", ex.Message);
    }

    [Fact]
    public void SetParent_WithThreeNodeCycle_ThrowsInvalidOperationException()
    {
        // Arrange
        var coordinatorA = new BidirectionalEventCoordinator();
        var coordinatorB = new BidirectionalEventCoordinator();
        var coordinatorC = new BidirectionalEventCoordinator();

        coordinatorA.SetParent(coordinatorB);
        coordinatorB.SetParent(coordinatorC);

        // Act & Assert
        // Trying to set C's parent to A would create cycle: A -> B -> C -> A
        var ex = Assert.Throws<InvalidOperationException>(() => coordinatorC.SetParent(coordinatorA));
        Assert.Contains("Cycle detected", ex.Message);
    }

    [Fact]
    public void SetParent_WithValidChain_Succeeds()
    {
        // Arrange
        var root = new BidirectionalEventCoordinator();
        var middle = new BidirectionalEventCoordinator();
        var leaf = new BidirectionalEventCoordinator();

        // Act
        middle.SetParent(root);
        leaf.SetParent(middle);

        // Assert
        // If we got here without exception, the chain is valid
        Assert.True(true);
    }

    [Fact]
    public void SetParent_CanChangeParent_WhenNoCycleCreated()
    {
        // Arrange
        var root1 = new BidirectionalEventCoordinator();
        var root2 = new BidirectionalEventCoordinator();
        var child = new BidirectionalEventCoordinator();

        // Act
        child.SetParent(root1);  // First parent
        child.SetParent(root2);  // Change to different parent

        // Assert
        // If we got here without exception, changing parent worked
        Assert.True(true);
    }

    [Fact]
    public void SetParent_WithComplexChain_DetectsCycleCorrectly()
    {
        // Arrange
        // Create chain: A -> B -> C -> D
        var coordinatorA = new BidirectionalEventCoordinator();
        var coordinatorB = new BidirectionalEventCoordinator();
        var coordinatorC = new BidirectionalEventCoordinator();
        var coordinatorD = new BidirectionalEventCoordinator();

        coordinatorA.SetParent(coordinatorB);
        coordinatorB.SetParent(coordinatorC);
        coordinatorC.SetParent(coordinatorD);

        // Act & Assert
        // Trying to set D's parent to B would create cycle: B -> C -> D -> B
        var ex = Assert.Throws<InvalidOperationException>(() => coordinatorD.SetParent(coordinatorB));
        Assert.Contains("Cycle detected", ex.Message);
    }

    [Fact]
    public void EventBubbling_WithSetParent_BubblesCorrectly()
    {
        // Arrange
        var root = new BidirectionalEventCoordinator();
        var middle = new BidirectionalEventCoordinator();
        var leaf = new BidirectionalEventCoordinator();

        middle.SetParent(root);
        leaf.SetParent(middle);

        var testEvent = new TestAgentEvent { Message = "Test event" };

        // Act
        leaf.Emit(testEvent);

        // Assert - Event should appear in all three channels (CRITICAL: including middle!)
        Assert.True(leaf.TryRead(out var leafEvt));
        Assert.IsType<TestAgentEvent>(leafEvt);
        Assert.Equal("Test event", ((TestAgentEvent)leafEvt).Message);

        Assert.True(middle.TryRead(out var middleEvt));
        Assert.IsType<TestAgentEvent>(middleEvt);
        Assert.Equal("Test event", ((TestAgentEvent)middleEvt).Message);  //  THIS IS THE KEY TEST - middle sees it!

        Assert.True(root.TryRead(out var rootEvt));
        Assert.IsType<TestAgentEvent>(rootEvt);
        Assert.Equal("Test event", ((TestAgentEvent)rootEvt).Message);
    }

    [Fact]
    public void EventBubbling_MultiLevel_AllAgentsReceiveEvents()
    {
        // Arrange - Create 3-level hierarchy
        var orchestrator = new BidirectionalEventCoordinator();
        var middle = new BidirectionalEventCoordinator();
        var worker = new BidirectionalEventCoordinator();

        middle.SetParent(orchestrator);
        worker.SetParent(middle);

        var testEvent = new TestAgentEvent { Message = "Multi-level test" };

        var receivedEvents = new List<(string agent, AgentEvent evt)>();

        // Act - Worker emits event
        worker.Emit(testEvent);

        // Assert - Verify event reached all three levels
        if (worker.TryRead(out var workerEvt))
            receivedEvents.Add(("Worker", workerEvt));

        if (middle.TryRead(out var middleEvt))
            receivedEvents.Add(("Middle", middleEvt));

        if (orchestrator.TryRead(out var orchEvt))
            receivedEvents.Add(("Orchestrator", orchEvt));

        // All three should have received the event
        Assert.Equal(3, receivedEvents.Count);
        Assert.Contains(receivedEvents, e => e.agent == "Worker");
        Assert.Contains(receivedEvents, e => e.agent == "Middle");  // 🔥 KEY: Middle sees it!
        Assert.Contains(receivedEvents, e => e.agent == "Orchestrator");
    }

    [Fact]
    public void EventBubbling_WithoutSetParent_DoesNotBubble()
    {
        // Arrange - Create coordinators WITHOUT linking them
        var coordinator1 = new BidirectionalEventCoordinator();
        var coordinator2 = new BidirectionalEventCoordinator();

        var testEvent = new TestAgentEvent { Message = "No bubbling test" };

        // Act - Emit on coordinator1
        coordinator1.Emit(testEvent);

        // Assert - Only coordinator1 should receive the event
        Assert.True(coordinator1.TryRead(out var evt1));
        Assert.IsType<TestAgentEvent>(evt1);

        // coordinator2 should NOT receive the event (no parent relationship)
        Assert.False(coordinator2.TryRead(out _));
    }

    // ===== P0: ExecutionContext Auto-Attachment =====

    [Fact]
    public void Emit_AutoAttachesExecutionContext_WhenEventHasNone()
    {
        // Arrange
        var agent = TestAgentFactory.Create();

        // Set ExecutionContext on agent
        agent.ExecutionContext = new AgentExecutionContext
        {
            AgentName = "TestAgent",
            AgentId = "test-abc123",
            Depth = 0
        };

        // Act - Emit event without ExecutionContext
        var evt = new TestAgentEvent { Message = "Test" };
        Assert.Null(evt.ExecutionContext); // Verify it starts null

        agent.EventCoordinator.Emit(evt);

        // Read emitted event from channel
        agent.EventCoordinator.TryRead(out var emittedEvent);

        // Assert - ExecutionContext should be auto-attached
        Assert.NotNull(emittedEvent);
        Assert.NotNull(emittedEvent.ExecutionContext);
        Assert.Equal("TestAgent", emittedEvent.ExecutionContext!.AgentName);
        Assert.Equal("test-abc123", emittedEvent.ExecutionContext.AgentId);
    }

    [Fact]
    public void Emit_PreservesExecutionContext_WhenEventAlreadyHasOne()
    {
        // Arrange
        var agent = TestAgentFactory.Create();

        agent.ExecutionContext = new AgentExecutionContext
        {
            AgentName = "TestAgent",
            AgentId = "test-abc123",
            Depth = 0
        };

        // Act - Emit event WITH ExecutionContext already set
        var customContext = new AgentExecutionContext
        {
            AgentName = "CustomAgent",
            AgentId = "custom-xyz789",
            Depth = 5
        };

        var evt = new TestAgentEvent { Message = "Test", ExecutionContext = customContext };
        agent.EventCoordinator.Emit(evt);

        // Read emitted event
        agent.EventCoordinator.TryRead(out var emittedEvent);

        // Assert - Original ExecutionContext should be preserved
        Assert.NotNull(emittedEvent!.ExecutionContext);
        Assert.Equal("CustomAgent", emittedEvent.ExecutionContext!.AgentName);
        Assert.Equal("custom-xyz789", emittedEvent.ExecutionContext.AgentId);
        Assert.Equal(5, emittedEvent.ExecutionContext.Depth);
    }

    [Fact]
    public void Emit_BubblesEventWithContext_ToParentCoordinator()
    {
        // Arrange - Create parent and child agents
        var parentAgent = TestAgentFactory.Create(new AgentConfig
        {
            Name = "ParentAgent",
            Provider = new ProviderConfig { ProviderKey = "test", ModelName = "test-model" }
        });
        var childAgent = TestAgentFactory.Create(new AgentConfig
        {
            Name = "ChildAgent",
            Provider = new ProviderConfig { ProviderKey = "test", ModelName = "test-model" }
        });

        // Set ExecutionContexts
        parentAgent.ExecutionContext = new AgentExecutionContext
        {
            AgentName = "ParentAgent",
            AgentId = "parent-abc123",
            Depth = 0
        };

        childAgent.ExecutionContext = new AgentExecutionContext
        {
            AgentName = "ChildAgent",
            AgentId = "parent-abc123-child-def456",
            ParentAgentId = "parent-abc123",
            AgentChain = new[] { "ParentAgent", "ChildAgent" },
            Depth = 1
        };

        // Link child to parent
        childAgent.EventCoordinator.SetParent(parentAgent.EventCoordinator);

        // Act - Child emits event
        var evt = new TestAgentEvent { Message = "Child event" };
        childAgent.EventCoordinator.Emit(evt);

        // Assert - Event should appear in both child and parent channels with context
        // Child channel
        Assert.True(childAgent.EventCoordinator.TryRead(out var childEvent));
        Assert.NotNull(childEvent!.ExecutionContext);
        Assert.Equal("ChildAgent", childEvent.ExecutionContext!.AgentName);

        // Parent channel (bubbled event)
        Assert.True(parentAgent.EventCoordinator.TryRead(out var parentEvent));
        Assert.NotNull(parentEvent!.ExecutionContext);
        Assert.Equal("ChildAgent", parentEvent.ExecutionContext!.AgentName); // Context preserved during bubbling
        Assert.Equal(1, parentEvent.ExecutionContext.Depth);
    }

    [Fact]
    public void Emit_MultipleChildrenEvents_BubbleWithCorrecTMetadatas()
    {
        // Arrange - Parent with two children
        var parent = TestAgentFactory.Create(new AgentConfig
        {
            Name = "Orchestrator",
            Provider = new ProviderConfig { ProviderKey = "test", ModelName = "test-model" }
        });
        var child1 = TestAgentFactory.Create(new AgentConfig
        {
            Name = "WeatherExpert",
            Provider = new ProviderConfig { ProviderKey = "test", ModelName = "test-model" }
        });
        var child2 = TestAgentFactory.Create(new AgentConfig
        {
            Name = "MathExpert",
            Provider = new ProviderConfig { ProviderKey = "test", ModelName = "test-model" }
        });

        parent.ExecutionContext = new AgentExecutionContext
        {
            AgentName = "Orchestrator",
            AgentId = "orch-123",
            Depth = 0
        };

        child1.ExecutionContext = new AgentExecutionContext
        {
            AgentName = "WeatherExpert",
            AgentId = "orch-123-weather-456",
            ParentAgentId = "orch-123",
            Depth = 1
        };

        child2.ExecutionContext = new AgentExecutionContext
        {
            AgentName = "MathExpert",
            AgentId = "orch-123-math-789",
            ParentAgentId = "orch-123",
            Depth = 1
        };

        // Link children to parent
        child1.EventCoordinator.SetParent(parent.EventCoordinator);
        child2.EventCoordinator.SetParent(parent.EventCoordinator);

        // Act - Both children emit events
        child1.EventCoordinator.Emit(new TestAgentEvent { Message = "Weather event" });
        child2.EventCoordinator.Emit(new TestAgentEvent { Message = "Math event" });

        // Assert - Parent should receive both events with correct contexts
        var parentEvents = new List<AgentEvent>();
        while (parent.EventCoordinator.TryRead(out var evt))
        {
            parentEvents.Add(evt);
        }

        Assert.Equal(2, parentEvents.Count);

        // Verify contexts are preserved
        var weatherEvent = parentEvents.FirstOrDefault(e => e.ExecutionContext?.AgentName == "WeatherExpert");
        var mathEvent = parentEvents.FirstOrDefault(e => e.ExecutionContext?.AgentName == "MathExpert");

        Assert.NotNull(weatherEvent);
        Assert.NotNull(mathEvent);
        Assert.Equal("orch-123-weather-456", weatherEvent!.ExecutionContext!.AgentId);
        Assert.Equal("orch-123-math-789", mathEvent!.ExecutionContext!.AgentId);
    }
}

/// <summary>
/// Test event record for event bubbling tests
/// </summary>
internal sealed record TestAgentEvent(string Message = "") : AgentEvent;
