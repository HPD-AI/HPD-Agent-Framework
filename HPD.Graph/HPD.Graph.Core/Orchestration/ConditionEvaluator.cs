using HPDAgent.Graph.Abstractions.Context;
using HPDAgent.Graph.Abstractions.Execution;
using HPDAgent.Graph.Abstractions.Graph;

namespace HPDAgent.Graph.Core.Orchestration;

/// <summary>
/// Evaluates edge conditions against node outputs.
/// Supports declarative condition evaluation without lambdas.
/// </summary>
public static class ConditionEvaluator
{
    /// <summary>
    /// Evaluate a condition against node outputs (legacy method for backward compatibility).
    /// For upstream conditions, use the overload that accepts context and edge.
    /// </summary>
    /// <param name="condition">Condition to evaluate</param>
    /// <param name="nodeOutputs">Outputs from the source node</param>
    /// <returns>True if condition is met, false otherwise</returns>
    public static bool Evaluate(EdgeCondition? condition, Dictionary<string, object>? nodeOutputs)
    {
        // Null condition means always traverse (unconditional edge)
        if (condition == null)
        {
            return true;
        }

        // No outputs means we can't evaluate field-based conditions
        if (nodeOutputs == null || nodeOutputs.Count == 0)
        {
            return condition.Type == ConditionType.Always;
        }

        return condition.Type switch
        {
            ConditionType.Always => true,
            ConditionType.FieldEquals => EvaluateFieldEquals(condition, nodeOutputs),
            ConditionType.FieldNotEquals => !EvaluateFieldEquals(condition, nodeOutputs),
            ConditionType.FieldGreaterThan => EvaluateFieldGreaterThan(condition, nodeOutputs),
            ConditionType.FieldLessThan => EvaluateFieldLessThan(condition, nodeOutputs),
            ConditionType.FieldExists => EvaluateFieldExists(condition, nodeOutputs),
            ConditionType.FieldNotExists => !EvaluateFieldExists(condition, nodeOutputs),
            ConditionType.FieldContains => EvaluateFieldContains(condition, nodeOutputs),
            ConditionType.UpstreamOneSuccess => throw new InvalidOperationException("Upstream conditions require context. Use Evaluate(condition, nodeOutputs, context, edge) overload."),
            ConditionType.UpstreamAllDone => throw new InvalidOperationException("Upstream conditions require context. Use Evaluate(condition, nodeOutputs, context, edge) overload."),
            ConditionType.UpstreamAllDoneOneSuccess => throw new InvalidOperationException("Upstream conditions require context. Use Evaluate(condition, nodeOutputs, context, edge) overload."),
            _ => false
        };
    }

    /// <summary>
    /// Evaluate a condition against node outputs and/or upstream states.
    /// </summary>
    /// <param name="condition">Condition to evaluate (null = always traverse)</param>
    /// <param name="nodeOutputs">Outputs from source node (for field-based conditions)</param>
    /// <param name="context">Graph context (required for upstream conditions)</param>
    /// <param name="edge">Edge being evaluated (required for upstream conditions)</param>
    /// <returns>True if condition is met, false otherwise</returns>
    public static bool Evaluate(
        EdgeCondition? condition,
        Dictionary<string, object>? nodeOutputs,
        IGraphContext context,
        Edge edge)
    {
        if (condition == null)
            return true;

        return condition.Type switch
        {
            // Field-based conditions (use existing logic)
            ConditionType.Always => true,
            ConditionType.FieldEquals => EvaluateFieldEquals(condition, nodeOutputs),
            ConditionType.FieldNotEquals => !EvaluateFieldEquals(condition, nodeOutputs),
            ConditionType.FieldGreaterThan => EvaluateFieldGreaterThan(condition, nodeOutputs),
            ConditionType.FieldLessThan => EvaluateFieldLessThan(condition, nodeOutputs),
            ConditionType.FieldExists => EvaluateFieldExists(condition, nodeOutputs),
            ConditionType.FieldNotExists => !EvaluateFieldExists(condition, nodeOutputs),
            ConditionType.FieldContains => EvaluateFieldContains(condition, nodeOutputs),
            ConditionType.Default => true, // Default edges handled separately

            // Upstream state conditions (new)
            ConditionType.UpstreamOneSuccess => EvaluateUpstreamOneSuccess(context, edge),
            ConditionType.UpstreamAllDone => EvaluateUpstreamAllDone(context, edge),
            ConditionType.UpstreamAllDoneOneSuccess => EvaluateUpstreamAllDoneOneSuccess(context, edge),

            _ => throw new InvalidOperationException($"Unknown condition type: {condition.Type}")
        };
    }

    private static bool EvaluateFieldEquals(EdgeCondition condition, Dictionary<string, object> nodeOutputs)
    {
        if (string.IsNullOrWhiteSpace(condition.Field))
        {
            return false;
        }

        if (!nodeOutputs.TryGetValue(condition.Field, out var fieldValue))
        {
            return false;
        }

        // Handle null comparisons
        if (fieldValue == null && condition.Value == null)
        {
            return true;
        }

        if (fieldValue == null || condition.Value == null)
        {
            return false;
        }

        // Try direct equality
        if (fieldValue.Equals(condition.Value))
        {
            return true;
        }

        // Try string comparison
        if (fieldValue.ToString() == condition.Value.ToString())
        {
            return true;
        }

        return false;
    }

