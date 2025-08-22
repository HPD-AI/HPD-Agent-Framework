using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

/// <summary>
/// Graph orchestrator that executes a workflow definition over multiple agents.
/// </summary>
public class GraphOrchestrator<TState> : IOrchestrator where TState : class, new()
{
    private readonly WorkflowDefinition _workflow;
    private readonly IReadOnlyDictionary<string, Agent> _agents;
    private readonly IConditionEvaluator<TState> _conditionEvaluator;
    private readonly ICheckpointStore<TState>? _checkpointStore;
    // Track which agents were used during execution
    private readonly List<string> _usedAgentsDuringExecution = new();

    public GraphOrchestrator(
        WorkflowDefinition workflow,
        IEnumerable<Agent> agents,
        IConditionEvaluator<TState>? conditionEvaluator = null,
        ICheckpointStore<TState>? checkpointStore = null)
    {
        _workflow = workflow;
        _agents = agents.ToDictionary(a => a.Name, a => a);
        _conditionEvaluator = conditionEvaluator ?? new SmartDefaultEvaluator<TState>();
        _checkpointStore = checkpointStore;
        ValidateWorkflow();
    }

    public async Task<ChatResponse> OrchestrateAsync(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<Agent> agents,
        string? conversationId = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Collect all streaming updates and return the final response
        var allUpdates = new List<ChatResponseUpdate>();
        await foreach (var update in OrchestrateStreamingAsync(history, agents, conversationId, options, cancellationToken))
        {
            allUpdates.Add(update);
        }

        // Return constructed response from all updates
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

        while (context.CurrentNodeId is not null && !cancellationToken.IsCancellationRequested)
        {
            // Create a new list to hold updates from the current node's execution
            var responseUpdates = new List<ChatResponseUpdate>();

            // Execute the node and stream its results
            await foreach (var update in ExecuteNodeStreamingAsync(context, history, options, cancellationToken))
            {
                responseUpdates.Add(update);
                yield return update; // Yield the update to the caller immediately
            }

            // After the stream for the current node is complete, update the context
            context = await UpdateWorkflowContextAfterStreamingAsync(context, responseUpdates, cancellationToken);
            
            if (_checkpointStore != null && conversationId != null)
            {
                await _checkpointStore.SaveAsync(conversationId, context, cancellationToken);
            }

            // Determine the next node to execute
            context = context with { CurrentNodeId = await GetNextNodeAsync(context, history, cancellationToken) };
        }
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
            ConversationId: conversationId,
            CurrentNodeId: _workflow.StartNodeId,
            LastUpdatedAt: DateTime.UtcNow
        );
    }


    // Determine next node based on condition evaluation
    private async Task<string?> GetNextNodeAsync(
        WorkflowContext<TState> context,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        // Evaluate outgoing edges; return first matching target or null
        foreach (var edge in _workflow.Edges.Where(e => e.FromNodeId == context.CurrentNodeId!))
        {
            // Create a temporary context with history for condition evaluation
            var contextWithHistory = CreateContextWithHistory(context, history);
            if (await _conditionEvaluator.EvaluateAsync(edge.Condition, contextWithHistory, cancellationToken))
                return edge.ToNodeId;
        }
        return null;
    }

    // Helper method to create context with history for condition evaluation only
    private WorkflowContext<TState> CreateContextWithHistory(WorkflowContext<TState> context, IReadOnlyList<ChatMessage> history)
    {
        // This is a temporary shim - ideally condition evaluators should not need full history
        // but work with workflow state only. For now, we create a compatibility layer.
        return context;
    }

    /// <summary>
    /// Override this method for custom state update logic. Default is no-op.
    /// </summary>
    protected virtual Task<TState> UpdateStateAsync(
        TState currentState,
        WorkflowNode node,
        ChatResponse response,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(currentState);
    }

    /// <summary>
    /// Override this method for node-specific input mapping. Default passes full history.
    /// </summary>
    protected virtual IReadOnlyList<ChatMessage> ApplyInputMappings(
        WorkflowContext<TState> context,
        WorkflowNode node,
        IReadOnlyList<ChatMessage> history)
    {
        return history;
    }

    private WorkflowNode GetNode(string nodeId)
        => _workflow.Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new InvalidOperationException($"Node '{nodeId}' not found in workflow '{_workflow.Name}'");

    private Agent GetAgent(string agentName)
        => _agents.TryGetValue(agentName, out var agent)
            ? agent
            : throw new InvalidOperationException($"Agent '{agentName}' not registered. Available agents: {string.Join(", ", _agents.Keys)}");

    private static IEnumerable<ChatMessage> ExtractMessages(ChatResponse response)
        => response.Messages?.Any() == true
            ? response.Messages
            : Array.Empty<ChatMessage>();

    private void ValidateWorkflow()
    {
        var nodeIds = _workflow.Nodes.Select(n => n.Id).ToHashSet();
        if (!nodeIds.Contains(_workflow.StartNodeId))
            throw new ArgumentException($"Start node '{_workflow.StartNodeId}' not found in workflow '{_workflow.Name}'");
        foreach (var edge in _workflow.Edges)
        {
            if (!nodeIds.Contains(edge.FromNodeId) || !nodeIds.Contains(edge.ToNodeId))
                throw new ArgumentException($"Invalid edge in workflow '{_workflow.Name}': {edge.FromNodeId} -> {edge.ToNodeId}");
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> ExecuteNodeStreamingAsync(
        WorkflowContext<TState> context,
        IReadOnlyList<ChatMessage> history,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var node = GetNode(context.CurrentNodeId!);
        var agent = GetAgent(node.AgentName);
        _usedAgentsDuringExecution.Add(agent.Name);
        var effectiveHistory = ApplyInputMappings(context, node, history);

        // Get the streaming response from the agent
        var streamingResponse = agent.GetStreamingResponseAsync(effectiveHistory, options, cancellationToken);

        await foreach (var update in streamingResponse)
        {
            yield return update;
        }
    }

    private async Task<WorkflowContext<TState>> UpdateWorkflowContextAfterStreamingAsync(
        WorkflowContext<TState> context,
        List<ChatResponseUpdate> updates,
        CancellationToken cancellationToken)
    {
        // Reconstruct a ChatResponse from the streamed updates for state update only
        var finalResponse = ConstructChatResponseFromUpdates(updates);

        var node = GetNode(context.CurrentNodeId!);
        var updatedState = await UpdateStateAsync(context.State, node, finalResponse, cancellationToken);

        return context with
        {
            State = updatedState,
            LastUpdatedAt = DateTime.UtcNow
        };
    }

    private static ChatResponse ConstructChatResponseFromUpdates(List<ChatResponseUpdate> updates)
    {
        // Collect all content from the updates
        var allContents = new List<AIContent>();
        ChatFinishReason? finishReason = null;
        string? modelId = null;
        string? responseId = null;
        DateTimeOffset? createdAt = null;
        
        foreach (var update in updates)
        {
            if (update.Contents != null)
            {
                allContents.AddRange(update.Contents);
            }
            
            if (update.FinishReason != null)
                finishReason = update.FinishReason;
                
            if (update.ModelId != null)
                modelId = update.ModelId;
                
            if (update.ResponseId != null)
                responseId = update.ResponseId;
                
            if (update.CreatedAt != null)
                createdAt = update.CreatedAt;
        }
        
        // Create a ChatMessage from the collected content
        var chatMessage = new ChatMessage(ChatRole.Assistant, allContents)
        {
            MessageId = responseId
        };
        
        return new ChatResponse(chatMessage)
        {
            FinishReason = finishReason,
            ModelId = modelId,
            CreatedAt = createdAt
        };
    }
}
