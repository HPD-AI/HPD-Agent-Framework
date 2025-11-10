using System.Collections.Generic;



/// <summary>
/// Extension methods for strongly-typed access to PromptFilterContext properties.
/// Provides discoverable, IntelliSense-friendly access to context information.
/// </summary>
public static class PromptFilterContextExtensions
{

    /// <summary>
    /// Gets the conversation ID from the filter context, if available.
    /// </summary>
    /// <param name="context">The prompt filter context</param>
    /// <returns>The conversation ID, or null if not available</returns>
    public static string? GetConversationId(this PromptFilterContext context)
    {
        return context.Properties.TryGetValue(PromptFilterContextKeys.ConversationId, out var value) 
            ? value as string 
            : null;
    }

    /// <summary>
    /// Gets the conversation ID from the post-invoke context, if available.
    /// </summary>
    /// <param name="context">The post-invoke context</param>
    /// <returns>The conversation ID, or null if not available</returns>
    public static string? GetConversationId(this PostInvokeContext context)
    {
        return context.Properties.TryGetValue(PromptFilterContextKeys.ConversationId, out var value) 
            ? value as string 
            : null;
    }

    /// <summary>
    /// Gets the conversation thread from the filter context, if available.
    /// </summary>
    /// <param name="context">The prompt filter context</param>
    /// <returns>The conversation thread instance, or null if not available</returns>
    public static ConversationThread? GetThread(this PromptFilterContext context)
    {
        return context.Properties.TryGetValue(PromptFilterContextKeys.Thread, out var value) 
            ? value as ConversationThread 
            : null;
    }

    /// <summary>
    /// Gets the conversation thread from the post-invoke context, if available.
    /// </summary>
    /// <param name="context">The post-invoke context</param>
    /// <returns>The conversation thread instance, or null if not available</returns>
    public static ConversationThread? GetThread(this PostInvokeContext context)
    {
        return context.Properties.TryGetValue(PromptFilterContextKeys.Thread, out var value) 
            ? value as ConversationThread 
            : null;
    }
}