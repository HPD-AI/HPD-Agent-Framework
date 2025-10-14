using System;
using System.Collections.Generic;
using HPD_Agent.Memory;

/// <summary>
/// Configuration options for agent knowledge management.
/// Supports both FullTextInjection and IndexedRetrieval strategies.
/// </summary>
public class StaticMemoryOptions
{
    /// <summary>
    /// Strategy for handling agent knowledge (FullTextInjection or IndexedRetrieval).
    /// Default: FullTextInjection for simplicity.
    /// </summary>
    public MemoryStrategy Strategy { get; set; } = MemoryStrategy.FullTextInjection;

    /// <summary>
    /// Document memory pipeline for IndexedRetrieval strategy.
    /// Required when using IndexedRetrieval, ignored for FullTextInjection.
    /// </summary>
    public IDocumentMemoryPipeline? MemoryPipeline { get; set; }

    /// <summary>
    /// Index/collection name to use for IndexedRetrieval operations.
    /// If not set, uses KnowledgeId or AgentName as the index name.
    /// </summary>
    public string? IndexName { get; set; }

    /// <summary>
    /// Maximum number of chunks to retrieve for IndexedRetrieval.
    /// Default: 5 chunks per query.
    /// </summary>
    public int MaxRetrievalResults { get; set; } = 5;

    /// <summary>
    /// Minimum relevance score threshold for IndexedRetrieval (0.0 to 1.0).
    /// Chunks below this threshold will be filtered out.
    /// Default: 0.7 (70% relevance)
    /// </summary>
    public double MinRelevanceScore { get; set; } = 0.7;

    /// <summary>
    /// Enable agent-controlled retrieval plugin.
    /// When true, agent gets a search_knowledge() function to manually search when needed.
    /// Can be used alongside automatic retrieval filter.
    /// Default: false (only automatic retrieval via filter)
    /// </summary>
    public bool EnableAgentControlledRetrieval { get; set; } = false;

    /// <summary>
    /// Directory where knowledge documents are stored (used when creating default JSON store).
    /// Default: "./agent-static-memory"
    /// </summary>
    public string StorageDirectory { get; set; } = "./agent-static-memory";

    /// <summary>
    /// Maximum tokens to inject when using FullTextInjection strategy.
    /// Default: 8000 tokens (~32K characters)
    /// </summary>
    public int MaxTokens { get; set; } = 8000;

    /// <summary>
    /// Optional knowledge identifier for scoping knowledge storage.
    /// If not set, will use AgentName (which defaults to agent name from builder).
    /// Use the same KnowledgeId across different agents to share the same knowledge base.
    /// Example: Multiple "SupportAgent" instances can share "CompanyKnowledge" by setting the same KnowledgeId.
    /// </summary>
    public string? KnowledgeId { get; set; }

    /// <summary>
    /// Optional agent name for scoping knowledge storage.
    /// If KnowledgeId is not set, this will be used as the storage key.
    /// If both are null, uses agent name from builder.
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// Custom memory store implementation. If not set, uses JsonStaticMemoryStore by default.
    /// </summary>
    public StaticMemoryStore? Store { get; set; }

    /// <summary>
    /// Documents to add at agent build time (file paths or URLs).
    /// </summary>
    internal List<StaticMemoryDocumentToAdd> DocumentsToAdd { get; set; } = new();

    /// <summary>
    /// Add a knowledge document at agent build time.
    /// </summary>
    public StaticMemoryOptions AddDocument(string pathOrUrl, string? description = null, List<string>? tags = null)
    {
        DocumentsToAdd.Add(new StaticMemoryDocumentToAdd
        {
            PathOrUrl = pathOrUrl,
            Description = description,
            Tags = tags ?? new List<string>()
        });
        return this;
    }

    /// <summary>
    /// Set the storage directory for knowledge documents.
    /// </summary>
    public StaticMemoryOptions WithStorageDirectory(string directory)
    {
        StorageDirectory = directory;
        return this;
    }

    /// <summary>
    /// Set the maximum tokens for FullTextInjection strategy.
    /// </summary>
    public StaticMemoryOptions WithMaxTokens(int maxTokens)
    {
        MaxTokens = maxTokens;
        return this;
    }

    /// <summary>
    /// Set the knowledge identifier for scoping knowledge storage.
    /// Use this to explicitly control knowledge sharing across agents.
    /// </summary>
    public StaticMemoryOptions WithKnowledgeId(string knowledgeId)
    {
        KnowledgeId = knowledgeId;
        return this;
    }

    /// <summary>
    /// Set the agent name for scoping knowledge storage.
    /// </summary>
    public StaticMemoryOptions WithAgentName(string agentName)
    {
        AgentName = agentName;
        return this;
    }

    /// <summary>
    /// Use FullTextInjection strategy (inject all knowledge into every prompt).
    /// </summary>
    public StaticMemoryOptions UseFullTextInjection()
    {
        Strategy = MemoryStrategy.FullTextInjection;
        return this;
    }

    /// <summary>
    /// Use IndexedRetrieval strategy (vector search for relevant knowledge).
    /// Requires a memory pipeline to be configured via WithMemoryPipeline().
    /// </summary>
    public StaticMemoryOptions UseIndexedRetrieval()
    {
        Strategy = MemoryStrategy.IndexedRetrieval;
        return this;
    }

    /// <summary>
    /// Fluently set a custom memory store implementation.
    /// </summary>
    public StaticMemoryOptions WithStore(StaticMemoryStore store)
    {
        Store = store;
        return this;
    }

    /// <summary>
    /// Set the document memory pipeline for IndexedRetrieval.
    /// Required when using IndexedRetrieval strategy.
    /// </summary>
    public StaticMemoryOptions WithMemoryPipeline(IDocumentMemoryPipeline pipeline)
    {
        MemoryPipeline = pipeline;
        return this;
    }

    /// <summary>
    /// Set the index/collection name for IndexedRetrieval operations.
    /// </summary>
    public StaticMemoryOptions WithIndexName(string indexName)
    {
        IndexName = indexName;
        return this;
    }

    /// <summary>
    /// Enable agent-controlled retrieval in addition to automatic retrieval.
    /// Gives the agent a search_knowledge() function to manually search when needed.
    /// </summary>
    public StaticMemoryOptions WithAgentControlledRetrieval(bool enabled = true)
    {
        EnableAgentControlledRetrieval = enabled;
        return this;
    }

    /// <summary>
    /// Configure retrieval parameters for IndexedRetrieval.
    /// </summary>
    public StaticMemoryOptions WithRetrievalConfig(int maxResults = 5, double minRelevanceScore = 0.7)
    {
        MaxRetrievalResults = maxResults;
        MinRelevanceScore = minRelevanceScore;
        return this;
    }
}

/// <summary>
/// Internal: Represents a knowledge document to be added at agent build time.
/// </summary>
internal class StaticMemoryDocumentToAdd
{
    public string PathOrUrl { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
}
