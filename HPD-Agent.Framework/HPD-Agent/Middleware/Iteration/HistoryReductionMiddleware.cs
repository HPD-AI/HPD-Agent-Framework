using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Reduces conversation history to manage context window size.
/// Uses caching to avoid expensive re-summarization on every turn.
/// </summary>
/// <remarks>
/// <para><b>STATELESS MIDDLEWARE:</b></para>
/// <para>
/// This middleware is stateless - all state flows through the context via
/// <see cref="HistoryReductionStateData"/>. This preserves Agent's thread-safety
/// guarantee for concurrent RunAsync() calls.
/// </para>
///
/// <para><b>Lifecycle:</b></para>
/// <para>
/// This middleware uses the <see cref="IAgentMiddleware.BeforeIterationAsync"/> hook
/// which runs BEFORE each LLM call. It modifies <see cref="BeforeIterationContext.Messages"/>
/// to inject the summary and remove old messages.
/// </para>
///
/// <para><b>Caching Strategy:</b></para>
/// <list type="number">
/// <item>Check if cached reduction exists and is valid (IsValidFor)</item>
/// <item>If cache hit: Apply cached summary to messages (fast path)</item>
/// <item>If cache miss: Run reducer (LLM call), cache result, apply to messages</item>
/// <item>Store reduction in state for next iteration</item>
/// </list>
///
/// <para><b>Configuration:</b></para>
/// <para>
/// Configured via <see cref="HistoryReductionConfig"/>:
/// - Strategy: MessageCounting or Summarizing
/// - TargetMessageCount: How many messages to keep
/// - SummarizationThreshold: How many new messages before re-reduction
/// - SummarizerProvider: Optional separate model for summarization
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Auto-registered when HistoryReductionConfig.Enabled = true
/// var agent = new AgentBuilder()
///     .WithHistoryReduction(new HistoryReductionConfig
///     {
///         Enabled = true,
///         Strategy = HistoryReductionStrategy.Summarizing,
///         TargetMessageCount = 20,
///         SummarizationThreshold = 5
///     })
///     .Build();
/// </code>
/// </example>
public class HistoryReductionMiddleware : IAgentMiddleware
{
    //
    // CONFIGURATION (not state - set at registration time)
    //

    /// <summary>
    /// Chat reducer to use for history reduction.
    /// Can be MessageCountingChatReducer or SummarizingChatReducer.
    /// </summary>
    public required IChatReducer? ChatReducer { get; init; }

    /// <summary>
    /// Configuration for history reduction behavior.
    /// </summary>
    public required HistoryReductionConfig Config { get; init; }

    /// <summary>
    /// System instructions to preserve when reducing.
    /// Extracted from SystemMessage or ChatOptions.Instructions.
    /// </summary>
    public string? SystemInstructions { get; init; }

    //
    // HOOKS
    //

