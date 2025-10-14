using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using HPD_Agent.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HPD_Agent.Memory.Agent.StaticMemory.IndexedRetrieval;

/// <summary>
/// Prompt filter that automatically retrieves relevant knowledge chunks based on the user's query.
/// Uses vector search to find the most relevant content and injects it into the prompt.
/// This is the "automatic" approach where retrieval happens transparently.
/// </summary>
public class IndexedRetrievalFilter : IPromptFilter
{
    private readonly IDocumentMemoryPipeline _pipeline;
    private readonly string _indexName;
    private readonly int _maxResults;
    private readonly double _minRelevanceScore;
    private readonly ILogger<IndexedRetrievalFilter>? _logger;

    public IndexedRetrievalFilter(
        IDocumentMemoryPipeline pipeline,
        string indexName,
        int maxResults = 5,
        double minRelevanceScore = 0.7,
        ILogger<IndexedRetrievalFilter>? logger = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _indexName = indexName ?? throw new ArgumentNullException(nameof(indexName));
        _maxResults = maxResults;
        _minRelevanceScore = minRelevanceScore;
        _logger = logger;
    }

    public async Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptFilterContext context,
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> next)
    {
        // Extract the user's latest query
        var userQuery = ExtractUserQuery(context.Messages);
        if (string.IsNullOrWhiteSpace(userQuery))
        {
            // No user query to retrieve against, skip retrieval
            return await next(context);
        }

        try
        {
            // Retrieve relevant chunks using vector search
            var result = await _pipeline.RetrieveAsync(
                userQuery,
                _indexName,
                _maxResults,
                _minRelevanceScore);

            if (!result.Success || result.Results.Count == 0)
            {
                _logger?.LogDebug("No relevant knowledge found for query: {Query}", userQuery);
                return await next(context);
            }

            // Build knowledge context from retrieved chunks
            var knowledgeContext = BuildKnowledgeContext(result.Results);

            // Inject knowledge as a system message
            var messagesWithKnowledge = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, knowledgeContext)
            };
            messagesWithKnowledge.AddRange(context.Messages);

            context.Messages = messagesWithKnowledge;

            _logger?.LogDebug(
                "Injected {Count} knowledge chunks (scores: {Scores}) for query: {Query}",
                result.Results.Count,
                string.Join(", ", result.Results.Select(r => $"{r.RelevanceScore:F2}")),
                userQuery);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to retrieve knowledge for query: {Query}", userQuery);
            // Continue without knowledge injection on error
        }

        return await next(context);
    }

    /// <summary>
    /// Extracts the most recent user message as the query.
    /// </summary>
    private string? ExtractUserQuery(IEnumerable<ChatMessage> messages)
    {
        var userMessages = messages
            .Where(m => m.Role == ChatRole.User)
            .Reverse()
            .ToList();

        return userMessages.FirstOrDefault()?.Text;
    }

    /// <summary>
    /// Builds a formatted knowledge context from retrieved chunks.
    /// </summary>
    private string BuildKnowledgeContext(List<RetrievedContent> results)
    {
        var sections = results.Select((chunk, index) =>
        {
            var source = chunk.DocumentId ?? "Unknown";
            var score = chunk.RelevanceScore;
            return $"[Knowledge Chunk {index + 1}] (source: {source}, relevance: {score:F2})\n{chunk.Content}";
        });

        return $@"# Relevant Knowledge

The following knowledge has been retrieved based on the user's query. Use this information to provide accurate, contextual responses.

{string.Join("\n\n---\n\n", sections)}

---

End of retrieved knowledge. Answer the user's query based on this context.";
    }
}
