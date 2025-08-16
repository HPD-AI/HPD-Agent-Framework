using Microsoft.KernelMemory;


/// <summary>
/// Lightweight context containing only shared memory resources (Project and Conversation)
/// Part of the "Context Provider" pattern to eliminate context leakage in multi-agent workflows
/// </summary>
public class SharedRAGContext
{
    /// <summary>
    /// Conversation-scoped memory shared across all agents in this conversation
    /// </summary>
    public IKernelMemory? ConversationMemory { get; init; }
    
    /// <summary>
    /// Project-scoped memory shared across all conversations in this project
    /// Null when conversation is not part of a project
    /// </summary>
    public IKernelMemory? ProjectMemory { get; init; }
    
    /// <summary>
    /// RAG configuration settings
    /// </summary>
    public RAGConfiguration Configuration { get; init; } = new();

    /// <summary>
    /// Creates a SharedRAGContext for project-associated conversations
    /// </summary>
    public static SharedRAGContext CreateWithProject(
        IKernelMemory? conversationMemory,
        IKernelMemory projectMemory,
        RAGConfiguration? configuration = null)
    {
        return new SharedRAGContext
        {
            ConversationMemory = conversationMemory,
            ProjectMemory = projectMemory ?? throw new ArgumentNullException(nameof(projectMemory)),
            Configuration = configuration ?? new RAGConfiguration()
        };
    }

    /// <summary>
    /// Creates a SharedRAGContext for standalone conversations (no project)
    /// </summary>
    public static SharedRAGContext CreateStandalone(
        IKernelMemory? conversationMemory,
        RAGConfiguration? configuration = null)
    {
        return new SharedRAGContext
        {
            ConversationMemory = conversationMemory,
            ProjectMemory = null,
            Configuration = configuration ?? new RAGConfiguration()
        };
    }

    /// <summary>
    /// Checks if any shared memory sources are available
    /// </summary>
    public bool HasAnyMemory => ConversationMemory != null || ProjectMemory != null;
}

