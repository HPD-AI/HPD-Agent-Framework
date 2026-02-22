using System.Diagnostics;
using HPD.Events;
using HPD.Events.Core;
using HPD.MultiAgent.Observability;
using HPDAgent.Graph.Abstractions.Events;
using HPDAgent.Graph.Abstractions.Execution;

namespace HPD.MultiAgent.Tests;

/// <summary>
/// Tests for Phase 5: Observability features.
/// </summary>
public class ObservabilityTests
{
    #region WorkflowMetrics Tests

    [Fact]
    public void WorkflowMetrics_Calculates_Duration()
    {
        var startTime = DateTimeOffset.UtcNow;
        var metrics = new WorkflowMetrics
        {
            ExecutionId = "test-123",
            StartedAt = startTime
        };

        // Duration should be calculated from now
        metrics.Duration.Should().BeCloseTo(TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // After completion
        metrics.CompletedAt = startTime.AddSeconds(5);
        metrics.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void WorkflowMetrics_Counts_Nodes_Correctly()
    {
        var metrics = new WorkflowMetrics
        {
            ExecutionId = "test-123",
            StartedAt = DateTimeOffset.UtcNow
        };

        // Add some node metrics
        var node1 = metrics.GetOrCreateNodeMetrics("node1");
        node1.Success = true;

        var node2 = metrics.GetOrCreateNodeMetrics("node2");
        node2.Success = false;

        var node3 = metrics.GetOrCreateNodeMetrics("node3");
        node3.WasSkipped = true;

        var node4 = metrics.GetOrCreateNodeMetrics("node4");
        node4.Success = true;

        metrics.TotalNodesExecuted.Should().Be(4);
        metrics.SuccessfulNodes.Should().Be(2);
        metrics.FailedNodes.Should().Be(1);
        metrics.SkippedNodes.Should().Be(1);
    }

    [Fact]
    public void WorkflowMetrics_Sums_Tokens()
    {
        var metrics = new WorkflowMetrics
        {
            ExecutionId = "test-123",
            StartedAt = DateTimeOffset.UtcNow
        };

        var node1 = metrics.GetOrCreateNodeMetrics("node1");
        node1.InputTokens = 100;
        node1.OutputTokens = 50;

        var node2 = metrics.GetOrCreateNodeMetrics("node2");
        node2.InputTokens = 200;
        node2.OutputTokens = 100;

        metrics.TotalInputTokens.Should().Be(300);
        metrics.TotalOutputTokens.Should().Be(150);
        metrics.TotalTokens.Should().Be(450);
    }

    [Fact]
    public void WorkflowMetrics_Counts_ToolCalls()
    {
        var metrics = new WorkflowMetrics
        {
            ExecutionId = "test-123",
            StartedAt = DateTimeOffset.UtcNow
        };

        var node1 = metrics.GetOrCreateNodeMetrics("node1");
        node1.ToolCallCount = 3;

        var node2 = metrics.GetOrCreateNodeMetrics("node2");
        node2.ToolCallCount = 2;

        metrics.TotalToolCalls.Should().Be(5);
    }

    [Fact]
    public void WorkflowMetrics_Tags_Can_Be_Added()
    {
        var metrics = new WorkflowMetrics
        {
            ExecutionId = "test-123",
            StartedAt = DateTimeOffset.UtcNow
        };

        metrics.Tags["environment"] = "production";
        metrics.Tags["user_id"] = "user-456";

        metrics.Tags.Should().ContainKey("environment");
        metrics.Tags["environment"].Should().Be("production");
    }

    #endregion

    #region NodeMetrics Tests

    [Fact]
    public void NodeMetrics_Default_Values()
    {
        var nodeMetrics = new NodeMetrics { NodeId = "test-node" };

        nodeMetrics.NodeId.Should().Be("test-node");
        nodeMetrics.StartedAt.Should().BeNull();
        nodeMetrics.CompletedAt.Should().BeNull();
        nodeMetrics.Success.Should().BeNull();
        nodeMetrics.WasSkipped.Should().BeFalse();
        nodeMetrics.RetryCount.Should().Be(0);
        nodeMetrics.InputTokens.Should().Be(0);
        nodeMetrics.OutputTokens.Should().Be(0);
        nodeMetrics.ToolCallCount.Should().Be(0);
        nodeMetrics.RequiredApproval.Should().BeFalse();
    }

    [Fact]
    public void NodeMetrics_TotalTokens_Calculates_Correctly()
    {
        var nodeMetrics = new NodeMetrics { NodeId = "test-node" };
        nodeMetrics.InputTokens = 150;
        nodeMetrics.OutputTokens = 75;

        nodeMetrics.TotalTokens.Should().Be(225);
    }

    [Fact]
    public void NodeMetrics_ToolsCalled_Tracks_Tools()
    {
        var nodeMetrics = new NodeMetrics { NodeId = "test-node" };

        nodeMetrics.ToolsCalled.Add("search");
        nodeMetrics.ToolsCalled.Add("calculate");
        nodeMetrics.ToolsCalled.Add("format");

        nodeMetrics.ToolsCalled.Should().HaveCount(3);
        nodeMetrics.ToolsCalled.Should().Contain("search");
    }

    [Fact]
    public void NodeMetrics_Metadata_Can_Be_Added()
    {
        var nodeMetrics = new NodeMetrics { NodeId = "test-node" };

        nodeMetrics.Metadata["custom_field"] = "custom_value";
        nodeMetrics.Metadata["count"] = 42;

        nodeMetrics.Metadata.Should().ContainKey("custom_field");
        nodeMetrics.Metadata["count"].Should().Be(42);
    }

    [Fact]
    public void NodeMetrics_Tracks_Approval()
    {
        var nodeMetrics = new NodeMetrics { NodeId = "test-node" };

        nodeMetrics.RequiredApproval = true;
        nodeMetrics.ApprovalGranted = true;
        nodeMetrics.ApprovalWaitTime = TimeSpan.FromSeconds(30);

        nodeMetrics.RequiredApproval.Should().BeTrue();
        nodeMetrics.ApprovalGranted.Should().BeTrue();
        nodeMetrics.ApprovalWaitTime.Should().Be(TimeSpan.FromSeconds(30));
    }

    #endregion

    #region MetricsObserver Tests

    [Fact]
    public void MetricsObserver_Creates_With_Default_Capacity()
    {
        var observer = new MetricsObserver();

        observer.ActiveWorkflows.Should().BeEmpty();
        observer.CompletedWorkflows.Should().BeEmpty();
    }

    [Fact]
    public void MetricsObserver_Creates_With_Custom_Capacity()
    {
        var observer = new MetricsObserver(maxCompletedWorkflows: 50);

        observer.Should().NotBeNull();
    }

    [Fact]
    public async Task MetricsObserver_ShouldProcess_Returns_True_For_Graph_Events()
    {
        var observer = new MetricsObserver();

        var graphEvent = new GraphExecutionStartedEvent { NodeCount = 5 };
        observer.ShouldProcess(graphEvent).Should().BeTrue();

        var nodeEvent = new NodeExecutionStartedEvent
        {
            NodeId = "test",
            HandlerName = "TestHandler"
        };
        observer.ShouldProcess(nodeEvent).Should().BeTrue();
    }

    [Fact]
    public async Task MetricsObserver_Handles_GraphStarted_Event()
    {
        var observer = new MetricsObserver();
        WorkflowMetrics? capturedMetrics = null;
        observer.OnMetricsUpdated += m => capturedMetrics = m;

        var evt = new GraphExecutionStartedEvent
        {
            NodeCount = 5,
            GraphContext = new GraphExecutionContext
            {
                GraphId = "workflow-123",
                TotalNodes = 5
            }
        };

        await observer.OnEventAsync(evt);

        capturedMetrics.Should().NotBeNull();
        capturedMetrics!.ExecutionId.Should().Be("workflow-123");
        observer.ActiveWorkflows.Should().HaveCount(1);
    }

    [Fact]
    public async Task MetricsObserver_Handles_GraphCompleted_Event()
    {
        var observer = new MetricsObserver();
        WorkflowMetrics? completedMetrics = null;
        observer.OnWorkflowCompleted += m => completedMetrics = m;

        // Start workflow
        var startEvt = new GraphExecutionStartedEvent
        {
            NodeCount = 5,
            GraphContext = new GraphExecutionContext { GraphId = "workflow-123", TotalNodes = 5 }
        };
        await observer.OnEventAsync(startEvt);

        // Complete workflow
        var endEvt = new GraphExecutionCompletedEvent
        {
            Duration = TimeSpan.FromSeconds(10),
            SuccessfulNodes = 5,
            FailedNodes = 0,
            GraphContext = new GraphExecutionContext { GraphId = "workflow-123", TotalNodes = 5 }
        };
        await observer.OnEventAsync(endEvt);

        completedMetrics.Should().NotBeNull();
        completedMetrics!.Success.Should().BeTrue();
        observer.ActiveWorkflows.Should().BeEmpty();
        observer.CompletedWorkflows.Should().HaveCount(1);
    }

    [Fact]
    public async Task MetricsObserver_Handles_NodeStarted_Event()
    {
        var observer = new MetricsObserver();

        // Start workflow first
        var startEvt = new GraphExecutionStartedEvent
        {
            NodeCount = 2,
            GraphContext = new GraphExecutionContext { GraphId = "workflow-123", TotalNodes = 2 }
        };
        await observer.OnEventAsync(startEvt);

        // Start node
        var nodeEvt = new NodeExecutionStartedEvent
        {
            NodeId = "node-1",
            HandlerName = "TestHandler",
            GraphContext = new GraphExecutionContext { GraphId = "workflow-123", TotalNodes = 2 }
        };
        await observer.OnEventAsync(nodeEvt);

        var metrics = observer.GetActiveWorkflow("workflow-123");
        metrics.Should().NotBeNull();
        metrics!.NodeMetrics.Should().ContainKey("node-1");
        metrics.NodeMetrics["node-1"].StartedAt.Should().NotBeNull();
    }

    [Fact]
    public void MetricsObserver_Clear_Removes_All_Metrics()
    {
        var observer = new MetricsObserver();

        // Add a workflow manually through the event
        var startEvt = new GraphExecutionStartedEvent
        {
            NodeCount = 1,
            GraphContext = new GraphExecutionContext { GraphId = "test", TotalNodes = 1 }
        };
        observer.OnEventAsync(startEvt);

        observer.ActiveWorkflows.Should().NotBeEmpty();

        observer.Clear();

        observer.ActiveWorkflows.Should().BeEmpty();
        observer.CompletedWorkflows.Should().BeEmpty();
    }

    #endregion

    #region TracingObserver Tests

    [Fact]
    public void TracingObserver_Creates_With_Default_ActivitySource()
    {
        var observer = new TracingObserver();

        observer.ActivitySource.Should().NotBeNull();
        observer.ActivitySource.Name.Should().Be(TracingObserver.ActivitySourceName);
    }

    [Fact]
    public void TracingObserver_Creates_With_Custom_ActivitySource()
    {
        var activitySource = new ActivitySource("Custom.Source");
        var observer = new TracingObserver(activitySource);

        observer.ActivitySource.Should().BeSameAs(activitySource);

        observer.Dispose();
        activitySource.Dispose();
    }

    [Fact]
    public void TracingObserver_ShouldProcess_Returns_True_For_Graph_Events()
    {
        var observer = new TracingObserver();

        var graphEvent = new GraphExecutionStartedEvent { NodeCount = 5 };
        observer.ShouldProcess(graphEvent).Should().BeTrue();
    }

    [Fact]
    public async Task TracingObserver_Handles_Events_Without_Throwing()
    {
        var observer = new TracingObserver();

        // Should not throw on any event
        var startEvt = new GraphExecutionStartedEvent
        {
            NodeCount = 1,
            GraphContext = new GraphExecutionContext { GraphId = "test", TotalNodes = 1 }
        };

        var act = async () => await observer.OnEventAsync(startEvt);
        await act.Should().NotThrowAsync();

        observer.Dispose();
    }

    [Fact]
    public void TracingObserver_Dispose_Cleans_Up()
    {
        var observer = new TracingObserver();

        var act = () => observer.Dispose();
        act.Should().NotThrow();
    }

    #endregion

    #region SpanInfo Tests

    [Fact]
    public void SpanInfo_Calculates_Duration()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddSeconds(5);

        var span = new SpanInfo
        {
            SpanId = "span-1",
            TraceId = "trace-1",
            OperationName = "test",
            StartTime = start,
            EndTime = end
        };

        span.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public void SpanInfo_Duration_Null_When_Not_Completed()
    {
        var span = new SpanInfo
        {
            SpanId = "span-1",
            TraceId = "trace-1",
            OperationName = "test",
            StartTime = DateTimeOffset.UtcNow,
            EndTime = null
        };

        span.Duration.Should().BeNull();
    }

    [Fact]
    public void SpanStatus_Has_Expected_Values()
    {
        var statuses = Enum.GetValues<SpanStatus>();

        statuses.Should().Contain(SpanStatus.Unset);
        statuses.Should().Contain(SpanStatus.Ok);
        statuses.Should().Contain(SpanStatus.Error);
    }

    #endregion
}
