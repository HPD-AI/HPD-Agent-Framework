using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using System.Text;


/// <summary>
/// Prompt filter that injects memory content into the system message.
/// </summary>
public class AgentInjectedMemoryFilter : IPromptFilter
{
    private readonly AgentInjectedMemoryOptions _options;
    private readonly ILogger<AgentInjectedMemoryFilter>? _logger;
    private string? _cachedMemoryContext;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheValidTime;
    private readonly object _cacheLock = new object();

    public AgentInjectedMemoryFilter(AgentInjectedMemoryOptions options, ILogger<AgentInjectedMemoryFilter>? logger = null)
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
            var mgr = project.AgentInjectedMemoryManager;
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

