using FluentAssertions;
using HPDAgent.Graph.Abstractions.Graph;
using HPDAgent.Graph.Core.Orchestration;
using Xunit;

namespace HPD.Graph.Tests.Routing;

/// <summary>
/// Tests for conditional edge routing and condition evaluation.
/// </summary>
public class ConditionalRoutingTests
{
    #region Basic Condition Tests

    [Fact]
    public void Evaluate_AlwaysCondition_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition { Type = ConditionType.Always };
        var outputs = new Dictionary<string, object> { ["key"] = "value" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_NullCondition_ReturnsTrue()
    {
        // Arrange
        EdgeCondition? condition = null;
        var outputs = new Dictionary<string, object> { ["key"] = "value" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert - Null condition means unconditional edge
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_FieldEquals_MatchingValue_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "status",
            Value = "success"
        };
        var outputs = new Dictionary<string, object> { ["status"] = "success" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_FieldEquals_DifferentValue_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "status",
            Value = "success"
        };
        var outputs = new Dictionary<string, object> { ["status"] = "failure" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_FieldEquals_NumericValues_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "count",
            Value = 42
        };
        var outputs = new Dictionary<string, object> { ["count"] = 42 };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_FieldNotEquals_DifferentValue_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldNotEquals,
            Field = "status",
            Value = "success"
        };
        var outputs = new Dictionary<string, object> { ["status"] = "failure" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_FieldNotEquals_SameValue_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldNotEquals,
            Field = "status",
            Value = "success"
        };
        var outputs = new Dictionary<string, object> { ["status"] = "success" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Field Existence Tests

    [Fact]
    public void Evaluate_FieldExists_NonNullField_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldExists,
            Field = "data"
        };
        var outputs = new Dictionary<string, object> { ["data"] = "some value" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_FieldExists_NullField_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldExists,
            Field = "data"
        };
        var outputs = new Dictionary<string, object> { ["data"] = null! };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_FieldExists_MissingField_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldExists,
            Field = "data"
        };
        var outputs = new Dictionary<string, object> { ["other"] = "value" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_FieldNotExists_MissingField_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldNotExists,
            Field = "data"
        };
        var outputs = new Dictionary<string, object> { ["other"] = "value" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_FieldNotExists_NullField_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldNotExists,
            Field = "data"
        };
        var outputs = new Dictionary<string, object> { ["data"] = null! };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region Comparison Tests - Numbers

