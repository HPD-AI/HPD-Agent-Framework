using System;
using System.Collections.Generic;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KernelMemory.Search;


public class AgentMemoryConfig
{
    public string AgentName { get; set; } = string.Empty;
    public AgentStorageType StorageType { get; set; } = AgentStorageType.InMemory;
    public bool ReadOnlyOptimization { get; set; } = true;
    public string[] DomainContexts { get; set; } = Array.Empty<string>();
    public MemoryEmbeddingProvider? EmbeddingProvider { get; set; }
    public string? EmbeddingModel { get; set; }
    public TextGenerationProvider? TextGenerationProvider { get; set; }
    public string? TextGenerationModel { get; set; }
    public string IndexPrefix { get; set; } = "agent";
    public Dictionary<string, string> CustomTags { get; set; } = new();

    public List<string> DocumentDirectories { get; } = new();
    public List<string> WebSourceUrls { get; } = new();
    public Dictionary<string, string> TextItems { get; } = new();

    public AgentMemoryConfig(string agentName)
    {
        AgentName = agentName;
    }
    
    // Custom RAG extension support
    public IMemoryDb? CustomMemoryDb { get; set; }
    public ISearchClient? CustomSearchClient { get; set; }
}

