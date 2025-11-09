

/// <summary>
/// Well-known context keys available in PromptFilterContext.Properties and PostInvokeContext.Properties.
/// These provide discoverable, strongly-typed access to context information.
/// </summary>
public static class PromptFilterContextKeys
{
    /// <summary>
    /// Key for accessing the current Project instance.
    /// Type: Project?
    /// Available when the conversation is associated with a project.
    /// </summary>
    public const string Project = "Project";

    /// <summary>
    /// Key for accessing the conversation thread ID.
    /// Type: string?
    /// Available for all conversations.
    /// </summary>
    public const string ConversationId = "ConversationId";

    /// <summary>
    /// Key for accessing the current run ID.
    /// Type: string?
    /// Available during agent runs.
    /// </summary>
    public const string RunId = "RunId";

    /// <summary>
    /// Key for accessing the conversation thread instance.
    /// Type: ConversationThread?
    /// Available when thread context is present.
    /// </summary>
    public const string Thread = "Thread";
}