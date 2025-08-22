using Microsoft.Extensions.AI;
using System.Threading.Channels;

/// <summary>
/// Metadata about function call operations for thread-safe per-call tracking
/// </summary>
public class OperationMetadata
{
    public bool HadFunctionCalls { get; set; }
    public List<string> FunctionCalls { get; set; } = new();
    public int FunctionCallCount { get; set; }
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

/// <summary>
/// Agent implementation that supports both traditional chat and AGUI streaming protocols
/// Now properly applies AIFuncton filters to all chat methods
/// </summary>
public class Agent : IChatClient, IAGUIAgent
{
    private readonly IChatClient _baseClient;
    private readonly string _name;
    private readonly ChatOptions? _defaultOptions;
    private readonly string? _systemInstructions;
    private readonly AGUIEventConverter _eventConverter;
    private readonly IReadOnlyList<IAiFunctionFilter> _aiFunctionFilters;
    private readonly ScopedFilterManager? _scopedFilterManager;
    private readonly IAiFunctionFilter? _permissionFilter;
    private readonly ContinuationPermissionManager? _continuationPermissionManager;
    //private readonly ContextualFunctionSelector? _contextualSelector;
    private readonly Dictionary<string, object> _capabilities = new();
    private readonly List<IPromptFilter> _promptFilters;
    // Memory management

    // Function calling configuration
    private readonly int _maxFunctionCalls;

    /// <summary>
    /// Agent configuration object containing all settings
    /// </summary>
    public AgentConfig? Config { get; private set; }
    // Operation metadata keys for ChatResponse.AdditionalProperties
    public static readonly string OperationHadFunctionCallsKey = "Agent.OperationHadFunctionCalls";
    public static readonly string OperationFunctionCallsKey = "Agent.OperationFunctionCalls";
    public static readonly string OperationFunctionCallCountKey = "Agent.OperationFunctionCallCount";

    /// <summary>
    /// Initializes a new Agent instance
    /// </summary>
    public Agent(IChatClient baseClient, string name, ChatOptions? defaultOptions = null, string? systemInstructions = null, IEnumerable<IAiFunctionFilter>? AIFunctionFilters = null, int maxFunctionCalls = 10)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _defaultOptions = defaultOptions;
        _systemInstructions = systemInstructions;
        _aiFunctionFilters = AIFunctionFilters?.ToList() ?? new List<IAiFunctionFilter>();
        _maxFunctionCalls = maxFunctionCalls;
        _eventConverter = new AGUIEventConverter();
        _promptFilters = new List<IPromptFilter>();
    }

