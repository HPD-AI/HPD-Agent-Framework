using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using HPD_Agent.TextExtraction;
using System.Diagnostics;

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
    // Memory management
    private ConversationDocumentHandling _uploadStrategy = ConversationDocumentHandling.FullTextInjection; // Default to FullTextInjection for simpler scenarios
    private readonly List<ConversationDocumentUpload> _pendingInjections = new();
    private TextExtractionUtility? _textExtractor;

    // OpenTelemetry Activity Source for conversation telemetry
    private static readonly ActivitySource ActivitySource = new("HPD.Conversation");
    
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
    public string Id { get; } = Guid.NewGuid().ToString();
    
    // Debug constructor to track conversation creation
    static Conversation()
    {
        // This will help us see when conversations are created
    }
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
        using var activity = ActivitySource.StartActivity("conversation.turn");
        var startTime = DateTimeOffset.UtcNow;

        // Set telemetry tags
        activity?.SetTag("conversation.id", Id);
        activity?.SetTag("conversation.message_count", _messages.Count);
        activity?.SetTag("conversation.has_documents", documentPaths?.Length > 0);
        activity?.SetTag("conversation.agent_count", _agents.Count);
        activity?.SetTag("conversation.primary_agent", PrimaryAgent?.Config?.Name);

        try
        {
            // Process documents if provided
            if (documentPaths?.Length > 0)
            {
                activity?.SetTag("conversation.document_count", documentPaths.Length);
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
                activity?.SetTag("conversation.orchestration_strategy", "SingleAgent");
                var response = await agent.GetResponseAsync(_messages, options, cancellationToken);
                orchestrationResult = new OrchestrationResult
                {
                    Response = response,
                    SelectedAgent = agent,
                    Metadata = new OrchestrationMetadata
                    {
                        StrategyName = "SingleAgent",
                        DecisionDuration = TimeSpan.Zero,
                        Context = agent.GetReductionMetadata() // Include reduction metadata
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

                activity?.SetTag("conversation.orchestration_strategy", effectiveOrchestrator.GetType().Name);
                orchestrationResult = await effectiveOrchestrator.OrchestrateAsync(
                    _messages, _agents, this.Id, options, cancellationToken);
            }

            // Apply reduction BEFORE adding response to history
            ApplyReductionIfPresent(orchestrationResult);

            // Commit response to history
            _messages.AddMessages(orchestrationResult.Response);
            UpdateActivity();

            // Record telemetry metrics
            var duration = DateTimeOffset.UtcNow - startTime;
            var tokenUsage = CreateTokenUsage(orchestrationResult.Response);

            activity?.SetTag("conversation.duration_ms", duration.TotalMilliseconds);
            activity?.SetTag("conversation.responding_agent", orchestrationResult.SelectedAgent?.Name);
            activity?.SetTag("conversation.tokens_used", tokenUsage?.TotalTokens ?? 0);
            activity?.SetTag("conversation.success", true);

            return new ConversationTurnResult
            {
                Response = orchestrationResult.Response,
                TurnHistory = ExtractTurnHistory(userMessage, orchestrationResult.Response),
                RespondingAgent = orchestrationResult.SelectedAgent!,
                UsedOrchestrator = orchestrator,
                Duration = duration,
                OrchestrationMetadata = orchestrationResult.Metadata,
                Usage = tokenUsage,
                RequestId = Guid.NewGuid().ToString(),
                ActivityId = System.Diagnostics.Activity.Current?.Id
            };
        }
        catch (Exception ex)
        {
            var duration = DateTimeOffset.UtcNow - startTime;
            activity?.SetTag("conversation.duration_ms", duration.TotalMilliseconds);
            activity?.SetTag("conversation.success", false);
            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetTag("error.message", ex.Message);
            throw;
        }
    }


    // Helper method to avoid code duplication
    private ChatOptions? InjectProjectContextIfNeeded(ChatOptions? options)
    {
        options ??= new ChatOptions();
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        
        // Always inject conversation ID
        options.AdditionalProperties["ConversationId"] = Id;
        
        // Inject project if available
        if (Metadata.TryGetValue("Project", out var obj) && obj is Project project)
        {
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

        // Create a channel to allow multiple consumers of the event stream
        var channel = System.Threading.Channels.Channel.CreateUnbounded<BaseEvent>();
        var writer = channel.Writer;
        var reader = channel.Reader;
        
        // Create TaskCompletionSource for the final result
        var resultTcs = new TaskCompletionSource<ConversationTurnResult>();
        
        // Start a task to produce events and build the final result
        _ = Task.Run(async () => 
        {
            var startTime = DateTime.UtcNow;
            ChatResponse? finalResponse = null;
            Agent? selectedAgent = null;
            OrchestrationMetadata? orchestrationMetadata = null;
            var userMessage = new ChatMessage(ChatRole.User, message);
            
            try
            {
                // Generate and broadcast events while capturing metadata
                await foreach (var evt in SendStreamingEventsAsync(message, options, orchestrator, documentPaths, cancellationToken))
                {
                    await writer.WriteAsync(evt, cancellationToken);
                }
                
                // Close the writer to signal completion
                writer.Complete();
                
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
                writer.Complete(ex);
                resultTcs.SetException(ex);
            }
        }, cancellationToken);
        
        // Create an async enumerable from the channel reader
        async IAsyncEnumerable<BaseEvent> eventStream([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                yield return evt;
            }
        }
        
        return new ConversationStreamingResult
        {
            EventStream = eventStream(cancellationToken),
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
            ReasoningContentEvent text => text.Content,
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

        // Inject conversation ID into ChatOptions for plugin access
        // This is more reliable than AsyncLocal when ExecutionContext may not flow through Microsoft.Extensions.AI
        options ??= new ChatOptions();
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties["ConversationId"] = Id;

        // Set conversation context for AsyncLocal access by plugins (backup mechanism)
        ConversationContext.SetConversationId(Id);
        try
        {
            if (_agents.Count == 0)
        {
            throw new InvalidOperationException("No agents configured for this conversation");
        }
        else if (_agents.Count == 1)
        {
            // DIRECT PATH - Single agent
            var agent = _agents[0];
            var result = await agent.ExecuteStreamingTurnAsync(_messages, options, documentPaths, cancellationToken);

            // Stream the events
            await foreach (var evt in result.EventStream.WithCancellation(cancellationToken))
            {
                yield return evt;
            }

            // Wait for final history and update conversation
            var finalHistory = await result.FinalHistory;

            // Check for reduction metadata and apply BEFORE adding new messages
            var reductionMetadata = agent.GetReductionMetadata();
            if (reductionMetadata.TryGetValue("SummaryMessage", out var summaryObj) &&
                summaryObj is ChatMessage summary &&
                reductionMetadata.TryGetValue("MessagesRemovedCount", out var countObj) &&
                countObj is int count)
            {
                int systemMsgCount = _messages.Count(m => m.Role == ChatRole.System);
                _messages.RemoveRange(systemMsgCount, count);
                _messages.Insert(systemMsgCount, summary);
                agent.ClearReductionMetadata();
            }

            _messages.AddRange(finalHistory);
            UpdateActivity();

            // Apply filters on the completed turn
            var lastMessage = finalHistory.LastOrDefault();
            if (lastMessage != null)
            {
                // Filters are now applied by the Agent directly
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

            // Apply reduction from orchestration metadata BEFORE adding response
            ApplyReductionIfPresent(finalResult);

            _messages.AddMessages(finalResult.Response);
            UpdateActivity();

            // Filters are now applied by the Agent directly
        }
        }
        finally
        {
            // Clear conversation context after turn execution
            ConversationContext.Clear();
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
    /// Extracts reduction metadata from OrchestrationMetadata.Context and applies to storage.
    /// </summary>
    private void ApplyReductionIfPresent(OrchestrationResult result)
    {
        var context = result.Metadata.Context;

        if (context.TryGetValue("SummaryMessage", out var summaryObj) &&
            summaryObj is ChatMessage summary &&
            context.TryGetValue("MessagesRemovedCount", out var countObj) &&
            countObj is int count)
        {
            // Find system message count (preserve them)
            int systemMsgCount = _messages.Count(m => m.Role == ChatRole.System);

            // Remove the summarized messages
            _messages.RemoveRange(systemMsgCount, count);

            // Insert summary right after system message(s)
            _messages.Insert(systemMsgCount, summary);

            // Clear agent's metadata after use
            result.SelectedAgent?.ClearReductionMetadata();
        }
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
