using HPDAgent.Graph.Abstractions;
using HPDAgent.Graph.Abstractions.Graph;

namespace HPD.MultiAgent.Routing;

/// <summary>
/// Fluent builder for defining edges between nodes.
/// </summary>
public class EdgeBuilder
{
    private readonly MultiAgent _workflowBuilder;
    private readonly string[] _sourceNodes;

    internal EdgeBuilder(MultiAgent workflowBuilder, string[] sourceNodes)
    {
        _workflowBuilder = workflowBuilder;
        _sourceNodes = sourceNodes;
    }

    /// <summary>
    /// Define target nodes for these edges.
    /// </summary>
    public EdgeTargetBuilder To(params string[] targetNodes)
    {
        return new EdgeTargetBuilder(_workflowBuilder, _sourceNodes, targetNodes);
    }

    /// <summary>
    /// Route by type when using union output.
    /// </summary>
    public TypeRouteBuilder RouteByType()
    {
        if (_sourceNodes.Length != 1)
        {
            throw new InvalidOperationException("RouteByType() can only be used with a single source node");
        }

        return new TypeRouteBuilder(_workflowBuilder, _sourceNodes[0]);
    }
}

/// <summary>
/// Builder for edge targets with optional conditions.
/// </summary>
public class EdgeTargetBuilder
{
    private readonly MultiAgent _workflowBuilder;
    private readonly string[] _sourceNodes;
    private readonly string[] _targetNodes;
    private EdgeCondition? _condition;

    internal EdgeTargetBuilder(
        MultiAgent workflowBuilder,
        string[] sourceNodes,
        string[] targetNodes)
    {
        _workflowBuilder = workflowBuilder;
        _sourceNodes = sourceNodes;
        _targetNodes = targetNodes;

        // Register edges immediately for unconditional case
        RegisterEdges();
    }

    /// <summary>
    /// Add a condition for traversing these edges.
    /// </summary>
    public EdgeTargetBuilder When(Func<EdgeConditionContext, bool> predicate)
    {
        // For now, we'll convert the predicate to a simple condition
        // Full support would require runtime evaluation
        throw new NotSupportedException(
            "Predicate-based conditions are not yet supported. " +
            "Use When(field, value) or WhenEquals(field, value) instead.");
    }

    /// <summary>
    /// Add a field equals condition.
    /// </summary>
    public EdgeTargetBuilder WhenEquals(string field, object value)
    {
        _condition = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = field,
            Value = value
        };
        UpdateEdges();
        return this;
    }

    /// <summary>
    /// Add a field not equals condition.
    /// </summary>
    public EdgeTargetBuilder WhenNotEquals(string field, object value)
    {
        _condition = new EdgeCondition
        {
            Type = ConditionType.FieldNotEquals,
            Field = field,
            Value = value
        };
        UpdateEdges();
        return this;
    }

    /// <summary>
    /// Add a field exists condition.
    /// </summary>
    public EdgeTargetBuilder WhenExists(string field)
    {
        _condition = new EdgeCondition
        {
            Type = ConditionType.FieldExists,
            Field = field
        };
        UpdateEdges();
        return this;
    }

    /// <summary>
    /// Add a field not exists condition.
    /// </summary>
    public EdgeTargetBuilder WhenNotExists(string field)
    {
        _condition = new EdgeCondition
        {
            Type = ConditionType.FieldNotExists,
            Field = field
        };
        UpdateEdges();
        return this;
    }

    /// <summary>
    /// Add a field greater than condition.
    /// </summary>
    public EdgeTargetBuilder WhenGreaterThan(string field, object value)
    {
        _condition = new EdgeCondition
        {
            Type = ConditionType.FieldGreaterThan,
            Field = field,
            Value = value
        };
        UpdateEdges();
        return this;
    }

    /// <summary>
    /// Add a field less than condition.
    /// </summary>
    public EdgeTargetBuilder WhenLessThan(string field, object value)
    {
        _condition = new EdgeCondition
        {
            Type = ConditionType.FieldLessThan,
            Field = field,
            Value = value
        };
        UpdateEdges();
        return this;
    }

    /// <summary>
    /// Add a field contains condition (for strings or collections).
    /// </summary>
    public EdgeTargetBuilder WhenContains(string field, object value)
    {
        _condition = new EdgeCondition
        {
            Type = ConditionType.FieldContains,
            Field = field,
            Value = value
        };
        UpdateEdges();
        return this;
    }

    /// <summary>
    /// Mark this as a default edge (fallback if no other conditions match).
    /// </summary>
    public EdgeTargetBuilder AsDefault()
    {
        _condition = new EdgeCondition
        {
            Type = ConditionType.Default
        };
        UpdateEdges();
        return this;
    }

    /// <summary>
    /// Continue defining more edges.
    /// </summary>
    public EdgeBuilder From(params string[] sourceNodes)
    {
        return _workflowBuilder.From(sourceNodes);
    }

    /// <summary>
    /// Build the workflow.
    /// </summary>
    public Task<AgentWorkflowInstance> BuildAsync(CancellationToken cancellationToken = default)
    {
        return _workflowBuilder.BuildAsync(cancellationToken);
    }

    private void RegisterEdges()
    {
        foreach (var source in _sourceNodes)
        {
            foreach (var target in _targetNodes)
            {
                _workflowBuilder.AddEdgeInternal(source, target, _condition);
            }
        }
    }

    private void UpdateEdges()
    {
        // Remove previously registered edges and add with new condition
        foreach (var source in _sourceNodes)
        {
            foreach (var target in _targetNodes)
            {
                _workflowBuilder.UpdateEdgeCondition(source, target, _condition);
            }
        }
    }
}

