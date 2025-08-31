using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

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
    /// Add a single message to the conversation
    /// </summary>
    public void AddMessage(ChatMessage message)
    {
        _messages.Add(message);
        UpdateActivity();
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
        

        // Inject project context AND agent tools
        options = InjectProjectContextIfNeeded(options);
        options = InjectAgentToolsIfNeeded(options);
        
        // Delegate response generation to orchestrator
        var finalResponse = await _orchestrator.OrchestrateAsync(_messages, _agents, this.Id, options, cancellationToken);

        // ✅ COMMIT response to history FIRST
        _messages.AddMessages(finalResponse);
        UpdateActivity();

        // ✅ THEN run filters on the completed turn
        var agentMetadata = CollectAgentMetadata(finalResponse);
        var context = new ConversationFilterContext(this, userMessage, finalResponse, agentMetadata, options, cancellationToken);
        await ApplyConversationFilters(context);

        return finalResponse;
    }


    // Helper method to avoid code duplication
    private ChatOptions? InjectProjectContextIfNeeded(ChatOptions? options)
    {
        if (Metadata.TryGetValue("Project", out var obj) && obj is Project project)
        {
            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["Project"] = project;
        }
        return options;
    }

    /// <summary>
    /// Merges agent tools into ChatOptions to ensure function calling works
    /// </summary>
    private ChatOptions? InjectAgentToolsIfNeeded(ChatOptions? options)
    {
        var primaryAgent = _agents.FirstOrDefault();
        if (primaryAgent?.DefaultOptions?.Tools?.Any() == true)
        {
            options ??= new ChatOptions();
            
            // Start with existing tools (if any)
            var existingTools = options.Tools?.ToList() ?? new List<AITool>();
            var agentTools = primaryAgent.DefaultOptions.Tools;
            
            // Add agent tools that aren't already present (avoid duplicates)
            foreach (var agentTool in agentTools)
            {
                if (agentTool is AIFunction af)
                {
                    // Check if this tool name already exists
                    bool alreadyExists = existingTools.OfType<AIFunction>()
                        .Any(existing => existing.Name == af.Name);
                    
                    if (!alreadyExists)
                    {
                        existingTools.Add(agentTool);
                    }
                }
            }
            
            options.Tools = existingTools;
            options.ToolMode = ChatToolMode.Auto; // Enable function calling
            
            // Successfully injected {agentTools.Count} agent tools. Total tools now: {existingTools.Count}
        }
        
        return options;
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


        // Inject project context AND agent tools
        options = InjectProjectContextIfNeeded(options);
        options = InjectAgentToolsIfNeeded(options);
        
        // ✅ FIXED: Call the streaming orchestrator and get StreamingTurnResult
        var turnResult = _orchestrator.OrchestrateStreamingAsync(_messages, _agents, this.Id, options, cancellationToken);
        
        // Stream the response to caller
        await foreach (var update in turnResult.ResponseStream.WithCancellation(cancellationToken))
        {
            yield return update;
        }
        
        // ✅ CRUCIAL: Wait for final history and add it to conversation history
        var finalHistory = await turnResult.FinalHistory;
        _messages.AddRange(finalHistory);
        UpdateActivity();

        // ✅ THEN run filters on the completed turn (using last message as the response)
        var lastMessage = finalHistory.LastOrDefault();
        if (lastMessage != null)
        {
            // Convert the final history messages to a ChatResponse for filter compatibility
            var finalResponse = new ChatResponse(lastMessage);
            var agentMetadata = CollectAgentMetadata(finalResponse);
            var context = new ConversationFilterContext(this, userMessage, finalResponse, agentMetadata, options, cancellationToken);
            await ApplyConversationFilters(context);
        }
    }

    /// <summary>
    protected void UpdateActivity() => LastActivity = DateTime.UtcNow;

    // Collect metadata of function calls from response
    private Dictionary<string, List<string>> CollectAgentMetadata(ChatResponse? response = null)
    {
        var metadata = new Dictionary<string, List<string>>();
        
        // If response is provided and contains operation metadata, use it
        if (response?.GetOperationHadFunctionCalls() == true)
        {
            var primaryAgent = _agents.FirstOrDefault();
            if (primaryAgent != null)
            {
                metadata[primaryAgent.Name] = response.GetOperationFunctionCalls().ToList();
            }
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
