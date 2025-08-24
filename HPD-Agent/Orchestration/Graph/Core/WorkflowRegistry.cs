using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// A delegate for a strongly-typed, executable condition that determines the next node in a workflow.
/// </summary>
/// <returns>A string key representing the outcome, which maps to the next node.</returns>
public delegate Task<string?> ConditionFunc<TState>(TState state);

/// <summary>
/// A runtime registry that maps string keys from a WorkflowDefinition to executable logic.
/// </summary>
/// <typeparam name="TState">The workflow's state type.</typeparam>
public class WorkflowRegistry<TState> where TState : class, new()
{
    private readonly Dictionary<string, StateNode<TState>> _nodes = new();
    private readonly Dictionary<string, ConditionFunc<TState>> _conditions = new();

    /// <summary>
    /// Registers an executable node with a unique key.
    /// </summary>
    public void RegisterNode(string key, StateNode<TState> node) => _nodes[key] = node;

    /// <summary>
    /// Registers a conditional function with a unique key.
    /// </summary>
    public void RegisterCondition(string key, ConditionFunc<TState> condition) => _conditions[key] = condition;

    internal StateNode<TState> GetNode(string key) => _nodes[key];
    internal ConditionFunc<TState> GetCondition(string key) => _conditions[key];

    internal IEnumerable<string> GetAllNodeKeys() => _nodes.Keys;
    internal IEnumerable<string> GetAllConditionKeys() => _conditions.Keys;
}