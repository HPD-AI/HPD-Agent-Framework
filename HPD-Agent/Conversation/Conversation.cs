using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Microsoft.KernelMemory;
using HPD_Agent.MemoryRAG;
using HPD_Agent.MemoryCAG;

/// <summary>
/// Clean conversation management built on Microsoft.Extensions.AI
/// Replaces SK's complex AgentChat hierarchy with simple, focused classes
/// </summary>
public class Conversation
{
    protected readonly List<ChatMessage> _messages = new();
    protected readonly Dictionary<string, object> _metadata = new();
    // Orchestration strategy and agents
    private IOrchestrator _orchestrator;
    /// <summary>
    /// Gets or sets the orchestration strategy for this conversation. Allows runtime switching.
    /// </summary>
    public IOrchestrator Orchestrator
    {
        get => _orchestrator;
        set => _orchestrator = value ?? throw new ArgumentNullException(nameof(value));
    }
    private readonly List<Agent> _agents;
    // Conversation filter list
    private readonly List<IConversationFilter> _conversationFilters = new();
    // Memory management
    private IKernelMemory? _memory;
    private ConversationMemoryBuilder? _memoryBuilder;
    private ConversationDocumentHandling _uploadStrategy = ConversationDocumentHandling.FullTextInjection; // Default to FullTextInjection for simpler scenarios
    private readonly List<ConversationDocumentUpload> _pendingInjections = new();

    public IReadOnlyList<ChatMessage> Messages => _messages.AsReadOnly();
    public IReadOnlyDictionary<string, object> Metadata => _metadata.AsReadOnly();
    /// <summary>Add metadata key/value to this conversation.</summary>
    public void AddMetadata(string key, object value)
    {
        _metadata[key] = value;
    }
    /// <summary>Add a conversation filter to process completed turns</summary>
    public void AddConversationFilter(IConversationFilter filter)
        => _conversationFilters.Add(filter);
    public string Id { get; } = Guid.NewGuid().ToString();
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;

    private readonly IReadOnlyList<IAiFunctionFilter> _filters;

    public Conversation(IEnumerable<IAiFunctionFilter>? filters = null)
    {
        _filters = filters?.ToList() ?? new List<IAiFunctionFilter>();
        // Default to direct orchestrator for simple flows
        this._orchestrator = new DirectOrchestrator();
        this._agents = new List<Agent>();
    }

    /// <summary>
    /// Creates a conversation with filters from an agent
    /// </summary>
    public Conversation(Agent agent) : this(agent.AIFunctionFilters)
    {
        // Additional initialization if needed
        this._agents.Add(agent);
    }
    