/// <summary>
/// Context for evaluating edge conditions.
/// </summary>
public class EdgeConditionContext
{
    private readonly Dictionary<string, object> _outputs;

    internal EdgeConditionContext(Dictionary<string, object> outputs)
    {
        _outputs = outputs;
    }

    /// <summary>
    /// Get a value from the source node's outputs.
    /// </summary>
    public T Get<T>(string key)
    {
        if (_outputs.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return default!;
    }

    /// <summary>
    /// Check if a key exists in the outputs.
    /// </summary>
    public bool HasKey(string key)
    {
        return _outputs.ContainsKey(key);
    }
}

/// <summary>
/// Builder for type-based routing with union outputs.
/// </summary>
public class TypeRouteBuilder
{
    private readonly MultiAgent _workflowBuilder;
    private readonly string _sourceNode;
    private readonly List<(Type Type, string Target)> _routes = new();

    internal TypeRouteBuilder(MultiAgent workflowBuilder, string sourceNode)
    {
        _workflowBuilder = workflowBuilder;
        _sourceNode = sourceNode;
    }

    /// <summary>
    /// Route to target when the matched type is T.
    /// </summary>
    public TypeRouteBuilder When<T>(string targetNode)
    {
        _routes.Add((typeof(T), targetNode));

        // Register edge with FieldEquals condition on matched_type
        _workflowBuilder.AddEdgeInternal(_sourceNode, targetNode, new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "matched_type",
            Value = typeof(T).Name
        });

        return this;
    }

    /// <summary>
    /// Add a default route for unmatched types.
    /// </summary>
    public TypeRouteBuilder Default(string targetNode)
    {
        _workflowBuilder.AddEdgeInternal(_sourceNode, targetNode, new EdgeCondition
        {
            Type = ConditionType.Default
        });

        return this;
    }

    /// <summary>
    /// Continue defining more edges.
    /// </summary>
    public EdgeBuilder From(params string[] sourceNodes)
    {
        return _workflowBuilder.From(sourceNodes);
    }

    /// <summary>
    /// Build the workflow.
    /// </summary>
    public Task<AgentWorkflowInstance> BuildAsync(CancellationToken cancellationToken = default)
    {
        return _workflowBuilder.BuildAsync(cancellationToken);
    }
}
