using System;
using System.ComponentModel;
using Microsoft.Extensions.AI;

/// <summary>
/// Simple math plugin for testing plugin registration and invocation.
/// </summary>
public class MathPluginMetadataContext : IPluginMetadataContext
{
    private readonly Dictionary<string, object> _properties = new();

    public MathPluginMetadataContext(long maxValue = 1000, bool allowNegative = true)
    {
        _properties["maxValue"] = maxValue;
        _properties["allowNegative"] = allowNegative;
        MaxValue = maxValue;
        AllowNegative = allowNegative;
    }

    // ✅ V2: Strongly-typed properties for compile-time validation
    public long MaxValue { get; }
    public bool AllowNegative { get; }

    public T? GetProperty<T>(string propertyName, T? defaultValue = default)
    {
        if (_properties.TryGetValue(propertyName, out var value))
        {
            if (value is T typedValue)
                return typedValue;
            if (typeof(T) == typeof(string))
                return (T)(object)value.ToString()!;
        }
        return defaultValue;
    }

    public bool HasProperty(string propertyName) => _properties.ContainsKey(propertyName);
    public IEnumerable<string> GetPropertyNames() => _properties.Keys;
}


public class MathPlugin
{
    [AIFunction<MathPluginMetadataContext>]
    [AIDescription("Adds two numbers and returns the sum.")]
    [RequiresPermission]
    public decimal Add(
        [AIDescription("First addend.")] decimal a,
        [AIDescription("Second addend.")] decimal b)
        => a + b;

    [AIFunction<MathPluginMetadataContext>]
    [AIDescription("Multiplies two numbers and returns the product.")]
    public long Multiply(
        [AIDescription("First factor.")] long a,
        [AIDescription("Second factor.")] long b)
        => a * b;

    [AIFunction<MathPluginMetadataContext>]
    [ConditionalFunction("AllowNegative == false")]
    [AIDescription("Returns the absolute value. Only available if negatives are not allowed.")]
    public long Abs(
        [AIDescription("Input value.")] long value)
        => Math.Abs(value);

    [AIFunction<MathPluginMetadataContext>]
    [ConditionalFunction("MaxValue > 1000")]
    [AIDescription("Squares a number. Only available if maxValue > 1000.")]
    public long Square(
        [AIDescription("Input value.")] long value)
        => value * value;

    [AIFunction<MathPluginMetadataContext>]
    [ConditionalFunction("AllowNegative == true")]
    [AIDescription("Subtracts b from a. Only available if negatives are allowed.")]
    public long Subtract(
        [AIDescription("Minuend.")] long a,
        [AIDescription("Subtrahend.")] long b)
        => a - b;

    [AIFunction<MathPluginMetadataContext>]
    [ConditionalFunction("MaxValue < 500")]
    [AIDescription("Returns the minimum of two numbers. Only available if maxValue < 500.")]
    public long Min(
        [AIDescription("First value.")] long a,
        [AIDescription("Second value.")] long b)
        => Math.Min(a, b);

    [Skill]
    public Skill SolveQuadratic(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "SolveQuadratic",
            description: "Solves quadratic equations (ax² + bx + c = 0)",
            instructions: @"
                    Step 1: Calculate discriminant (b² - 4ac)
                    Step 2: Calculate square root using sqrt function
                    Step 3: Calculate two solutions using Add/Subtract",
            options: options,
            "MathPlugin.Multiply",
            "MathPlugin.Add",
            "MathPlugin.Subtract"
        );
    }
}
