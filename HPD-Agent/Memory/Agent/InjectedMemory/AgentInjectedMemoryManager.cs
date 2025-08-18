using System.Text.Json;
using Microsoft.Extensions.Logging;
#pragma warning disable RS1035, IL2026, IL3050 // Allow file IO and dynamic JSON code in CAG manager

/// <summary>
/// CRUD manager for Injected Memory (formerly Context-Aware Generation) with project-scoped context support.
/// </summary>
public class AgentInjectedMemoryManager
{
    private readonly string _storageDirectory;
    private readonly ILogger<AgentInjectedMemoryManager>? _logger;
    private readonly List<Action> _invalidationCallbacks = new();
    private readonly object _fileLock = new();

    /// <summary>Current context (e.g., project id) appended to file name.</summary>
    public string? CurrentContext { get; private set; }

    public AgentInjectedMemoryManager(string storageDirectory, ILogger<AgentInjectedMemoryManager>? logger = null)
    {
        _storageDirectory = Path.GetFullPath(storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        _logger = logger;
    }

    public void SetContext(string? context)
    {
        CurrentContext = context;
    }

    public void ClearContext() => CurrentContext = null;

    public void RegisterCacheInvalidationCallback(Action invalidateCallback)
    {
        _invalidationCallbacks.Add(invalidateCallback);
    }

    public async Task<List<AgentInjectedMemory>> GetMemoriesAsync(string agentName, CancellationToken cancellationToken = default)
    {
        var file = GetFilePath(agentName);
        if (!File.Exists(file))
        {
            return new List<AgentInjectedMemory>();
        }
        try
        {
            using var stream = File.OpenRead(file);
            var memories = await JsonSerializer.DeserializeAsync<List<AgentInjectedMemory>>(stream, cancellationToken: cancellationToken)
                ?? new List<AgentInjectedMemory>();
            return memories.OrderByDescending(m => m.LastAccessed).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read memories from {File}", file);
            return new List<AgentInjectedMemory>();
        }
    }

    public async Task<AgentInjectedMemory?> GetMemoryAsync(string agentName, string memoryId, CancellationToken cancellationToken = default)
    {
        var memories = await GetMemoriesAsync(agentName, cancellationToken);
        return memories.FirstOrDefault(m => m.Id == memoryId);
    }

    public async Task<AgentInjectedMemory> CreateMemoryAsync(string agentName, string title, string content, CancellationToken cancellationToken = default)
    {
        var memories = await GetMemoriesAsync(agentName, cancellationToken);
        var now = DateTime.UtcNow;
        var memory = new AgentInjectedMemory
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 6),
            Title = title,
            Content = content,
            Created = now,
            LastUpdated = now,
            LastAccessed = now
        };
        memories.Add(memory);
        SaveMemories(agentName, memories);
        InvokeInvalidation();
        return memory;
    }

    public async Task<AgentInjectedMemory> UpdateMemoryAsync(string agentName, string memoryId, string title, string content, CancellationToken cancellationToken = default)
    {
        var memories = await GetMemoriesAsync(agentName, cancellationToken);
        var memory = memories.First(m => m.Id == memoryId);
        memory.Title = title;
        memory.Content = content;
        memory.LastUpdated = DateTime.UtcNow;
        memory.LastAccessed = DateTime.UtcNow;
        SaveMemories(agentName, memories);
        InvokeInvalidation();
        return memory;
    }

    public async Task DeleteMemoryAsync(string agentName, string memoryId, CancellationToken cancellationToken = default)
    {
        var memories = await GetMemoriesAsync(agentName, cancellationToken);
        memories.RemoveAll(m => m.Id == memoryId);
        SaveMemories(agentName, memories);
        InvokeInvalidation();
    }

    public Task<List<string>> GetAvailableContextsAsync(string agentName, CancellationToken cancellationToken = default)
    {
        var files = Directory.EnumerateFiles(_storageDirectory, agentName + "*.json");
        var contexts = files.Select(path =>
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length > agentName.Length + 1 && name.StartsWith(agentName + "_"))
            {
                return name.Substring(agentName.Length + 1);
            }
            return string.Empty;
        })
        .Where(ctx => ctx != string.Empty)
        .ToList();
        return Task.FromResult(contexts);
    }

    private string GetFilePath(string agentName)
    {
        var fileName = agentName;
        if (!string.IsNullOrEmpty(CurrentContext))
        {
            fileName += "_" + CurrentContext;
        }
        return Path.Combine(_storageDirectory, fileName + ".json");
    }

    private void SaveMemories(string agentName, List<AgentInjectedMemory> memories)
    {
        var file = GetFilePath(agentName);
        lock (_fileLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(memories, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(file, json);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to write memories to {File}", file);
            }
        }
    }

    private void InvokeInvalidation()
    {
        foreach (var cb in _invalidationCallbacks)
        {
            try { cb(); } catch { }
        }
    }
}

#pragma warning restore RS1035, IL2026, IL3050

