/// <summary>
/// Defines a generic aggregator for collecting and reducing values across multiple nodes in a workflow step.
/// </summary>
/// <typeparam name="T">The type of value this aggregator handles.</typeparam>
public interface IAggregator<T>
{
    /// <summary>
    /// Adds a new value to be aggregated. This method must be thread-safe.
    /// </summary>
    void Add(T value);

    /// <summary>
    /// Gets the current aggregated result.
    /// </summary>
    T GetResult();

    /// <summary>
    /// Resets the aggregator to its initial state for the next superstep.
    /// </summary>
    void Reset();
}