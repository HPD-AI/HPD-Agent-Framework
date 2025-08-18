
/// <summary>
/// Defines how documents uploaded to a project should be handled
/// </summary>
public enum ProjectDocumentHandling
{
    /// <summary>
    /// Documents are indexed in a shared knowledge base for selective, semantic retrieval by conversations. (Default)
    /// This strategy will build and use an IKernelMemory instance.
    /// </summary>
    IndexedRetrieval,

    /// <summary>
    /// The full text of all project documents is automatically injected into every conversation's prompt context.
    /// This strategy uses the ProjectDocumentManager and does NOT build an IKernelMemory instance.
    /// </summary>
    FullTextInjection
}

