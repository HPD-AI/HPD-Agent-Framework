using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A high-level facade for creating agent-based workflows with a fluent API.
/// This builder automates the creation of StateNodes, ConditionFuncs, and the underlying GraphOrchestrator.
/// </summary>
public class AgentWorkflowBuilder<TState> where TState : class, new()
{
    private readonly WorkflowBuilder _internalBuilder;
    private readonly WorkflowRegistry<TState> _registry = new();
    private readonly Dictionary<string, Agent> _agents = new();

    private AgentWorkflowBuilder(string workflowName)
    {
        _internalBuilder = WorkflowBuilder.Create(workflowName);
    }

    public static AgentWorkflowBuilder<TState> Create(string workflowName = "AgentWorkflow")
    {
        return new AgentWorkflowBuilder<TState>(workflowName);
    }

    /// <summary>
    /// Adds an agent as a node in the workflow and defines how its response maps to the workflow state.
    /// </summary>
    /// <param name="agent">The agent to execute for this node.</param>
    /// <param name="nodeName">A unique name for this node in the workflow.</param>
    /// <param name="mapsResponseTo">A function that takes the agent's response and the current state, and returns the updated state.</param>
    public AgentWorkflowBuilder<TState> AddAgent(
        Agent agent,
        string nodeName,
        Func<ChatResponse, TState, TState> mapsResponseTo)
    {
        _agents[nodeName] = agent;

        // Dynamically create a StateNode wrapper for this agent
        var agentStateNode = new AgentStateNode(agent, mapsResponseTo);
        _registry.RegisterNode(nodeName, agentStateNode);
        
        // If this is the first agent, set it as the start node
        if (_agents.Count == 1)
        {
            _internalBuilder.StartWith(nodeName, nodeName);
        }
        else
        {
            _internalBuilder.AddNode(nodeName, nodeName);
        }

        return this;
    }
    
    /// <summary>
    /// Adds an agent as a node and provides a simple mapping from the agent's text response to a string property on the state object.
    /// </summary>
    public AgentWorkflowBuilder<TState> AddAgent(
        Agent agent,
        string nodeName,
        Expression<Func<TState, string>> propertyExpression)
    {
        var propertyInfo = GetPropertyInfo(propertyExpression);

        Func<ChatResponse, TState, TState> mapFunc = (response, currentState) =>
        {
            var newState = CloneState(currentState);
            propertyInfo.SetValue(newState, response.GetTextContent());
            return newState;
        };

        return AddAgent(agent, nodeName, mapFunc);
    }

    /// <summary>
    /// Begins defining transitions from a specific node.
    /// </summary>
    public AgentWorkflowTransitionBuilder<TState> From(string fromNodeName)
    {
        return new AgentWorkflowTransitionBuilder<TState>(this, _internalBuilder, _registry, fromNodeName);
    }

    /// <summary>
    /// Builds and returns a ready-to-use IOrchestrator instance.
    /// </summary>
    public IOrchestrator BuildOrchestrator(ICheckpointStore<TState>? checkpointStore = null)
    {
        var workflowDefinition = _internalBuilder.Build();
        return new GraphOrchestrator<TState>(workflowDefinition, _registry, checkpointStore);
    }

    // --- Helper for the StateNode wrapper ---
    private class AgentStateNode : StateNode<TState>
    {
        private readonly Agent _agent;
        private readonly Func<ChatResponse, TState, TState> _mapFunc;

        public AgentStateNode(Agent agent, Func<ChatResponse, TState, TState> mapFunc)
        {
            _agent = agent;
            _mapFunc = mapFunc;
        }

        public override async Task<TState> ExecuteAsync(WorkflowContext<TState> context, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
        {
            var response = await _agent.GetResponseAsync(history, cancellationToken: cancellationToken);
            return _mapFunc(response, context.State);
        }
    }

    // --- Helpers for Expression-based mapping ---
    private static PropertyInfo GetPropertyInfo(Expression<Func<TState, string>> propertyLambda)
    {
        if (propertyLambda.Body is MemberExpression member)
        {
            if (member.Member is PropertyInfo property)
            {
                return property;
            }
        }
        throw new ArgumentException("Expression is not a valid property accessor.", nameof(propertyLambda));
    }

    private static TState CloneState(TState original)
    {
        // This simple clone works for records. For classes, a more robust cloning mechanism might be needed.
        if (original is ICloneable cloneable)
        {
            return (TState)cloneable.Clone();
        }
        // Fallback to reflection-based copy if TState is a class without ICloneable
        var newState = new TState();
        foreach (var prop in typeof(TState).GetProperties())
        {
            if (prop.CanWrite)
            {
                prop.SetValue(newState, prop.GetValue(original));
            }
        }
        return newState;
    }
}

/// <summary>
/// Helper class to provide a fluent API for defining workflow transitions.
/// </summary>
public class AgentWorkflowTransitionBuilder<TState> where TState : class, new()
{
    private readonly AgentWorkflowBuilder<TState> _parentBuilder;
    private readonly WorkflowBuilder _internalBuilder;
    private readonly WorkflowRegistry<TState> _registry;
    private readonly string _fromNodeName;

    internal AgentWorkflowTransitionBuilder(
        AgentWorkflowBuilder<TState> parent,
        WorkflowBuilder internalBuilder,
        WorkflowRegistry<TState> registry,
        string fromNodeName)
    {
        _parentBuilder = parent;
        _internalBuilder = internalBuilder;
        _registry = registry;
        _fromNodeName = fromNodeName;
    }

    /// <summary>
    /// Creates an unconditional transition to the specified node.
    /// </summary>
    public AgentWorkflowBuilder<TState> GoTo(string toNodeName)
    {
        _internalBuilder.AddEdge(_fromNodeName, toNodeName);
        return _parentBuilder;
    }

    /// <summary>
    /// Creates a conditional branch based on a boolean evaluation of the state.
    /// </summary>
    public AgentWorkflowBuilder<TState> Branch(
        Func<TState, bool> when,
        string ifTrue,
        string ifFalse)
    {
        var conditionKey = $"{_fromNodeName}_Branch_{Guid.NewGuid():N}";
        
        ConditionFunc<TState> conditionFunc = state => 
            Task.FromResult<string?>(when(state) ? "true" : "false");
            
        _registry.RegisterCondition(conditionKey, conditionFunc);

        var routeMap = new Dictionary<string, string>
        {
            { "true", ifTrue },
            { "false", ifFalse }
        };

        _internalBuilder.AddConditionalEdge(_fromNodeName, conditionKey, routeMap);
        return _parentBuilder;
    }

    /// <summary>
    /// Creates a multi-way conditional branch based on a string result from a state evaluation.
    /// </summary>
    public AgentWorkflowBuilder<TState> Branch(
        Func<TState, string> on,
        IReadOnlyDictionary<string, string> routes)
    {
        var conditionKey = $"{_fromNodeName}_MultiBranch_{Guid.NewGuid():N}";

        ConditionFunc<TState> conditionFunc = state => Task.FromResult<string?>(on(state));

        _registry.RegisterCondition(conditionKey, conditionFunc);
        _internalBuilder.AddConditionalEdge(_fromNodeName, conditionKey, routes);
        return _parentBuilder;
    }
}