using System;

namespace HPD.Agent.Memory;

/// <summary>
/// Configuration for the agent's Dynamic Memory (formerly Context-Aware Generation).
/// Dynamic Memory stores contextual facts that can be automatically evicted when approaching token limits.
/// </summary>
public class DynamicMemoryConfig
{
    /// <summary>
    /// The root directory where agent memories will be stored.
    /// </summary>
    public string StorageDirectory { get; set; } = "./agent-dynamic-memory";

    /// <summary>
    /// The maximum number of tokens to include from the Dynamic Memory.
    /// </summary>
    public int MaxTokens { get; set; } = 4000;

    /// <summary>
    /// Automatically evict old memories when approaching token limit.
    /// </summary>
    public bool EnableAutoEviction { get; set; } = true;

    /// <summary>
    /// Token threshold for triggering auto-eviction (percentage).
    /// </summary>
    public int AutoEvictionThreshold { get; set; } = 85;
}
