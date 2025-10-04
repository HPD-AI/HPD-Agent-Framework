using Microsoft.Extensions.AI;

/// <summary>
/// Extension methods for ChatMessage to support token-aware history reduction.
/// Follows BAML's pattern of storing provider-returned token counts in message metadata.
/// </summary>
public static class ChatMessageExtensions
{
    private const string TokenCountKey = "TokenCount";
    private const double CharsPerTokenEstimate = 3.5; // Conservative estimate: 1 token ≈ 3.5 chars

    /// <summary>
    /// Gets or sets the token count for this message.
    /// Returns actual count from provider response if available, otherwise null.
    /// </summary>
    public static int? GetTokenCount(this ChatMessage message)
    {
        if (message.AdditionalProperties?.TryGetValue(TokenCountKey, out var value) == true)
        {
            return value switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                double doubleValue => (int)doubleValue,
                _ => null
            };
        }
        return null;
    }

    /// <summary>
    /// Sets the token count for this message.
    /// Stores the count in AdditionalProperties for persistence.
    /// </summary>
    public static void SetTokenCount(this ChatMessage message, int tokenCount)
    {
        message.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        message.AdditionalProperties[TokenCountKey] = tokenCount;
    }

    /// <summary>
    /// Gets the token count for this message, using estimation if actual count is unavailable.
    /// Uses provider-returned counts when available (most accurate).
    /// Falls back to character-based estimation for messages without usage data.
    /// </summary>
    public static int GetTokenCountOrEstimate(this ChatMessage message)
    {
        // Return actual count if available (from provider response)
        var actualCount = message.GetTokenCount();
        if (actualCount.HasValue)
            return actualCount.Value;

        // Estimate based on text content
        var text = ExtractTextContent(message);
        return EstimateTokenCount(text);
    }

    /// <summary>
    /// Calculates total token count for a collection of messages.
    /// Uses actual provider counts when available, estimates otherwise.
    /// </summary>
    public static int CalculateTotalTokens(this IEnumerable<ChatMessage> messages)
    {
        return messages.Sum(m => m.GetTokenCountOrEstimate());
    }

    /// <summary>
    /// Estimates token count from text using conservative character-based heuristic.
    /// This is a fallback when provider counts are unavailable.
    ///
    /// Note: This is approximate. Actual tokenization varies by provider:
    /// - OpenAI (GPT-4): ~4 chars/token for English, ~2 for code
    /// - Anthropic (Claude): Uses sentencepiece tokenizer
    /// - Google (Gemini): Different sentencepiece vocabulary
    ///
    /// Conservative estimate (3.5 chars/token) tends to overestimate, which is safer
    /// for context window management than underestimating.
    /// </summary>
    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return (int)Math.Ceiling(text.Length / CharsPerTokenEstimate);
    }

    /// <summary>
    /// Extracts all text content from a message's contents.
    /// </summary>
    private static string ExtractTextContent(ChatMessage message)
    {
        var textContents = message.Contents
            .OfType<TextContent>()
            .Select(tc => tc.Text)
            .Where(text => !string.IsNullOrEmpty(text));

        return string.Join(" ", textContents);
    }
}
