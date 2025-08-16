using System;
using System.Collections.Generic;


public enum RetrievalStrategy
{
    Push,    // Automatic injection only
    Pull,    // Plugin-based tools only
    Hybrid   // Both: automatic injection + tools available
}

public class RAGConfiguration
{
    public RetrievalStrategy Strategy { get; set; } = RetrievalStrategy.Hybrid;
    public int ContextWindowThreshold { get; set; } = 3000;
    public double AutoRetrievalThreshold { get; set; } = 0.7;
    public int MaxAutoResults { get; set; } = 5;
    public bool PreferRecentContext { get; set; } = true;
    public Dictionary<string, float> MemorySourceWeights { get; set; } = new Dictionary<string, float>
    {
        ["ConversationRAG"] = 0.8f,
        ["ProjectMemory"] = 0.6f,
        ["AgentMemory"] = 0.4f
    };
    public TimeSpan RetrievalTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxConcurrentSearches { get; set; } = 3;
}
