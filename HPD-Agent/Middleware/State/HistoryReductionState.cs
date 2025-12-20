namespace HPD.Agent;

/// <summary>
/// State for history reduction middleware. Immutable record with static abstract key.
/// Caches history reduction results to avoid expensive re-summarization on every turn.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b></para>
/// <para>
/// This state is immutable and flows through the context.
/// It is NOT stored in middleware instance fields, preserving thread safety for concurrent RunAsync() calls.
/// </para>
///
/// <para><b>Caching Strategy:</b></para>
/// <list type="bullet">
/// <item>Cache key: Message hash (first N messages)</item>
/// <item>Cache validity: Based on message count + threshold</item>
/// <item>Cache hit: Reuse summary, no LLM call</item>
/// <item>Cache miss: Re-summarize, update cache</item>
/// </list>
///
/// <para><b>Usage:</b></para>
/// <code>
/// // Read state
/// var hrState = context.State.MiddlewareState.HistoryReduction ?? new();
/// if (hrState.LastReduction?.IsValidFor(messages.Count) == true)
/// {
///     // Cache hit - use hrState.LastReduction
/// }
///
/// // Update state
/// context.UpdateState(s => s with
/// {
///     MiddlewareState = s.MiddlewareState.WithHistoryReduction(hrState.WithReduction(newReduction))
/// });
/// </code>
/// </remarks>
[MiddlewareState]
public sealed record HistoryReductionStateData
{

    /// <summary>
    /// Last successful reduction (null if no reduction performed yet).
    /// Contains the summary, metadata, and validity information.
    /// </summary>
    public CachedReduction? LastReduction { get; init; }

    /// <summary>
    /// Records a new reduction, replacing the previous cache entry.
    /// </summary>
    /// <param name="reduction">New reduction to cache</param>
    /// <returns>New state with updated cache</returns>
    public HistoryReductionStateData WithReduction(CachedReduction reduction) =>
        this with { LastReduction = reduction };
}

/// <summary>
/// Represents a cached history reduction result.
/// Contains summary, metadata, and integrity checking for cache validation.
/// </summary>
/// <remarks>
/// <para><b>Design Rationale:</b></para>
/// <para>
/// This record captures everything needed to reuse a reduction across multiple turns:
/// - Summary content (to inject in place of old messages)
/// - Metadata for cache validation (message count, hash, threshold)
/// - Integrity checking (to detect message modifications)
/// </para>
///
/// <para><b>Cache Validity:</b></para>
/// <code>
/// ┌────────────────────────────────────────────────────────┐
/// │ Turn 1: 100 messages → Reduce → Summary (0-89)        │
/// │         Cache: MessageCountAtReduction=100             │
/// │                SummarizedUpToIndex=90                  │
/// │                ReductionThreshold=5                    │
/// └────────────────────────────────────────────────────────┘
///                          ↓
///              IsValidFor(currentCount)?
///                          ↓
/// ┌────────────────────────────────────────────────────────┐
/// │ Turn 2: 104 messages                                   │
/// │         104 - 100 = 4 new messages                     │
/// │         4 &lt;= 5 (threshold) → CACHE HIT               │
/// └────────────────────────────────────────────────────────┘
///                          ↓
/// ┌────────────────────────────────────────────────────────┐
/// │ Turn 3: 106 messages                                   │
/// │         106 - 100 = 6 new messages                     │
/// │         6 &gt; 5 (threshold) → CACHE MISS ❌               │
/// │         Re-summarize required                          │
/// └────────────────────────────────────────────────────────┘
/// </code>
/// </remarks>
public sealed record CachedReduction
{
    /// <summary>
    /// Message index where summary ends (exclusive).
    /// Messages [0..SummarizedUpToIndex) were summarized.
    /// Messages [SummarizedUpToIndex..end) are kept verbatim.
    /// </summary>
    public int SummarizedUpToIndex { get; init; }

    /// <summary>
    /// Total message count when this reduction was performed.
    /// Used to detect how many NEW messages have been added since reduction.
    /// </summary>
    public int MessageCountAtReduction { get; init; }

    /// <summary>
    /// Generated summary text (replaces messages 0..SummarizedUpToIndex).
    /// </summary>
    public string SummaryContent { get; init; } = string.Empty;

    /// <summary>
    /// When this reduction was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Hash of summarized messages (for integrity checking).
    /// Detects if messages were modified/deleted/reordered.
    /// </summary>
    public string MessageHash { get; init; } = string.Empty;

    /// <summary>
    /// Target message count from configuration (for reference).
    /// </summary>
    public int TargetMessageCount { get; init; }

