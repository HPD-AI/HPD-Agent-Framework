using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Microsoft.KernelMemory;
using HPD_Agent.MemoryRAG;

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
    /// Add a single message to the conversation
    /// </summary>
    public void AddMessage(ChatMessage message)
    {
        _messages.Add(message);
        UpdateActivity();
    }

    /// <summary>
    /// Gets or creates the memory instance for this conversation
    /// </summary>
    /// <returns>The kernel memory instance</returns>
    public IKernelMemory GetOrCreateMemory()
    {
        if (_memory != null) return _memory;
        
        if (_memoryBuilder == null)
        {
            // Create default memory builder if none provided
            _memoryBuilder = new ConversationMemoryBuilder(Id);
        }
        
        return _memory ??= _memoryBuilder.Build();
    }
    
    /// <summary>
    /// Sets the memory builder for this conversation
    /// </summary>
    /// <param name="builder">The memory builder to use</param>
    public void SetMemoryBuilder(ConversationMemoryBuilder builder)
    {
        _memoryBuilder = builder ?? throw new ArgumentNullException(nameof(builder));
        _memory = null; // Clear existing memory to force rebuild with new builder
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

        // CONTEXT ASSEMBLY: Gather all available memory handles
        var ragContext = AssembleRAGContext(options);
        
        // Inject RAG context for agent capabilities
        if (ragContext != null)
        {
            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["RAGContext"] = ragContext;
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

        // CONTEXT ASSEMBLY: Gather all available memory handles
        var ragContext = AssembleRAGContext(options);
        
        // Inject RAG context for agent capabilities
        if (ragContext != null)
        {
            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["RAGContext"] = ragContext;
        }

        // Inject project context for Memory CAG if available
        if (Metadata.TryGetValue("Project", out var obj) && obj is Project project)
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
    /// Simple state serialization (no complex channel management needed)
    /// </summary>
    public string Serialize() => JsonSerializer.Serialize(new ConversationState
    {
        Id = Id,
        Messages = _messages,
        Metadata = _metadata,
        CreatedAt = CreatedAt,
        LastActivity = LastActivity
    }, ConversationJsonContext.Default.ConversationState);

    public static Conversation Deserialize(string json)
    {
        var state = JsonSerializer.Deserialize(json, ConversationJsonContext.Default.ConversationState)!;
        return new Conversation
        {
            // Restore state - much simpler than SK's complex channel restoration
        };
    }

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
    private RAGContext? AssembleRAGContext(ChatOptions? options)
    {
        var agentMemories = new Dictionary<string, IKernelMemory?>();
        IKernelMemory? conversationMemory = null;
        IKernelMemory? projectMemory = null;

        // Gather agent memories
        foreach (var agent in _agents)
        {
            try
            {
                agentMemories[agent.Name] = agent.GetOrCreateMemory();
            }
            catch
            {
                // Agent may not have memory configured - continue
                agentMemories[agent.Name] = null;
            }
        }

        // Gather conversation memory
        try
        {
            conversationMemory = this.GetOrCreateMemory();
        }
        catch
        {
            // Conversation may not have memory configured
        }

        // Gather project memory
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

        // Only create context if at least one memory source is available
        if (!agentMemories.Values.Any(m => m != null) && conversationMemory == null && projectMemory == null)
            return null;

        // Use default configuration for now
        var config = new RAGConfiguration();
        
        return new RAGContext
        {
            AgentMemories = agentMemories,
            ConversationMemory = conversationMemory,
            ProjectMemory = projectMemory,
            Configuration = config
        };
    }

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
