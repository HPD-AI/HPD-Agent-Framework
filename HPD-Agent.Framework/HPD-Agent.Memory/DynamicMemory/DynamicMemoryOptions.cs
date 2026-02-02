using System;

namespace HPD.Agent.Memory;
/// <summary>
/// Configuration options for Dynamic Memory (formerly Context-Aware Generation).
/// </summary>
public class DynamicMemoryOptions
{
    /// <summary>Maximum tokens allocated for Dynamic memories (default: 4000)</summary>
    public int MaxTokens { get; set; } = 4000;

    /// <summary>Storage directory for Dynamic memories (used when creating default JSON store)</summary>
    public string StorageDirectory { get; set; } = "./agent-dynamic-memory";

    /// <summary>Automatically evict old memories when approaching token limit</summary>
    public bool EnableAutoEviction { get; set; } = true;

    /// <summary>Token threshold for triggering auto-eviction (percentage)</summary>
    public int AutoEvictionThreshold { get; set; } = 85;

    /// <summary>Optional memory identifier. If not set, will use agent name for memory storage.</summary>
    public string? MemoryId { get; set; }

    /// <summary>Custom memory store implementation. If not set, uses JsonDynamicMemoryStore by default.</summary>
    public DynamicMemoryStore? Store { get; set; }

    /// <summary>Fluently set maximum token count.</summary>
    public DynamicMemoryOptions WithMaxTokens(int tokens)
    {
        MaxTokens = tokens;
        return this;
    }
    
    /// <summary>Fluently set storage directory.</summary>
    public DynamicMemoryOptions WithStorageDirectory(string directory)
    {
        StorageDirectory = directory;
        return this;
    }
    
    /// <summary>Fluently enable auto-eviction with threshold percent.</summary>
    public DynamicMemoryOptions WithAutoEviction(int thresholdPercent = 85)
    {
        EnableAutoEviction = true;
        AutoEvictionThreshold = thresholdPercent;
        return this;
    }

    /// <summary>Fluently set memory identifier for storage.</summary>
    public DynamicMemoryOptions WithMemoryId(string memoryId)
    {
        MemoryId = memoryId;
        return this;
    }

    /// <summary>Fluently set a custom memory store implementation.</summary>
    public DynamicMemoryOptions WithStore(DynamicMemoryStore store)
    {
        Store = store;
        return this;
    }
}

