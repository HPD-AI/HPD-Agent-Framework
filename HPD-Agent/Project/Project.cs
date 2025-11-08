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

    /// <summary>
    /// All conversation threads in this project.
    /// Each thread contains the state and history for one conversation.
    /// </summary>
    public List<ConversationThread> Threads { get; } = new();

    // Note: AgentInjectedMemoryManager removed - use Agent-level DynamicMemory instead
    // See AgentBuilder.WithDynamicMemory() and DynamicMemoryStore abstraction

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
        // Note: AgentInjectedMemoryManager removed - use Agent-level DynamicMemory instead
        // See AgentBuilder.WithDynamicMemory() and DynamicMemoryStore abstraction

        // Initialize document manager with same directory structure
        var textExtractor = new TextExtractionUtility();
        var logger = NullLogger<ProjectDocumentManager>.Instance;
        DocumentManager = new ProjectDocumentManager(directory, textExtractor, logger);
        DocumentManager.SetContext(Id);
    }


    /// <summary>
    /// Creates a new conversation thread within this project.
    /// Thread is automatically added to the project's thread list.
    /// </summary>
    /// <returns>A new thread associated with this project</returns>
    /// <remarks>
    /// Usage:
    /// <code>
    /// var agent = AgentBuilder.Create().Build();
    /// var thread = project.CreateThread();
    /// await agent.RunAsync(messages, thread);
    /// </code>
    /// </remarks>
    public ConversationThread CreateThread()
    {
        var thread = new ConversationThread();
        thread.SetProject(this); // Use the new SetProject method
        UpdateActivity();
        return thread;
    }

    /// <summary>
    /// Adds an existing conversation thread to this project.
    /// This enables "thread-first, project-later" workflows where threads are created
    /// independently and then associated with a project.
    /// </summary>
    /// <param name="thread">The thread to add to this project</param>
    /// <remarks>
    /// The thread will automatically receive project documents via ProjectInjectedMemoryFilter.
    /// If the thread is already associated with this project, this is a no-op.
    ///
    /// Usage:
    /// <code>
    /// var thread = new ConversationThread();
    /// // ... use thread independently ...
    /// project.AddThread(thread); // Now thread joins project context
    /// </code>
    /// </remarks>
    public void AddThread(ConversationThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        thread.SetProject(this);
        UpdateActivity();
    }


    /// <summary>Update last activity timestamp.</summary>
    public void UpdateActivity() => LastActivity = DateTime.UtcNow;

    // Convenience methods for managing threads
    /// <summary>Finds a conversation thread by ID.</summary>
    public ConversationThread? GetThread(string threadId)
        => Threads.FirstOrDefault(t => t.Id == threadId);

    /// <summary>Removes a conversation thread by ID.</summary>
    public bool RemoveThread(string threadId)
    {
        var thread = GetThread(threadId);
        if (thread != null)
        {
            Threads.Remove(thread);
            UpdateActivity();
            return true;
        }
        return false;
    }

    /// <summary>Gets the number of conversation threads.</summary>
    public int ThreadCount => Threads.Count;

    /// <summary>
    /// Gets the number of conversations (alias for ThreadCount for backward compatibility).
    /// </summary>
    [Obsolete("Use ThreadCount instead. Each thread represents a conversation.")]
    public int ConversationCount => ThreadCount;

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
        // Calculate total messages across all threads
        var totalMessages = 0;
        var activeThreads = 0;
        foreach (var thread in Threads)
        {
            var count = await thread.GetMessageCountAsync();
            totalMessages += count;
            if (count > 0)
                activeThreads++;
        }
        
        var documents = await DocumentManager.GetDocumentsAsync();
        
        return new ProjectSummary
        {
            Id = Id,
            Name = Name,
            Description = Description,
            ConversationCount = Threads.Count,
            ActiveConversationCount = activeThreads,
            TotalMessages = totalMessages,
            DocumentCount = documents.Count,
            CreatedAt = CreatedAt,
            LastActivity = LastActivity
        };
    }

    /// <summary>
    /// PHASE 2: Gets the most recent conversation thread in this project.
    /// Convenience method for common project navigation patterns.
    /// </summary>
    /// <returns>Most recent thread or null if none exist</returns>
    public ConversationThread? GetMostRecentThread()
    {
        return Threads.OrderByDescending(t => t.LastActivity).FirstOrDefault();
    }

    // TODO: Implement SearchThreads with proper async access to messages
    // This requires either:
    // 1. Making it internal (if only used by framework)
    // 2. Creating a specialized search API that doesn't expose messages directly
    // 3. Using a search index rather than scanning all messages
    /*
    /// <summary>
    /// PHASE 2: Searches conversation threads in this project by text content.
    /// Simple text-based search across conversation messages.
    /// </summary>
    /// <param name="searchTerm">Term to search for</param>
    /// <param name="maxResults">Maximum number of results to return (default 10)</param>
    /// <returns>Conversation threads containing the search term</returns>
    public async Task<IEnumerable<ConversationThread>> SearchThreadsAsync(string searchTerm, int maxResults = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return Enumerable.Empty<ConversationThread>();

        var matchingThreads = new List<ConversationThread>();
        
        foreach (var thread in Threads)
        {
            var messages = await thread.GetMessagesAsync(cancellationToken); // Requires internal access
            if (messages.Any(m => m.Text?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) == true))
            {
                matchingThreads.Add(thread);
            }
        }
        
        return matchingThreads
            .OrderByDescending(c => c.LastActivity)
            .Take(maxResults);
    }
    */
    
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

