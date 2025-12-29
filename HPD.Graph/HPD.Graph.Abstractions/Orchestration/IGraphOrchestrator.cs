using HPD.Events;
using HPDAgent.Graph.Abstractions.Context;
using HPDAgent.Graph.Abstractions.Execution;

namespace HPDAgent.Graph.Abstractions.Orchestration;

/// <summary>
/// Orchestrates graph execution.
/// Handles topological sorting, parallel execution, conditional routing,
/// retries, and error propagation.
/// Generic over TContext to support different execution contexts.
/// </summary>
/// <typeparam name="TContext">The graph context type</typeparam>
public interface IGraphOrchestrator<TContext> where TContext : class, IGraphContext
{
    /// <summary>
    /// Execute graph from start to finish.
    /// </summary>
    /// <param name="context">Execution context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated context after execution</returns>
    Task<TContext> ExecuteAsync(
        TContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume graph execution from checkpoint.
    /// Skips already-completed nodes.
    /// </summary>
    /// <param name="context">Execution context with completed nodes tracked</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated context after execution</returns>
    Task<TContext> ResumeAsync(
        TContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute graph with real-time event streaming.
    /// Emits events as nodes complete, enabling progress tracking and live updates.
    /// </summary>
    /// <param name="context">Execution context</param>
    /// <param name="options">Streaming options (controls event emission)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of events (GraphExecutionStarted, NodeExecutionCompleted, GraphExecutionCompleted, etc.)</returns>
    IAsyncEnumerable<Event> ExecuteStreamingAsync(
        TContext context,
        StreamingOptions? options = null,
        CancellationToken cancellationToken = default);
}
