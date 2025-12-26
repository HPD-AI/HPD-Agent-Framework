using HPDAgent.Graph.Abstractions.Graph;

namespace HPDAgent.Graph.Core.Orchestration;

/// <summary>
/// Evaluates edge conditions against node outputs.
/// Supports declarative condition evaluation without lambdas.
/// </summary>
public static class ConditionEvaluator
{
    /// <summary>
    /// Evaluate a condition against node outputs.
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
            _ => false
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
}