    [Fact]
    public void Evaluate_FieldGreaterThan_Numbers_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldGreaterThan,
            Field = "score",
            Value = 50
        };
        var outputs = new Dictionary<string, object> { ["score"] = 75 };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_FieldGreaterThan_Numbers_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldGreaterThan,
            Field = "score",
            Value = 50
        };
        var outputs = new Dictionary<string, object> { ["score"] = 25 };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_FieldLessThan_Numbers_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldLessThan,
            Field = "score",
            Value = 50
        };
        var outputs = new Dictionary<string, object> { ["score"] = 25 };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_FieldLessThan_Numbers_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldLessThan,
            Field = "score",
            Value = 50
        };
        var outputs = new Dictionary<string, object> { ["score"] = 75 };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_FieldGreaterThan_DifferentNumericTypes_Works()
    {
        // Arrange - Comparing int field with long value
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldGreaterThan,
            Field = "count",
            Value = 50L  // long
        };
        var outputs = new Dictionary<string, object> { ["count"] = 100 };  // int

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region Comparison Tests - Strings

    [Fact]
    public void Evaluate_FieldGreaterThan_Strings_OrdinalComparison()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldGreaterThan,
            Field = "name",
            Value = "Alice"
        };
        var outputs = new Dictionary<string, object> { ["name"] = "Bob" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert - "Bob" > "Alice" (ordinal comparison)
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_FieldLessThan_Strings_OrdinalComparison()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldLessThan,
            Field = "name",
            Value = "Bob"
        };
        var outputs = new Dictionary<string, object> { ["name"] = "Alice" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert - "Alice" < "Bob" (ordinal comparison)
        result.Should().BeTrue();
    }

    #endregion

    #region Contains Tests

    [Fact]
    public void Evaluate_FieldContains_String_CaseInsensitive_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldContains,
            Field = "message",
            Value = "error"
        };
        var outputs = new Dictionary<string, object> { ["message"] = "An ERROR occurred" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert - Case insensitive
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_FieldContains_String_NotFound_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldContains,
            Field = "message",
            Value = "error"
        };
        var outputs = new Dictionary<string, object> { ["message"] = "Success" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_FieldContains_Collection_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldContains,
            Field = "tags",
            Value = "important"
        };
        var outputs = new Dictionary<string, object>
        {
            ["tags"] = new List<string> { "urgent", "important", "reviewed" }
        };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_FieldContains_Collection_NotFound_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldContains,
            Field = "tags",
            Value = "important"
        };
        var outputs = new Dictionary<string, object>
        {
            ["tags"] = new List<string> { "urgent", "reviewed" }
        };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Evaluate_NoOutputs_FieldCondition_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "status",
            Value = "success"
        };
        Dictionary<string, object>? outputs = null;

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert - Can't evaluate field conditions without outputs
        result.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_NoOutputs_AlwaysCondition_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition { Type = ConditionType.Always };
        Dictionary<string, object>? outputs = null;

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert - Always condition doesn't need outputs
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_EmptyOutputs_FieldCondition_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "status",
            Value = "success"
        };
        var outputs = new Dictionary<string, object>();

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_NullValues_Comparison_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldGreaterThan,
            Field = "score",
            Value = 50
        };
        var outputs = new Dictionary<string, object> { ["score"] = null! };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert - Can't compare null values
        result.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_FieldEquals_BothNull_ReturnsTrue()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "data",
            Value = null
        };
        var outputs = new Dictionary<string, object> { ["data"] = null! };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert - null == null
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_MissingField_ReturnsFalse()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "missing",
            Value = "value"
        };
        var outputs = new Dictionary<string, object> { ["other"] = "value" };

        // Act
        var result = ConditionEvaluator.Evaluate(condition, outputs);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region EdgeCondition GetDescription Tests

    [Fact]
    public void GetDescription_Always_ReturnsCorrectString()
    {
        // Arrange
        var condition = new EdgeCondition { Type = ConditionType.Always };

        // Act
        var description = condition.GetDescription();

        // Assert
        description.Should().Be("Always");
    }

    [Fact]
    public void GetDescription_FieldEquals_ReturnsCorrectString()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "status",
            Value = "success"
        };

        // Act
        var description = condition.GetDescription();

        // Assert
        description.Should().Be("status == success");
    }

    [Fact]
    public void GetDescription_FieldGreaterThan_ReturnsCorrectString()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldGreaterThan,
            Field = "score",
            Value = 50
        };

        // Act
        var description = condition.GetDescription();

        // Assert
        description.Should().Be("score > 50");
    }

    [Fact]
    public void GetDescription_FieldContains_ReturnsCorrectString()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.FieldContains,
            Field = "message",
            Value = "error"
        };

        // Act
        var description = condition.GetDescription();

        // Assert
        description.Should().Be("message contains error");
    }

    [Fact]
    public void GetDescription_Default_ReturnsCorrectString()
    {
        // Arrange
        var condition = new EdgeCondition
        {
            Type = ConditionType.Default
        };

        // Act
        var description = condition.GetDescription();

        // Assert
        description.Should().Be("Default (fallback)");
    }

    #endregion

    #region Default Edge Tests

    [Fact]
    public void DefaultEdge_FiresWhenNoRegularConditionsMatch()
    {
        // Arrange
        var condition1 = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "status",
            Value = "low"
        };

        var condition2 = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "status",
            Value = "high"
        };

        var defaultCondition = new EdgeCondition
        {
            Type = ConditionType.Default
        };

        var outputs = new Dictionary<string, object> { ["status"] = "medium" };

        // Act
        var result1 = ConditionEvaluator.Evaluate(condition1, outputs);
        var result2 = ConditionEvaluator.Evaluate(condition2, outputs);

        // Assert
        result1.Should().BeFalse("regular condition should not match");
        result2.Should().BeFalse("regular condition should not match");
        // Default edge should be used when both regular conditions fail
        // This will be tested in integration tests with the orchestrator
    }

    [Fact]
    public void DefaultEdge_DoesNotFireWhenRegularConditionMatches()
    {
        // Arrange
        var regularCondition = new EdgeCondition
        {
            Type = ConditionType.FieldEquals,
            Field = "status",
            Value = "success"
        };

        var outputs = new Dictionary<string, object> { ["status"] = "success" };

        // Act
        var regularResult = ConditionEvaluator.Evaluate(regularCondition, outputs);

        // Assert
        regularResult.Should().BeTrue("regular condition should match");
        // Default edge should NOT be used - verified in integration tests
    }

    #endregion
}