    /// <summary>
    /// Threshold for triggering re-reduction.
    /// Number of new messages allowed before cache expires.
    /// </summary>
    public int ReductionThreshold { get; init; }

    /// <summary>
    /// Checks if this reduction is still valid for the given message count.
    /// Returns true if new messages are within threshold, false if re-reduction needed.
    /// </summary>
    /// <param name="currentMessageCount">Current total message count</param>
    /// <returns>True if cache can be reused, false if re-reduction needed</returns>
    public bool IsValidFor(int currentMessageCount)
    {
        // If messages were deleted, cache is invalid
        if (currentMessageCount < MessageCountAtReduction)
            return false;

        // Calculate new messages added since reduction
        var newMessagesCount = currentMessageCount - MessageCountAtReduction;

        // Valid if new messages are within threshold
        return newMessagesCount <= ReductionThreshold;
    }

    /// <summary>
    /// Validates that the summarized portion of messages hasn't been modified.
    /// Compares hash of first N messages against stored hash.
    /// </summary>
    /// <param name="messages">Current message list to validate</param>
    /// <returns>True if messages are unchanged, false if modified</returns>
    /// <exception cref="ArgumentException">If message count is less than SummarizedUpToIndex</exception>
    public bool ValidateIntegrity(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        if (messages.Count < SummarizedUpToIndex)
            throw new ArgumentException(
                $"Message count ({messages.Count}) is less than SummarizedUpToIndex ({SummarizedUpToIndex}). " +
                "This indicates messages were deleted, which invalidates the cache.");

        // If no messages were summarized, integrity is always valid
        if (SummarizedUpToIndex == 0)
            return true;

        // Compute hash of summarized portion and compare
        var currentHash = ComputeMessageHash(messages.Take(SummarizedUpToIndex));
        return currentHash == MessageHash;
    }

    /// <summary>
    /// Applies this reduction to a message list, returning reduced messages.
    /// Replaces summarized messages with summary, keeps recent messages verbatim.
    /// </summary>
    /// <param name="allMessages">Full message history (non-system messages only)</param>
    /// <param name="systemMessage">Optional system message to prepend</param>
    /// <returns>Reduced message list: [System?] [Summary] [Recent messages...]</returns>
    /// <exception cref="InvalidOperationException">If integrity check fails</exception>
    public IEnumerable<Microsoft.Extensions.AI.ChatMessage> ApplyToMessages(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> allMessages,
        Microsoft.Extensions.AI.ChatMessage? systemMessage = null)
    {
        var messagesList = allMessages.ToList();

        // Validate integrity before applying
        if (!ValidateIntegrity(messagesList))
        {
            throw new InvalidOperationException(
                "Cannot apply reduction: Message integrity check failed. " +
                "Messages have been modified since reduction was created.");
        }

        // Build reduced list
        var result = new List<Microsoft.Extensions.AI.ChatMessage>();

        // 1. System message (if provided)
        if (systemMessage != null)
            result.Add(systemMessage);

        // 2. Summary message (replaces 0..SummarizedUpToIndex)
        result.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant,
            SummaryContent));

        // 3. Recent messages (SummarizedUpToIndex..end)
        result.AddRange(messagesList.Skip(SummarizedUpToIndex));

        return result;
    }

    /// <summary>
    /// Creates a new CachedReduction from reduction results.
    /// </summary>
    public static CachedReduction Create(
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages,
        string summaryContent,
        int summarizedUpToIndex,
        int targetMessageCount,
        int reductionThreshold)
    {
        var messageHash = ComputeMessageHash(messages.Take(summarizedUpToIndex));

        return new CachedReduction
        {
            SummarizedUpToIndex = summarizedUpToIndex,
            MessageCountAtReduction = messages.Count,
            SummaryContent = summaryContent,
            CreatedAt = DateTime.UtcNow,
            MessageHash = messageHash,
            TargetMessageCount = targetMessageCount,
            ReductionThreshold = reductionThreshold
        };
    }

    /// <summary>
    /// Computes a deterministic hash of messages for integrity checking.
    /// Uses Microsoft's AIJsonUtilities.HashDataToString for proper content normalization.
    /// </summary>
    private static string ComputeMessageHash(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        // Use Microsoft's official hashing utility instead of custom SHA256
        // Benefits:
        // - Handles JSON normalization automatically (property order, whitespace)
        // - Properly handles AIContent polymorphism (TextContent, FunctionCallContent, etc.)
        // - More robust than string concatenation (e.g., role:text collision: "user:hello|assistant" vs "user|hello:assistant")
        return Microsoft.Extensions.AI.AIJsonUtilities.HashDataToString(
            messages.ToArray(),
            Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions);
    }
}
