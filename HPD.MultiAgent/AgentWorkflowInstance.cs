using System.Runtime.CompilerServices;
using HPD.Agent;
using HPD.Events;
using HPD.Events.Core;
using HPD.MultiAgent.Internal;
using HPDAgent.Graph.Abstractions.Events;
using HPDAgent.Graph.Abstractions.Execution;
using HPDAgent.Graph.Abstractions.Graph;
using HPDAgent.Graph.Abstractions.Handlers;
using HPDAgent.Graph.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using GraphDefinition = HPDAgent.Graph.Abstractions.Graph.Graph;

namespace HPD.MultiAgent;

/// <summary>
/// Result of a workflow execution.
/// </summary>
public sealed record WorkflowResult
{
    /// <summary>
    /// The final answer/output from the workflow.
    /// </summary>
    public string? FinalAnswer { get; init; }

    /// <summary>
    /// All outputs from the final node(s).
    /// </summary>
    public Dictionary<string, object> Outputs { get; init; } = new();

    /// <summary>
    /// Total execution duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Whether the workflow completed successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the workflow failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Exception if the workflow failed.
    /// </summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// A built multi-agent workflow ready for execution.
/// </summary>
public sealed class AgentWorkflowInstance
{
    private readonly GraphDefinition _graph;
    private readonly Dictionary<string, Agent.Agent> _agents;
    private readonly Dictionary<string, AgentNodeOptions> _options;
    private readonly IServiceProvider _serviceProvider;

