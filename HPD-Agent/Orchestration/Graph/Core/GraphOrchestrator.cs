using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Graph orchestrator that executes a declarative workflow definition using a registry of executable components.
/// </summary>
public class GraphOrchestrator<TState> : IOrchestrator where TState : class, new()
{
    private readonly WorkflowDefinition _workflow;
    private readonly WorkflowRegistry<TState> _registry;
    private readonly ICheckpointStore<TState>? _checkpointStore;
    private readonly AggregatorCollection _aggregators = new();

    public GraphOrchestrator(
        WorkflowDefinition workflow,
        WorkflowRegistry<TState> registry,
        ICheckpointStore<TState>? checkpointStore = null)
    {
        _workflow = workflow;
        _registry = registry;
        _checkpointStore = checkpointStore;
        ValidateWorkflow();
    }

    public async Task<ChatResponse> OrchestrateAsync(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<Agent> agents, // Note: agents are now part of StateNode, this is for interface compliance
        string? conversationId = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var allUpdates = new List<ChatResponseUpdate>();
        await foreach (var update in OrchestrateStreamingAsync(history, agents, conversationId, options, cancellationToken))
        {
            allUpdates.Add(update);
        }
        return ConstructChatResponseFromUpdates(allUpdates);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> OrchestrateStreamingAsync(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<Agent> agents,
        string? conversationId = null,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var context = await RestoreOrCreateWorkflowContext(conversationId);
        var stopwatch = new Stopwatch();

        while (context.CurrentNodeId != null && context.CurrentNodeId != "END" && !cancellationToken.IsCancellationRequested)
        {
            var workflowNode = _workflow.Nodes.First(n => n.Id == context.CurrentNodeId);
            var stateNode = _registry.GetNode(workflowNode.NodeKey);

            var inputState = context.State; // Snapshot state before execution
            stopwatch.Restart();

            // Execute the node's logic
            var outputState = await stateNode.ExecuteAsync(context, history, cancellationToken);
            
            stopwatch.Stop();

            // Determine the next node
            var (nextNodeId, conditionKey, conditionResult) = await GetNextNodeDetails(context.CurrentNodeId, outputState);

            // Create the execution step for the trace
            var step = new ExecutionStep(
                workflowNode.Id,
                workflowNode.NodeKey,
                inputState,
                outputState,
                conditionKey,
                conditionResult,
                stopwatch.Elapsed
            );

            // Update the context with the new state and trace
            var newTrace = new List<ExecutionStep>(context.Trace) { step };
            context = new WorkflowContext<TState>(outputState, nextNodeId, newTrace, _aggregators);
            
            if (_checkpointStore != null && conversationId != null)
            {
                await _checkpointStore.SaveAsync(conversationId, context, cancellationToken);
            }

            // Yield a representation of the state change (implementation detail)
            // For now, we can yield a simple text update.
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"Completed step: {workflowNode.NodeKey}. New state: {outputState.ToString()}");

            // Reset aggregators for the next superstep
            _aggregators.ResetAll();
        }
    }
    
    private async Task<(string? NextNodeId, string? ConditionKey, string? ConditionResult)> GetNextNodeDetails(string currentNodeId, TState state)
    {
        foreach (var edge in _workflow.Edges.Where(e => e.FromNodeId == currentNodeId))
        {            var conditionFunc = _registry.GetCondition(edge.ConditionKey);
            var resultKey = await conditionFunc(state);

            if (resultKey != null && edge.RouteMap.TryGetValue(resultKey, out var targetNodeId))
            {                return (targetNodeId, edge.ConditionKey, resultKey);
            }
            
            // Handle direct edges where ToNodeId is specified and condition is simple
            if (edge.ToNodeId != null && (resultKey == "true" || resultKey == null))
            {                 return (edge.ToNodeId, edge.ConditionKey, resultKey);
            }
        }
        return (null, null, null); // End of workflow
    }

    private async Task<WorkflowContext<TState>> RestoreOrCreateWorkflowContext(string? conversationId)
    {
        if (_checkpointStore != null && conversationId != null)
        {
            var restored = await _checkpointStore.LoadAsync(conversationId, default);
            if (restored != null)
                return restored;
        }

        return new WorkflowContext<TState>(
            State: new TState(),
            CurrentNodeId: _workflow.StartNodeId,
            Trace: new List<ExecutionStep>(),
            Aggregators: _aggregators // Initialize here
        );
    }
    
    private static ChatResponse ConstructChatResponseFromUpdates(List<ChatResponseUpdate> updates)
    {
        // This helper can be simplified or enhanced later.
        var lastContent = updates.LastOrDefault()?.Text ?? string.Empty;
        var message = new ChatMessage(ChatRole.Assistant, lastContent);
        return new ChatResponse(message);
    }

    private void ValidateWorkflow()
    {
        var nodeKeys = new HashSet<string>(_registry.GetAllNodeKeys());
        var conditionKeys = new HashSet<string>(_registry.GetAllConditionKeys());

        if (!_workflow.Nodes.Any(n => n.Id == _workflow.StartNodeId))
        {
            throw new InvalidOperationException($"Start node '{_workflow.StartNodeId}' is not defined in the workflow's node list.");
        }

        foreach (var node in _workflow.Nodes)
        {
            if (!nodeKeys.Contains(node.NodeKey))
            {
                throw new InvalidOperationException($"Node with ID '{node.Id}' references NodeKey '{node.NodeKey}', which is not registered.");
            }
        }

        foreach (var edge in _workflow.Edges)
        {
            if (!_workflow.Nodes.Any(n => n.Id == edge.FromNodeId))
            {
                throw new InvalidOperationException($"Edge references a 'FromNodeId' of '{edge.FromNodeId}', which is not a defined node.");
            }

            if (!conditionKeys.Contains(edge.ConditionKey))
            {
                throw new InvalidOperationException($"Edge from '{edge.FromNodeId}' references ConditionKey '{edge.ConditionKey}', which is not registered.");
            }
        }
    }
}