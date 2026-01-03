using Microsoft.Extensions.Logging;
using System.ComponentModel; // For [Description]
using System.Threading.Tasks;
namespace HPD.Agent.Memory;
/// <summary>
/// HPD-Agent AI Toolkit for Dynamic Memory management
/// </summary>
public class DynamicMemoryToolkit
{
    private readonly DynamicMemoryStore _store;
    private readonly string _memoryId;
    private readonly ILogger<DynamicMemoryToolkit>? _logger;

    public DynamicMemoryToolkit(DynamicMemoryStore store, string memoryId, ILogger<DynamicMemoryToolkit>? logger = null)
    {
        _store = store;
        _memoryId = memoryId;
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
        
        var created = await _store.CreateMemoryAsync(_memoryId, title, content);
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

        var updated = await _store.UpdateMemoryAsync(_memoryId, memoryId, title, content);
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

        await _store.DeleteMemoryAsync(_memoryId, memoryId);
        return $"Deleted memory {memoryId}";
    }
}