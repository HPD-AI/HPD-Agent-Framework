using System;
using System.ComponentModel;

namespace AgentConsoleTest
{
    /// <summary>
    /// Test context for validation testing.
    /// </summary>
    public class ValidationTestContext : IPluginMetadataContext
    {
        private readonly Dictionary<string, object> _properties = new();

        public ValidationTestContext()
        {
            _properties["Count"] = 5;
            _properties["Name"] = "Test";
            _properties["IsEnabled"] = true;
            Count = 5;
            Name = "Test";
            IsEnabled = true;
        }

        public int Count { get; }
        public string Name { get; } = "";
        public bool IsEnabled { get; }

        public T GetProperty<T>(string propertyName, T defaultValue = default)
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

    /// <summary>
    /// Test plugin to demonstrate the enhanced validation system.
    /// This plugin intentionally contains validation errors to test the new validation features.
    /// </summary>
    public class ValidationTestPlugin
    {
        /// <summary>
        /// Function with invalid syntax in conditional expression (unbalanced parentheses).
        /// This should trigger HPD004 validation error.
        /// </summary>
        [AIFunction<ValidationTestContext>]
        [ConditionalFunction("Count > 5 && (IsEnabled")]
        [AIDescription("Test function with syntax error")]
        public string TestSyntaxError()
        {
            return "This function has a syntax error in its conditional expression";
        }

        /// <summary>
        /// Function with invalid operator usage (comparing string with > operator).
        /// This should trigger HPD004 validation error for type incompatibility.
        /// </summary>
        [AIFunction<ValidationTestContext>]
        [ConditionalFunction("Name > 10")]
        [AIDescription("Test function with type compatibility error")]
        public string TestTypeError()
        {
            return "This function has a type compatibility error";
        }

        /// <summary>
        /// Function with non-existent property reference.
        /// This should trigger HPD002 validation error.
        /// </summary>
        [AIFunction<ValidationTestContext>]
        [ConditionalFunction("NonExistentProperty == true")]
        [AIDescription("Test function with missing property error")]
        public string TestPropertyError()
        {
            return "This function references a non-existent property";
        }

        /// <summary>
        /// Function with invalid template property.
        /// This should trigger HPD001 validation error.
        /// </summary>
        [AIFunction<ValidationTestContext>]
        [AIDescription("Test function for {context.InvalidProperty} validation")]
        public string TestTemplateError()
        {
            return "This function has an invalid template property";
        }

        /// <summary>
        /// Valid function for comparison.
        /// This should pass all validation checks.
        /// </summary>
        [AIFunction<ValidationTestContext>]
        [ConditionalFunction("Count > 0 && IsEnabled")]
        [AIDescription("Test function for {context.Name} with count {context.Count}")]
        public string TestValidFunction()
        {
            return "This function should pass all validation checks";
        }
    }
}
