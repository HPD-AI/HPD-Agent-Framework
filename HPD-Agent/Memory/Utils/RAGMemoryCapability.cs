using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;


/// <summary>
/// RAG Memory Capability implementing push strategy for automatic context injection
/// Follows the AudioCapability pattern for consistent capability architecture
/// </summary>
public class RAGMemoryCapability : IAsyncDisposable
{
    private readonly Agent _agent;
    private readonly RAGConfiguration _configuration;
    private readonly RAGOrchestrationManager _orchestrationManager;
    private readonly ILogger<RAGMemoryCapability>? _logger;
    private RAGContext? _currentContext;
    private bool _disposed;

    public RAGMemoryCapability(
        Agent agent,
        RAGConfiguration configuration,
        ILogger<RAGMemoryCapability>? logger = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger;
        _orchestrationManager = new RAGOrchestrationManager();
    }

    /// <summary>
    /// Search across all available memory sources using the current RAG context
    /// </summary>
    public async Task<List<RetrievalResult>> SearchAllSourcesAsync(
        string query, 
        RAGContext context, 
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // Store context for plugin access
        _currentContext = context;

        try
        {
            return await _orchestrationManager.SearchAllAvailableSourcesAsync(query, context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error searching memory sources for query: {Query}", query);
            return new List<RetrievalResult>();
        }
    }

    /// <summary>
    /// Create retrieval tools based on available memory sources
    /// </summary>
    public List<AITool> CreateRetrievalTools(RAGContext context)
    {
        ThrowIfDisposed();
        
        // Store context for plugin access
        _currentContext = context;

        return _orchestrationManager.CreateRetrievalTools(context);
    }

    /// <summary>
    /// Apply retrieval strategy to inject context before agent processing (Push strategy)
    /// </summary>
    public async Task<IEnumerable<ChatMessage>> ApplyRetrievalStrategyAsync(
        IEnumerable<ChatMessage> messages, 
        RAGContext context, 
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // Store context for plugin access
        _currentContext = context;

        try
        {
            // For push strategy, automatically inject relevant context
            return await InjectRetrievalContext(messages, context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error applying retrieval strategy");
            return messages; // Return original messages on error
        }
    }

    /// <summary>
    /// Get the current RAG context (used by RAGPlugin)
    /// </summary>
    public RAGContext? GetCurrentContext()
    {
        return _currentContext;
    }

    /// <summary>
    /// Inject retrieval context into messages for push strategy
    /// </summary>
    private async Task<IEnumerable<ChatMessage>> InjectRetrievalContext(
        IEnumerable<ChatMessage> messages,
        RAGContext context,
        CancellationToken cancellationToken)
    {
        var messagesList = messages.ToList();
        var lastUserMessage = messagesList.LastOrDefault(m => m.Role == ChatRole.User);
        
        if (lastUserMessage?.Text == null)
            return messages;

        // Search for relevant context
        var searchResults = await SearchAllSourcesAsync(lastUserMessage.Text, context, cancellationToken);
        
        if (!searchResults.Any() || searchResults.All(r => r.Relevance < _configuration.AutoRetrievalThreshold))
            return messages;

        // Create context injection message
        var contextText = FormatContextForInjection(searchResults);
        var contextMessage = new ChatMessage(ChatRole.System, 
            $"Relevant context from memory:\n{contextText}");

        // Insert context before the last user message
        var result = new List<ChatMessage>();
        result.AddRange(messagesList.Take(messagesList.Count - 1));
        result.Add(contextMessage);
        result.Add(lastUserMessage);

        return result;
    }

    private string FormatContextForInjection(List<RetrievalResult> results)
    {
        var topResults = results
            .Where(r => r.Relevance >= _configuration.AutoRetrievalThreshold)
            .Take(_configuration.MaxAutoResults)
            .OrderByDescending(r => r.Relevance);

        return string.Join("\n\n", topResults.Select(r => 
            $"From {r.Source}: {r.Content}"));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RAGMemoryCapability));
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _currentContext = null;
            _disposed = true;
        }
        await Task.CompletedTask;
    }
}
