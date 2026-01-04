using System.Collections.Concurrent;
using HPD.Agent;
using HPD.Events;
using HPDAgent.Graph.Abstractions.Events;
using HPDAgent.Graph.Abstractions.Execution;

namespace HPD.MultiAgent.Observability;

/// <summary>
/// Observer that collects metrics from workflow execution events.
/// Implements IEventObserver for fire-and-forget metric collection.
/// </summary>
public class MetricsObserver : IEventObserver<Event>
{
    private readonly ConcurrentDictionary<string, WorkflowMetrics> _activeWorkflows = new();
    private readonly ConcurrentQueue<WorkflowMetrics> _completedWorkflows = new();
    private readonly int _maxCompletedWorkflows;

    /// <summary>
    /// Creates a new metrics observer.
    /// </summary>
    /// <param name="maxCompletedWorkflows">Maximum completed workflows to retain. Default: 100.</param>
    public MetricsObserver(int maxCompletedWorkflows = 100)
    {
        _maxCompletedWorkflows = maxCompletedWorkflows;
    }

    /// <summary>
    /// Event raised when workflow metrics are updated.
    /// </summary>
    public event Action<WorkflowMetrics>? OnMetricsUpdated;

    /// <summary>
    /// Event raised when a workflow completes.
    /// </summary>
    public event Action<WorkflowMetrics>? OnWorkflowCompleted;

    /// <summary>
    /// Get metrics for an active workflow.
    /// </summary>
    public WorkflowMetrics? GetActiveWorkflow(string executionId)
    {
        return _activeWorkflows.TryGetValue(executionId, out var metrics) ? metrics : null;
    }

    /// <summary>
    /// Get all active workflow metrics.
    /// </summary>
    public IReadOnlyCollection<WorkflowMetrics> ActiveWorkflows => _activeWorkflows.Values.ToList();

    /// <summary>
    /// Get recently completed workflow metrics.
    /// </summary>
    public IReadOnlyCollection<WorkflowMetrics> CompletedWorkflows => _completedWorkflows.ToList();

    /// <inheritdoc/>
    public bool ShouldProcess(Event evt)
    {
        // Process graph events and agent events we care about
        return evt is GraphEvent
            or TextDeltaEvent
            or ToolCallStartEvent
            or ToolCallEndEvent
            or MessageTurnFinishedEvent
            or NodeApprovalRequestEvent
            or NodeApprovalResponseEvent;
    }

    /// <inheritdoc/>
    public Task OnEventAsync(Event evt, CancellationToken cancellationToken = default)
    {
        try
        {
            switch (evt)
            {
                // Graph lifecycle events
                case GraphExecutionStartedEvent started:
                    HandleGraphStarted(started);
                    break;

                case GraphExecutionCompletedEvent completed:
                    HandleGraphCompleted(completed);
                    break;

                // Node execution events
                case NodeExecutionStartedEvent nodeStarted:
                    HandleNodeStarted(nodeStarted);
                    break;

                case NodeExecutionCompletedEvent nodeCompleted:
                    HandleNodeCompleted(nodeCompleted);
                    break;

                // Iteration events
                case IterationStartedEvent iterationStarted:
                    HandleIterationStarted(iterationStarted);
                    break;

                // Tool events (from agent)
                case ToolCallStartEvent toolStart:
                    HandleToolCallStart(toolStart);
                    break;

                // Token usage (from agent turn completion)
                case MessageTurnFinishedEvent turnFinished:
                    HandleTurnFinished(turnFinished);
                    break;

                // Approval events
                case NodeApprovalRequestEvent approvalRequest:
                    HandleApprovalRequest(approvalRequest);
                    break;

                case NodeApprovalResponseEvent approvalResponse:
                    HandleApprovalResponse(approvalResponse);
                    break;
            }
        }
        catch
        {
            // Observers should never crash the system
            // In production, log this error
        }

        return Task.CompletedTask;
    }

    private void HandleGraphStarted(GraphExecutionStartedEvent evt)
    {
        var executionId = evt.GraphContext?.GraphId ?? Guid.NewGuid().ToString();

        var metrics = new WorkflowMetrics
        {
            ExecutionId = executionId,
            WorkflowName = null, // Graph name not directly on event
            StartedAt = DateTimeOffset.UtcNow
        };

        _activeWorkflows[executionId] = metrics;
        OnMetricsUpdated?.Invoke(metrics);
    }

