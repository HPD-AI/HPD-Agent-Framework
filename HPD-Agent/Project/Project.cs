using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Tasks;
using System.Threading;
using System.Text.Json.Serialization;
using HPD_Agent.TextExtraction;

/// <summary>
/// Project information for FFI/API responses.
/// </summary>
public class ProjectInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("conversation_count")]
    public int ConversationCount { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("last_activity")]
    public string LastActivity { get; set; } = string.Empty;
}

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
    public AgentInjectedMemoryManager AgentInjectedMemoryManager { get; }

    /// <summary>Document manager for this project</summary>
    public ProjectDocumentManager DocumentManager { get; }


    // Static factory methods for progressive disclosure

    /// <summary>
    /// Creates a new project with default memory handling (FullTextInjection).
    /// This is the simple, recommended approach for most use cases.
    /// </summary>
    public static Project Create(string name, string? storageDirectory = null)
    {
        var project = new Project(name, storageDirectory);
        project._documentStrategy = ProjectDocumentHandling.FullTextInjection;
        return project;
    }


    // Memory management
    private ProjectDocumentHandling _documentStrategy = ProjectDocumentHandling.FullTextInjection; // Default to FullTextInjection for simpler scenarios

    /// <summary>Private constructor - use static factory methods Create() or CreateWithIndexedRetrieval() instead.</summary>
    private Project(string name, string? storageDirectory = null)
    {
        Id = Guid.NewGuid().ToString("N");
        Name = name;
        CreatedAt = DateTime.UtcNow;
        LastActivity = CreatedAt;

        var directory = storageDirectory ?? "./injected-memory-storage";
        AgentInjectedMemoryManager = new AgentInjectedMemoryManager(directory);
        // Note: Project scope removed from memory system - memories are now scoped by agent name only

        // Initialize document manager with same directory structure
        var textExtractor = new TextExtractionUtility();
        var logger = NullLogger<ProjectDocumentManager>.Instance;
        DocumentManager = new ProjectDocumentManager(directory, textExtractor, logger);
        DocumentManager.SetContext(Id);
    }


    /// <summary>Creates a conversation with the specified agent</summary>
    public Conversation CreateConversation(Agent agent)
    {
        var conv = new Conversation(agent);
        conv.AddMetadata("Project", this);
        Conversations.Add(conv);
        UpdateActivity();
        return conv;
    }

    /// <summary>
    /// Creates a new conversation with default memory handling (FullTextInjection).
    /// </summary>
    public Conversation CreateConversation(IEnumerable<Agent> agents)
    {
        var conv = new Conversation(this, agents, ConversationDocumentHandling.FullTextInjection);
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

    /// <summary>
    /// Uploads a shared document to the project using the configured strategy.
    /// </summary>
    /// <param name="filePath">Path to the document file</param>
    /// <param name="description">Optional description for the document</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Project document metadata</returns>
    public async Task<ProjectDocument> UploadDocumentAsync(string filePath, string? description = null, CancellationToken cancellationToken = default)
    {
        switch (_documentStrategy)
        {
            case ProjectDocumentHandling.FullTextInjection:
                // Use the existing ProjectDocumentManager to handle the upload and storage for injection
                return await DocumentManager.UploadDocumentAsync(filePath, description, cancellationToken);
            
            default:
                throw new InvalidOperationException($"Invalid document strategy configured for the project: {_documentStrategy}");
        }
    }

    /// <summary>
    /// Uploads a shared document from URL to the project using the configured strategy.
    /// </summary>
    /// <param name="url">URL of the document to upload</param>
    /// <param name="description">Optional description for the document</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Project document metadata</returns>
    public async Task<ProjectDocument> UploadDocumentFromUrlAsync(string url, string? description = null, CancellationToken cancellationToken = default)
    {
        switch (_documentStrategy)
        {
            case ProjectDocumentHandling.FullTextInjection:
                // Use the existing ProjectDocumentManager to handle the upload and storage for injection
                return await DocumentManager.UploadDocumentFromUrlAsync(url, description, cancellationToken);
            
            default:
                throw new InvalidOperationException($"Invalid document strategy configured for the project: {_documentStrategy}");
        }
    }
    
    #region Project Helpers (Phase 2 Implementation)
    
    /// <summary>
    /// PHASE 2: Gets a summary of the project's activity.
    /// Absorbs project-related functionality from helper classes.
    /// </summary>
    /// <returns>Project activity summary</returns>
    public async Task<ProjectSummary> GetSummaryAsync()
    {
        var totalMessages = Conversations.Sum(c => c.Messages.Count);
        var activeConversations = Conversations.Count(c => c.Messages.Any());
        var documents = await DocumentManager.GetDocumentsAsync();
        
        return new ProjectSummary
        {
            Id = Id,
            Name = Name,
            Description = Description,
            ConversationCount = Conversations.Count,
            ActiveConversationCount = activeConversations,
            TotalMessages = totalMessages,
            DocumentCount = documents.Count,
            CreatedAt = CreatedAt,
            LastActivity = LastActivity
        };
    }
    
    /// <summary>
    /// PHASE 2: Gets the most recent conversation in this project.
    /// Convenience method for common project navigation patterns.
    /// </summary>
    /// <returns>Most recent conversation or null if none exist</returns>
    public Conversation? GetMostRecentConversation()
    {
        return Conversations.OrderByDescending(c => c.LastActivity).FirstOrDefault();
    }
    
    /// <summary>
    /// PHASE 2: Creates a new conversation within this project.
    /// Convenience method that handles project-aware conversation setup.
    /// </summary>
    /// <param name="agents">Agents to include in the conversation</param>
    /// <param name="documentHandling">How documents should be handled in this conversation</param>
    /// <param name="filters">Optional AI function filters</param>
    /// <returns>New conversation instance</returns>
    public Conversation CreateConversation(
        IEnumerable<Agent> agents, 
        ConversationDocumentHandling documentHandling = ConversationDocumentHandling.FullTextInjection,
        IEnumerable<IAiFunctionFilter>? filters = null)
    {
        var conversation = new Conversation(this, agents, documentHandling, filters);
        Conversations.Add(conversation);
        UpdateLastActivity();
        return conversation;
    }
    
    /// <summary>
    /// PHASE 2: Searches conversations in this project by text content.
    /// Simple text-based search across conversation messages.
    /// </summary>
    /// <param name="searchTerm">Term to search for</param>
    /// <param name="maxResults">Maximum number of results to return (default 10)</param>
    /// <returns>Conversations containing the search term</returns>
    public IEnumerable<Conversation> SearchConversations(string searchTerm, int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return Enumerable.Empty<Conversation>();
            
        var results = Conversations
            .Where(c => c.Messages.Any(m => 
                m.Text?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(c => c.LastActivity)
            .Take(maxResults);
            
        return results;
    }
    
    /// <summary>
    /// PHASE 2: Updates the last activity timestamp.
    /// Internal method called when project content changes.
    /// </summary>
    private void UpdateLastActivity()
    {
        LastActivity = DateTime.UtcNow;
    }
    
    #endregion
}

/// <summary>
/// PHASE 2: Project summary information for dashboard and overview scenarios.
/// </summary>
public class ProjectSummary
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public int ConversationCount { get; set; }
    public int ActiveConversationCount { get; set; }
    public int TotalMessages { get; set; }
    public int DocumentCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivity { get; set; }
}

