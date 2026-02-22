namespace HPDAgent.Graph.Abstractions.Graph;

/// <summary>
/// Type of condition for edge traversal.
/// </summary>
public enum ConditionType
{
    /// <summary>
    /// Always traverse (unconditional edge).
    /// </summary>
    Always,

    /// <summary>
    /// Traverse if field equals value.
    /// </summary>
    FieldEquals,

    /// <summary>
    /// Traverse if field does not equal value.
    /// </summary>
    FieldNotEquals,

    /// <summary>
    /// Traverse if field is greater than value.
    /// </summary>
    FieldGreaterThan,

    /// <summary>
    /// Traverse if field is less than value.
    /// </summary>
    FieldLessThan,

    /// <summary>
    /// Traverse if field exists (not null).
    /// </summary>
    FieldExists,

    /// <summary>
    /// Traverse if field does not exist (is null).
    /// </summary>
    FieldNotExists,

    /// <summary>
    /// Traverse if field contains value (for strings/collections).
    /// </summary>
    FieldContains,

    /// <summary>
    /// Default/fallback edge - traverse only if no other conditions from the same source node match.
    /// Only one default edge per source node is allowed.
    /// </summary>
    Default,

    // ========================================
    // Upstream State Conditions
    // ========================================

    /// <summary>
    /// Traverse if at least one upstream node succeeded.
    /// Use case: Fallback chains (try A, if fails try B).
    /// Requires context at evaluation time.
    /// </summary>
    UpstreamOneSuccess,

    /// <summary>
    /// Traverse if all upstream nodes completed (any terminal state).
    /// Use case: Aggregation regardless of success/failure.
    /// Requires context at evaluation time.
    /// </summary>
    UpstreamAllDone,

    /// <summary>
    /// Traverse if all upstream nodes completed AND at least one succeeded.
    /// Use case: Partial success handling.
    /// Requires context at evaluation time.
    /// </summary>
    UpstreamAllDoneOneSuccess
}

/// <summary>
/// Condition for edge traversal (declarative only - no lambdas).
/// Evaluated against node outputs.
/// </summary>
public sealed record EdgeCondition
{
    /// <summary>
    /// Condition type.
    /// </summary>
    public required ConditionType Type { get; init; }

    /// <summary>
    /// Field name to evaluate (from node outputs).
    /// Required for field-based conditions.
    /// </summary>
    public string? Field { get; init; }

    /// <summary>
    /// Value to compare against.
    /// Required for comparison conditions.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// Get a human-readable description of this condition.
    /// </summary>
    public string GetDescription()
    {
        return Type switch
        {
            ConditionType.Always => "Always",
            ConditionType.FieldEquals => $"{Field} == {Value}",
            ConditionType.FieldNotEquals => $"{Field} != {Value}",
            ConditionType.FieldGreaterThan => $"{Field} > {Value}",
            ConditionType.FieldLessThan => $"{Field} < {Value}",
            ConditionType.FieldExists => $"{Field} exists",
            ConditionType.FieldNotExists => $"{Field} not exists",
            ConditionType.FieldContains => $"{Field} contains {Value}",
            ConditionType.Default => "Default (fallback)",
            ConditionType.UpstreamOneSuccess => "At least one upstream succeeded",
            ConditionType.UpstreamAllDone => "All upstreams completed",
            ConditionType.UpstreamAllDoneOneSuccess => "All upstreams done, at least one succeeded",
            _ => Type.ToString()
        };
    }
}
