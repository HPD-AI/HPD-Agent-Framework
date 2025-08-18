using Microsoft.Extensions.AI;
using System.Threading.Channels;
using Microsoft.KernelMemory;

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
    private readonly ContextualFunctionSelector? _contextualSelector;
    private readonly Dictionary<string, object> _capabilities = new();
    private readonly List<IPromptFilter> _promptFilters;
    // Memory management
    private IKernelMemory? _memory;
    private AgentMemoryBuilder? _memoryBuilder;
    // Function calling configuration
    private readonly int _maxFunctionCalls;
    // Metadata Tracking
    public bool LastOperationHadFunctionCalls { get; private set; }
    public List<string> LastOperationFunctionCalls { get; private set; } = new();
    public int LastOperationFunctionCallCount { get; private set; }

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
        ContextualFunctionSelector? contextualSelector,
        int maxFunctionCalls = 10)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _defaultOptions = defaultOptions;
        _systemInstructions = systemInstructions;
        _scopedFilterManager = scopedFilterManager ?? throw new ArgumentNullException(nameof(scopedFilterManager));
        _aiFunctionFilters = new List<IAiFunctionFilter>(); // Will use scoped filters instead
        _promptFilters = promptFilters?.ToList() ?? new List<IPromptFilter>();
        _contextualSelector = contextualSelector;
        _maxFunctionCalls = maxFunctionCalls;
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
    public ContextualFunctionSelector? ContextualSelector => _contextualSelector;

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

    #region Memory Management

    /// <summary>
    /// Gets or creates the memory instance for this agent.
    /// Returns null if no memory builder has been explicitly configured.
    /// </summary>
    /// <returns>The kernel memory instance, or null if not configured</returns>
    public IKernelMemory? GetOrCreateMemory()
    {
        if (_memory != null) return _memory;
        
        // Only create memory if explicitly configured via SetMemoryBuilder
        if (_memoryBuilder == null)
        {
            return null; // No memory builder configured - return null instead of creating default
        }
        
        return _memory ??= _memoryBuilder.Build();
    }
    
    /// <summary>
    /// Sets the memory builder for this agent
    /// </summary>
    /// <param name="builder">The memory builder to use</param>
    public void SetMemoryBuilder(AgentMemoryBuilder builder)
    {
        _memoryBuilder = builder ?? throw new ArgumentNullException(nameof(builder));
        _memory = null; // Clear existing memory to force rebuild with new builder
    }

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
        
        // Apply RAG strategy if the capability is available
        effectiveMessages = await ApplyRAGStrategy(effectiveMessages, effectiveOptions, cancellationToken);
        
        // Apply contextual function filtering if configured
        effectiveOptions = await ApplyContextualFiltering(effectiveMessages, effectiveOptions, cancellationToken);
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
        
        // Apply RAG strategy if the capability is available
        effectiveMessages = await ApplyRAGStrategy(effectiveMessages, effectiveOptions, cancellationToken);
        
        // Apply contextual function filtering if configured
        effectiveOptions = await ApplyContextualFiltering(effectiveMessages, effectiveOptions, cancellationToken);

        // Apply prompt filters (including Memory CAG)
        effectiveMessages = await ApplyPromptFilters(effectiveMessages, effectiveOptions, cancellationToken);
        
        // Check if we need to apply AIFunction filters - if so, we need to fall back to non-streaming
        bool needsFilterProcessing = (_aiFunctionFilters.Any() || _scopedFilterManager != null) && 
                                   effectiveOptions?.Tools?.Any() == true;
        
        if (needsFilterProcessing)
        {
            // For function calling, we need to collect the full response first to apply filters
            // This is a limitation of the current filter design
            var fullResponse = await _baseClient.GetResponseAsync(effectiveMessages, effectiveOptions, cancellationToken);
            fullResponse = await ProcessResponseWithFilters(fullResponse, effectiveMessages, effectiveOptions, cancellationToken);
            
            // Convert the full response back to streaming format with proper text content
            // Extract text content from the response and create updates
            var textContent = string.Empty;
            var lastMessage = fullResponse.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (lastMessage != null)
            {
                foreach (var content in lastMessage.Contents)
                {
                    if (content is TextContent textContentItem)
                    {
                        textContent += textContentItem.Text;
                    }
                }
            }
            
            // Create streaming updates that simulate real streaming with proper text content
            if (!string.IsNullOrEmpty(textContent))
            {
                // Split text into chunks to simulate streaming
                const int chunkSize = 10; // Characters per chunk
                for (int i = 0; i < textContent.Length; i += chunkSize)
                {
                    var chunk = textContent.Substring(i, Math.Min(chunkSize, textContent.Length - i));
                    yield return new ChatResponseUpdate
                    {
                        Contents = [new TextContent(chunk)]
                    };
                    
                    // Small delay to simulate real streaming
                    await Task.Delay(50, cancellationToken);
                }
            }
            
            // Emit the final update to indicate completion
            yield return new ChatResponseUpdate
            {
                Contents = [],
                FinishReason = fullResponse.FinishReason
            };
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
        
        // Reset tracking at start of entire operation
        LastOperationHadFunctionCalls = false;
        LastOperationFunctionCalls.Clear();
        LastOperationFunctionCallCount = 0;

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

            // Mark that we had function calls
            LastOperationHadFunctionCalls = true;
            LastOperationFunctionCalls.AddRange(functionCallContents!.Select(fc => fc.Name));
            LastOperationFunctionCallCount = iteration + 1;

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
            
            // Create a temporary conversation for the filter context
            var tempConversation = new Conversation();
            foreach (var msg in messages)
            {
                tempConversation.AddMessage(msg);
            }
            
            var context = new AiFunctionContext(tempConversation, toolCallRequest);
            
            // Build and execute the filter pipeline
            Func<AiFunctionContext, Task> finalInvoke = async (ctx) =>
            {
                var function = FindFunction(ctx.ToolCallRequest.FunctionName, options?.Tools);
                if (function != null)
                {
                    try
                    {
                        var args = new AIFunctionArguments(ctx.ToolCallRequest.Arguments);
                        ctx.Result = await function.InvokeAsync(args, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        ctx.Result = $"Error invoking function: {ex.Message}";
                    }
                }
                else
                {
                    ctx.Result = $"Function '{ctx.ToolCallRequest.FunctionName}' not found";
                }
            };

            var pipeline = finalInvoke;
            
            // Get applicable filters - use scoped filters if available, otherwise use legacy global filters
            IEnumerable<IAiFunctionFilter> applicableFilters;
            if (_scopedFilterManager != null)
            {
                applicableFilters = _scopedFilterManager.GetApplicableFilters(functionCall.Name);
            }
            else
            {
                applicableFilters = _aiFunctionFilters;
            }
            
            // Build filter pipeline by reversing the order
            foreach (var filter in applicableFilters.Reverse())
            {
                var previous = pipeline;
                pipeline = ctx => filter.InvokeAsync(ctx, previous);
            }
            
            await pipeline(context);
            
            // Add function result to the result messages
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
        var channel = System.Threading.Channels.Channel.CreateUnbounded<BaseEvent>();
        
        try
        {
            var agentTask = RunAsync(input, channel.Writer, cancellationToken);

            await foreach (var sseEvent in channel.Reader.ReadAllAsync(cancellationToken))
            {
                var json = EventSerialization.SerializeEvent(sseEvent);
                var sseString = $"data: {json}\n\n";
                var bytes = System.Text.Encoding.UTF8.GetBytes(sseString);
                await responseStream.WriteAsync(bytes, cancellationToken);
                await responseStream.FlushAsync(cancellationToken);

                if (sseEvent is RunFinishedEvent) break;
            }

            await agentTask;
        }
        catch (Exception ex)
        {
            // Send error as SSE event before rethrowing
            var errorEvent = AGUIEventConverter.LifecycleEvents.CreateRunError(input, ex);
            var errorJson = EventSerialization.SerializeEvent(errorEvent);
            var errorSse = $"data: {errorJson}\n\n";
            var errorBytes = System.Text.Encoding.UTF8.GetBytes(errorSse);
            
            try
            {
                await responseStream.WriteAsync(errorBytes, cancellationToken);
                await responseStream.FlushAsync(cancellationToken);
            }
            catch
            {
                // Ignore write errors during error handling
            }
            
            throw;
        }
        finally
        {
            // Only complete the channel if it's not already closed
            // The RunAsync method may have already completed it
            try
            {
                channel.Writer.Complete();
            }
            catch (InvalidOperationException)
            {
                // Channel already closed - this is expected
            }
        }
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
        var channel = System.Threading.Channels.Channel.CreateUnbounded<BaseEvent>();
        
        try
        {
            var agentTask = RunAsync(input, channel.Writer, cancellationToken);

            await foreach (var aguiEvent in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (webSocket.State != System.Net.WebSockets.WebSocketState.Open) break;

                var json = EventSerialization.SerializeEvent(aguiEvent);
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);

                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes), 
                    System.Net.WebSockets.WebSocketMessageType.Text, 
                    true, 
                    cancellationToken);

                if (aguiEvent is RunFinishedEvent) break;
            }

            await agentTask;
        }
        catch (Exception ex)
        {
            // Send error as WebSocket message before closing
            if (webSocket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var errorEvent = AGUIEventConverter.LifecycleEvents.CreateRunError(input, ex);
                var errorJson = EventSerialization.SerializeEvent(errorEvent);
                var errorBytes = System.Text.Encoding.UTF8.GetBytes(errorJson);
                
                try
                {
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(errorBytes),
                        System.Net.WebSockets.WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                }
                catch
                {
                    // Ignore write errors during error handling
                }
            }
            
            throw;
        }
        finally
        {
            // Only complete the channel if it's not already closed
            try
            {
                channel.Writer.Complete();
            }
            catch (InvalidOperationException)
            {
                // Channel already closed - this is expected
            }
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Applies contextual function filtering to chat options if configured
    /// </summary>
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

    /// <summary>
    /// Applies the RAG strategy by invoking the RAGMemoryCapability if it's available.
    /// </summary>
    private async Task<IEnumerable<ChatMessage>> ApplyRAGStrategy(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        // Check if the RAG capability is attached to this agent
        var ragCapability = GetCapability<RAGMemoryCapability>("RAG");
        if (ragCapability == null)
        {
            return messages; // RAG is not configured for this agent, continue normally.
        }

        // Try to get the SharedRAGContext first (new Context Provider pattern)
        if (options?.AdditionalProperties?.TryGetValue("SharedRAGContext", out var sharedContextObj) == true &&
            sharedContextObj is SharedRAGContext sharedContext)
        {
            // NEW PATTERN: Agent assembles its own scoped context using shared resources
            var scopedContext = AssembleScopedRAGContext(sharedContext);
            return await ragCapability.ApplyRetrievalStrategyAsync(messages, scopedContext, cancellationToken);
        }


        return messages; // No context was provided, so nothing to do.
    }

    /// <summary>
    /// NEW: Agent assembles its own scoped RAG context by combining its memory with shared resources
    /// This eliminates context leakage by ensuring agents only see their own specialized memory
    /// </summary>
    private RAGContext AssembleScopedRAGContext(SharedRAGContext sharedContext)
    {
        // Create agent-specific memories dictionary with ONLY this agent's memory
        var agentMemories = new Dictionary<string, IKernelMemory?>();
        
        try
        {
            // Each agent fetches ONLY its own memory - no cross-contamination
            agentMemories[this.Name] = this.GetOrCreateMemory();
        }
        catch
        {
            // Agent may not have memory configured
            agentMemories[this.Name] = null;
        }

        // Combine agent's memory with shared resources from the conversation
        return new RAGContext
        {
            AgentMemories = agentMemories, // SCOPED: Only this agent's memory
            ConversationMemory = sharedContext.ConversationMemory, // SHARED: From conversation
            ProjectMemory = sharedContext.ProjectMemory, // SHARED: From project (if any)
            Configuration = sharedContext.Configuration
        };
    }

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
}