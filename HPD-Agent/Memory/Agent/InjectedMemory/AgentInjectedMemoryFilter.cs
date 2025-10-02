using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using System.Text;


/// <summary>
/// Prompt filter that injects memory content into the system message.
/// </summary>
public class AgentInjectedMemoryFilter : IPromptFilter
{
    private readonly AgentInjectedMemoryManager _memoryManager;
    private readonly AgentInjectedMemoryOptions _options;
    private readonly ILogger<AgentInjectedMemoryFilter>? _logger;
    private readonly string? _memoryId;
    private string? _cachedMemoryContext;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheValidTime;
    private readonly object _cacheLock = new object();

    public AgentInjectedMemoryFilter(AgentInjectedMemoryOptions options, ILogger<AgentInjectedMemoryFilter>? logger = null)
    {
        _options = options;
        _logger = logger;
        _memoryId = options.MemoryId;
        _cacheValidTime = TimeSpan.FromMinutes(1);

        // Create the memory manager from options (pass null for logger as it expects ILogger<AgentInjectedMemoryManager>)
        _memoryManager = new AgentInjectedMemoryManager(options.StorageDirectory, null);
        _memoryManager.RegisterCacheInvalidationCallback(InvalidateCache);
    }

    public async Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptFilterContext context,
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> next)
    {
        var now = DateTime.UtcNow;
        string memoryTag = string.Empty;
        bool useCache;

        lock (_cacheLock)
        {
            if (_cachedMemoryContext != null && (now - _lastCacheTime) < _cacheValidTime)
            {
                memoryTag = _cachedMemoryContext;
                useCache = true;
            }
            else
            {
                useCache = false;
            }
        }

        if (!useCache)
        {
            // Use MemoryId if set, otherwise fall back to agent name
            var storageKey = _memoryId ?? context.AgentName;
            var memories = await _memoryManager.GetMemoriesAsync(storageKey);
            memoryTag = BuildMemoryTag(memories);
            lock (_cacheLock)
            {
                _cachedMemoryContext = memoryTag;
                _lastCacheTime = now;
            }
        }

        context.Messages = InjectMemories(context.Messages, memoryTag);
        return await next(context);
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedMemoryContext = null;
        }
    }

    private string BuildMemoryTag(List<AgentInjectedMemory> memories)
    {
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

    private IEnumerable<ChatMessage> InjectMemories(IEnumerable<ChatMessage> messages, string memoryContext)
    {
        var output = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, memoryContext)
        };
        output.AddRange(messages);
        return output;
    }
}

