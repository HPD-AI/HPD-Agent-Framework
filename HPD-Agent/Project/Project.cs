using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Tasks;
using Microsoft.KernelMemory;

/// <summary>
/// Represents a project containing conversations and scoped memories.
/// </summary>

public class Project
{
    /// <summary>Unique project identifier.</summary>
    public string Id { get; }

    /// <summary>Friendly project name.</summary>
    public string Name { get; set; }

    /// <summary>Project description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTime CreatedAt { get; }

    /// <summary>Last activity across all conversations (UTC).</summary>
    public DateTime LastActivity { get; private set; }

    /// <summary>All conversations in this project.</summary>
    public List<Conversation> Conversations { get; } = new();

    /// <summary>Scoped memory manager for this project.</summary>
    public AgentMemoryCagManager AgentMemoryCagManager { get; }

    /// <summary>Document manager for this project</summary>
    public ProjectDocumentManager DocumentManager { get; }

    /// <summary>The agent instance for this project with memory capability</summary>
    public Agent? Agent { get; private set; }

    // Memory management
    private IKernelMemory? _memory;
    private ProjectMemoryBuilder? _memoryBuilder;

    /// <summary>Constructor initializes project, memory and document managers.</summary>
    public Project(string name, string? storageDirectory = null)
    {
        Id = Guid.NewGuid().ToString("N");
        Name = name;
        CreatedAt = DateTime.UtcNow;
        LastActivity = CreatedAt;

        var directory = storageDirectory ?? "./cag-storage";
        AgentMemoryCagManager = new AgentMemoryCagManager(directory);
        AgentMemoryCagManager.SetContext(Id);

        // Initialize document manager with same directory structure
        var textExtractor = new TextExtractionUtility();
        var logger = NullLogger<ProjectDocumentManager>.Instance;
        DocumentManager = new ProjectDocumentManager(directory, textExtractor, logger);
        DocumentManager.SetContext(Id);
    }

    /// <summary>Sets the agent for this project (should be done once)</summary>
    public void SetAgent(Agent agent)
    {
        Agent = agent;
    }

    /// <summary>
    /// Gets or creates the memory instance for this project
    /// </summary>
    /// <returns>The kernel memory instance</returns>
    public IKernelMemory GetOrCreateMemory()
    {
        if (_memory != null) return _memory;
        
        if (_memoryBuilder == null)
        {
            // Create default memory builder if none provided
            _memoryBuilder = new ProjectMemoryBuilder(Id);
        }
        
        return _memory ??= _memoryBuilder.Build();
    }
    
    /// <summary>
    /// Sets the memory builder for this project
    /// </summary>
    /// <param name="builder">The memory builder to use</param>
    public void SetMemoryBuilder(ProjectMemoryBuilder builder)
    {
        _memoryBuilder = builder ?? throw new ArgumentNullException(nameof(builder));
        _memory = null; // Clear existing memory to force rebuild with new builder
    }

    /// <summary>Creates a conversation using the project's agent</summary>
    public Conversation CreateConversation()
    {
        if (Agent == null)
            throw new InvalidOperationException("Agent must be set before creating conversations");

        var conv = new Conversation(Agent);
        conv.AddMetadata("Project", this);
        Conversations.Add(conv);
        UpdateActivity();
        return conv;
    }

    // Keep the old method for backward compatibility
    public Conversation CreateConversation(Agent agent)
    {
        var conv = new Conversation(agent);
        conv.AddMetadata("Project", this);
        Conversations.Add(conv);
        UpdateActivity();
        return conv;
    }

    /// <summary>Update last activity timestamp.</summary>
    public void UpdateActivity() => LastActivity = DateTime.UtcNow;

    // Convenience methods for managing conversations
    /// <summary>Finds a conversation by ID.</summary>
    public Conversation? GetConversation(string conversationId)
        => Conversations.FirstOrDefault(c => c.Id == conversationId);

    /// <summary>Removes a conversation by ID.</summary>
    public bool RemoveConversation(string conversationId)
    {
        var conv = GetConversation(conversationId);
        if (conv != null)
        {
            Conversations.Remove(conv);
            UpdateActivity();
            return true;
        }
        return false;
    }

    /// <summary>Gets the number of conversations.</summary>
    public int ConversationCount => Conversations.Count;

    /// <summary>Upload a document from local file path.</summary>
    public Task<ProjectDocument> UploadDocumentAsync(string filePath, string? description = null)
        => DocumentManager.UploadDocumentAsync(filePath, description);

    /// <summary>Upload a document from URL.</summary>
    public Task<ProjectDocument> UploadDocumentFromUrlAsync(string url, string? description = null)
        => DocumentManager.UploadDocumentFromUrlAsync(url, description);
}

