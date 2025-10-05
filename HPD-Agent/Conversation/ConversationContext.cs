using System;
using System.Collections.Generic;
using System.Threading;

/// <summary>
/// Provides ambient context for the current conversation using AsyncLocal storage.
/// This allows plugins and filters to access conversation-scoped data without explicit parameter passing.
/// </summary>
public static class ConversationContext
{
    private static readonly AsyncLocal<ConversationExecutionContext?> _current = new();

    // Legacy static fallback for backwards compatibility
    private static ConversationExecutionContext? _fallbackContext;

    /// <summary>
    /// Gets the full conversation execution context for the current async context.
    /// Returns null if no conversation context is set.
    /// </summary>
    public static ConversationExecutionContext? Current
    {
        get
        {
            var asyncLocalValue = _current.Value;
            if (asyncLocalValue != null)
                return asyncLocalValue;

            // Fallback to static field if AsyncLocal is empty
            return _fallbackContext;
        }
    }

    /// <summary>
    /// Gets the conversation ID for the current async context.
    /// Returns null if no conversation context is set.
    /// Backwards compatible property - existing code continues to work.
    /// </summary>
    public static string? CurrentConversationId => Current?.ConversationId;

    /// <summary>
    /// Sets the conversation context for the current async context.
    /// This should be called by the Conversation class before executing agent turns.
    /// </summary>
    internal static void Set(ConversationExecutionContext? context)
    {
        _current.Value = context;
        _fallbackContext = context; // Set fallback too
    }

    /// <summary>
    /// Sets only the conversation ID (backwards compatible method).
    /// Creates a minimal context with just the conversation ID.
    /// </summary>
    internal static void SetConversationId(string? conversationId)
    {
        if (string.IsNullOrEmpty(conversationId))
        {
            Clear();
            return;
        }

        var context = new ConversationExecutionContext(conversationId);
        Set(context);
    }

    /// <summary>
    /// Clears the conversation context (cleanup after turn execution).
    /// </summary>
    internal static void Clear()
    {
        _current.Value = null;
        _fallbackContext = null;
    }
}

/// <summary>
/// Rich context object containing conversation-scoped execution state.
/// Extensible for future features without breaking existing code.
/// </summary>
public class ConversationExecutionContext
{
    /// <summary>
    /// Unique identifier for this conversation.
    /// </summary>
    public string ConversationId { get; }

    /// <summary>
    /// Name of the agent executing in this conversation (optional).
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// The current agent run context (if available).
    /// Provides access to iteration count, elapsed time, and other runtime state.
    /// </summary>
    public AgentRunContext? RunContext { get; set; }

    /// <summary>
    /// Current iteration number within the agent's execution.
    /// Returns 0 if RunContext is not available.
    /// </summary>
    public int CurrentIteration => RunContext?.CurrentIteration ?? 0;

    /// <summary>
    /// Maximum iterations allowed for this execution.
    /// Returns 10 if RunContext is not available.
    /// </summary>
    public int MaxIterations => RunContext?.MaxIterations ?? 10;

    /// <summary>
    /// Time elapsed since the start of execution.
    /// Returns TimeSpan.Zero if RunContext is not available.
    /// </summary>
    public TimeSpan ElapsedTime => RunContext?.ElapsedTime ?? TimeSpan.Zero;

    /// <summary>
    /// Flexible metadata storage for plugins to share data within a conversation.
    /// Use namespaced keys to avoid conflicts (e.g., "myPlugin.lastResult").
    /// </summary>
    public Dictionary<string, object> Metadata { get; } = new();

    /// <summary>
    /// Creates a new conversation execution context with the specified conversation ID.
    /// </summary>
    public ConversationExecutionContext(string conversationId)
    {
        ConversationId = conversationId ?? throw new ArgumentNullException(nameof(conversationId));
    }

    /// <summary>
    /// Checks if execution is approaching a timeout threshold.
    /// </summary>
    /// <param name="threshold">Time buffer before timeout (e.g., 30 seconds)</param>
    /// <param name="maxDuration">Maximum allowed duration (defaults to 5 minutes)</param>
    /// <returns>True if elapsed time is within threshold of max duration</returns>
    public bool IsNearTimeout(TimeSpan threshold, TimeSpan? maxDuration = null)
    {
        var max = maxDuration ?? TimeSpan.FromMinutes(5);
        return ElapsedTime > (max - threshold);
    }

    /// <summary>
    /// Checks if execution is near the iteration limit.
    /// </summary>
    /// <param name="buffer">Number of iterations before limit (e.g., 2 means stop if 2 iterations remain)</param>
    /// <returns>True if current iteration is within buffer of max iterations</returns>
    public bool IsNearIterationLimit(int buffer = 2)
    {
        return CurrentIteration >= MaxIterations - buffer;
    }
}
