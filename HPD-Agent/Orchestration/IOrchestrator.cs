using Microsoft.Extensions.AI;

/// <summary>
/// Core orchestration interface for v0.
/// Replaces existing interface entirely with rich result types and BaseEvent streaming.
/// </summary>
internal interface IOrchestrator
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
internal record OrchestrationMetadata
{
    public TimeSpan DecisionDuration { get; init; }
    public string StrategyName { get; init; } = "";
    public IReadOnlyDictionary<string, float> AgentScores { get; init; } = new Dictionary<string, float>();
    public IReadOnlyDictionary<string, object> Context { get; init; } = new Dictionary<string, object>();
}

/// <summary>
/// Primary orchestration result.
/// </summary>
internal record OrchestrationResult
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
internal record OrchestrationStreamingResult
{
    public required IAsyncEnumerable<BaseEvent> EventStream { get; init; }
    public required Task<OrchestrationResult> FinalResult { get; init; }
    public required Task<IReadOnlyList<ChatMessage>> FinalHistory { get; init; }
}
