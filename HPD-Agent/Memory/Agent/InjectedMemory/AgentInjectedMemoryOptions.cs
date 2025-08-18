using System;


/// <summary>
/// Configuration options for Injected Memory (formerly Context-Aware Generation).
/// </summary>
public class AgentInjectedMemoryOptions
{
    /// <summary>Maximum tokens allocated for injected memories (default: 4000)</summary>
    public int MaxTokens { get; set; } = 4000;
    
    /// <summary>Storage directory for injected memories</summary>
    public string StorageDirectory { get; set; } = "./agent-injected-memory-storage";
    
    /// <summary>Automatically evict old memories when approaching token limit</summary>
    public bool EnableAutoEviction { get; set; } = true;
    
    /// <summary>Token threshold for triggering auto-eviction (percentage)</summary>
    public int AutoEvictionThreshold { get; set; } = 85;
    
    /// <summary>Fluently set maximum token count.</summary>
    public AgentInjectedMemoryOptions WithMaxTokens(int tokens)
    {
        MaxTokens = tokens;
        return this;
    }
    
    /// <summary>Fluently set storage directory.</summary>
    public AgentInjectedMemoryOptions WithStorageDirectory(string directory)
    {
        StorageDirectory = directory;
        return this;
    }
    
    /// <summary>Fluently enable auto-eviction with threshold percent.</summary>
    public AgentInjectedMemoryOptions WithAutoEviction(int thresholdPercent = 85)
    {
        EnableAutoEviction = true;
        AutoEvictionThreshold = thresholdPercent;
        return this;
    }
}

