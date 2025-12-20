using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HPD.Agent.Memory;

/// <summary>
/// Injects agent knowledge documents into the message context before LLM calls.
/// Used with FullTextInjection strategy to make agent knowledge available in every prompt.
/// </summary>
/// <remarks>
/// <para><b>UNIFIED MIDDLEWARE:</b></para>
/// <para>
/// Implements <see cref="IAgentMiddleware"/> with <see cref="IAgentMiddleware.BeforeMessageTurnAsync"/>
/// to inject static knowledge at the start of each message turn.
/// </para>
///
/// <para><b>Caching:</b></para>
/// <para>
/// Knowledge is cached for 5 minutes to avoid repeated disk/database reads.
/// The store can trigger cache invalidation when knowledge is updated.
/// </para>
/// </remarks>
public class StaticMemoryAgentMiddleware : IAgentMiddleware
{
    private readonly StaticMemoryStore _store;
    private readonly string? _knowledgeId;
    private readonly int _maxTokens;
    private readonly ILogger<StaticMemoryAgentMiddleware>? _logger;
    private string? _cachedKnowledgeContext;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheValidTime = TimeSpan.FromMinutes(5);
    private readonly object _cacheLock = new();

    public StaticMemoryAgentMiddleware(
        StaticMemoryStore store,
        string? knowledgeId = null,
        int maxTokens = 8000,
        ILogger<StaticMemoryAgentMiddleware>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _knowledgeId = knowledgeId;
        _maxTokens = maxTokens;
        _logger = logger;

        // Register cache invalidation callback
        _store.RegisterInvalidationCallback(InvalidateCache);
    }

    /// <summary>
    /// Injects static knowledge into the message context before the message turn begins.
    /// </summary>
    public async Task BeforeMessageTurnAsync(
        BeforeMessageTurnContext context,
        CancellationToken cancellationToken)
    {
        var knowledgeTag = await GetKnowledgeContextAsync();

        if (!string.IsNullOrEmpty(knowledgeTag))
        {
            var injectedMessages = InjectKnowledge(context.ConversationHistory, knowledgeTag);

            // V2: ConversationHistory is mutable - replace content
            context.ConversationHistory.Clear();
            foreach (var msg in injectedMessages)
            {
                context.ConversationHistory.Add(msg);
            }

            _logger?.LogDebug(
                "Injected static knowledge ({Length} chars) for agent {AgentName}",
                knowledgeTag.Length,
                context.AgentName);
        }
    }

    private async Task<string> GetKnowledgeContextAsync()
    {
        var now = DateTime.UtcNow;

        lock (_cacheLock)
        {
            if (_cachedKnowledgeContext != null && (now - _lastCacheTime) < _cacheValidTime)
            {
                return _cachedKnowledgeContext;
            }
        }

        // Use knowledge ID from middleware config (falls back to "default" if not set)
        var knowledgeId = _knowledgeId ?? "default";
        var knowledgeTag = await _store.GetCombinedKnowledgeTextAsync(knowledgeId, _maxTokens);

        lock (_cacheLock)
        {
            _cachedKnowledgeContext = knowledgeTag;
            _lastCacheTime = now;
        }

        return knowledgeTag;
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedKnowledgeContext = null;
        }
    }

    private static IEnumerable<ChatMessage> InjectKnowledge(
        IEnumerable<ChatMessage> messages,
        string knowledgeContext)
    {
        // Find the first system message and append knowledge, or create new system message
        var messagesList = messages.ToList();
        var firstSystemMsg = messagesList.FirstOrDefault(m => m.Role == ChatRole.System);

        if (firstSystemMsg != null)
        {
            // Append knowledge to existing system message
            var index = messagesList.IndexOf(firstSystemMsg);
            var updatedContent = firstSystemMsg.Text + "\n\n" + knowledgeContext;
            messagesList[index] = new ChatMessage(ChatRole.System, updatedContent);
        }
        else
        {
            // Insert new system message at the beginning
            messagesList.Insert(0, new ChatMessage(ChatRole.System, knowledgeContext));
        }

        return messagesList;
    }
}
