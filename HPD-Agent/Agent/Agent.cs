using Microsoft.Extensions.AI;
using System.Threading.Channels;
using Microsoft.KernelMemory;
using HPD_Agent.MemoryRAG;

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
    // Metadata Tracking
    public bool LastOperationHadFunctionCalls { get; private set; }
    public List<string> LastOperationFunctionCalls { get; private set; } = new();

    /// <summary>
    /// Initializes a new Agent instance
    /// </summary>
    public Agent(IChatClient baseClient, string name, ChatOptions? defaultOptions = null, string? systemInstructions = null, IEnumerable<IAiFunctionFilter>? AIFunctionFilters = null)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _defaultOptions = defaultOptions;
        _systemInstructions = systemInstructions;
        _aiFunctionFilters = AIFunctionFilters?.ToList() ?? new List<IAiFunctionFilter>();
        _eventConverter = new AGUIEventConverter();
        _promptFilters = new List<IPromptFilter>();
    }

    /// <summary>
    /// Initializes a new Agent instance with scoped filter manager
    /// </summary>
    public Agent(IChatClient baseClient, string name, ChatOptions? defaultOptions, string? systemInstructions, ScopedFilterManager scopedFilterManager)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _defaultOptions = defaultOptions;
        _systemInstructions = systemInstructions;
        _scopedFilterManager = scopedFilterManager ?? throw new ArgumentNullException(nameof(scopedFilterManager));
        _aiFunctionFilters = new List<IAiFunctionFilter>(); // Will use scoped filters instead
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
        ContextualFunctionSelector? contextualSelector)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _defaultOptions = defaultOptions;
        _systemInstructions = systemInstructions;
        _scopedFilterManager = scopedFilterManager ?? throw new ArgumentNullException(nameof(scopedFilterManager));
        _aiFunctionFilters = new List<IAiFunctionFilter>(); // Will use scoped filters instead
        _promptFilters = promptFilters?.ToList() ?? new List<IPromptFilter>();
        _contextualSelector = contextualSelector;
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
    /// Gets or creates the memory instance for this agent
    /// </summary>
    /// <returns>The kernel memory instance</returns>
    public IKernelMemory GetOrCreateMemory()
    {
        if (_memory != null) return _memory;
        
        if (_memoryBuilder == null)
        {
            // Create default memory builder if none provided
            _memoryBuilder = new AgentMemoryBuilder(_name);
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
        
        // For streaming, we need to collect the full response first to apply filters
        // This is a limitation of the current filter design
        var fullResponse = await _baseClient.GetResponseAsync(effectiveMessages, effectiveOptions, cancellationToken);
        
        // Apply AIFuncton filters if there are any function calls
        if ((_aiFunctionFilters.Any() || _scopedFilterManager != null))
        {
            fullResponse = await ProcessResponseWithFilters(fullResponse, effectiveMessages, effectiveOptions, cancellationToken);
        }
        
        // Convert the full response back to streaming format
        // Emit each update from the combined response
        foreach (var update in fullResponse.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    /// <summary>
    /// Process response with AIFuncton filters when function calls are present
    /// </summary>
    private async Task<ChatResponse> ProcessResponseWithFilters(
        ChatResponse response, 
        IEnumerable<ChatMessage> messages, 
        ChatOptions? options, 
        CancellationToken cancellationToken)
    {
    // Reset tracking at start
    LastOperationHadFunctionCalls = false;
    LastOperationFunctionCalls.Clear();
    var messagesList = messages.ToList();
        var responseCopy = response;
        
        // Check if response contains function calls
        var lastMessage = response.Messages.LastOrDefault();
        if (lastMessage == null) return response;
        
        var functionCalls = lastMessage.Contents.OfType<FunctionCallContent>().ToList();
    if (!functionCalls.Any()) return response;
    // Add tracking
    LastOperationHadFunctionCalls = true;
    LastOperationFunctionCalls.AddRange(functionCalls.Select(fc => fc.Name));
        
        // Process each function call through the filter pipeline
        var modifiedMessages = messagesList.ToList();
        modifiedMessages.AddRange(response.Messages); // Add the assistant's response with function calls
        
        foreach (var functionCall in functionCalls)
        {
            var toolCallRequest = new ToolCallRequest
            {
                FunctionName = functionCall.Name,
                Arguments = functionCall.Arguments ?? new Dictionary<string, object?>()
            };
            
            // Create a temporary conversation for the filter context
            var tempConversation = new Conversation();
            foreach (var msg in modifiedMessages)
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
            
            // Add function result to messages
            // Create function result content using CallId and pipeline result
            var functionResult = new FunctionResultContent(functionCall.CallId, context.Result);
            // Wrap in AIContent list for ChatMessage
            var functionMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { functionResult });
            modifiedMessages.Add(functionMessage);
        }
        
        // If we added function results, get a new response from the assistant
        if (modifiedMessages.Count > messagesList.Count + response.Messages.Count())
        {
            return await _baseClient.GetResponseAsync(modifiedMessages, options, cancellationToken);
        }
        
        return response;
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

        // Try to get the RAGContext that was assembled by the Conversation
        RAGContext? ragContext = null;
        if (options?.AdditionalProperties?.TryGetValue("RAGContext", out var contextObj) == true)
        {
            ragContext = contextObj as RAGContext;
        }

        if (ragContext == null)
        {
            return messages; // No context was provided, so nothing to do.
        }

        // Use the capability to apply the retrieval strategy (this is the "Push" part)
        return await ragCapability.ApplyRetrievalStrategyAsync(messages, ragContext, cancellationToken);
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
}