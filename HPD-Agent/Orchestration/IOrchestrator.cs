using Microsoft.Extensions.AI;

/// <summary>
/// Core orchestration interface for v0.
/// Replaces existing interface entirely with rich result types and BaseEvent streaming.
///
/// IMPORTANT: Orchestrator implementations must pass through history reduction metadata
/// from StreamingTurnResult.Reduction to OrchestrationMetadata.Context to support
/// conversation history reduction. See example implementation below.
/// </summary>
/// <example>
/// Example orchestrator implementation showing reduction metadata flow:
/// <code>
/// public async Task&lt;OrchestrationResult&gt; OrchestrateAsync(
///     IReadOnlyList&lt;ChatMessage&gt; history,
///     IReadOnlyList&lt;Agent&gt; agents,
///     string? conversationId = null,
///     ChatOptions? options = null,
///     CancellationToken cancellationToken = default)
/// {
///     // 1. Select agent (your orchestration logic)
///     var selectedAgent = SelectBestAgent(history, agents);
///
///     // 2. Call agent and get streaming result
///     var streamingResult = await selectedAgent.ExecuteStreamingTurnAsync(
///         history, options, cancellationToken: cancellationToken);
///
///     // 3. Consume stream
///     await foreach (var evt in streamingResult.EventStream.WithCancellation(cancellationToken))
///     {
///         // Process events as needed
///     }
///
///     // 4. Get final history
///     var finalHistory = await streamingResult.FinalHistory;
///
///     // 5. Package reduction metadata into Context dictionary
///     var reductionContext = new Dictionary&lt;string, object&gt;();
///     if (streamingResult.Reduction != null)
///     {
///         if (streamingResult.Reduction.SummaryMessage != null)
///         {
///             reductionContext["SummaryMessage"] = streamingResult.Reduction.SummaryMessage;
///         }
///         reductionContext["MessagesRemovedCount"] = streamingResult.Reduction.MessagesRemovedCount;
///     }
///
///     // 6. Return orchestration result
///     return new OrchestrationResult
///     {
///         Response = new ChatResponse(finalHistory),
///         SelectedAgent = selectedAgent,
///         Metadata = new OrchestrationMetadata
///         {
///             StrategyName = "YourStrategy",
///             DecisionDuration = TimeSpan.Zero,
///             Context = reductionContext // ← CRITICAL: Include reduction metadata
///         }
///     };
/// }
/// </code>
/// </example>
public interface IOrchestrator
{
    /// <summary>
    /// Simple orchestration returning rich result with metadata.
    /// </summary>
    /// <param name="history">The full conversation history up to this point.</param>
    /// <param name="agents">The pool of available agents to use in the orchestration.</param>
    /// <param name="conversationId">Optional conversation identifier for stateful orchestrators.</param>
    /// <param name="options">Optional chat settings.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Rich orchestration result containing response, selected agent, and metadata.</returns>
    Task<OrchestrationResult> OrchestrateAsync(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<Agent> agents,
        string? conversationId = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming orchestration with BaseEvent emission for full observability.
    /// </summary>
    /// <param name="history">The full conversation history up to this point.</param>
    /// <param name="agents">The pool of available agents to use in the orchestration.</param>
    /// <param name="conversationId">Optional conversation identifier for stateful orchestrators.</param>
    /// <param name="options">Optional chat settings.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Streaming result with BaseEvent stream and completion tasks.</returns>
    Task<OrchestrationStreamingResult> OrchestrateStreamingAsync(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<Agent> agents,
        string? conversationId = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestration decision metadata.
/// </summary>
public record OrchestrationMetadata
{
    public TimeSpan DecisionDuration { get; init; }
    public string StrategyName { get; init; } = "";
    public IReadOnlyDictionary<string, float> AgentScores { get; init; } = new Dictionary<string, float>();
    public IReadOnlyDictionary<string, object> Context { get; init; } = new Dictionary<string, object>();
}

/// <summary>
/// Primary orchestration result.
/// </summary>
public record OrchestrationResult
{
    public required ChatResponse Response { get; init; }
    public required Agent SelectedAgent { get; init; }
    public OrchestrationMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Implicit conversion for convenience and backward compatibility.
    /// </summary>
    public static implicit operator ChatResponse(OrchestrationResult result)
        => result.Response;
}

/// <summary>
/// Streaming orchestration result with BaseEvent stream.
/// </summary>
public record OrchestrationStreamingResult
{
    public required IAsyncEnumerable<BaseEvent> EventStream { get; init; }
    public required Task<OrchestrationResult> FinalResult { get; init; }
    public required Task<IReadOnlyList<ChatMessage>> FinalHistory { get; init; }
}
