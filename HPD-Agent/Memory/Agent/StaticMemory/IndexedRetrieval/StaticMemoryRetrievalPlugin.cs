using Microsoft.Extensions.Logging;
using HPD_Agent.Memory;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace HPD_Agent.Memory.Agent.StaticMemory.IndexedRetrieval;

/// <summary>
/// HPD-Agent AI plugin that gives agents manual control over knowledge retrieval.
/// This is the "agent-controlled" approach where the agent decides when to search.
/// Can be used alongside IndexedRetrievalFilter for hybrid automatic + manual retrieval.
/// </summary>
public class StaticMemoryRetrievalPlugin
{
    private readonly IDocumentMemoryPipeline _pipeline;
    private readonly string _indexName;
    private readonly int _defaultMaxResults;
    private readonly double _defaultMinRelevanceScore;
    private readonly ILogger<StaticMemoryRetrievalPlugin>? _logger;

    public StaticMemoryRetrievalPlugin(
        IDocumentMemoryPipeline pipeline,
        string indexName,
        int defaultMaxResults = 5,
        double defaultMinRelevanceScore = 0.7,
        ILogger<StaticMemoryRetrievalPlugin>? logger = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _indexName = indexName ?? throw new ArgumentNullException(nameof(indexName));
        _defaultMaxResults = defaultMaxResults;
        _defaultMinRelevanceScore = defaultMinRelevanceScore;
        _logger = logger;
    }

    [AIFunction]
    [Description("Search the knowledge base for relevant information. Use this when you need specific knowledge to answer the user's question.")]
    public async Task<string> SearchKnowledgeAsync(
        [Description("The search query describing what knowledge you need")] string query,
        [Description("Maximum number of results to return (default: 5)")] int? maxResults = null,
        [Description("Minimum relevance score threshold 0.0-1.0 (default: 0.7)")] double? minRelevanceScore = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Error: Search query is required.";
        }

        try
        {
            var max = maxResults ?? _defaultMaxResults;
            var minScore = minRelevanceScore ?? _defaultMinRelevanceScore;

            _logger?.LogDebug("Agent searching knowledge: {Query} (max: {Max}, min score: {MinScore})",
                query, max, minScore);

            var result = await _pipeline.RetrieveAsync(
                query,
                _indexName,
                max,
                minScore);

            if (!result.Success)
            {
                var error = result.ErrorMessage ?? "Unknown error";
                _logger?.LogWarning("Knowledge search failed: {Error}", error);
                return $"Search failed: {error}";
            }

            if (result.Results.Count == 0)
            {
                return "No relevant knowledge found for that query. Try rephrasing or broadening your search.";
            }

            // Format results for the agent
            return FormatSearchResults(result.Results, query);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception during knowledge search: {Query}", query);
            return $"Search error: {ex.Message}";
        }
    }

    /// <summary>
    /// Formats search results in a clear, structured format for the agent.
    /// </summary>
    private string FormatSearchResults(System.Collections.Generic.List<RetrievedContent> results, string query)
    {
        var formatted = new System.Text.StringBuilder();
        formatted.AppendLine($"Found {results.Count} relevant knowledge chunks for: \"{query}\"");
        formatted.AppendLine();

        for (int i = 0; i < results.Count; i++)
        {
            var chunk = results[i];
            var source = chunk.DocumentId ?? "Unknown source";
            var score = chunk.RelevanceScore;

            formatted.AppendLine($"[{i + 1}] {source} (relevance: {score:P0})");
            formatted.AppendLine(chunk.Content);

            if (i < results.Count - 1)
            {
                formatted.AppendLine();
                formatted.AppendLine("---");
                formatted.AppendLine();
            }
        }

        return formatted.ToString();
    }
}
