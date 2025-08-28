using Microsoft.Extensions.AI;
using System.Threading.Channels;


/// <summary>
/// Agent facade that delegates to specialized components for clean separation of concerns.
/// Supports traditional chat, AGUI streaming protocols, and extended capabilities.
/// </summary>
public class Agent : IChatClient
{
    private readonly IChatClient _baseClient;
    private readonly string _name;
    private readonly ScopedFilterManager? _scopedFilterManager;
    private readonly int _maxFunctionCalls;

    // Specialized component fields for delegation
    private readonly MessageProcessor _messageProcessor;
    private readonly FunctionCallProcessor _functionCallProcessor;
    private readonly StreamingManager _streamingManager;
    private readonly AGUIEventHandler _aguiEventHandler;
    private readonly CapabilityManager _capabilityManager;
    private readonly ContinuationPermissionManager? _continuationPermissionManager;

    /// <summary>
    /// Agent configuration object containing all settings
    /// </summary>
    public AgentConfig? Config { get; private set; }
    // Operation metadata keys for ChatResponse.AdditionalProperties
    public static readonly string OperationHadFunctionCallsKey = "Agent.OperationHadFunctionCalls";
    public static readonly string OperationFunctionCallsKey = "Agent.OperationFunctionCalls";
    public static readonly string OperationFunctionCallCountKey = "Agent.OperationFunctionCallCount";


    /// <summary>
    /// Initializes a new Agent instance from an AgentConfig object
    /// </summary>
    public Agent(
        AgentConfig config,
        IChatClient baseClient,
        ChatOptions? mergedOptions,
        List<IPromptFilter> promptFilters,
        ScopedFilterManager scopedFilterManager,
        IAiFunctionFilter? permissionFilter = null,
        ContinuationPermissionManager? continuationPermissionManager = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = config.Name;
        _scopedFilterManager = scopedFilterManager ?? throw new ArgumentNullException(nameof(scopedFilterManager));
        _maxFunctionCalls = config.MaxFunctionCalls;
        _messageProcessor = new MessageProcessor(config.SystemInstructions, mergedOptions ?? config.Provider?.DefaultChatOptions, promptFilters);
        _functionCallProcessor = new FunctionCallProcessor(scopedFilterManager, permissionFilter, new List<IAiFunctionFilter>(), _continuationPermissionManager, config.MaxFunctionCalls);
        _streamingManager = new StreamingManager(_baseClient, _functionCallProcessor, config.MaxFunctionCalls);
        _aguiEventHandler = new AGUIEventHandler(_baseClient, _messageProcessor, _name, config);
        _capabilityManager = new CapabilityManager();
        _continuationPermissionManager = continuationPermissionManager;
    }

    /// <summary>
    /// Agent name
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// System instructions/persona
    /// </summary>
    public string? SystemInstructions => Config?.SystemInstructions ?? _messageProcessor.SystemInstructions;

    /// <summary>
    /// Default chat options
    /// </summary>
    public ChatOptions? DefaultOptions => Config?.Provider?.DefaultChatOptions ?? _messageProcessor.DefaultOptions;

    /// <summary>
    /// AIFuncton filters applied to tool calls in conversations (via ScopedFilterManager)
    /// </summary>
    public IReadOnlyList<IAiFunctionFilter> AIFunctionFilters => new List<IAiFunctionFilter>();

    /// <summary>
    /// Maximum number of function calls allowed in a single conversation turn
    /// </summary>
    public int MaxFunctionCalls => _maxFunctionCalls;

    /// <summary>
    /// Scoped filter manager for applying filters based on function/plugin scope
    /// </summary>
    public ScopedFilterManager? ScopedFilterManager => _scopedFilterManager;


    #region Capability Management

    /// <summary>
    /// Gets a capability by name and type
    /// </summary>
    /// <typeparam name="T">The type of capability to retrieve</typeparam>
    /// <param name="name">The name of the capability</param>
    /// <returns>The capability instance if found, otherwise null</returns>
    public T? GetCapability<T>(string name) where T : class
        => _capabilityManager.GetCapability<T>(name);

