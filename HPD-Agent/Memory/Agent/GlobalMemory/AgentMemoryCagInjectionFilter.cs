using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;


/// <summary>
/// Prompt filter that injects CAG memories into the system message.
/// </summary>
public class AgentMemoryCagInjectionFilter : IPromptFilter
{
    private readonly AgentCagOptions _options;
    private readonly ILogger<AgentMemoryCagInjectionFilter>? _logger;
    private string? _cachedMemoryContext;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheValidTime;
    private readonly object _cacheLock = new object();

    public AgentMemoryCagInjectionFilter(AgentCagOptions options, ILogger<AgentMemoryCagInjectionFilter>? logger = null)
    {
        _options = options;
        _logger = logger;
        _cacheValidTime = TimeSpan.FromMinutes(1);
    }

    public async Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptFilterContext context,
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> next)
    {
        if (context.Properties.TryGetValue("Project", out var proj) && proj is Project project)
        {
            var now = DateTime.UtcNow;
            string memoryTag = string.Empty;
            var mgr = project.AgentMemoryCagManager;
            mgr.RegisterCacheInvalidationCallback(InvalidateCache);
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
                var memories = await mgr.GetMemoriesAsync(context.AgentName);
                memoryTag = BuildMemoryTag(memories);
                lock (_cacheLock)
                {
                    _cachedMemoryContext = memoryTag;
                    _lastCacheTime = now;
                }
            }
            context.Messages = InjectMemories(context.Messages, memoryTag);
        }
        return await next(context);
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedMemoryContext = null;
        }
    }

    private string BuildMemoryTag(List<AgentCagMemory> memories)
    {
        var tag = "[AGENT_MEMORY_START]";
        foreach (var m in memories)
        {
            tag += $"\n[MEMORY[{m.Id}]]{m.Content}[/MEMORY]";
        }
        tag += "\n[AGENT_MEMORY_END]";
        return tag;
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

