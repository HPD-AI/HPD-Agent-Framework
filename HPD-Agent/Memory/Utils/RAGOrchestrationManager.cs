using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Microsoft.KernelMemory;




/// <summary>
/// Orchestrates retrieval across all available memory sources (Agent, Conversation, Project)
/// Implements Push/Pull/Hybrid strategies with graceful handling of optional sources
/// </summary>
public class RAGOrchestrationManager
{
    private readonly ILogger<RAGOrchestrationManager>? _logger;

    public RAGOrchestrationManager(ILogger<RAGOrchestrationManager>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Core orchestration method - searches all available memory sources
    /// Handles nullable project memory gracefully
    /// </summary>
    public async Task<List<RetrievalResult>> SearchAllAvailableSourcesAsync(
        string query,
        RAGContext context,
        CancellationToken cancellationToken = default)
    {
        var allResults = new List<RetrievalResult>();
        var config = context.Configuration;

        // 1. Search Agent Memories (always available)
        foreach (var (agentName, agentMemory) in context.AgentMemories)
        {
            if (agentMemory != null)
            {
                var agentResults = await SearchMemorySourceAsync(
                    agentMemory, query, agentName, "Agent",
                    config.MemorySourceWeights.GetValueOrDefault("AgentMemory", 0.4f),
                    2, cancellationToken);
                allResults.AddRange(agentResults);
            }
        }



        // 3. Search Conversation Memory (if available)
        if (context.ConversationMemory != null)
        {
            var convResults = await SearchMemorySourceAsync(
                context.ConversationMemory, query, "conversation", "ConversationRAG",
                config.MemorySourceWeights.GetValueOrDefault("ConversationRAG", 0.8f),
                3, cancellationToken);
            allResults.AddRange(convResults);
        }

        // 4. Search Project Memory (only if available - nullable handling)
        if (context.ProjectMemory != null)
        {
            var projectResults = await SearchMemorySourceAsync(
                context.ProjectMemory, query, "project", "ProjectMemory",
                config.MemorySourceWeights.GetValueOrDefault("ProjectMemory", 0.6f),
                3, cancellationToken);
            allResults.AddRange(projectResults);
        }

        // 5. Apply weighting and ranking
        return RankAndLimitResults(allResults, config.MaxAutoResults);
    }

    /// <summary>
    /// Creates retrieval tools based on available memory sources
    /// Only creates tools for sources that actually exist
    /// </summary>
    public List<AITool> CreateRetrievalTools(RAGContext context)
    {
        var tools = new List<AITool>();

        // Always available: Agent search tool
        if (context.AgentMemories.Any(kv => kv.Value != null))
        {
            tools.Add(CreateAgentSearchTool(context.AgentMemories));
        }



        // Conditional: Conversation search tool
        if (context.ConversationMemory != null)
        {
            tools.Add(CreateConversationSearchTool(context.ConversationMemory));
        }

        // Conditional: Project search tool (only if project exists)
        if (context.ProjectMemory != null)
        {
            tools.Add(CreateProjectSearchTool(context.ProjectMemory));
        }

        return tools;
    }

    /// <summary>
    /// Applies retrieval strategy to conversation messages
    /// Push: Auto-inject context, Pull: Tools only, Hybrid: Both
    /// </summary>
    public async Task<IEnumerable<ChatMessage>> ApplyRetrievalStrategyAsync(
        IEnumerable<ChatMessage> messages,
        RAGContext context,
        CancellationToken cancellationToken = default)
    {
        var messagesList = messages.ToList();
        var config = context.Configuration;

        // Extract user query from last message
        var lastMessage = messagesList.LastOrDefault();
        if (lastMessage?.Role != ChatRole.User)
            return messagesList;

        var userQuery = lastMessage.Text;
        if (string.IsNullOrWhiteSpace(userQuery))
            return messagesList;

        switch (config.Strategy)
        {
            case RetrievalStrategy.Push:
                // Auto-inject relevant context
                return await InjectRetrievalContextAsync(messagesList, userQuery, context, cancellationToken);

            case RetrievalStrategy.Pull:
                // No auto-injection, tools will be provided separately
                return messagesList;

            case RetrievalStrategy.Hybrid:
                // Both: auto-inject + tools available
                return await InjectRetrievalContextAsync(messagesList, userQuery, context, cancellationToken);

            default:
                return messagesList;
        }
    }

    private async Task<List<RetrievalResult>> SearchMemorySourceAsync(
        IKernelMemory memory,
        string query,
        string sourceName,
        string sourceType,
        float weight,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var searchResult = await memory.SearchAsync(
                query: query,
                index: null,
                minRelevance: 0.6f,
                limit: limit,
                cancellationToken: cancellationToken);

            var results = new List<RetrievalResult>();

            foreach (var citation in searchResult.Results.Take(limit))
            {
                results.Add(new RetrievalResult(
                    Id: citation.Link ?? citation.DocumentId ?? Guid.NewGuid().ToString(),
                    Title: ExtractTitle(citation, sourceName),
                    Content: citation.Partitions?.FirstOrDefault()?.Text ?? "",
                    Source: sourceType,
                    Relevance: citation.Partitions?.FirstOrDefault()?.Relevance ?? 0f,
                    Metadata: new Dictionary<string, object>
                    {
                        ["sourceWeight"] = weight,
                        ["sourceName"] = sourceName,
                        ["documentId"] = citation.DocumentId ?? "",
                        ["type"] = sourceType.ToLowerInvariant()
                    }
                ));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to search {SourceType} memory: {Error}", sourceType, ex.Message);
            return new List<RetrievalResult>();
        }
    }



    private string ExtractTitle(Citation citation, string fallback)
    {
        if (!string.IsNullOrEmpty(citation.DocumentId))
            return citation.DocumentId;
        if (!string.IsNullOrEmpty(citation.Link))
            return citation.Link;
        
        var firstPartition = citation.Partitions?.FirstOrDefault();
        if (firstPartition != null && !string.IsNullOrEmpty(firstPartition.Text))
        {
            var words = firstPartition.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(5)) + (words.Length > 5 ? "..." : "");
        }
        
        return fallback;
    }

