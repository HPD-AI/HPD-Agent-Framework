namespace HPDAgent.Graph.Abstractions.Handlers;

/// <summary>
/// Typed inputs provided to a handler from upstream nodes.
/// The orchestrator creates this from previous node outputs.
/// Handlers consume this without knowing about the orchestrator's internal storage.
/// </summary>
public sealed class HandlerInputs
{
    private readonly Dictionary<string, object> _inputs = new();

    /// <summary>
    /// Get a required input value.
    /// </summary>
    /// <typeparam name="T">Expected type of the value</typeparam>
    /// <param name="inputName">Input name</param>
    /// <returns>Typed value</returns>
    /// <exception cref="InvalidOperationException">Thrown if input not found or type mismatch</exception>
    public T Get<T>(string inputName)
    {
        if (string.IsNullOrWhiteSpace(inputName))
        {
            throw new ArgumentException("Input name cannot be null or whitespace.", nameof(inputName));
        }

        if (!_inputs.TryGetValue(inputName, out var value))
        {
            throw new InvalidOperationException($"Required input '{inputName}' not found.");
        }

        if (value == null)
        {
            if (default(T) == null)
            {
                return default!;
            }

            throw new InvalidOperationException(
                $"Input '{inputName}' is null, but type {typeof(T).Name} is not nullable.");
        }

        if (value is T typedValue)
        {
            return typedValue;
        }

        throw new InvalidOperationException(
            $"Input '{inputName}' has type {value.GetType().Name}, but {typeof(T).Name} was requested.");
    }

    /// <summary>
    /// Get an optional input value with a default.
    /// </summary>
    /// <typeparam name="T">Expected type of the value</typeparam>
    /// <param name="inputName">Input name</param>
    /// <param name="defaultValue">Default value if not found</param>
    /// <returns>Typed value or default</returns>
    public T GetOrDefault<T>(string inputName, T defaultValue)
    {
        if (string.IsNullOrWhiteSpace(inputName))
        {
            return defaultValue;
        }

        if (!_inputs.TryGetValue(inputName, out var value))
        {
            return defaultValue;
        }

        if (value == null)
        {
            return defaultValue;
        }

        if (value is T typedValue)
        {
            return typedValue;
        }

        return defaultValue;
    }

    /// <summary>
    /// Try to get an input value.
    /// </summary>
    /// <typeparam name="T">Expected type of the value</typeparam>
    /// <param name="inputName">Input name</param>
    /// <param name="value">Output value if found</param>
    /// <returns>True if value was found and has correct type, false otherwise</returns>
    public bool TryGet<T>(string inputName, out T? value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(inputName))
        {
            return false;
        }

        if (!_inputs.TryGetValue(inputName, out var rawValue))
        {
            return false;
        }

        if (rawValue == null)
        {
            value = default;
            return true;
        }

        if (rawValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Add an input value (used by orchestrator).
    /// </summary>
    public void Add(string inputName, object value)
    {
        if (string.IsNullOrWhiteSpace(inputName))
        {
            throw new ArgumentException("Input name cannot be null or whitespace.", nameof(inputName));
        }

        _inputs[inputName] = value;
    }

    /// <summary>
    /// Get all inputs as a dictionary (for debugging/logging).
    /// </summary>
    public IReadOnlyDictionary<string, object> GetAll() => _inputs;

    /// <summary>
    /// Check if an input exists.
    /// </summary>
    public bool Contains(string inputName) =>
        !string.IsNullOrWhiteSpace(inputName) && _inputs.ContainsKey(inputName);
}
