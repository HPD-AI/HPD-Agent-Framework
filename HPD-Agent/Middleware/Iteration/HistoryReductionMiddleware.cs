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
    /// Called BEFORE each LLM call.
    /// Applies history reduction to context.Messages if needed.
    /// </summary>
    public async Task BeforeIterationAsync(
        BeforeIterationContext context,
        CancellationToken cancellationToken)
    {
        // Skip if no messages or reduction disabled
        if (context.Messages == null || context.Messages.Count == 0)
            return;

        // Skip if first iteration (no history to reduce yet)
        if (context.Iteration == 0 && context.Messages.Count <= Config.TargetMessageCount + (Config.SummarizationThreshold ?? 5))
            return;

        // Read state
        var hrState = context.GetMiddlewareState<HistoryReductionStateData>();

        // Separate system messages from conversation messages
        var systemMessages = context.Messages.Where(m => m.Role == ChatRole.System).ToList();
        var conversationMessages = context.Messages.Where(m => m.Role != ChatRole.System).ToList();

        // Try cache first
        CachedReduction? activeReduction = null;

        if (hrState?.LastReduction != null && hrState.LastReduction.IsValidFor(conversationMessages.Count))
        {
            //  CACHE HIT: Reuse existing reduction
            activeReduction = hrState.LastReduction;

            // Apply cached reduction to messages
            var reducedMessages = activeReduction.ApplyToMessages(conversationMessages, systemMessage: null).ToList();

            // Update context with reduced messages (system messages + reduced conversation)
            // V2: Messages is mutable - clear and repopulate instead of reassigning
            context.Messages.Clear();
            foreach (var msg in systemMessages.Concat(reducedMessages))
            {
                context.Messages.Add(msg);
            }

            // Emit event for observability
            EmitCacheHitEvent(context, activeReduction);

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
                    // V2: Messages is mutable - clear and repopulate instead of reassigning
                    context.Messages.Clear();
                    foreach (var msg in systemMessages.Concat(reducedList))
                    {
                        context.Messages.Add(msg);
                    }

                    // Store reduction in state for next iteration
                    context.UpdateMiddlewareState<HistoryReductionStateData>(s =>
                        s.WithReduction(activeReduction)
                    );

                    // Emit events for observability
                    EmitReductionPerformedEvent(context, activeReduction, removedCount);

                    return; // Done - reduction performed
                }
            }
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
    /// Emits a cache hit event for observability.
    /// </summary>
    private void EmitCacheHitEvent(BeforeIterationContext context, CachedReduction reduction)
    {
        try
        {
            context.Emit(new HistoryReductionCacheHitEvent(
                AgentName: context.AgentName,
                Iteration: context.Iteration,
                MessageCount: context.Messages?.Count ?? 0,
                CachedReductionAge: DateTime.UtcNow - reduction.CreatedAt,
                Timestamp: DateTimeOffset.UtcNow));
        }
        catch (InvalidOperationException)
        {
            // EventCoordinator not configured - event emission is optional
        }
    }

    /// <summary>
    /// Emits a reduction performed event for observability.
    /// </summary>
    private void EmitReductionPerformedEvent(BeforeIterationContext context, CachedReduction reduction, int removedCount)
    {
        try
        {
            context.Emit(new HistoryReductionPerformedEvent(
                AgentName: context.AgentName,
                Iteration: context.Iteration,
                OriginalMessageCount: reduction.MessageCountAtReduction,
                ReducedMessageCount: reduction.MessageCountAtReduction - removedCount,
                MessagesRemoved: removedCount,
                SummaryLength: reduction.SummaryContent.Length,
                Strategy: Config.Strategy.ToString(),
                Timestamp: DateTimeOffset.UtcNow));
        }
        catch (InvalidOperationException)
        {
            // EventCoordinator not configured - event emission is optional
        }
    }
}

//     
// OBSERVABILITY EVENTS
//     

/// <summary>
/// Event emitted when a cached reduction is reused (cache hit).
/// </summary>
public sealed record HistoryReductionCacheHitEvent(
    string AgentName,
    int Iteration,
    int MessageCount,
    TimeSpan CachedReductionAge,
    DateTimeOffset Timestamp) : AgentEvent;

/// <summary>
/// Event emitted when a new reduction is performed (cache miss).
/// </summary>
public sealed record HistoryReductionPerformedEvent(
    string AgentName,
    int Iteration,
    int OriginalMessageCount,
    int ReducedMessageCount,
    int MessagesRemoved,
    int SummaryLength,
    string Strategy,
    DateTimeOffset Timestamp) : AgentEvent;
