using Microsoft.Extensions.Logging;
using System.ComponentModel; // For [Description]
using System.Threading.Tasks;

/// <summary>
/// HPD-Agent AI plugin for Memory CAG management
/// </summary>
public class AgentMemoryCagPlugin
{
    private readonly AgentMemoryCagManager _manager;
    private readonly string _agentName;
    private readonly ILogger<AgentMemoryCagPlugin>? _logger;

    public AgentMemoryCagPlugin(AgentMemoryCagManager manager, string agentName, ILogger<AgentMemoryCagPlugin>? logger = null)
    {
        _manager = manager;
        _agentName = agentName;
        _logger = logger;
    }

    [AIFunction]
    [Description("Create a new persistent memory that will be available in all future conversations.")]
    public async Task<string> CreateMemoryAsync(
        [Description("The memory title.")] string title,
        [Description("The memory content.")] string content)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(content))
        {
            return "Error: Title and content are required for creating a memory.";
        }
        
        var created = await _manager.CreateMemoryAsync(_agentName, title, content);
        return $"Created memory {created.Id}";
    }

    [AIFunction]
    [Description("Update an existing persistent memory.")]
    public async Task<string> UpdateMemoryAsync(
        [Description("The memory ID to update.")] string memoryId,
        [Description("The new memory title.")] string title,
        [Description("The new memory content.")] string content)
    {
        if (string.IsNullOrEmpty(memoryId))
        {
            return "Error: Memory ID is required for updating a memory.";
        }
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(content))
        {
            return "Error: Title and content are required for updating a memory.";
        }
        
        var updated = await _manager.UpdateMemoryAsync(_agentName, memoryId, title, content);
        return $"Updated memory {updated.Id}";
    }

    [AIFunction]
    [Description("Delete a persistent memory.")]
    public async Task<string> DeleteMemoryAsync(
        [Description("The memory ID to delete.")] string memoryId)
    {
        if (string.IsNullOrEmpty(memoryId))
        {
            return "Error: Memory ID is required for deleting a memory.";
        }
        
        await _manager.DeleteMemoryAsync(_agentName, memoryId);
        return $"Deleted memory {memoryId}";
    }
}