/// <summary>
/// Defines how agent memory (static or dynamic) should be handled.
/// Shared enum for both Static Memory (knowledge) and Dynamic Memory strategies.
/// </summary>
public enum MemoryStrategy
{
    /// <summary>
    /// Memory/knowledge is indexed in a vector database for selective, semantic retrieval.
    /// Best for large knowledge bases (100K+ tokens) where selective retrieval is needed.
    /// Requires vector store integration (future: IKernelMemory instance).
    /// </summary>
    IndexedRetrieval,

    /// <summary>
    /// The full text of all memory/knowledge is automatically injected into every prompt.
    /// Best for small, critical content (under 10K tokens) that should always be available.
    /// </summary>
    FullTextInjection
}