    /// <summary>
    /// Called BEFORE each message turn.
    /// This is the perfect place for history reduction - runs once per user message.
    /// </summary>
    public async Task BeforeMessageTurnAsync(
        BeforeMessageTurnContext context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        // Check for explicit skip flag in RunConfig (highest priority)
        if (context.RunConfig.SkipHistoryReduction)
        {
            EmitHistoryReductionEvent(context, HistoryReductionStatus.Skipped,
                reason: "Explicitly skipped via RunConfig.SkipHistoryReduction", startTime: startTime);
            return;
        }

        // Skip if no messages or reduction disabled
        if (context.ConversationHistory == null || context.ConversationHistory.Count == 0)
        {
            EmitHistoryReductionEvent(context, HistoryReductionStatus.Skipped,
                reason: "No messages present", startTime: startTime);
            return;
        }

        // Check for explicit trigger flag in RunConfig (bypasses threshold checks)
        var shouldTrigger = context.RunConfig.TriggerHistoryReduction;

        // If not explicitly triggered, check automatic thresholds
        if (!shouldTrigger)
        {
            // Skip if message count is below threshold
            if (context.ConversationHistory.Count <= Config.TargetMessageCount + (Config.SummarizationThreshold ?? 5))
            {
                EmitHistoryReductionEvent(context, HistoryReductionStatus.Skipped,
                    reason: "Message count below threshold", startTime: startTime,
                    originalMessageCount: context.ConversationHistory.Count);
                return;
            }
        }

        // Read state
        var hrState = context.GetMiddlewareState<HistoryReductionStateData>();

        // Separate system messages from conversation messages
        var systemMessages = context.ConversationHistory.Where(m => m.Role == ChatRole.System).ToList();
        var conversationMessages = context.ConversationHistory.Where(m => m.Role != ChatRole.System).ToList();

        // Try cache first
        CachedReduction? activeReduction = null;

        if (hrState?.LastReduction != null && hrState.LastReduction.IsValidFor(conversationMessages.Count))
        {
            //  CACHE HIT: Reuse existing reduction
            activeReduction = hrState.LastReduction;

            // Apply cached reduction to messages
            var reducedMessages = activeReduction.ApplyToMessages(conversationMessages, systemMessage: null).ToList();

            // Update context with reduced messages (system messages + reduced conversation)
            // ConversationHistory is mutable - clear and repopulate instead of reassigning
            context.ConversationHistory.Clear();
            foreach (var msg in systemMessages.Concat(reducedMessages))
            {
                context.ConversationHistory.Add(msg);
            }

            // Emit event for observability
            EmitHistoryReductionEvent(context, HistoryReductionStatus.CacheHit,
                startTime: startTime,
                originalMessageCount: conversationMessages.Count,
                reducedMessageCount: reducedMessages.Count,
                summaryContent: activeReduction.SummaryContent,
                cacheAge: DateTime.UtcNow - activeReduction.CreatedAt);

            // NOTE: Circuit breaker does NOT fire on cache hits
            // Cache hits are transparent - just reusing an existing summary.
            // Circuit breaker ONLY fires when a NEW reduction is performed.

            return; // Done - cache hit path
        }

        //    CACHE MISS: Need to perform reduction
        if (ChatReducer != null && ShouldTriggerReduction(conversationMessages))
        {
            // Run reduction (this calls the LLM for summarization)
            var reduced = await ChatReducer.ReduceAsync(conversationMessages, cancellationToken).ConfigureAwait(false);

            if (reduced != null)
            {
                var reducedList = reduced.ToList();

                // Extract summary message by position (Microsoft's reducer doesn't add markers)
                // SummarizingChatReducer returns: [Summary?] [Unsummarized messages...]
                var summaryMsg = reducedList.FirstOrDefault(m => m.Role == ChatRole.Assistant);

                if (summaryMsg != null)
                {
                    // Calculate how many messages were removed
                    int removedCount = conversationMessages.Count - reducedList.Count + 1; // +1 for summary itself
                    var summarizedUpToIndex = removedCount;

                    // Create new cached reduction
                    activeReduction = CachedReduction.Create(
                        conversationMessages,
                        summaryMsg.Text ?? string.Empty,
                        summarizedUpToIndex,
                        Config.TargetMessageCount,
                        Config.SummarizationThreshold ?? 5);

                    // Update context with reduced messages (system messages + reduced conversation)
                    // ConversationHistory is mutable - clear and repopulate instead of reassigning
                    context.ConversationHistory.Clear();
                    foreach (var msg in systemMessages.Concat(reducedList))
                    {
                        context.ConversationHistory.Add(msg);
                    }

                    // Store reduction in state for next iteration
                    context.UpdateMiddlewareState<HistoryReductionStateData>(s =>
                        s.WithReduction(activeReduction)
                    );

                    // Emit event for observability
                    EmitHistoryReductionEvent(context, HistoryReductionStatus.Performed,
                        startTime: startTime,
                        originalMessageCount: conversationMessages.Count,
                        reducedMessageCount: reducedList.Count,
                        messagesRemoved: removedCount,
                        summaryContent: activeReduction.SummaryContent);

                    // Check if we should terminate (circuit breaker mode)
                    TerminateIfCircuitBreaker(context, "History reduction performed - circuit breaker triggered");

                    return; // Done - reduction performed
                }
            }
        }
        else
        {
            EmitHistoryReductionEvent(context, HistoryReductionStatus.Skipped,
                reason: "Reduction threshold not met", startTime: startTime,
                originalMessageCount: conversationMessages.Count);
        }
    }

