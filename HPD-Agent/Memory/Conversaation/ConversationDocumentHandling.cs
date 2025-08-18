
    /// <summary>
    /// Defines how documents uploaded to a conversation should be handled
    /// </summary>
    public enum ConversationDocumentHandling
    {
        /// <summary>
        /// Documents are indexed for selective, semantic retrieval. (Default)
        /// This strategy will build and use an IKernelMemory instance.
        /// </summary>
        IndexedRetrieval,

        /// <summary>
        /// The full text of documents is injected directly into the prompt context.
        /// This strategy does NOT build an IKernelMemory instance.
        /// </summary>
        FullTextInjection
    }