    private float CalculateSimpleRelevance(string content, string query)
    {
        var queryWords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = queryWords.Count(word => 
            content.Contains(word, StringComparison.OrdinalIgnoreCase));
        return queryWords.Length > 0 ? (float)matches / queryWords.Length : 0f;
    }

    private List<RetrievalResult> RankAndLimitResults(List<RetrievalResult> results, int maxResults)
    {
        return results
            .OrderByDescending(r => {
                var weight = r.Metadata?.GetValueOrDefault("sourceWeight", 1.0f) ?? 1.0f;
                return r.Relevance * (float)weight;
            })
            .Take(maxResults)
            .ToList();
    }

    private AITool CreateAgentSearchTool(Dictionary<string, IKernelMemory?> agentMemories)
    {
        return AIFunctionFactory.Create(
            async (string query, int limit = 3) =>
            {
                var results = new List<RetrievalResult>();
                foreach (var (agentName, memory) in agentMemories)
                {
                    if (memory != null)
                    {
                        var agentResults = await SearchMemorySourceAsync(
                            memory, query, agentName, "Agent", 0.4f, limit, default);
                        results.AddRange(agentResults);
                    }
                }
                return FormatSearchResults(results.Take(limit));
            },
            name: "search_agent_memory",
            description: "Search across agent domain expertise memories");
    }



    private AITool CreateConversationSearchTool(IKernelMemory conversationMemory)
    {
        return AIFunctionFactory.Create(
            async (string query, int limit = 3) =>
            {
                var results = await SearchMemorySourceAsync(
                    conversationMemory, query, "conversation", "ConversationRAG", 0.8f, limit, default);
                return FormatSearchResults(results);
            },
            name: "search_conversation_memory",
            description: "Search documents uploaded during this conversation");
    }

    private AITool CreateProjectSearchTool(IKernelMemory projectMemory)
    {
        return AIFunctionFactory.Create(
            async (string query, int limit = 3) =>
            {
                var results = await SearchMemorySourceAsync(
                    projectMemory, query, "project", "ProjectMemory", 0.6f, limit, default);
                return FormatSearchResults(results);
            },
            name: "search_project_memory",
            description: "Search project-wide shared knowledge and documentation");
    }

    private string FormatSearchResults(IEnumerable<RetrievalResult> results)
    {
        if (!results.Any())
            return "No relevant information found.";
        
        var formatted = results.Select(r => 
            $"**{r.Title}** ({r.Source})\n{r.Content}\n---");
        return string.Join("\n", formatted);
    }

    private async Task<IEnumerable<ChatMessage>> InjectRetrievalContextAsync(
        List<ChatMessage> messages,
        string userQuery,
        RAGContext context,
        CancellationToken cancellationToken)
    {
        // Retrieve top memories based on current query
        var retrievals = await SearchAllAvailableSourcesAsync(userQuery, context, cancellationToken);
        // Convert each retrieval into a system message with context snippet
        var contextMessages = retrievals
            .Take(context.Configuration.MaxAutoResults)
            .Select(r => new ChatMessage(ChatRole.System, $"[{r.Source}:{r.Title}] {r.Content}"));
        // Prepend context messages before original conversation
        return contextMessages.Concat(messages);
    }
}