    /// <summary>
    /// Adds or updates a capability
    /// </summary>
    /// <param name="name">The name of the capability</param>
    /// <param name="capability">The capability instance</param>
    public void AddCapability(string name, object capability)
        => _capabilityManager.AddCapability(name, capability);

    /// <summary>
    /// Removes a capability by name
    /// </summary>
    /// <param name="name">The name of the capability to remove</param>
    /// <returns>True if the capability was removed, false if it wasn't found</returns>
    public bool RemoveCapability(string name)
        => _capabilityManager.RemoveCapability(name);

    /// <summary>
    /// Gets the audio capability if available
    /// </summary>
    public AudioCapability? Audio => GetCapability<AudioCapability>("Audio");

    #endregion

    #region IChatClient Implementation

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Use MessageProcessor
        var conversation = new Conversation(this);
        messages.ToList().ForEach(m => conversation.AddMessage(m));

        var (effectiveMessages, effectiveOptions) = await _messageProcessor.PrepareMessagesAsync(
            messages, options, conversation, _name, cancellationToken);

        var response = await _baseClient.GetResponseAsync(effectiveMessages, effectiveOptions, cancellationToken);

        // Use FunctionCallProcessor
        if (_scopedFilterManager != null)
        {
            response = await _functionCallProcessor.ProcessResponseWithFiltersAsync(response, effectiveMessages, effectiveOptions, _baseClient, cancellationToken);
        }

        return response;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Use MessageProcessor
        var conversation = new Conversation(this);
        messages.ToList().ForEach(m => conversation.AddMessage(m));

        var (effectiveMessages, effectiveOptions) = await _messageProcessor.PrepareMessagesAsync(
            messages, options, conversation, _name, cancellationToken);

        bool needsFilterProcessing = _scopedFilterManager != null &&
                                   effectiveOptions?.Tools?.Any() == true;

