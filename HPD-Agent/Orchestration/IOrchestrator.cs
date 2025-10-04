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
///         PrimaryAgent = selectedAgent,
///         RunId = conversationId ?? Guid.NewGuid().ToString("N"),
///         CreatedAt = DateTimeOffset.UtcNow,
///         Status = OrchestrationStatus.Completed,  // Single-turn orchestration
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
/// Orchestration execution status.
/// </summary>
public enum OrchestrationStatus
{
    /// <summary>
    /// Orchestration has not started yet.
    /// </summary>
    Pending,

    /// <summary>
    /// Orchestration is currently executing.
    /// </summary>
    Executing,

    /// <summary>
    /// Orchestration completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Orchestration failed or was terminated.
    /// </summary>
    Failed
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

    /// <summary>
    /// Whether orchestrator state was modified during this turn.
    /// Null if orchestrator is stateless.
    /// </summary>
    public bool? StateModified { get; init; }

    /// <summary>
    /// Which state keys were modified during this turn.
    /// Null if orchestrator doesn't track state changes.
    /// </summary>
    public IReadOnlyList<string>? ModifiedStateKeys { get; init; }
}

/// <summary>
/// Primary orchestration result.
/// Contains universal fields applicable to all orchestrator types.
/// </summary>
public record OrchestrationResult
{
    // ========================================
    // REQUIRED (All orchestrators must provide)
    // ========================================

    /// <summary>
    /// The final response from the orchestration.
    /// </summary>
    public required ChatResponse Response { get; init; }

    /// <summary>
    /// The primary agent that produced the response.
    /// For multi-agent orchestrations, this is the agent that generated the final output.
    /// </summary>
    public required Agent PrimaryAgent { get; init; }

    /// <summary>
    /// Unique identifier for this orchestration run.
    /// Use to correlate multiple turns in the same orchestration session.
    /// </summary>
    public required string RunId { get; init; }

    // ========================================
    // WITH DEFAULTS (All orchestrators benefit)
    // ========================================

    /// <summary>
    /// When this result was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Orchestration execution status.
    /// </summary>
    public OrchestrationStatus Status { get; init; } = OrchestrationStatus.Completed;

    /// <summary>
    /// Basic metadata about the orchestration decision.
    /// </summary>
    public OrchestrationMetadata Metadata { get; init; } = new();

    // ========================================
    // OPTIONAL (Only some orchestrators use)
    // ========================================

    /// <summary>
    /// All agents that were activated during this turn.
    /// Null if orchestrator only activated a single agent (the PrimaryAgent).
    /// </summary>
    public IReadOnlyList<Agent>? ActivatedAgents { get; init; }

    /// <summary>
    /// Current turn number in multi-turn orchestration (0-based).
    /// Null if orchestrator doesn't track turns.
    /// </summary>
    public int? TurnNumber { get; init; }

    /// <summary>
    /// Whether this orchestration requires user input before continuing.
    /// Only relevant for human-in-the-loop workflows.
    /// </summary>
    public bool RequiresUserInput { get; init; }

    /// <summary>
    /// Checkpoint for resuming orchestration.
    /// Null if orchestrator doesn't support checkpointing.
    /// </summary>
    public OrchestrationCheckpoint? Checkpoint { get; init; }

    /// <summary>
    /// Aggregated token usage across all agents in this orchestration.
    /// Null if no token usage information is available.
    /// </summary>
    public TokenUsage? AggregatedUsage { get; init; }

    /// <summary>
    /// Total number of agents/nodes that executed during orchestration.
    /// </summary>
    public int ExecutionCount { get; init; } = 1;

    /// <summary>
    /// Total execution time for the entire orchestration in milliseconds.
    /// </summary>
    public int ExecutionTimeMs { get; init; }

    /// <summary>
    /// Execution order (which agents ran in sequence).
    /// Null if orchestrator doesn't track execution order.
    /// </summary>
    public IReadOnlyList<string>? ExecutionOrder { get; init; }

    // ========================================
    // BACKWARD COMPATIBILITY
    // ========================================

    /// <summary>
    /// Whether the orchestration is complete (backward compatibility).
    /// - true: Orchestration completed successfully
    /// - false: Orchestration is pending, executing, or failed
    /// </summary>
    public bool IsComplete => Status == OrchestrationStatus.Completed;

    /// <summary>
    /// Implicit conversion for convenience.
    /// </summary>
    public static implicit operator ChatResponse(OrchestrationResult result)
        => result.Response;
}

/// <summary>
/// Checkpoint for resuming orchestration.
/// </summary>
public record OrchestrationCheckpoint
{
    /// <summary>
    /// The run this checkpoint belongs to.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Unique identifier for this specific checkpoint.
    /// </summary>
    public required string CheckpointId { get; init; }

    /// <summary>
    /// When this checkpoint was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Current state identifier (e.g., graph node, workflow step).
    /// Orchestrator-specific.
    /// </summary>
    public string? CurrentState { get; init; }

    /// <summary>
    /// Orchestrator-specific variables for resuming.
    /// </summary>
    public Dictionary<string, object>? Variables { get; init; }
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
