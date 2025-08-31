using Microsoft.Extensions.AI;

/// <summary>
/// Defines a strategy for orchestrating one or more agents to generate a response.
/// </summary>
public interface IOrchestrator
{
    /// <summary>
    /// Invokes the orchestration strategy to get the next response in a conversation.
    /// </summary>
    /// <param name="history">The full conversation history up to this point.</param>
    /// <param name="agents">The pool of available agents to use in the orchestration.</param>
    /// <param name="conversationId">Optional conversation identifier for stateful orchestrators.</param>
    /// <param name="options">Optional chat settings.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The final chat response from the orchestration.</returns>
    Task<ChatResponse> OrchestrateAsync(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<Agent> agents,
        string? conversationId = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes the orchestration strategy and streams the response.
    /// </summary>
    /// <param name="history">The full conversation history up to this point.</param>
    /// <param name="agents">The pool of available agents to use in the orchestration.</param>
    /// <param name="conversationId">Optional conversation identifier for stateful orchestrators.</param>
    /// <param name="options">Optional chat settings.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A StreamingTurnResult containing both the response stream and final turn history.</returns>
    Task<StreamingTurnResult> OrchestrateStreamingAsync(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<Agent> agents,
        string? conversationId = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);
}
