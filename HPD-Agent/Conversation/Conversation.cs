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
    // Agents
    private readonly List<Agent> _agents;
    // Conversation filter list
    private readonly List<IConversationFilter> _conversationFilters = new();
    // Memory management
    private ConversationDocumentHandling _uploadStrategy = ConversationDocumentHandling.FullTextInjection; // Default to FullTextInjection for simpler scenarios
    private readonly List<ConversationDocumentUpload> _pendingInjections = new();
    private TextExtractionUtility? _textExtractor;
    
    /// <summary>
    /// Gets or sets the default orchestrator for multi-agent scenarios.
    /// When set, this orchestrator will be used if no orchestrator is provided to Send methods.
    /// </summary>
    public IOrchestrator? DefaultOrchestrator { get; set; }

    public IReadOnlyList<ChatMessage> Messages => _messages.AsReadOnly();
    public IReadOnlyDictionary<string, object> Metadata => _metadata.AsReadOnly();
    /// <summary>Gets the primary agent in this conversation, or null if no agents are present.</summary>
    public Agent? PrimaryAgent => _agents.FirstOrDefault();
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
        _agents = new List<Agent>();
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
    /// Creates a conversation with an agent list
    /// </summary>
    public Conversation(IEnumerable<Agent> agents, IEnumerable<IAiFunctionFilter>? filters = null)
        : this(filters)
    {
        _agents = agents?.ToList() ?? throw new ArgumentNullException(nameof(agents));
    }

    /// <summary>
    /// Creates a conversation within a project with specified document handling strategy
    /// </summary>
    public Conversation(Project project, IEnumerable<Agent> agents, ConversationDocumentHandling documentHandling, IEnumerable<IAiFunctionFilter>? filters = null)
        : this(filters)
    {
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
    /// Send a message in the conversation.
    /// For multi-agent scenarios, uses the provided orchestrator or falls back to DefaultOrchestrator.
    /// BREAKING: Now returns ConversationTurnResult instead of ChatResponse.
    /// </summary>
    /// <param name="message">The message to send</param>
    /// <param name="options">Optional chat options</param>
    /// <param name="orchestrator">Optional orchestrator for multi-agent scenarios (falls back to DefaultOrchestrator)</param>
    /// <param name="documentPaths">Optional document paths to include</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<ConversationTurnResult> SendAsync(
        string message,
        ChatOptions? options = null,
        IOrchestrator? orchestrator = null,
        string[]? documentPaths = null,
        CancellationToken cancellationToken = default)
    {
        // Process documents if provided
        if (documentPaths?.Length > 0)
        {
            var textExtractor = GetOrCreateTextExtractor();
            var uploads = await ProcessDocumentUploadsAsync(documentPaths, textExtractor, cancellationToken);
            
            message = _uploadStrategy switch
            {
                ConversationDocumentHandling.FullTextInjection => 
                    ConversationDocumentHelper.FormatMessageWithDocuments(message, uploads),
                ConversationDocumentHandling.IndexedRetrieval => message,
                _ => throw new InvalidOperationException($"Unknown upload strategy: {_uploadStrategy}")
            };
        }

        var startTime = DateTime.UtcNow;
        var userMessage = new ChatMessage(ChatRole.User, message);
        _messages.Add(userMessage);
        UpdateActivity();

        // Inject context
        options = InjectProjectContextIfNeeded(options);

        OrchestrationResult orchestrationResult;

        if (_agents.Count == 0)
        {
            throw new InvalidOperationException("No agents configured");
        }
        else if (_agents.Count == 1)
        {
            // Single agent path - no orchestration needed
            var agent = _agents[0];
            var response = await agent.GetResponseAsync(_messages, options, cancellationToken);
            orchestrationResult = new OrchestrationResult
            {
                Response = response,
                SelectedAgent = agent,
                Metadata = new OrchestrationMetadata
                {
                    StrategyName = "SingleAgent",
                    DecisionDuration = TimeSpan.Zero
                }
            };
        }
        else
        {
            // Multi-agent orchestration - use provided or default orchestrator
            var effectiveOrchestrator = orchestrator ?? DefaultOrchestrator;
            if (effectiveOrchestrator == null)
            {
                throw new InvalidOperationException("Multiple agents configured but no orchestrator provided. Set DefaultOrchestrator or pass an orchestrator parameter.");
            }
            
            orchestrationResult = await effectiveOrchestrator.OrchestrateAsync(
                _messages, _agents, this.Id, options, cancellationToken);
        }

        // Commit response to history
        _messages.AddMessages(orchestrationResult.Response);
        UpdateActivity();

        // Apply filters
        var agentMetadata = CollectAgentMetadata(orchestrationResult.Response);
        var context = new ConversationFilterContext(this, userMessage, orchestrationResult.Response, agentMetadata, options, cancellationToken);
        await ApplyConversationFilters(context);


        return new ConversationTurnResult
        {
            Response = orchestrationResult.Response,
            TurnHistory = ExtractTurnHistory(userMessage, orchestrationResult.Response),
            RespondingAgent = orchestrationResult.SelectedAgent,
            UsedOrchestrator = orchestrator,
            Duration = DateTime.UtcNow - startTime,
            OrchestrationMetadata = orchestrationResult.Metadata,
            Usage = CreateTokenUsage(orchestrationResult.Response),
            RequestId = Guid.NewGuid().ToString(),
            ActivityId = System.Diagnostics.Activity.Current?.Id
        };
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
    /// Stream a conversation turn and return both event stream and final metadata.
    /// For multi-agent scenarios, uses the provided orchestrator or falls back to DefaultOrchestrator.
    /// </summary>
    /// <param name="message">The user message to send</param>
    /// <param name="options">Chat options</param>
    /// <param name="orchestrator">Optional orchestrator for multi-agent scenarios (falls back to DefaultOrchestrator)</param>
    /// <param name="documentPaths">Optional document paths to process and include</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Streaming result with event stream and final metadata</returns>
    public async Task<ConversationStreamingResult> SendStreamingAsync(
        string message,
        ChatOptions? options = null,
        IOrchestrator? orchestrator = null,
        string[]? documentPaths = null,
        CancellationToken cancellationToken = default)
    {
        // Process documents if provided
        if (documentPaths?.Length > 0)
        {
            var textExtractor = GetOrCreateTextExtractor();
            var uploads = await ProcessDocumentUploadsAsync(documentPaths, textExtractor, cancellationToken);
            
            message = _uploadStrategy switch
            {
                ConversationDocumentHandling.FullTextInjection => 
                    ConversationDocumentHelper.FormatMessageWithDocuments(message, uploads),
                ConversationDocumentHandling.IndexedRetrieval => message,
                _ => throw new InvalidOperationException($"Unknown upload strategy: {_uploadStrategy}")
            };
        }

        // Create TaskCompletionSource for the final result
        var resultTcs = new TaskCompletionSource<ConversationTurnResult>();
        
        // Create the event stream
        var eventStream = SendStreamingEventsAsync(message, options, orchestrator, documentPaths, cancellationToken);
        
        // Start a task to consume events and build the final result
        _ = Task.Run(async () => 
        {
            var startTime = DateTime.UtcNow;
            ChatResponse? finalResponse = null;
            Agent? selectedAgent = null;
            OrchestrationMetadata? orchestrationMetadata = null;
            var userMessage = new ChatMessage(ChatRole.User, message);
            
            try
            {
                // Consume the stream to capture metadata
                await foreach (var evt in eventStream.WithCancellation(cancellationToken))
                {
                    // The event stream processing already handles adding messages to conversation
                    // We just need to wait for it to complete
                }
                
                // After stream completes, extract the final response from conversation history
                var lastMessages = _messages.TakeLast(10).ToList();
                var assistantMessage = lastMessages.LastOrDefault(m => m.Role == ChatRole.Assistant);
                
                if (assistantMessage != null)
                {
                    finalResponse = new ChatResponse(assistantMessage);
                    selectedAgent = _agents.FirstOrDefault();
                    orchestrationMetadata = new OrchestrationMetadata
                    {
                        StrategyName = _agents.Count == 1 ? "SingleAgent" : "Orchestrated",
                        DecisionDuration = TimeSpan.Zero
                    };
                }
                
                // Set the final result
                resultTcs.SetResult(new ConversationTurnResult
                {
                    Response = finalResponse ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, "")),
                    TurnHistory = ExtractTurnHistory(userMessage, finalResponse ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, ""))),
                    RespondingAgent = selectedAgent ?? _agents.FirstOrDefault()!,
                    UsedOrchestrator = orchestrator,
                    Duration = DateTime.UtcNow - startTime,
                    OrchestrationMetadata = orchestrationMetadata ?? new OrchestrationMetadata(),
                    Usage = CreateTokenUsage(finalResponse ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, ""))),
                    RequestId = Guid.NewGuid().ToString(),
                    ActivityId = System.Diagnostics.Activity.Current?.Id
                });
            }
            catch (Exception ex)
            {
                resultTcs.SetException(ex);
            }
        }, cancellationToken);
        
        return new ConversationStreamingResult
        {
            EventStream = eventStream,
            FinalResult = resultTcs.Task
        };
    }

    /// <summary>
    /// Stream a conversation turn with default console display formatting.
    /// Provides a user-friendly experience with automatic event formatting and output.
    /// For advanced event control, use SendStreamingAsync instead.
    /// </summary>
    /// <param name="message">The user message to send</param>
    /// <param name="outputHandler">Optional custom output handler. Defaults to Console.Write</param>
    /// <param name="options">Chat options</param>
    /// <param name="orchestrator">Optional orchestrator for multi-agent scenarios (falls back to DefaultOrchestrator)</param>
    /// <param name="documentPaths">Optional document paths to process and include</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Final conversation turn result with all metadata</returns>
    public async Task<ConversationTurnResult> SendStreamingWithOutputAsync(
        string message,
        Action<string>? outputHandler = null,
        ChatOptions? options = null,
        IOrchestrator? orchestrator = null,
        string[]? documentPaths = null,
        CancellationToken cancellationToken = default)
    {
        outputHandler ??= Console.Write;
        
        var result = await SendStreamingAsync(message, options, orchestrator, documentPaths, cancellationToken);
        
        // Stream events to output handler
        await foreach (var evt in result.EventStream.WithCancellation(cancellationToken))
        {
            var formattedOutput = FormatEventForDisplay(evt);
            if (!string.IsNullOrEmpty(formattedOutput))
            {
                outputHandler(formattedOutput);
            }
        }
        
        // Return the final result with all metadata
        return await result.FinalResult;
    }

    /// <summary>
    /// Formats a BaseEvent for display with clean text output plus reasoning steps
    /// </summary>
    private static string FormatEventForDisplay(BaseEvent evt)
    {
        return evt switch
        {
            StepStartedEvent step => $"\n\n",
            TextMessageContentEvent text => text.Delta,
            _ => "" // Only show reasoning steps and assistant text, ignore other events
        };
    }

    /// <summary>
    /// Stream a conversation turn with full event transparency (advanced users)
    /// </summary>
    internal async IAsyncEnumerable<BaseEvent> SendStreamingEventsAsync(
        string message,
        ChatOptions? options = null,
        IOrchestrator? orchestrator = null,
        string[]? documentPaths = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Process documents if provided
        if (documentPaths?.Length > 0)
        {
            var textExtractor = GetOrCreateTextExtractor();
            var uploads = await ProcessDocumentUploadsAsync(documentPaths, textExtractor, cancellationToken);
            
            message = _uploadStrategy switch
            {
                ConversationDocumentHandling.FullTextInjection => 
                    ConversationDocumentHelper.FormatMessageWithDocuments(message, uploads),
                ConversationDocumentHandling.IndexedRetrieval => message,
                _ => throw new InvalidOperationException($"Unknown upload strategy: {_uploadStrategy}")
            };
        }

        var userMessage = new ChatMessage(ChatRole.User, message);
        _messages.Add(userMessage);
        UpdateActivity();

        // Inject project context
        options = InjectProjectContextIfNeeded(options);

        if (_agents.Count == 0)
        {
            throw new InvalidOperationException("No agents configured for this conversation");
        }
        else if (_agents.Count == 1)
        {
            // DIRECT PATH - Single agent
            var result = await _agents[0].ExecuteStreamingTurnAsync(_messages, options, cancellationToken);

            // Stream the events
            await foreach (var evt in result.EventStream.WithCancellation(cancellationToken))
            {
                yield return evt;
            }

            // Wait for final history and update conversation
            var finalHistory = await result.FinalHistory;
            _messages.AddRange(finalHistory);
            UpdateActivity();

            // Apply filters on the completed turn
            var lastMessage = finalHistory.LastOrDefault();
            if (lastMessage != null)
            {
                var finalResponse = new ChatResponse(lastMessage);
                var agentMetadata = CollectAgentMetadata(finalResponse);
                var context = new ConversationFilterContext(this, userMessage, finalResponse, agentMetadata, options, cancellationToken);
                await ApplyConversationFilters(context);
            }
        }
        else
        {
            // ORCHESTRATED PATH - Multi-agent
            var effectiveOrchestrator = orchestrator ?? DefaultOrchestrator;
            if (effectiveOrchestrator == null)
            {
                throw new InvalidOperationException(
                    $"Multi-agent conversations ({_agents.Count} agents) require an orchestrator. Set DefaultOrchestrator or pass an orchestrator parameter.");
            }

            // Use the orchestrator's streaming method
            var orchestrationResult = await effectiveOrchestrator.OrchestrateStreamingAsync(
                _messages, _agents, this.Id, options, cancellationToken);

            // Stream all orchestration and agent events
            await foreach (var evt in orchestrationResult.EventStream.WithCancellation(cancellationToken))
            {
                yield return evt;
            }

            // Wait for final result and update conversation
            var finalResult = await orchestrationResult.FinalResult;
            _messages.AddMessages(finalResult.Response);
            UpdateActivity();

            // Apply filters on the completed turn
            var agentMetadata = CollectAgentMetadata(finalResult.Response);
            var context = new ConversationFilterContext(
                this, userMessage, finalResult.Response, agentMetadata, options, cancellationToken);
            await ApplyConversationFilters(context);
        }
    }

    /// <summary>
    /// Gets or creates a TextExtractionUtility instance for document processing
    /// </summary>
    private TextExtractionUtility GetOrCreateTextExtractor()
    {
        return _textExtractor ??= new TextExtractionUtility();
    }

    /// <summary>
    protected void UpdateActivity() => LastActivity = DateTime.UtcNow;

    /// <summary>
    /// Extracts the turn history from user message and response.
    /// </summary>
    private IReadOnlyList<ChatMessage> ExtractTurnHistory(ChatMessage userMessage, ChatResponse response)
    {
        var turnMessages = new List<ChatMessage> { userMessage };
        turnMessages.AddRange(response.Messages);
        return turnMessages.AsReadOnly();
    }

    // Collect metadata of function calls from response
    private Dictionary<string, List<string>> CollectAgentMetadata(ChatResponse? response = null)
    {
        var metadata = new Dictionary<string, List<string>>();
        
        // Extract function call information directly from the response messages
        if (response?.Messages != null)
        {
            var functionCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .Select(fc => fc.Name)
                .ToList();

            if (functionCalls.Any())
            {
                var primaryAgent = _agents.FirstOrDefault();
                if (primaryAgent != null)
                {
                    metadata[primaryAgent.Name] = functionCalls;
                }
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

    /// <summary>
    /// Creates TokenUsage from ChatResponse.Usage if available
    /// </summary>
    private static TokenUsage? CreateTokenUsage(ChatResponse response)
    {
        if (response.Usage == null)
            return null;

        return new TokenUsage
        {
            PromptTokens = (int)(response.Usage.InputTokenCount ?? 0),
            CompletionTokens = (int)(response.Usage.OutputTokenCount ?? 0),
            TotalTokens = (int)(response.Usage.TotalTokenCount ?? 0),
            ModelId = response.ModelId
            // EstimatedCost is intentionally left null - cost calculation should be handled by business logic layer
        };
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
/// Primary return type for conversation turns.
/// BREAKING: SendAsync now returns this instead of ChatResponse.
/// </summary>
/// <summary>
/// Token usage and cost information for a conversation turn
/// </summary>
public record TokenUsage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
    public decimal? EstimatedCost { get; init; }
    public string? ModelId { get; init; }
}


/// <summary>
/// Result of a conversation turn with rich metadata for business decisions
/// </summary>
public record ConversationTurnResult
{
    public required ChatResponse Response { get; init; }
    public required IReadOnlyList<ChatMessage> TurnHistory { get; init; }
    public required Agent RespondingAgent { get; init; }
    public IOrchestrator? UsedOrchestrator { get; init; }
    public required TimeSpan Duration { get; init; }
    public required OrchestrationMetadata OrchestrationMetadata { get; init; }

    // NEW: Core business data for immediate decisions
    public TokenUsage? Usage { get; init; }
    public string RequestId { get; init; } = Guid.NewGuid().ToString();
    public string? ActivityId { get; init; }

    /// <summary>
    /// Convenience conversions for backward compatibility.
    /// </summary>
    public static implicit operator ChatResponse(ConversationTurnResult result)
        => result.Response;

    public static implicit operator ChatMessage(ConversationTurnResult result)
        => result.Response.Messages.FirstOrDefault() ?? new ChatMessage();

    public string Text => Response.Text;
}

/// <summary>
/// Streaming result for conversation turns, providing both event stream and final metadata.
/// </summary>
public record ConversationStreamingResult
{
    public required IAsyncEnumerable<BaseEvent> EventStream { get; init; }
    public required Task<ConversationTurnResult> FinalResult { get; init; }
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
