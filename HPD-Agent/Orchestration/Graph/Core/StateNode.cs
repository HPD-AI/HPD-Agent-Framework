using Microsoft.Extensions.AI;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Represents a self-contained, executable node in a workflow graph.
/// Each node is responsible for its own logic and for returning the new, updated state.
/// </summary>
/// <typeparam name="TState">The workflow's state type.</typeparam>
public abstract class StateNode<TState> where TState : class, new()
{
    /// <summary>
    /// Executes the node's logic.
    /// </summary>
    /// <param name="context">The current context of the workflow before this node's execution.</param>
    /// <param name="history">The read-only history of the conversation.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that resolves to the new, updated state of the workflow after this node's execution.</returns>
    public abstract Task<TState> ExecuteAsync(
        WorkflowContext<TState> context,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken);
}