using Microsoft.Extensions.Logging;
using HPD.Agent.Internal.Filters;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// Prompt filter that injects agent knowledge documents into system context.
/// Used with FullTextInjection strategy to make agent knowledge available in every prompt.
/// </summary>
internal class StaticMemoryFilter : IPromptFilter
{
    private readonly StaticMemoryStore _store;
    private readonly string? _knowledgeId;
    private readonly int _maxTokens;
    private readonly ILogger<StaticMemoryFilter>? _logger;
    private string? _cachedKnowledgeContext;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheValidTime = TimeSpan.FromMinutes(5); // Longer cache since knowledge is static
    private readonly object _cacheLock = new object();

    public StaticMemoryFilter(
        StaticMemoryStore store,
        string? knowledgeId,
        int maxTokens,
        ILogger<StaticMemoryFilter>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _knowledgeId = knowledgeId;
        _maxTokens = maxTokens;
        _logger = logger;

        // Register cache invalidation callback
        _store.RegisterInvalidationCallback(InvalidateCache);
    }

    public async Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptFilterContext context,
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> next)
    {
        var now = DateTime.UtcNow;
        string knowledgeTag = string.Empty;

        bool useCache;
        lock (_cacheLock)
        {
            if (_cachedKnowledgeContext != null && (now - _lastCacheTime) < _cacheValidTime)
            {
                knowledgeTag = _cachedKnowledgeContext;
                useCache = true;
            }
            else
            {
                useCache = false;
            }
        }

        if (!useCache)
        {
            // Use knowledge ID from filter (falls back to "default" if not set)
            var knowledgeId = _knowledgeId ?? "default";
            knowledgeTag = await _store.GetCombinedKnowledgeTextAsync(knowledgeId, _maxTokens);

            lock (_cacheLock)
            {
                _cachedKnowledgeContext = knowledgeTag;
                _lastCacheTime = now;
            }
        }

        if (!string.IsNullOrEmpty(knowledgeTag))
        {
            context.Messages = InjectKnowledge(context.Messages, knowledgeTag);
        }

        return await next(context);
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedKnowledgeContext = null;
        }
    }

    private IEnumerable<ChatMessage> InjectKnowledge(IEnumerable<ChatMessage> messages, string knowledgeContext)
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
