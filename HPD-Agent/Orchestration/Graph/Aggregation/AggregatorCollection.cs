using System;
using System.Collections.Concurrent;

/// <summary>
/// A thread-safe collection for managing multiple IAggregator instances during a workflow execution.
/// </summary>
public class AggregatorCollection
{
    private readonly ConcurrentDictionary<string, object> _aggregators = new();

    /// <summary>
    /// Gets or creates an aggregator of a specific type with a given key.
    /// </summary>
    /// <typeparam name="TValue">The value type of the aggregator.</typeparam>
    /// <typeparam name="TAggregator">The concrete type of the aggregator, which must have a parameterless constructor.</typeparam>
    /// <param name="key">The unique key for the aggregator instance.</param>
    public TAggregator GetOrCreate<TValue, TAggregator>(string key) where TAggregator : class, IAggregator<TValue>, new()
    {
        return (TAggregator)_aggregators.GetOrAdd(key, _ => new TAggregator());
    }

    /// <summary>
    /// Resets all registered aggregators to their initial state.
    /// </summary>
    internal void ResetAll()
    {
        foreach (var aggregator in _aggregators.Values)
        {
            // Use reflection to call the Reset method on the IAggregator<T> instance
            aggregator.GetType().GetMethod("Reset")?.Invoke(aggregator, null);
        }
    }
}