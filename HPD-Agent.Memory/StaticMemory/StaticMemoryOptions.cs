using System;
using System.Collections.Generic;

namespace HPD.Agent.Memory;
// Note: All references to HPD.Agent.Memory have been removed as per user instruction.

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
    /// Enable agent-controlled retrieval plugin.
    /// When true, agent gets a search_knowledge() function to manually search when needed.
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
    /// Default: int.MaxValue (no limit) - injects all static memory content
    /// Set explicitly to limit injection size
    /// </summary>
    public int MaxTokens { get; set; } = int.MaxValue;

    /// <summary>
    /// Optional knowledge identifier for scoping knowledge storage.
    /// </summary>
    public string? KnowledgeId { get; set; }

    /// <summary>
    /// Optional agent name for scoping knowledge storage.
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// Custom memory store implementation for FullTextInjection. If not set, uses JsonStaticMemoryStore by default.
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
    /// Use IndexedRetrieval strategy. This is currently a placeholder and will throw a NotImplementedException.
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
    /// Enable agent-controlled retrieval in addition to automatic retrieval.
    /// </summary>
    public StaticMemoryOptions WithAgentControlledRetrieval(bool enabled = true)
    {
        EnableAgentControlledRetrieval = enabled;
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