    private static bool EvaluateFieldGreaterThan(EdgeCondition condition, Dictionary<string, object> nodeOutputs)
    {
        if (string.IsNullOrWhiteSpace(condition.Field))
        {
            return false;
        }

        if (!nodeOutputs.TryGetValue(condition.Field, out var fieldValue))
        {
            return false;
        }

        if (fieldValue == null || condition.Value == null)
        {
            return false;
        }

        // Try numeric comparison
        if (TryGetNumericValue(fieldValue, out var fieldNum) &&
            TryGetNumericValue(condition.Value, out var conditionNum))
        {
            return fieldNum > conditionNum;
        }

        // Try string comparison
        if (fieldValue is string fieldStr && condition.Value is string conditionStr)
        {
            return string.Compare(fieldStr, conditionStr, StringComparison.Ordinal) > 0;
        }

        return false;
    }

    private static bool EvaluateFieldLessThan(EdgeCondition condition, Dictionary<string, object> nodeOutputs)
    {
        if (string.IsNullOrWhiteSpace(condition.Field))
        {
            return false;
        }

        if (!nodeOutputs.TryGetValue(condition.Field, out var fieldValue))
        {
            return false;
        }

        if (fieldValue == null || condition.Value == null)
        {
            return false;
        }

        // Try numeric comparison
        if (TryGetNumericValue(fieldValue, out var fieldNum) &&
            TryGetNumericValue(condition.Value, out var conditionNum))
        {
            return fieldNum < conditionNum;
        }

        // Try string comparison
        if (fieldValue is string fieldStr && condition.Value is string conditionStr)
        {
            return string.Compare(fieldStr, conditionStr, StringComparison.Ordinal) < 0;
        }

        return false;
    }

    private static bool EvaluateFieldExists(EdgeCondition condition, Dictionary<string, object> nodeOutputs)
    {
        if (string.IsNullOrWhiteSpace(condition.Field))
        {
            return false;
        }

        return nodeOutputs.ContainsKey(condition.Field) && nodeOutputs[condition.Field] != null;
    }

    private static bool EvaluateFieldContains(EdgeCondition condition, Dictionary<string, object> nodeOutputs)
    {
        if (string.IsNullOrWhiteSpace(condition.Field))
        {
            return false;
        }

        if (!nodeOutputs.TryGetValue(condition.Field, out var fieldValue))
        {
            return false;
        }

        if (fieldValue == null || condition.Value == null)
        {
            return false;
        }

        // String contains
        if (fieldValue is string fieldStr && condition.Value is string conditionStr)
        {
            return fieldStr.Contains(conditionStr, StringComparison.OrdinalIgnoreCase);
        }

        // Collection contains
        if (fieldValue is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item != null && item.Equals(condition.Value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetNumericValue(object value, out double result)
    {
        result = 0;

        if (value == null)
        {
            return false;
        }

        // Direct numeric types
        if (value is double d)
        {
            result = d;
            return true;
        }

        if (value is float f)
        {
            result = f;
            return true;
        }

        if (value is int i)
        {
            result = i;
            return true;
        }

        if (value is long l)
        {
            result = l;
            return true;
        }

        if (value is decimal dec)
        {
            result = (double)dec;
            return true;
        }

        // Try parsing string
        if (value is string str)
        {
            return double.TryParse(str, out result);
        }

        return false;
    }

    // ========================================
    // Upstream State Condition Evaluation
    // ========================================

    /// <summary>
    /// Evaluate UpstreamOneSuccess: At least one upstream must have succeeded.
    /// </summary>
    private static bool EvaluateUpstreamOneSuccess(IGraphContext context, Edge edge)
    {
        var upstreamNodes = GetUpstreamNodes(context.Graph, edge.To);

        // At least one upstream must have succeeded
        foreach (var upstream in upstreamNodes)
        {
            if (!context.IsNodeComplete(upstream.Id))
                continue; // Still waiting

            var resultChannel = context.Channels[$"node_result:{upstream.Id}"];
            var result = resultChannel.Get<NodeExecutionResult>();
            if (result is NodeExecutionResult.Success)
            {
                return true; // Found a success - traverse
            }
        }

        // Check if all completed (no success found)
        if (upstreamNodes.All(u => context.IsNodeComplete(u.Id)))
            return false; // All done, none succeeded - don't traverse

        // Still waiting for some upstreams
        return false;
    }

    /// <summary>
    /// Evaluate UpstreamAllDone: All upstreams must be complete (any state).
    /// </summary>
    private static bool EvaluateUpstreamAllDone(IGraphContext context, Edge edge)
    {
        var upstreamNodes = GetUpstreamNodes(context.Graph, edge.To);

        // All upstreams must be complete (any state)
        return upstreamNodes.All(u => context.IsNodeComplete(u.Id));
    }

    /// <summary>
    /// Evaluate UpstreamAllDoneOneSuccess: All must complete AND at least one must succeed.
    /// </summary>
    private static bool EvaluateUpstreamAllDoneOneSuccess(IGraphContext context, Edge edge)
    {
        var upstreamNodes = GetUpstreamNodes(context.Graph, edge.To);

        // All must complete AND at least one must succeed
        if (!upstreamNodes.All(u => context.IsNodeComplete(u.Id)))
            return false;

        return upstreamNodes.Any(u =>
        {
            var resultChannel = context.Channels[$"node_result:{u.Id}"];
            var result = resultChannel.Get<NodeExecutionResult>();
            return result is NodeExecutionResult.Success;
        });
    }

    /// <summary>
    /// Get all upstream nodes (nodes with edges pointing to targetNodeId).
    /// </summary>
    private static IReadOnlyList<Node> GetUpstreamNodes(Abstractions.Graph.Graph graph, string targetNodeId)
    {
        var upstreamIds = graph.Edges
            .Where(e => e.To == targetNodeId)
            .Select(e => e.From)
            .Distinct()
            .ToList();

        return graph.Nodes
            .Where(n => upstreamIds.Contains(n.Id))
            .ToList();
    }
}
