using System;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KernelMemory.Search;


public class ConversationMemoryConfig
{
    public string ConversationId { get; set; } = string.Empty;
    public ConversationStorageType StorageType { get; set; } = ConversationStorageType.InMemory;
    public bool ContextWindowOptimization { get; set; } = true;
    public bool FastRetrieval { get; set; } = true;
    public MemoryEmbeddingProvider? EmbeddingProvider { get; set; }
    public string? EmbeddingModel { get; set; }
    public TextGenerationProvider? TextGenerationProvider { get; set; }
    public string? TextGenerationModel { get; set; }
    public string IndexPrefix { get; set; } = "conversation";
    public double MinRelevanceScore { get; set; } = 0.7;
    public int MaxRetrievalResults { get; set; } = 5;
    
    public ConversationMemoryConfig(string conversationId)
    {
        ConversationId = conversationId;
    }
    
    // Custom RAG extension support
    public IMemoryDb? CustomMemoryDb { get; set; }
    public ISearchClient? CustomSearchClient { get; set; }
}