    /// <summary>
    /// Initializes a new Agent instance with scoped filter manager
    /// </summary>
    public Agent(IChatClient baseClient, string name, ChatOptions? defaultOptions, string? systemInstructions, ScopedFilterManager scopedFilterManager, int maxFunctionCalls = 10)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _defaultOptions = defaultOptions;
        _systemInstructions = systemInstructions;
        _scopedFilterManager = scopedFilterManager ?? throw new ArgumentNullException(nameof(scopedFilterManager));
        _aiFunctionFilters = new List<IAiFunctionFilter>(); // Will use scoped filters instead
        _maxFunctionCalls = maxFunctionCalls;
        _eventConverter = new AGUIEventConverter();
        _promptFilters = new List<IPromptFilter>();
    }

    /// <summary>
    /// Initializes a new Agent instance with prompt filters, scoped manager, and contextual selector
    /// </summary>
    public Agent(
    IChatClient baseClient,
    string name,
    ChatOptions? defaultOptions,
    string? systemInstructions,
    List<IPromptFilter> promptFilters,
    ScopedFilterManager scopedFilterManager,
    IAiFunctionFilter? permissionFilter,
    ContinuationPermissionManager? continuationPermissionManager,
    int maxFunctionCalls = 10)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _defaultOptions = defaultOptions;
        _systemInstructions = systemInstructions;
        _scopedFilterManager = scopedFilterManager ?? throw new ArgumentNullException(nameof(scopedFilterManager));
        _aiFunctionFilters = new List<IAiFunctionFilter>();
    _promptFilters = promptFilters?.ToList() ?? new List<IPromptFilter>();
    _permissionFilter = permissionFilter; // <-- Ensure this assignment is present
    _continuationPermissionManager = continuationPermissionManager;
    _maxFunctionCalls = maxFunctionCalls;
    _eventConverter = new AGUIEventConverter();
    Config = null; // Legacy constructor doesn't have config
    }

    /// <summary>
    /// Initializes a new Agent instance from an AgentConfig object (new preferred constructor)
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
        _defaultOptions = mergedOptions ?? config.Provider?.DefaultChatOptions;
        _systemInstructions = config.SystemInstructions;
        _scopedFilterManager = scopedFilterManager ?? throw new ArgumentNullException(nameof(scopedFilterManager));
        _aiFunctionFilters = new List<IAiFunctionFilter>();
        _promptFilters = promptFilters?.ToList() ?? new List<IPromptFilter>();
        _permissionFilter = permissionFilter;
        _continuationPermissionManager = continuationPermissionManager;
        _maxFunctionCalls = config.MaxFunctionCalls;
        _eventConverter = new AGUIEventConverter();
    }

    /// <summary>
    /// Agent name
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// System instructions/persona
    /// </summary>
    public string? SystemInstructions => _systemInstructions;

    /// <summary>
    /// Default chat options
    /// </summary>
    public ChatOptions? DefaultOptions => _defaultOptions;

    /// <summary>
    /// AIFuncton filters applied to tool calls in conversations
    /// </summary>
    public IReadOnlyList<IAiFunctionFilter> AIFunctionFilters => _aiFunctionFilters;

    /// <summary>
    /// Maximum number of function calls allowed in a single conversation turn
    /// </summary>
    public int MaxFunctionCalls => _maxFunctionCalls;

    /// <summary>
    /// Scoped filter manager for applying filters based on function/plugin scope
    /// </summary>
    public ScopedFilterManager? ScopedFilterManager => _scopedFilterManager;

    /// <summary>
    /// Contextual function selector for intelligent function filtering
    /// </summary>
    //public ContextualFunctionSelector? ContextualSelector => _contextualSelector;

    #region Capability Management

    /// <summary>
    /// Gets a capability by name and type
    /// </summary>
    /// <typeparam name="T">The type of capability to retrieve</typeparam>
    /// <param name="name">The name of the capability</param>
    /// <returns>The capability instance if found, otherwise null</returns>
    public T? GetCapability<T>(string name) where T : class
        => _capabilities.TryGetValue(name, out var capability) ? capability as T : null;

    /// <summary>
    /// Adds or updates a capability
    /// </summary>
    /// <param name="name">The name of the capability</param>
    /// <param name="capability">The capability instance</param>
    public void AddCapability(string name, object capability)
        => _capabilities[name] = capability ?? throw new ArgumentNullException(nameof(capability));

    /// <summary>
    /// Removes a capability by name
    /// </summary>
    /// <param name="name">The name of the capability to remove</param>
    /// <returns>True if the capability was removed, false if it wasn't found</returns>
    public bool RemoveCapability(string name)
        => _capabilities.Remove(name);

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
        var effectiveMessages = PrependSystemInstructions(messages);
        var effectiveOptions = MergeOptions(options);
        
        // Apply contextual function filtering if configured
        //effectiveOptions = await ApplyContextualFiltering(effectiveMessages, effectiveOptions, cancellationToken);
        // Apply prompt filters
        effectiveMessages = await ApplyPromptFilters(effectiveMessages, effectiveOptions, cancellationToken);
        
        // Get the response from the base client
        var response = await _baseClient.GetResponseAsync(effectiveMessages, effectiveOptions, cancellationToken);
        
        // Apply AIFuncton filters if there are any function calls
        if ((_aiFunctionFilters.Any() || _scopedFilterManager != null))
        {
            response = await ProcessResponseWithFilters(response, effectiveMessages, effectiveOptions, cancellationToken);
        }
        
        return response;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, 
        ChatOptions? options = null, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var effectiveMessages = PrependSystemInstructions(messages);
        var effectiveOptions = MergeOptions(options);
        
        // Apply contextual function filtering if configured
        //effectiveOptions = await ApplyContextualFiltering(effectiveMessages, effectiveOptions, cancellationToken);

        // Apply prompt filters (including Memory CAG)
        effectiveMessages = await ApplyPromptFilters(effectiveMessages, effectiveOptions, cancellationToken);
        
        // Check if we need to apply AIFunction filters for interleaved streaming
        bool needsFilterProcessing = (_aiFunctionFilters.Any() || _scopedFilterManager != null) && 
                                   effectiveOptions?.Tools?.Any() == true;
        
        if (needsFilterProcessing)
        {
            // Use true interleaved streaming: stream text, pause for tools, continue
            await foreach (var update in GetInterleavedStreamingResponseAsync(effectiveMessages, effectiveOptions, cancellationToken))
            {
                yield return update;
            }
        }
        else
        {
            // Use real streaming when no function calling filters are needed
            await foreach (var update in _baseClient.GetStreamingResponseAsync(effectiveMessages, effectiveOptions, cancellationToken))
            {
                yield return update;
            }
        }
    }

    /// <summary>
    /// Provides true interleaved streaming that emits text immediately and pauses for tool execution
    /// </summary>
    private async IAsyncEnumerable<ChatResponseUpdate> GetInterleavedStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, 
        ChatOptions? options, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var currentMessages = messages.ToList();
        var operationMetadata = new OperationMetadata();
        
        for (int iteration = 0; iteration < _maxFunctionCalls; iteration++)
        {
            var completedFunctionCalls = new List<FunctionCallContent>();
            var hasStreamedContent = false;
            var streamFinished = false;
            
            // Stream the response and immediately process function calls as they appear
            await foreach (var update in _baseClient.GetStreamingResponseAsync(currentMessages, options, cancellationToken))
            {
                if (update.Contents != null)
                {
                    var textContent = new List<AIContent>();
                    var functionCalls = new List<FunctionCallContent>();
                    
                    // Separate text content from function calls
                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent funcCall)
                        {
                            functionCalls.Add(funcCall);
                        }
                        else
                        {
                            textContent.Add(content);
                        }
                    }
                    
                    // Emit text content immediately
                    if (textContent.Any())
                    {
                        yield return new ChatResponseUpdate
                        {
                            Contents = textContent,
                            AdditionalProperties = update.AdditionalProperties
                        };
                        hasStreamedContent = true;
                    }
                    
                    // Process function calls immediately when they appear
                    if (functionCalls.Any())
                    {
                        // Track function call metadata
                        operationMetadata.HadFunctionCalls = true;
                        operationMetadata.FunctionCalls.AddRange(functionCalls.Select(fc => fc.Name));
                        operationMetadata.FunctionCallCount = iteration + 1;
                        
                        // Execute function calls immediately
                        var functionCallMessages = await ProcessFunctionCallsAsync(currentMessages, options, functionCalls, cancellationToken);
                        
                        // Emit function call results immediately
                        foreach (var funcMessage in functionCallMessages)
                        {
                            foreach (var content in funcMessage.Contents)
                            {
                                yield return new ChatResponseUpdate
                                {
                                    Contents = [content]
                                };
                            }
                        }
                        
                        // Add function results to message history for continuation
                        currentMessages.AddRange(functionCallMessages);
                        completedFunctionCalls.AddRange(functionCalls);
                    }
                }
                
                // Check if stream finished (finish reason present)
                if (update.FinishReason != null)
                {
                    streamFinished = true;
                    
                    // Emit finish reason
                    yield return new ChatResponseUpdate
                    {
                        Contents = [],
                        FinishReason = update.FinishReason,
                        AdditionalProperties = update.AdditionalProperties
                    };
                }
            }
            
            // If no function calls were made, or stream finished naturally, we're done
            if (!completedFunctionCalls.Any() || streamFinished)
            {
                break;
            }
            
            // If we had function calls, continue with next iteration
            // Update options for next turn (allow model to not call tools)
            options = options == null
                ? new ChatOptions()
                : new ChatOptions
                {
                    Tools = options.Tools,
                    ToolMode = AutoChatToolMode.Auto, // Allow model to choose
                    AllowMultipleToolCalls = options.AllowMultipleToolCalls,
                    MaxOutputTokens = options.MaxOutputTokens,
                    Temperature = options.Temperature,
                    TopP = options.TopP,
                    FrequencyPenalty = options.FrequencyPenalty,
                    PresencePenalty = options.PresencePenalty,
                    ResponseFormat = options.ResponseFormat,
                    Seed = options.Seed,
                    StopSequences = options.StopSequences,
                    ModelId = options.ModelId,
                    AdditionalProperties = options.AdditionalProperties
                };
        }
        
        // Emit final update with operation metadata
        yield return new ChatResponseUpdate
        {
            Contents = [],
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [OperationHadFunctionCallsKey] = operationMetadata.HadFunctionCalls,
                [OperationFunctionCallsKey] = operationMetadata.FunctionCalls.ToArray(),
                [OperationFunctionCallCountKey] = operationMetadata.FunctionCallCount
            }
        };
    }

    /// <summary>
    /// Process response with AIFunction filters when function calls are present
    /// Supports multi-turn function calling by continuing until the LLM provides a final answer
    /// Based on Microsoft.Extensions.AI.FunctionInvokingChatClient pattern
    /// </summary>
    private async Task<ChatResponse> ProcessResponseWithFilters(
        ChatResponse response, 
        IEnumerable<ChatMessage> messages, 
        ChatOptions? options, 
        CancellationToken cancellationToken)
    {
        // Copy the original messages to avoid multiple enumeration
        List<ChatMessage> originalMessages = [.. messages];
        var currentMessages = originalMessages.AsEnumerable();
        
        List<ChatMessage>? augmentedHistory = null; // the actual history of messages sent on turns other than the first
        ChatResponse? currentResponse = response; // the response from the inner client
        List<ChatMessage>? responseMessages = null; // tracked list of messages, across multiple turns, to be used for the final response
        List<FunctionCallContent>? functionCallContents = null; // function call contents that need responding to in the current turn
        
        // Initialize operation metadata
        var operationMetadata = new OperationMetadata();

        for (int iteration = 0; iteration < _maxFunctionCalls; iteration++)
        {
            functionCallContents?.Clear();

            // Any function call work to do? If yes, ensure we're tracking that work in functionCallContents.
            bool requiresFunctionInvocation =
                (options?.Tools is { Count: > 0 }) &&
                CopyFunctionCalls(currentResponse.Messages, ref functionCallContents);

            // In a common case where we make a request and there's no function calling work required,
            // fast path out by just returning the original response.
            if (iteration == 0 && !requiresFunctionInvocation)
            {
                return currentResponse;
            }

            // Track aggregate details from the response, including all of the response messages
            (responseMessages ??= []).AddRange(currentResponse.Messages);

            // If there are no tools to call, or for any other reason we should stop, we're done.
            // Break out of the loop and allow the handling at the end to configure the response
            // with aggregated data from previous requests.
            if (!requiresFunctionInvocation)
            {
                break;
            }

            // Track function call metadata
            operationMetadata.HadFunctionCalls = true;
            operationMetadata.FunctionCalls.AddRange(functionCallContents!.Select(fc => fc.Name));
            operationMetadata.FunctionCallCount = iteration + 1;

            // Prepare the history for the next iteration.
            PrepareHistoryForNextIteration(originalMessages, ref currentMessages, ref augmentedHistory, currentResponse, responseMessages);

            // Add the responses from the function calls into the augmented history and also into the tracked
            // list of response messages.
            var addedMessages = await ProcessFunctionCallsAsync(augmentedHistory ?? currentMessages.ToList(), options, functionCallContents!, cancellationToken);
            // Add the tool results into the history for the NEXT turn so the model sees them.
            (augmentedHistory ?? (List<ChatMessage>)currentMessages).AddRange(addedMessages);
            responseMessages.AddRange(addedMessages);

            // Call the LLM again with the updated history
            // For the next turn, create a new set of options to avoid forcing the model to call a tool again.
            // This prevents an infinite loop where the model keeps invoking functions.
            var nextTurnOptions = options == null
                ? new ChatOptions()
                : new ChatOptions
                {
                    Tools = options.Tools,
                    // Explicitly reset ToolMode to null (defaults to 'Auto') so the model is free to answer.
                    ToolMode = AutoChatToolMode.Auto,
                    AllowMultipleToolCalls = options.AllowMultipleToolCalls,
                    MaxOutputTokens = options.MaxOutputTokens,
                    Temperature = options.Temperature,
                    TopP = options.TopP,
                    FrequencyPenalty = options.FrequencyPenalty,
                    PresencePenalty = options.PresencePenalty,
                    ResponseFormat = options.ResponseFormat,
                    Seed = options.Seed,
                    StopSequences = options.StopSequences,
                    ModelId = options.ModelId,
                    AdditionalProperties = options.AdditionalProperties
                };

            currentResponse = await _baseClient.GetResponseAsync(currentMessages, nextTurnOptions, cancellationToken);
        }

        // Configure the final response with aggregated data
        if (responseMessages != null)
        {
            currentResponse.Messages = responseMessages;
        }

        // Add operation metadata to response
        currentResponse.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        currentResponse.AdditionalProperties[OperationHadFunctionCallsKey] = operationMetadata.HadFunctionCalls;
        currentResponse.AdditionalProperties[OperationFunctionCallsKey] = operationMetadata.FunctionCalls.ToArray();
        currentResponse.AdditionalProperties[OperationFunctionCallCountKey] = operationMetadata.FunctionCallCount;

        return currentResponse;
    }

    /// <summary>
    /// Prepares the various chat message lists after a response from the inner client and before invoking functions
    /// </summary>
    private static void PrepareHistoryForNextIteration(
        IEnumerable<ChatMessage> originalMessages,
        ref IEnumerable<ChatMessage> currentMessages,
        ref List<ChatMessage>? augmentedHistory,
        ChatResponse response,
        List<ChatMessage> allTurnsResponseMessages)
    {
        // We're going to need to augment the history with function result contents.
        // That means we need a separate list to store the augmented history.
        augmentedHistory ??= originalMessages.ToList();

        // Now add the most recent response messages.
        augmentedHistory.AddRange(response.Messages);

        // Use the augmented history as the new set of messages to send.
        currentMessages = augmentedHistory;
    }

    /// <summary>
    /// Copies any FunctionCallContent from messages to functionCalls
    /// </summary>
    private static bool CopyFunctionCalls(
        IList<ChatMessage> messages, ref List<FunctionCallContent>? functionCalls)
    {
        bool any = false;
        int count = messages.Count;
        for (int i = 0; i < count; i++)
        {
            any |= CopyFunctionCalls(messages[i].Contents, ref functionCalls);
        }

        return any;
    }

    /// <summary>
    /// Copies any FunctionCallContent from content to functionCalls
    /// </summary>
    private static bool CopyFunctionCalls(
        IList<AIContent> content, ref List<FunctionCallContent>? functionCalls)
    {
        bool any = false;
        int count = content.Count;
        for (int i = 0; i < count; i++)
        {
            if (content[i] is FunctionCallContent functionCall)
            {
                (functionCalls ??= []).Add(functionCall);
                any = true;
            }
        }

        return any;
    }

    /// <summary>
    /// Processes the function calls and returns the messages to add to the conversation
    /// </summary>
    private async Task<IList<ChatMessage>> ProcessFunctionCallsAsync(
        List<ChatMessage> messages, 
        ChatOptions? options, 
        List<FunctionCallContent> functionCallContents, 
        CancellationToken cancellationToken)
    {
        var resultMessages = new List<ChatMessage>();

        // Process each function call through the filter pipeline

        foreach (var functionCall in functionCallContents)
        {
            var toolCallRequest = new ToolCallRequest
            {
                FunctionName = functionCall.Name,
                Arguments = functionCall.Arguments ?? new Dictionary<string, object?>()
            };

            var tempConversation = new Conversation();
            foreach (var msg in messages) { tempConversation.AddMessage(msg); }

            var context = new AiFunctionContext(tempConversation, toolCallRequest)
            {
                Function = FindFunction(toolCallRequest.FunctionName, options?.Tools)
            };

            // The final step in the pipeline is the actual function invocation.
            Func<AiFunctionContext, Task> finalInvoke = async (ctx) =>
            {
                if (ctx.Function is null)
                {
                    ctx.Result = $"Function '{ctx.ToolCallRequest.FunctionName}' not found.";
                    return;
                }
                try
                {
                    var args = new AIFunctionArguments(ctx.ToolCallRequest.Arguments);
                    ctx.Result = await ctx.Function.InvokeAsync(args, cancellationToken);
                }
                catch (Exception ex)
                {
                    ctx.Result = $"Error invoking function: {ex.Message}";
                }
            };

            var pipeline = finalInvoke;

            // Get all standard applicable filters.
            var applicableFilters = _scopedFilterManager?.GetApplicableFilters(functionCall.Name)
                                    ?? Enumerable.Empty<IAiFunctionFilter>();

            // Wrap standard filters first.
            foreach (var filter in applicableFilters.Reverse())
            {
                var previous = pipeline;
                pipeline = ctx => filter.InvokeAsync(ctx, previous);
            }

            // *** CRITICAL: Wrap the permission filter last, so it runs FIRST. ***
            if (_permissionFilter != null)
            {
                var previous = pipeline;
                pipeline = ctx => _permissionFilter.InvokeAsync(ctx, previous);
            }

            // Execute the full pipeline.
            await pipeline(context);

            var functionResult = new FunctionResultContent(functionCall.CallId, context.Result);
            var functionMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { functionResult });
            resultMessages.Add(functionMessage);
        }

        return resultMessages;
    }

    /// <summary>
    /// Helper method to find a function by name in the tools collection
    /// </summary>
    private AIFunction? FindFunction(string functionName, IList<AITool>? tools)
    {
        if (tools == null) return null;
        return tools.OfType<AIFunction>().FirstOrDefault(f => f.Name == functionName);
    }

    /// <inheritdoc />
    object? IChatClient.GetService(Type serviceType, object? serviceKey)
    {
        return serviceType switch
        {
            Type t when t == typeof(Agent) => this,
            Type t when t == typeof(IAGUIAgent) => this,
            _ => _baseClient.GetService(serviceType, serviceKey)
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _baseClient?.Dispose();
    }

    #endregion

    #region IAGUIAgent Implementation

    /// <summary>
    /// Runs the agent in AGUI streaming mode, emitting events to the provided channel
    /// </summary>
    public async Task RunAsync(
        RunAgentInput input, 
        ChannelWriter<BaseEvent> events, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Emit run started event
            await events.WriteAsync(AGUIEventConverter.LifecycleEvents.CreateRunStarted(input), cancellationToken);

            // Convert AGUI input to Extensions.AI format
            var messages = _eventConverter.ConvertToExtensionsAI(input);
            var effectiveMessages = PrependSystemInstructions(messages);
            
            // Convert tools and options
            var chatOptions = _eventConverter.ConvertToExtensionsAIChatOptions(input, _defaultOptions);

            // Generate message ID for this response
            var messageId = Guid.NewGuid().ToString();

            // Emit message start
            await events.WriteAsync(AGUIEventConverter.LifecycleEvents.CreateTextMessageStart(messageId), cancellationToken);

            // Use the Agent's streaming method instead of the base client directly
            // This ensures that AIFuncton filters are applied for function calls
            await foreach (var update in GetStreamingResponseAsync(
                effectiveMessages, chatOptions, cancellationToken))
            {
                // Convert each update to AGUI events
                var agUIEvents = _eventConverter.ConvertToAGUIEvents(update, messageId, emitBackendToolCalls: true);
                
                foreach (var eventItem in agUIEvents)
                {
                    await events.WriteAsync(eventItem, cancellationToken);
                }
            }

            // Emit message end
            await events.WriteAsync(AGUIEventConverter.LifecycleEvents.CreateTextMessageEnd(messageId), cancellationToken);

            // Emit run finished event
            await events.WriteAsync(AGUIEventConverter.LifecycleEvents.CreateRunFinished(input), cancellationToken);
        }
        catch (Exception ex)
        {
            // Emit error event
            await events.WriteAsync(AGUIEventConverter.LifecycleEvents.CreateRunError(input, ex), cancellationToken);
            throw;
        }
        finally
        {
            // Complete the channel
            events.Complete();
        }
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
        // This method now calls the new overload, passing its OWN stream as the source.
        var selfGeneratedStream = this.GetStreamingResponseAsync(
            _eventConverter.ConvertToExtensionsAI(input),
            _eventConverter.ConvertToExtensionsAIChatOptions(input, _defaultOptions),
            cancellationToken
        );
        await StreamAGUIResponseAsync(input, selfGeneratedStream, responseStream, cancellationToken);
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
        // This method now calls the new overload, passing its OWN stream as the source.
        var selfGeneratedStream = this.GetStreamingResponseAsync(
            _eventConverter.ConvertToExtensionsAI(input),
            _eventConverter.ConvertToExtensionsAIChatOptions(input, _defaultOptions),
            cancellationToken
        );
        await StreamToWebSocketAsync(input, selfGeneratedStream, webSocket, cancellationToken);
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
        await using var writer = new StreamWriter(responseStream, leaveOpen: true);
        try
        {
            await writer.WriteAsync($"data: {EventSerialization.SerializeEvent(AGUIEventConverter.LifecycleEvents.CreateRunStarted(input))}\n\n");
            var messageId = Guid.NewGuid().ToString();
            await writer.WriteAsync($"data: {EventSerialization.SerializeEvent(AGUIEventConverter.LifecycleEvents.CreateTextMessageStart(messageId))}\n\n");

            await foreach (var update in orchestratedStream.WithCancellation(cancellationToken))
            {
                var aguiEvents = _eventConverter.ConvertToAGUIEvents(update, messageId, emitBackendToolCalls: true);
                foreach (var aguiEvent in aguiEvents)
                {
                    await writer.WriteAsync($"data: {EventSerialization.SerializeEvent(aguiEvent)}\n\n");
                    await writer.FlushAsync();
                }
            }

            await writer.WriteAsync($"data: {EventSerialization.SerializeEvent(AGUIEventConverter.LifecycleEvents.CreateTextMessageEnd(messageId))}\n\n");
            await writer.WriteAsync($"data: {EventSerialization.SerializeEvent(AGUIEventConverter.LifecycleEvents.CreateRunFinished(input))}\n\n");
        }
        catch (Exception ex)
        {
            var errorEvent = AGUIEventConverter.LifecycleEvents.CreateRunError(input, ex);
            await writer.WriteAsync($"data: {EventSerialization.SerializeEvent(errorEvent)}\n\n");
            throw;
        }
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
        try
        {
            await SendSocketMessage(AGUIEventConverter.LifecycleEvents.CreateRunStarted(input));
            var messageId = Guid.NewGuid().ToString();
            await SendSocketMessage(AGUIEventConverter.LifecycleEvents.CreateTextMessageStart(messageId));

            await foreach (var update in orchestratedStream.WithCancellation(cancellationToken))
            {
                var aguiEvents = _eventConverter.ConvertToAGUIEvents(update, messageId, emitBackendToolCalls: true);
                foreach (var aguiEvent in aguiEvents)
                {
                    await SendSocketMessage(aguiEvent);
                }
            }

            await SendSocketMessage(AGUIEventConverter.LifecycleEvents.CreateTextMessageEnd(messageId));
            await SendSocketMessage(AGUIEventConverter.LifecycleEvents.CreateRunFinished(input));
        }
        catch (Exception ex)
        {
            await SendSocketMessage(AGUIEventConverter.LifecycleEvents.CreateRunError(input, ex));
            throw;
        }

        async Task SendSocketMessage(BaseEvent evt)
        {
            if (webSocket.State != System.Net.WebSockets.WebSocketState.Open) return;
            var json = EventSerialization.SerializeEvent(evt);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Applies contextual function filtering to chat options if configured
    /// </summary>
    /// 
    /*
    private async Task<ChatOptions?> ApplyContextualFiltering(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        if (_contextualSelector == null || options?.Tools == null)
            return options;
            
        var relevantFunctions = await _contextualSelector
            .SelectRelevantFunctionsAsync(messages, cancellationToken);
            
        // Create new options with filtered functions
        return new ChatOptions
        {
            Tools = relevantFunctions.Cast<AITool>().ToList(),
            ToolMode = options.ToolMode,
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            MaxOutputTokens = options.MaxOutputTokens,
            Temperature = options.Temperature,
            TopP = options.TopP,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            ResponseFormat = options.ResponseFormat,
            Seed = options.Seed,
            StopSequences = options.StopSequences,
            ModelId = options.ModelId,
            AdditionalProperties = options.AdditionalProperties
        };
    }

    */
    
    /// <summary>
    /// Prepends system instructions to the message list if configured
    /// </summary>
    private IEnumerable<ChatMessage> PrependSystemInstructions(IEnumerable<ChatMessage> messages)
    {
        if (string.IsNullOrEmpty(_systemInstructions))
            return messages;

        var messagesList = messages.ToList();

        // Check if there's already a system message
        if (messagesList.Any(m => m.Role == ChatRole.System))
            return messagesList;

        // Prepend system instruction
        var systemMessage = new ChatMessage(ChatRole.System, _systemInstructions);
        return new[] { systemMessage }.Concat(messagesList);
    }

    /// <summary>
    /// Merges provided options with default options
    /// </summary>
    private ChatOptions? MergeOptions(ChatOptions? providedOptions)
    {
        if (_defaultOptions == null)
            return providedOptions;

        if (providedOptions == null)
            return _defaultOptions;

        // Merge options - provided options take precedence
        return new ChatOptions
        {
            Tools = providedOptions.Tools ?? _defaultOptions.Tools,
            ToolMode = providedOptions.ToolMode ?? _defaultOptions.ToolMode,
            AllowMultipleToolCalls = providedOptions.AllowMultipleToolCalls ?? _defaultOptions.AllowMultipleToolCalls,
            MaxOutputTokens = providedOptions.MaxOutputTokens ?? _defaultOptions.MaxOutputTokens,
            Temperature = providedOptions.Temperature ?? _defaultOptions.Temperature,
            TopP = providedOptions.TopP ?? _defaultOptions.TopP,
            FrequencyPenalty = providedOptions.FrequencyPenalty ?? _defaultOptions.FrequencyPenalty,
            PresencePenalty = providedOptions.PresencePenalty ?? _defaultOptions.PresencePenalty,
            ResponseFormat = providedOptions.ResponseFormat ?? _defaultOptions.ResponseFormat,
            Seed = providedOptions.Seed ?? _defaultOptions.Seed,
            StopSequences = providedOptions.StopSequences ?? _defaultOptions.StopSequences,
            ModelId = providedOptions.ModelId ?? _defaultOptions.ModelId,
            AdditionalProperties = MergeDictionaries(_defaultOptions.AdditionalProperties, providedOptions.AdditionalProperties)
        };
    }

    /// <summary>
    /// Merges two dictionaries, with the second taking precedence
    /// </summary>
    private static AdditionalPropertiesDictionary? MergeDictionaries(
        AdditionalPropertiesDictionary? first, 
        AdditionalPropertiesDictionary? second)
    {
        if (first == null) return second;
        if (second == null) return first;

        var merged = new AdditionalPropertiesDictionary(first);
        foreach (var kvp in second)
        {
            merged[kvp.Key] = kvp.Value;
        }
        return merged;
    }
    
    /// <summary>
    /// Applies the registered prompt filters pipeline
    /// </summary>
    private async Task<IEnumerable<ChatMessage>> ApplyPromptFilters(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        // Build conversation context from existing messages
        var conversation = new Conversation();
        foreach (var m in messages)
        {
            conversation.AddMessage(m);
        }
        // Create filter context
        var context = new PromptFilterContext(messages, options, conversation, _name, cancellationToken);
        // Transfer additional properties to filter context
        if (options?.AdditionalProperties != null)
        {
            foreach (var kvp in options.AdditionalProperties)
            {
                context.Properties[kvp.Key] = kvp.Value!;
            }
        }
        // Core next delegate returns current messages
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> pipeline = ctx => Task.FromResult(ctx.Messages);
        // Wrap filters in reverse order
        foreach (var filter in _promptFilters.AsEnumerable().Reverse())
        {
            var next = pipeline;
            pipeline = ctx => filter.InvokeAsync(ctx, next);
        }
        return await pipeline(context);
    }

    #endregion

    #region AGUI Event Conversion & Streaming (Complete Integration)
    
    /// <summary>
    /// Converts ChatResponseUpdate to AG-UI events using simple defaults.
    /// This absorbs the AGUIEventConverter functionality into the Agent class.
    /// </summary>
    /// <param name="update">The chat response update to convert</param>
    /// <param name="messageId">Optional message ID (auto-generated if not provided)</param>
    /// <returns>Collection of AG-UI events ready for streaming</returns>
    public IEnumerable<BaseEvent> ConvertToAGUIEvents(ChatResponseUpdate update, string messageId = "")
    {
        if (string.IsNullOrEmpty(messageId))
            messageId = Guid.NewGuid().ToString();
            
        return _eventConverter.ConvertToAGUIEvents(update, messageId);
    }
    
    /// <summary>
    /// Converts ChatResponseUpdate to AG-UI events with advanced options.
    /// Provides escape hatch for power users who need full control.
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
        return _eventConverter.ConvertToAGUIEvents(update, messageId, emitBackendToolCalls);
    }
    
    /// <summary>
    /// <summary>
    /// PHASE 2: Serializes any AG-UI event to JSON using the correct polymorphic serialization.
    /// Implements the functionality previously in EventHelpers.SerializeEvent.
    /// </summary>
    /// <param name="aguiEvent">The AG-UI event to serialize</param>
    /// <returns>JSON string with proper polymorphic serialization</returns>
    public string SerializeEvent(BaseEvent aguiEvent)
    {
        return EventSerialization.SerializeEvent(aguiEvent);
    }
    #endregion

    private string ExtractConversationId(IEnumerable<ChatMessage> messages)
    {
        if (messages.Any())
            return messages.First().MessageId ?? "temp_conv_id";
        return "unknown_conv_id";
    }

    private string? ExtractProjectId(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.TryGetValue("Project", out var projectObj) == true && projectObj is Project project)
            return project.Id;
        return null;
    }

    private static string[] GetCompletedFunctionNames(List<ChatMessage> responseMessages)
    {
        var completedCallIds = responseMessages
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(fr => fr.CallId)
            .ToHashSet();

        return responseMessages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Where(fc => completedCallIds.Contains(fc.CallId))
            .Select(fc => fc.Name)
            .Distinct()
            .ToArray();
    }

    private static string[] GetPlannedFunctionNames(List<FunctionCallContent>? functionCallContents)
    {
        return functionCallContents?.Select(fc => fc.Name).ToArray() ?? Array.Empty<string>();
    }
}