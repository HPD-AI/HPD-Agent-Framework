using HPDAgent.Graph.Abstractions.Context;

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
}
