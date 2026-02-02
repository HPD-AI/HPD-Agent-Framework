using System;

namespace HPD.Agent.Memory;

/// <summary>
/// Configuration for the agent's static knowledge base.
/// This is read-only domain expertise (e.g., Python docs, design patterns, API references).
/// </summary>
public class StaticMemoryConfig
{
    /// <summary>
    /// Strategy for handling agent knowledge (FullTextInjection or IndexedRetrieval).
    /// </summary>
    public MemoryStrategy Strategy { get; set; } = MemoryStrategy.FullTextInjection;

    /// <summary>
    /// Directory where knowledge documents are stored.
    /// </summary>
    public string StorageDirectory { get; set; } = "./agent-static-memory";

    /// <summary>
    /// Maximum tokens to inject when using FullTextInjection strategy.
    /// </summary>
    public int MaxTokens { get; set; } = 8000;

    /// <summary>
    /// Optional agent name for Collapsing knowledge storage.
    /// </summary>
    public string? AgentName { get; set; }
}
