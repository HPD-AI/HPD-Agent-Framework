using System;


/// <summary>
/// Configuration options for Context-Aware Generation memories.
/// </summary>
public class AgentCagOptions
{
    /// <summary>Maximum tokens allocated for CAG memories (default: 4000)</summary>
    public int MaxTokens { get; set; } = 4000;
    
    /// <summary>Storage directory for CAG memories</summary>
    public string StorageDirectory { get; set; } = "./agent-cag-storage";
    
    /// <summary>Automatically evict old memories when approaching token limit</summary>
    public bool EnableAutoEviction { get; set; } = true;
    
    /// <summary>Token threshold for triggering auto-eviction (percentage)</summary>
    public int AutoEvictionThreshold { get; set; } = 85;
    
    /// <summary>Fluently set maximum token count.</summary>
    public AgentCagOptions WithMaxTokens(int tokens)
    {
        MaxTokens = tokens;
        return this;
    }
    
    /// <summary>Fluently set storage directory.</summary>
    public AgentCagOptions WithStorageDirectory(string directory)
    {
        StorageDirectory = directory;
        return this;
    }
    
    /// <summary>Fluently enable auto-eviction with threshold percent.</summary>
    public AgentCagOptions WithAutoEviction(int thresholdPercent = 85)
    {
        EnableAutoEviction = true;
        AutoEvictionThreshold = thresholdPercent;
        return this;
    }
}

