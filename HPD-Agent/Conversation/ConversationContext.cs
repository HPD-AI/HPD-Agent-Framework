using System.Threading;

/// <summary>
/// Provides ambient context for the current conversation using AsyncLocal storage.
/// This allows plugins and filters to access conversation-scoped data without explicit parameter passing.
/// </summary>
public static class ConversationContext
{
    private static readonly AsyncLocal<string?> _currentConversationId = new();

    // Static fallback in case AsyncLocal doesn't flow (e.g., through Microsoft.Extensions.AI pipeline)
    // This is thread-safe for single-threaded scenarios but may have issues with concurrent requests
    private static string? _fallbackConversationId;

    /// <summary>
    /// Gets the conversation ID for the current async context.
    /// Returns null if no conversation context is set.
    /// Tries AsyncLocal first, then falls back to static field.
    /// </summary>
    public static string? CurrentConversationId
    {
        get
        {
            var asyncLocalValue = _currentConversationId.Value;
            if (!string.IsNullOrEmpty(asyncLocalValue))
                return asyncLocalValue;

            // Fallback to static field if AsyncLocal is empty
            return _fallbackConversationId;
        }
    }

    /// <summary>
    /// Sets the conversation ID for the current async context.
    /// This should be called by the Conversation class before executing agent turns.
    /// </summary>
    internal static void SetConversationId(string? conversationId)
    {
        _currentConversationId.Value = conversationId;
        _fallbackConversationId = conversationId; // Set fallback too
    }

    /// <summary>
    /// Clears the conversation context (cleanup after turn execution).
    /// </summary>
    internal static void Clear()
    {
        _currentConversationId.Value = null;
        _fallbackConversationId = null;
    }
}