    private void HandleGraphCompleted(GraphExecutionCompletedEvent evt)
    {
        var executionId = evt.GraphContext?.GraphId;
        if (string.IsNullOrEmpty(executionId)) return;

        if (_activeWorkflows.TryRemove(executionId, out var metrics))
        {
            metrics.CompletedAt = DateTimeOffset.UtcNow;
            metrics.Success = evt.FailedNodes == 0;

            // Move to completed queue
            _completedWorkflows.Enqueue(metrics);

            // Trim if over limit
            while (_completedWorkflows.Count > _maxCompletedWorkflows)
            {
                _completedWorkflows.TryDequeue(out _);
            }

            OnWorkflowCompleted?.Invoke(metrics);
        }
    }

    private void HandleNodeStarted(NodeExecutionStartedEvent evt)
    {
        var executionId = evt.GraphContext?.GraphId;
        var metrics = GetOrCreateWorkflowMetrics(executionId);
        if (metrics == null) return;

        var nodeMetrics = metrics.GetOrCreateNodeMetrics(evt.NodeId);
        nodeMetrics.StartedAt = DateTimeOffset.UtcNow;
        nodeMetrics.Iteration = evt.LayerIndex ?? 0;

        OnMetricsUpdated?.Invoke(metrics);
    }

    private void HandleNodeCompleted(NodeExecutionCompletedEvent evt)
    {
        var executionId = evt.GraphContext?.GraphId;
        var metrics = GetOrCreateWorkflowMetrics(executionId);
        if (metrics == null) return;

        var nodeMetrics = metrics.GetOrCreateNodeMetrics(evt.NodeId);
        nodeMetrics.CompletedAt = DateTimeOffset.UtcNow;
        nodeMetrics.Duration = evt.Duration;

        // Determine success/skip from Result
        nodeMetrics.Success = evt.Result is NodeExecutionResult.Success;
        nodeMetrics.WasSkipped = evt.Result is NodeExecutionResult.Skipped;

        if (evt.Result is NodeExecutionResult.Skipped skipped)
        {
            nodeMetrics.SkipReason = skipped.Message;
        }
        else if (evt.Result is NodeExecutionResult.Failure failure)
        {
            nodeMetrics.ErrorMessage = failure.Exception.Message;
        }

        OnMetricsUpdated?.Invoke(metrics);
    }

    private void HandleIterationStarted(IterationStartedEvent evt)
    {
        var executionId = evt.GraphContext?.GraphId;
        var metrics = GetOrCreateWorkflowMetrics(executionId);
        if (metrics == null) return;

        metrics.IterationCount = evt.IterationIndex + 1;
        OnMetricsUpdated?.Invoke(metrics);
    }

    private void HandleToolCallStart(ToolCallStartEvent evt)
    {
        // We need to track which workflow/node this belongs to
        // For now, we'll track tool calls globally and associate later
        // This is a limitation - we'd need context to know which node
    }

    private void HandleTurnFinished(MessageTurnFinishedEvent evt)
    {
        // Token usage is in the event
        // We need context to associate with the right node
        // This would be handled by the AgentNodeHandler emitting enriched events
    }

    private void HandleApprovalRequest(NodeApprovalRequestEvent evt)
    {
        // Find the workflow by searching active workflows for the node
        foreach (var metrics in _activeWorkflows.Values)
        {
            if (metrics.NodeMetrics.TryGetValue(evt.NodeId, out var nodeMetrics))
            {
                nodeMetrics.RequiredApproval = true;
                OnMetricsUpdated?.Invoke(metrics);
                break;
            }
        }
    }

    private void HandleApprovalResponse(NodeApprovalResponseEvent evt)
    {
        // Find the workflow and update approval status
        foreach (var metrics in _activeWorkflows.Values)
        {
            foreach (var nodeMetrics in metrics.NodeMetrics.Values)
            {
                if (nodeMetrics.RequiredApproval && nodeMetrics.ApprovalGranted == null)
                {
                    nodeMetrics.ApprovalGranted = evt.Approved;
                    if (nodeMetrics.StartedAt.HasValue)
                    {
                        nodeMetrics.ApprovalWaitTime = DateTimeOffset.UtcNow - nodeMetrics.StartedAt.Value;
                    }
                    OnMetricsUpdated?.Invoke(metrics);
                    return;
                }
            }
        }
    }

    private WorkflowMetrics? GetOrCreateWorkflowMetrics(string? executionId)
    {
        if (string.IsNullOrEmpty(executionId))
            return null;

        return _activeWorkflows.GetOrAdd(executionId, id => new WorkflowMetrics
        {
            ExecutionId = id,
            StartedAt = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Clear all metrics.
    /// </summary>
    public void Clear()
    {
        _activeWorkflows.Clear();
        while (_completedWorkflows.TryDequeue(out _)) { }
    }
}