    /// <summary>
    /// Creates a conversation with a custom orchestration strategy and agent list
    /// </summary>
    public Conversation(IOrchestrator orchestrator, IEnumerable<Agent> agents, IEnumerable<IAiFunctionFilter>? filters = null)
        : this(filters)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _agents = agents?.ToList() ?? throw new ArgumentNullException(nameof(agents));
    }

    /// <summary>
    /// Creates a conversation within a project with specified document handling strategy
    /// </summary>
    public Conversation(Project project, IEnumerable<Agent> agents, ConversationDocumentHandling documentHandling, IEnumerable<IAiFunctionFilter>? filters = null)
        : this(filters)
    {
        _orchestrator = new DirectOrchestrator(); // Default orchestrator
        _agents = agents?.ToList() ?? throw new ArgumentNullException(nameof(agents));
        _uploadStrategy = documentHandling;
        AddMetadata("Project", project);
    }

    /// <summary>
    /// Creates a standalone conversation with specified document handling strategy
    /// </summary>
    public Conversation(IEnumerable<Agent> agents, ConversationDocumentHandling documentHandling, IEnumerable<IAiFunctionFilter>? filters = null)
        : this(filters)
    {
        _orchestrator = new DirectOrchestrator(); // Default orchestrator
        _agents = agents?.ToList() ?? throw new ArgumentNullException(nameof(agents));
        _uploadStrategy = documentHandling;
    }

    // Static factory methods for progressive disclosure

    /// <summary>
    /// Creates a new standalone conversation with default memory handling (FullTextInjection).
    /// </summary>
    public static Conversation Create(IEnumerable<Agent> agents)
    {
        return new Conversation(agents, ConversationDocumentHandling.FullTextInjection);
    }

    /// <summary>
    /// Creates a new standalone conversation with advanced Indexed Retrieval capabilities.
    /// </summary>
    public static Conversation CreateWithIndexedRetrieval(
        IEnumerable<Agent> agents,
        Action<ConversationMemoryBuilder> configure)
    {
        var conversation = new Conversation(agents, ConversationDocumentHandling.IndexedRetrieval);
        var builder = new ConversationMemoryBuilder(conversation.Id);
        configure(builder);
        conversation.SetMemoryBuilder(builder);
        return conversation;
    }

    /// <summary>
    /// Add a single message to the conversation
    /// </summary>
    public void AddMessage(ChatMessage message)
    {
        _messages.Add(message);
        UpdateActivity();
    }

    /// <summary>
    /// Gets or creates the memory instance for this conversation
    /// <summary>
    /// Gets or creates the RAG memory instance for this conversation.
    /// Returns null if no memory builder has been explicitly configured.
    /// </summary>
    /// <returns>The kernel memory instance, or null if not configured</returns>
    public IKernelMemory? GetOrCreateMemory()
    {
        if (_uploadStrategy == ConversationDocumentHandling.FullTextInjection)
        {
            return null; // No RAG memory needed for DirectInjection
        }
        
        if (_memory != null) return _memory;
        
        // Only create memory if explicitly configured via SetMemoryBuilder
        if (_memoryBuilder == null)
        {
            return null; // No memory builder configured - return null instead of creating default
        }
        
        var builtMemory = _memoryBuilder.Build();
        if (builtMemory == null)
        {
            throw new InvalidOperationException("Memory builder returned null. This should not happen with RAG strategy.");
        }
        
        return _memory = builtMemory;
    }
    
    /// <summary>
    /// Sets the memory builder for this conversation
    /// </summary>
    /// <param name="builder">The memory builder to use</param>
    public void SetMemoryBuilder(ConversationMemoryBuilder builder)
    {
        _memoryBuilder = builder ?? throw new ArgumentNullException(nameof(builder));
        _uploadStrategy = builder.UploadStrategy; // Track the strategy
        _memory = null; // Clear existing memory to force rebuild with new builder
    }

    /// <summary>
    /// Upload and process documents for this conversation based on the configured upload strategy
    /// </summary>
    /// <param name="filePaths">Paths to documents to upload</param>
    /// <param name="textExtractor">Text extraction utility for processing documents</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of upload results</returns>
    public async Task<ConversationDocumentUpload[]> ProcessDocumentUploadsAsync(
        string[] filePaths,
        TextExtractionUtility textExtractor,
        CancellationToken cancellationToken = default)
    {
        if (filePaths == null || filePaths.Length == 0)
            return Array.Empty<ConversationDocumentUpload>();

        switch (_uploadStrategy)
        {
            case ConversationDocumentHandling.IndexedRetrieval:
                return await ProcessDocumentsForRAG(filePaths, textExtractor, cancellationToken);
                
            case ConversationDocumentHandling.FullTextInjection:
                return await ConversationDocumentHelper.ProcessUploadsAsync(filePaths, textExtractor, cancellationToken);
                
            default:
                throw new InvalidOperationException($"Unknown upload strategy: {_uploadStrategy}");
        }
    }

    /// <summary>
    /// Send a message with attached documents, handling them according to the upload strategy
    /// </summary>
    /// <param name="message">User message</param>
    /// <param name="documentPaths">Paths to documents to attach</param>
    /// <param name="textExtractor">Text extraction utility</param>
    /// <param name="options">Chat options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Chat response</returns>
    public async Task<ChatResponse> SendWithDocumentsAsync(
        string message,
        string[] documentPaths,
        TextExtractionUtility textExtractor,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (documentPaths == null || documentPaths.Length == 0)
            return await SendAsync(message, options, cancellationToken);

        var uploads = await ProcessDocumentUploadsAsync(documentPaths, textExtractor, cancellationToken);
        
        switch (_uploadStrategy)
        {
            case ConversationDocumentHandling.IndexedRetrieval:
                // Documents are already indexed in RAG memory, send message normally
                return await SendAsync(message, options, cancellationToken);
                
            case ConversationDocumentHandling.FullTextInjection:
                // Inject document content directly into the message
                var enhancedMessage = ConversationDocumentHelper.FormatMessageWithDocuments(message, uploads);
                return await SendAsync(enhancedMessage, options, cancellationToken);
                
            default:
                throw new InvalidOperationException($"Unknown upload strategy: {_uploadStrategy}");
        }
    }

    private async Task<ConversationDocumentUpload[]> ProcessDocumentsForRAG(
        string[] filePaths,
        TextExtractionUtility textExtractor,
        CancellationToken cancellationToken)
    {
        var memory = GetOrCreateMemory();
        if (memory == null)
        {
            // Fallback to direct injection if no RAG memory available
            return await ConversationDocumentHelper.ProcessUploadsAsync(filePaths, textExtractor, cancellationToken);
        }

        var results = new List<ConversationDocumentUpload>();
        
        foreach (var filePath in filePaths)
        {
            try
            {
                // Import document into RAG memory
                var documentId = await memory.ImportDocumentAsync(filePath, cancellationToken: cancellationToken);
                
                // Create success result
                var fileInfo = new System.IO.FileInfo(filePath);
                results.Add(new ConversationDocumentUpload
                {
                    FileName = fileInfo.Name,
                    ExtractedText = $"Document indexed for retrieval (ID: {documentId})",
                    MimeType = "application/octet-stream", // Could be enhanced to detect actual MIME type
                    FileSize = fileInfo.Exists ? fileInfo.Length : 0,
                    ProcessedAt = DateTime.UtcNow,
                    Success = true,
                    DecoderUsed = "RAG_INDEXER"
                });
            }
            catch (Exception ex)
            {
                // Create error result
                results.Add(new ConversationDocumentUpload
                {
                    FileName = System.IO.Path.GetFileName(filePath),
                    ExtractedText = string.Empty,
                    MimeType = string.Empty,
                    FileSize = 0,
                    ProcessedAt = DateTime.UtcNow,
                    Success = false,
                    ErrorMessage = $"RAG indexing failed: {ex.Message}",
                    DecoderUsed = null
                });
            }
        }
        
        return results.ToArray();
    }

    /// <summary>
    /// Send a message using the default agent or specified agent
    /// </summary>
    public async Task<ChatResponse> SendAsync(
        string message,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Add user message
        var userMessage = new ChatMessage(ChatRole.User, message);
        _messages.Add(userMessage);
        UpdateActivity();

        // CONTEXT ASSEMBLY: Use new Context Provider pattern
        var sharedRagContext = AssembleSharedRAGContext(options);
        
        // Inject the RAG context for agent capabilities
        if (sharedRagContext != null)
        {
            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["SharedRAGContext"] = sharedRagContext;
        }

        // Inject project context for Memory CAG if available
        if (Metadata.TryGetValue("Project", out var obj) && obj is Project project)
        {
            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["Project"] = project;
        }
        
    // Delegate response generation to orchestrator
    var finalResponse = await _orchestrator.OrchestrateAsync(_messages, _agents, options, cancellationToken);

    // ✅ COMMIT response to history FIRST
    _messages.AddMessages(finalResponse);
    UpdateActivity();

    // ✅ THEN run filters on the completed turn
    var agentMetadata = CollectAgentMetadata();
    var context = new ConversationFilterContext(this, userMessage, finalResponse, agentMetadata, options, cancellationToken);
    await ApplyConversationFilters(context);

    return finalResponse;
    }

    /// <summary>
    /// Stream a conversation turn
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> SendStreamingAsync(
        string message,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var userMessage = new ChatMessage(ChatRole.User, message);
        _messages.Add(userMessage);
        UpdateActivity();

        // CONTEXT ASSEMBLY: Use new Context Provider pattern
        var sharedRagContext = AssembleSharedRAGContext(options);
        
        // Inject the RAG context for agent capabilities
        if (sharedRagContext != null)
        {
            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["SharedRAGContext"] = sharedRagContext;
        }

        // Inject project context for Memory CAG if available
        if (Metadata.TryGetValue("Project", out var projectObj) && projectObj is Project project)
        {
            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["Project"] = project;
        }
        
        // Delegate streaming via orchestrator (fallback to non-streaming then convert)
        var finalResponse = await _orchestrator.OrchestrateAsync(_messages, _agents, options, cancellationToken);

        // Stream the response to caller
        foreach (var update in finalResponse.ToChatResponseUpdates())
        {
            yield return update;
        }

        // ✅ COMMIT response to history FIRST
        _messages.AddMessages(finalResponse);
        UpdateActivity();

        // ✅ THEN run filters on the completed turn
        var agentMetadata = CollectAgentMetadata();
        var context = new ConversationFilterContext(this, userMessage, finalResponse, agentMetadata, options, cancellationToken);
        await ApplyConversationFilters(context);
    }

    /// <summary>
    /// Initiates an AG-UI streaming response for web clients and writes the SSE events 
    /// directly to the provided output stream. Respects orchestration strategy and context assembly.
    /// </summary>
    public async Task StreamResponseAsync(
        string message,
        Stream responseStream,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var userMessage = new ChatMessage(ChatRole.User, message);
        _messages.Add(userMessage);
        UpdateActivity();

        // CONTEXT ASSEMBLY: Use existing context provider pattern (same as SendAsync)
        var sharedRagContext = AssembleSharedRAGContext(options);
        
        // Inject contexts using existing patterns
        if (sharedRagContext != null)
        {
            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["SharedRAGContext"] = sharedRagContext;
        }

        // Inject project context for Memory CAG if available
        if (Metadata.TryGetValue("Project", out var obj) && obj is Project project)
        {
            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["Project"] = project;
        }

        // ORCHESTRATION: Determine the appropriate agent using orchestrator logic
        // This ensures consistency with SendAsync behavior while adapting for streaming
        var selectedAgent = await SelectAgentForStreaming(options, cancellationToken);
        if (selectedAgent == null)
        {
            throw new InvalidOperationException("No agent is available to handle the streaming request.");
        }

        // ENCAPSULATED BOILERPLATE: Create RunAgentInput using centralized helper
        // Pass the full conversation history to maintain context
        var runInput = AGUIEventConverter.CreateRunAgentInput(this, _messages, options);

        // Call the agent's new high-level streaming helper
        await selectedAgent.StreamAGUIResponseAsync(runInput, responseStream, cancellationToken);

        // CONSISTENCY: Apply conversation filters like other send methods
        // Note: For streaming, we apply filters after the stream completes
        // This is a simplified approach - in a full implementation, you might want to
        // track the streaming response and apply filters when the stream finishes
        var agentMetadata = CollectAgentMetadata();
        var context = new ConversationFilterContext(this, userMessage, new ChatResponse([]), agentMetadata, options, cancellationToken);
        await ApplyConversationFilters(context);
    }

    /// <summary>
    /// Initiates an AG-UI streaming response and sends the events over a WebSocket connection.
    /// This is the recommended method for real-time, bi-directional web applications.
    /// Respects orchestration strategy and context assembly like StreamResponseAsync.
    /// </summary>
    public async Task StreamResponseToWebSocketAsync(
        string message,
        System.Net.WebSockets.WebSocket webSocket,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var userMessage = new ChatMessage(ChatRole.User, message);
        _messages.Add(userMessage);
        UpdateActivity();

        // CONTEXT ASSEMBLY: Use existing context provider pattern (same as StreamResponseAsync)
        var sharedRagContext = AssembleSharedRAGContext(options);
        
        // Inject contexts using existing patterns
        if (sharedRagContext != null)
        {
            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["SharedRAGContext"] = sharedRagContext;
        }

        // Inject project context for Memory CAG if available
        if (Metadata.TryGetValue("Project", out var obj) && obj is Project project)
        {
            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["Project"] = project;
        }

        // ORCHESTRATION: Determine the appropriate agent using orchestrator logic
        var selectedAgent = await SelectAgentForStreaming(options, cancellationToken);
        if (selectedAgent == null)
        {
            throw new InvalidOperationException("No agent is available to handle the WebSocket streaming request.");
        }

        // ENCAPSULATED BOILERPLATE: Create RunAgentInput using centralized helper
        var runInput = AGUIEventConverter.CreateRunAgentInput(this, _messages, options);

        // Call the agent's WebSocket streaming helper
        await selectedAgent.StreamToWebSocketAsync(runInput, webSocket, cancellationToken);

        // CONSISTENCY: Apply conversation filters like other send methods
        var agentMetadata = CollectAgentMetadata();
        var context = new ConversationFilterContext(this, userMessage, new ChatResponse([]), agentMetadata, options, cancellationToken);
        await ApplyConversationFilters(context);
    }

    /// <summary>
    /// Selects the appropriate agent for streaming. For most orchestrators,
    /// streaming uses the first available agent since it's typically a single-agent response.
    /// </summary>
    private Task<Agent?> SelectAgentForStreaming(ChatOptions? options, CancellationToken cancellationToken)
    {
        // Simple approach: streaming typically uses the first agent
        // This works for DirectOrchestrator and most custom orchestrators
        // Custom orchestrators can override this behavior if needed in the future
        return Task.FromResult(_agents.FirstOrDefault());
    }

    /// <summary>
    protected void UpdateActivity() => LastActivity = DateTime.UtcNow;

    // Collect metadata of function calls from each agent
    private Dictionary<string, List<string>> CollectAgentMetadata()
    {
        var metadata = new Dictionary<string, List<string>>();
        foreach (var agent in _agents.Where(a => a.LastOperationHadFunctionCalls))
        {
            metadata[agent.Name] = new List<string>(agent.LastOperationFunctionCalls);
        }
        return metadata;
    }

    // Apply conversation filters (stub - no filters by default)
    private async Task ApplyConversationFilters(ConversationFilterContext context)
    {
        if (!_conversationFilters.Any()) return;
        // Build reverse pipeline
        Func<ConversationFilterContext, Task> pipeline = _ => Task.CompletedTask;
        foreach (var filter in _conversationFilters.AsEnumerable().Reverse())
        {
            var next = pipeline;
            pipeline = ctx => filter.InvokeAsync(ctx, next);
        }
        await pipeline(context);
    }

    /// <summary>
    /// Assembles RAG context from all available memory sources
    /// </summary>
    /// <summary>
    /// NEW: Context Provider Pattern - Assembles only shared memory resources
    /// Eliminates context leakage by excluding agent-specific memories
    /// Each agent will fetch its own memory when needed
    /// </summary>
    private SharedRAGContext? AssembleSharedRAGContext(ChatOptions? options)
    {
        IKernelMemory? conversationMemory = null;
        IKernelMemory? projectMemory = null;

        // Gather conversation memory (shared across all agents)
        // IMPORTANT: Only try to get conversation memory if NOT using FullTextInjection strategy
        if (_uploadStrategy != ConversationDocumentHandling.FullTextInjection)
        {
            try
            {
                conversationMemory = this.GetOrCreateMemory();
            }
            catch
            {
                // Conversation may not have memory configured or builder returned null
            }
        }

        // Gather project memory (shared across all conversations in project)
        if (Metadata.TryGetValue("Project", out var obj) && obj is Project project)
        {
            try
            {
                projectMemory = project.GetOrCreateMemory();
            }
            catch
            {
                // Project may not have memory configured
            }
        }

        // Create context even if both shared memories are null - agents may still have their own memories
        // This ensures that the new Context Provider pattern is always used for multi-agent scenarios
        var config = new RAGConfiguration();
        
        return new SharedRAGContext
        {
            ConversationMemory = conversationMemory,  // null is OK
            ProjectMemory = projectMemory,            // null is OK
            Configuration = config
        };
    }

    #region Conversation Helpers (Phase 2 Implementation)
    
    /// <summary>
    /// PHASE 2: Gets a human-readable display name for this conversation.
    /// Implements the functionality previously in ConversionHelpers.GenerateConversationDisplayName.
    /// </summary>
    /// <param name="maxLength">Maximum length for the display name</param>
    /// <returns>Human-readable conversation name</returns>
    public string GetDisplayName(int maxLength = 30)
    {
        // Check for explicit display name in metadata first
        if (_metadata.TryGetValue("DisplayName", out var name) && !string.IsNullOrEmpty(name?.ToString()))
        {
            return name.ToString()!;
        }
        
        // Find first user message and extract text content
        var firstUserMessage = _messages.FirstOrDefault(m => m.Role == ChatRole.User);
        if (firstUserMessage != null)
        {
            var text = ExtractTextContentInternal(firstUserMessage);
            if (!string.IsNullOrEmpty(text))
            {
                return text.Length <= maxLength 
                    ? text 
                    : text[..maxLength] + "...";
            }
        }
        
        return $"Chat {Id[..Math.Min(8, Id.Length)]}";
    }
    
    /// <summary>
    /// PHASE 2: Extracts text content from a message in this conversation.
    /// Implements the functionality previously in ConversionHelpers.ExtractTextContent.
    /// </summary>
    /// <param name="message">The message to extract text from</param>
    /// <returns>Combined text content</returns>
    public string ExtractTextContent(ChatMessage message)
    {
        return ExtractTextContentInternal(message);
    }
    
    /// <summary>
    /// Internal helper for text content extraction to avoid circular dependencies during cleanup.
    /// </summary>
    private static string ExtractTextContentInternal(ChatMessage message)
    {
        var textContents = message.Contents
            .OfType<TextContent>()
            .Select(tc => tc.Text)
            .Where(text => !string.IsNullOrEmpty(text));
            
        return string.Join(" ", textContents);
    }
    

    #endregion

}


/// <summary>
/// Simple state class - no complex channel states or broadcast queues
/// </summary>
public class ConversationState
{
    public string Id { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivity { get; set; }
}

/// <summary>
/// JSON source generation context for AOT compatibility
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ConversationState))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatRole))]
[JsonSerializable(typeof(DateTime))]
internal partial class ConversationJsonContext : JsonSerializerContext
{
}