        // Delegate to StreamingManager
        await foreach (var update in _streamingManager.GetStreamingResponseAsync(effectiveMessages, effectiveOptions, needsFilterProcessing, cancellationToken))
        {
            yield return update;
        }
    }



    /// <inheritdoc />
    object? IChatClient.GetService(Type serviceType, object? serviceKey)
    {
        return serviceType switch
        {
            Type t when t == typeof(Agent) => this,
            Type t when t == typeof(IAGUIAgent) => _aguiEventHandler,
            _ => _baseClient.GetService(serviceType, serviceKey)
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _baseClient?.Dispose();
    }

    #endregion

    #region AGUI Delegation Methods

    /// <summary>
    /// Runs the agent in AGUI streaming mode, emitting events to the provided channel
    /// </summary>
    public async Task RunAsync(
        RunAgentInput input,
        ChannelWriter<BaseEvent> events,
        CancellationToken cancellationToken = default)
    {
        await _aguiEventHandler.RunAsync(input, events, cancellationToken);
    }

    /// <summary>
    /// Executes the agent using the AG-UI RunAsync pattern and streams the SSE results 
    /// directly to a given stream. This encapsulates the AG-UI channel logic and provides
    /// proper error handling for web streaming scenarios.
    /// </summary>
    public async Task StreamAGUIResponseAsync(
        RunAgentInput input,
        Stream responseStream,
        CancellationToken cancellationToken = default)
    {
        await _aguiEventHandler.StreamAGUIResponseAsync(input, responseStream, cancellationToken);
    }

    /// <summary>
    /// Executes the agent and streams the AG-UI events directly to a WebSocket connection.
    /// This encapsulates the WebSocket protocol logic and provides proper error handling.
    /// </summary>
    public async Task StreamToWebSocketAsync(
        RunAgentInput input,
        System.Net.WebSockets.WebSocket webSocket,
        CancellationToken cancellationToken)
    {
        await _aguiEventHandler.StreamToWebSocketAsync(input, webSocket, cancellationToken);
    }

    /// <summary>
    /// Executes the agent using orchestrated streaming and emits SSE results directly to a stream.
    /// This version accepts an external, orchestrated stream instead of generating its own.
    /// </summary>
    public async Task StreamAGUIResponseAsync(
        RunAgentInput input,
        IAsyncEnumerable<ChatResponseUpdate> orchestratedStream,
        Stream responseStream,
        CancellationToken cancellationToken = default)
    {
        await _aguiEventHandler.StreamAGUIResponseAsync(input, orchestratedStream, responseStream, cancellationToken);
    }

    /// <summary>
    /// Executes the agent using orchestrated streaming and emits events directly to a WebSocket.
    /// This version accepts an external, orchestrated stream instead of generating its own.
    /// </summary>
    public async Task StreamToWebSocketAsync(
        RunAgentInput input,
        IAsyncEnumerable<ChatResponseUpdate> orchestratedStream,
        System.Net.WebSockets.WebSocket webSocket,
        CancellationToken cancellationToken)
    {
        await _aguiEventHandler.StreamToWebSocketAsync(input, orchestratedStream, webSocket, cancellationToken);
    }

    #endregion


    /// <summary>
    /// Converts ChatResponseUpdate to AG-UI events using simple defaults.
    /// This delegates to the AGUIEventHandler for processing.
    /// </summary>
    /// <param name="update">The chat response update to convert</param>
    /// <param name="messageId">Optional message ID (auto-generated if not provided)</param>
    /// <returns>Collection of AG-UI events ready for streaming</returns>
    public IEnumerable<BaseEvent> ConvertToAGUIEvents(ChatResponseUpdate update, string messageId = "")
    {
        return _aguiEventHandler.ConvertToAGUIEvents(update, messageId);
    }

    /// <summary>
    /// Converts ChatResponseUpdate to AG-UI events with advanced options.
    /// This delegates to the AGUIEventHandler for processing.
    /// </summary>
    /// <param name="update">The chat response update to convert</param>
    /// <param name="messageId">Message ID for the events</param>
    /// <param name="emitBackendToolCalls">Whether to emit backend tool call events</param>
    /// <returns>Collection of AG-UI events with customized behavior</returns>
    public IEnumerable<BaseEvent> ConvertToAGUIEvents(
        ChatResponseUpdate update,
        string messageId,
        bool emitBackendToolCalls = false)
    {
        return _aguiEventHandler.ConvertToAGUIEvents(update, messageId, emitBackendToolCalls);
    }

    /// <summary>
    /// Serializes any AG-UI event to JSON using the correct polymorphic serialization.
    /// This delegates to the AGUIEventHandler for processing.
    /// </summary>
    /// <param name="aguiEvent">The AG-UI event to serialize</param>
    /// <returns>JSON string with proper polymorphic serialization</returns>
    public string SerializeEvent(BaseEvent aguiEvent)
    {
        return _aguiEventHandler.SerializeEvent(aguiEvent);
    }
}

/// <summary>
/// Extension methods for accessing Agent operation metadata from ChatResponse
/// </summary>
public static class ChatResponseExtensions
{
    /// <summary>
    /// Gets whether the operation had function calls
    /// </summary>
    public static bool GetOperationHadFunctionCalls(this ChatResponse response)
    {
        return response.AdditionalProperties?.TryGetValue(Agent.OperationHadFunctionCallsKey, out var value) == true 
            && value is bool hadCalls && hadCalls;
    }

    /// <summary>
    /// Gets the list of function calls made during the operation
    /// </summary>
    public static string[] GetOperationFunctionCalls(this ChatResponse response)
    {
        return response.AdditionalProperties?.TryGetValue(Agent.OperationFunctionCallsKey, out var value) == true 
            && value is string[] calls ? calls : Array.Empty<string>();
    }

    /// <summary>
    /// Gets the number of function call iterations during the operation
    /// </summary>
    public static int GetOperationFunctionCallCount(this ChatResponse response)
    {
        return response.AdditionalProperties?.TryGetValue(Agent.OperationFunctionCallCountKey, out var value) == true 
            && value is int count ? count : 0;
    }
}