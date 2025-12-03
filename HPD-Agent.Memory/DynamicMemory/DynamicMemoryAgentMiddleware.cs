using System.Text;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HPD.Agent.Memory;

/// <summary>
/// Injects dynamic memory content into the system message before LLM calls.
/// Memories are user/conversation-specific and can be updated during execution.
/// </summary>
/// <remarks>
/// <para><b>UNIFIED MIDDLEWARE:</b></para>
/// <para>
/// Implements <see cref="IAgentMiddleware"/> with <see cref="IAgentMiddleware.BeforeMessageTurnAsync"/>
/// to inject dynamic memories at the start of each message turn.
/// </para>
///
/// <para><b>Memory Format:</b></para>
/// <para>
/// Memories are injected as structured text with Id, Title, and Content.
/// The LLM can reference these memories by Id when calling update/delete functions.
/// </para>
///
/// <para><b>Caching:</b></para>
/// <para>
/// Memories are cached for 1 minute to avoid repeated database reads.
/// Cache is invalidated when memories are modified.
/// </para>
/// </remarks>
public class DynamicMemoryAgentMiddleware : IAgentMiddleware
{
    private readonly DynamicMemoryStore _store;
    private readonly DynamicMemoryOptions _options;
    private readonly ILogger<DynamicMemoryAgentMiddleware>? _logger;
    private readonly string? _memoryId;
    private string? _cachedMemoryContext;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheValidTime = TimeSpan.FromMinutes(1);
    private readonly object _cacheLock = new();

    public DynamicMemoryAgentMiddleware(
        DynamicMemoryStore store,
        DynamicMemoryOptions options,
        ILogger<DynamicMemoryAgentMiddleware>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _memoryId = options.MemoryId;

        // Register cache invalidation callback
        _store.RegisterInvalidationCallback(InvalidateCache);
    }

    /// <summary>
    /// Injects dynamic memories into the message context before the message turn begins.
    /// </summary>
    public async Task BeforeMessageTurnAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        if (context.Messages == null)
            return;

        var memoryTag = await GetMemoryContextAsync(context.AgentName);

        if (!string.IsNullOrEmpty(memoryTag))
        {
            context.Messages = InjectMemories(context.Messages, memoryTag).ToList();
            _logger?.LogDebug(
                "Injected dynamic memories ({Length} chars) for agent {AgentName}",
                memoryTag.Length,
                context.AgentName);
        }
    }

    private async Task<string> GetMemoryContextAsync(string agentName)
    {
        var now = DateTime.UtcNow;

        lock (_cacheLock)
        {
            if (_cachedMemoryContext != null && (now - _lastCacheTime) < _cacheValidTime)
            {
                return _cachedMemoryContext;
            }
        }

        // Use MemoryId if set, otherwise fall back to agent name
        var storageKey = _memoryId ?? agentName;
        var memories = await _store.GetMemoriesAsync(storageKey);
        var memoryTag = BuildMemoryTag(memories);

        lock (_cacheLock)
        {
            _cachedMemoryContext = memoryTag;
            _lastCacheTime = now;
        }

        return memoryTag;
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedMemoryContext = null;
        }
    }

    private static string BuildMemoryTag(List<DynamicMemory> memories)
    {
        if (memories.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("[AGENT_MEMORY_START]");
        sb.AppendLine("NOTE: To edit or delete a memory, call the memory management functions with the memory Id field below (UpdateMemoryAsync / DeleteMemoryAsync).\n");

        foreach (var m in memories)
        {
            // Include explicit Id and Title so the agent can reference the memory when calling update/delete
            sb.AppendLine("---");
            sb.AppendLine($"Id: {m.Id}");
            sb.AppendLine($"Title: {m.Title}");
            sb.AppendLine("Content:");
            sb.AppendLine(m.Content);
        }

        sb.AppendLine("[AGENT_MEMORY_END]");
        return sb.ToString();
    }

    private static IEnumerable<ChatMessage> InjectMemories(
        IEnumerable<ChatMessage> messages,
        string memoryContext)
    {
        // Insert memory context as a system message at the beginning
        var output = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, memoryContext)
        };
        output.AddRange(messages);
        return output;
    }
}