    internal AgentWorkflowInstance(
        GraphDefinition graph,
        Dictionary<string, Agent.Agent> agents,
        Dictionary<string, AgentNodeOptions> options,
        IServiceProvider serviceProvider)
    {
        _graph = graph;
        _agents = agents;
        _options = options;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Execute the workflow and return the final result.
    /// </summary>
    public async Task<WorkflowResult> RunAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var outputs = new Dictionary<string, object>();
        string? finalAnswer = null;
        string? error = null;
        Exception? exception = null;
        var success = true;

        try
        {
            await foreach (var evt in ExecuteStreamingAsync(input, cancellationToken))
            {
                // Capture the last TextDeltaEvent content for final answer
                if (evt is TextDeltaEvent textDelta)
                {
                    // This will be overwritten by the last agent's output
                    // For a proper implementation, we'd track the final node
                }

                // Capture node completion outputs
                if (evt is HPDAgent.Graph.Abstractions.Events.NodeExecutionCompletedEvent nodeComplete)
                {
                    if (nodeComplete.Outputs != null)
                    {
                        foreach (var kvp in nodeComplete.Outputs)
                        {
                            outputs[$"{nodeComplete.NodeId}.{kvp.Key}"] = kvp.Value;
                        }

                        // Check for answer in the outputs
                        if (nodeComplete.Outputs.TryGetValue("answer", out var answer))
                        {
                            finalAnswer = answer?.ToString();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            success = false;
            error = "Workflow was cancelled";
        }
        catch (Exception ex)
        {
            success = false;
            error = ex.Message;
            exception = ex;
        }

        return new WorkflowResult
        {
            FinalAnswer = finalAnswer,
            Outputs = outputs,
            Duration = DateTimeOffset.UtcNow - startTime,
            Success = success,
            Error = error,
            Exception = exception
        };
    }

    /// <summary>
    /// Execute the workflow with streaming events.
    /// Returns unified stream of graph and agent events.
    /// </summary>
    public IAsyncEnumerable<Event> ExecuteStreamingAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        return ExecuteStreamingAsync(input, parentCoordinator: null, cancellationToken);
    }

    /// <summary>
    /// Execute the workflow with streaming events, with optional parent coordinator for event bubbling.
    /// When a parent coordinator is provided, events will automatically bubble up to it.
    /// This enables nested workflows where events from inner workflows appear in the parent's event stream.
    /// </summary>
    /// <param name="input">The input to the workflow.</param>
    /// <param name="parentCoordinator">Optional parent event coordinator for hierarchical event bubbling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Unified stream of graph and agent events.</returns>
    public async IAsyncEnumerable<Event> ExecuteStreamingAsync(
        string input,
        HPD.Events.IEventCoordinator? parentCoordinator,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Create event coordinator for unified streaming
        var eventCoordinator = new EventCoordinator();

        // Set up parent for hierarchical event bubbling if provided
        if (parentCoordinator != null)
        {
            eventCoordinator.SetParent(parentCoordinator);
        }

        // Create context
        var context = new AgentGraphContext(
            executionId: Guid.NewGuid().ToString(),
            graph: _graph,
            services: _serviceProvider,
            agents: _agents,
            agentOptions: _options,
            originalInput: input)
        {
            EventCoordinator = eventCoordinator
        };

        // Set initial input in channels
        context.Channels["input"].Set(input);

        // Create orchestrator with service provider
        var orchestrator = new GraphOrchestrator<AgentGraphContext>(_serviceProvider);

        // Start execution in background task
        var executionTask = Task.Run(async () =>
        {
            try
            {
                await orchestrator.ExecuteAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                // Emit error event
                eventCoordinator.Emit(new MessageTurnErrorEvent(
                    Message: ex.Message,
                    Exception: ex));
            }
        }, cancellationToken);

        // Stream events from coordinator
        await foreach (var evt in eventCoordinator.ReadAllAsync(cancellationToken))
        {
            yield return evt;

            // Check if execution is complete
            if (context.IsComplete || context.IsCancelled)
            {
                break;
            }
        }

        // Wait for execution to complete
        await executionTask;
    }

    /// <summary>
    /// Get the underlying graph definition.
    /// </summary>
    public GraphDefinition Graph => _graph;

    /// <summary>
    /// Get Mermaid diagram of the workflow.
    /// </summary>
    public string ToDiagram()
    {
        return _graph.ToMermaid();
    }

    /// <summary>
    /// Export the workflow configuration as JSON.
    /// </summary>
    public string ExportConfigJson()
    {
        // TODO: Implement config export
        throw new NotImplementedException("Config export not yet implemented");
    }
}

/// <summary>
/// Extension methods for handling approval workflow events.
/// </summary>
public static class ApprovalWorkflowExtensions
{
    /// <summary>
    /// Respond to a node approval request (approve).
    /// </summary>
    /// <param name="coordinator">The event coordinator.</param>
    /// <param name="requestId">The request ID from NodeApprovalRequestEvent.</param>
    /// <param name="reason">Optional reason for approval.</param>
    /// <param name="resumeData">Optional data to pass back to the node.</param>
    public static void Approve(
        this HPD.Events.IEventCoordinator coordinator,
        string requestId,
        string? reason = null,
        object? resumeData = null)
    {
        coordinator.SendResponse(requestId, new NodeApprovalResponseEvent
        {
            RequestId = requestId,
            SourceName = "User",
            Approved = true,
            Reason = reason,
            ResumeData = resumeData
        });
    }

    /// <summary>
    /// Respond to a node approval request (deny).
    /// </summary>
    /// <param name="coordinator">The event coordinator.</param>
    /// <param name="requestId">The request ID from NodeApprovalRequestEvent.</param>
    /// <param name="reason">Reason for denial.</param>
    public static void Deny(
        this HPD.Events.IEventCoordinator coordinator,
        string requestId,
        string reason = "Denied by user")
    {
        coordinator.SendResponse(requestId, new NodeApprovalResponseEvent
        {
            RequestId = requestId,
            SourceName = "User",
            Approved = false,
            Reason = reason
        });
    }

    /// <summary>
    /// Create an approval response event.
    /// </summary>
    public static NodeApprovalResponseEvent CreateApprovalResponse(
        string requestId,
        bool approved,
        string? reason = null,
        object? resumeData = null)
    {
        return new NodeApprovalResponseEvent
        {
            RequestId = requestId,
            SourceName = "User",
            Approved = approved,
            Reason = reason,
            ResumeData = resumeData
        };
    }
}
