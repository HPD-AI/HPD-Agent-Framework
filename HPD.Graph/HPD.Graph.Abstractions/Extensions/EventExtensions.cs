using HPD.Events;
using HPDAgent.Graph.Abstractions.Events;

namespace HPDAgent.Graph.Extensions;

/// <summary>
/// Extension methods for accessing graph-specific context from any event.
/// Enables cross-domain event consumption (e.g., agents reading graph events).
/// </summary>
public static class EventExtensions
{
    /// <summary>
    /// Get graph execution context from any event.
    /// Checks GraphEvent.GraphContext first (strongly typed),
    /// then falls back to Extensions dictionary (cross-domain enrichment).
    /// </summary>
    /// <param name="evt">Event to extract context from</param>
    /// <returns>Graph execution context if available, null otherwise</returns>
    public static GraphExecutionContext? GetGraphContext(this Event evt)
    {
        // Primary: strongly typed GraphEvent field (fast, type-safe)
        if (evt is GraphEvent graphEvt)
            return graphEvt.GraphContext;

        // Fallback: extension dictionary (for cross-domain scenarios)
        if (evt.Extensions?.TryGetValue("GraphContext", out var ctx) == true)
            return ctx as GraphExecutionContext;

        return null;
    }
}