    //     
    // HELPERS
    //     

    /// <summary>
    /// Determines if history reduction should be triggered based on configured thresholds.
    /// </summary>
    private bool ShouldTriggerReduction(List<ChatMessage> messages)
    {
        // Message-count based reduction (default strategy)
        var targetCount = Config.TargetMessageCount;
        var threshold = Config.SummarizationThreshold ?? 5;
        var triggerCount = targetCount + threshold;

        return messages.Count > triggerCount;
    }

    /// <summary>
    /// Emits a unified history reduction event for all scenarios.
    /// </summary>
    private void EmitHistoryReductionEvent(
        BeforeMessageTurnContext context,
        HistoryReductionStatus status,
        DateTimeOffset startTime,
        int? originalMessageCount = null,
        int? reducedMessageCount = null,
        int? messagesRemoved = null,
        string? summaryContent = null,
        TimeSpan? cacheAge = null,
        string? reason = null)
    {
        try
        {
            var duration = DateTimeOffset.UtcNow - startTime;

            context.Emit(new HistoryReductionEvent(
                AgentName: context.AgentName,
                Iteration: 0, // Always 0 since we're in BeforeMessageTurn
                Status: status,
                Strategy: Config.Strategy,
                OriginalMessageCount: originalMessageCount,
                ReducedMessageCount: reducedMessageCount,
                MessagesRemoved: messagesRemoved,
                SummaryContent: summaryContent,
                SummaryLength: summaryContent?.Length,
                CacheAge: cacheAge,
                Duration: duration,
                Reason: reason,
                Timestamp: DateTimeOffset.UtcNow));
        }
        catch (InvalidOperationException)
        {
            // EventCoordinator not configured - event emission is optional
        }
    }

    /// <summary>
    /// Checks if circuit breaker mode is enabled and terminates the agent if so.
    /// Circuit breaker mode stops execution after reduction, requiring user to send next message.
    /// </summary>
    private void TerminateIfCircuitBreaker(BeforeMessageTurnContext context, string reason)
    {
        // Determine effective behavior (RunConfig override takes precedence)
        var effectiveBehavior = context.RunConfig.HistoryReductionBehaviorOverride ?? Config.Behavior;

        // If circuit breaker mode, terminate the turn
        if (effectiveBehavior == HistoryReductionBehavior.CircuitBreaker)
        {
            context.UpdateState(s => s with
            {
                IsTerminated = true,
                TerminationReason = reason
            });
        }
    }
}

//
// OBSERVABILITY EVENTS
//

/// <summary>
/// Status of history reduction operation.
/// </summary>
public enum HistoryReductionStatus
{
    /// <summary>Reduction was skipped (no messages, below threshold, etc.)</summary>
    Skipped,

    /// <summary>Cached reduction was reused (no LLM call needed)</summary>
    CacheHit,

    /// <summary>New reduction was performed (LLM call made to summarize)</summary>
    Performed
}

/// <summary>
/// Unified event emitted for all history reduction scenarios.
/// Consolidates started, skipped, cache hit, and performed events into a single event.
/// </summary>
/// <remarks>
/// <para><b>Design Rationale:</b></para>
/// <para>
/// This consolidated event simplifies observability by providing a single event type
/// for all history reduction outcomes. The Status field discriminates between:
/// - Skipped: Reduction not needed (early return conditions)
/// - CacheHit: Reused cached summary (no LLM call)
/// - Performed: New summarization (LLM call made)
/// </para>
/// <para>
/// When Strategy is Summarizing and Status is CacheHit or Performed,
/// the SummaryContent field contains the actual summary text that was applied.
/// </para>
/// </remarks>
public sealed record HistoryReductionEvent(
    string AgentName,
    int Iteration,
    HistoryReductionStatus Status,
    HistoryReductionStrategy Strategy,
    int? OriginalMessageCount,
    int? ReducedMessageCount,
    int? MessagesRemoved,
    string? SummaryContent,
    int? SummaryLength,
    TimeSpan? CacheAge,
    TimeSpan Duration,
    string? Reason,
    DateTimeOffset Timestamp) : AgentEvent, IObservabilityEvent;
