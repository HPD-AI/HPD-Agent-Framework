using Microsoft.Extensions.AI;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
    private readonly AgentTurn _agentTurn;
    private readonly ToolScheduler _toolScheduler;
    private readonly AGUIEventHandler _aguiEventHandler;
    private readonly CapabilityManager _capabilityManager;
    private readonly ContinuationPermissionManager? _continuationPermissionManager;

    /// <summary>
    /// Agent configuration object containing all settings
    /// </summary>
    public AgentConfig? Config { get; private set; }


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
        _agentTurn = new AgentTurn(_baseClient);
        _toolScheduler = new ToolScheduler(_functionCallProcessor);
        _aguiEventHandler = new AGUIEventHandler(this, _baseClient, _messageProcessor, _name, config);
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
        // Use the unified streaming approach and collect all updates
        var allUpdates = new List<ChatResponseUpdate>();
        
        var turnResult = await ExecuteStreamingTurnAsync(messages, options, cancellationToken);
        
        // Consume the entire stream
        await foreach (var update in turnResult.ResponseStream.WithCancellation(cancellationToken))
        {
            allUpdates.Add(update);
        }
        
        // Wait for final history to ensure turn is complete
        await turnResult.FinalHistory;
        
        // Construct and return the final response
        return ConstructChatResponseFromUpdates(allUpdates);
    }

    /// <summary>
    /// Streams native AG-UI BaseEvent objects directly from the agent's core loop
    /// </summary>
    public IAsyncEnumerable<BaseEvent> StreamEventsAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var turnHistory = new List<ChatMessage>();
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>();
        
        // Prepare messages using MessageProcessor
        var conversation = new Conversation(this);
        messages.ToList().ForEach(m => conversation.AddMessage(m));
        
        return PrepareAndStreamEventsAsync(messages, options, turnHistory, historyCompletionSource, cancellationToken);
    }
    
    private async IAsyncEnumerable<BaseEvent> PrepareAndStreamEventsAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        List<ChatMessage> turnHistory,
        TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletionSource,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var conversation = new Conversation(this);
        messages.ToList().ForEach(m => conversation.AddMessage(m));
        
        var (effectiveMessages, effectiveOptions) = await _messageProcessor.PrepareMessagesAsync(
            messages, options, conversation, _name, cancellationToken);
        
        await foreach (var baseEvent in RunAgenticLoopCore(effectiveMessages, effectiveOptions, turnHistory, historyCompletionSource, cancellationToken))
        {
            yield return baseEvent;
        }
    }

    /// <summary>
    /// Executes a streaming turn for the agent, returning both the stream and final history
    /// </summary>
    public async Task<StreamingTurnResult> ExecuteStreamingTurnAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Create a TaskCompletionSource for the final history
        var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>();
        
        // Create the streaming enumerable
        var responseStream = RunAgenticLoopAsync(messages, options, historyCompletionSource, cancellationToken);
        
        // Return the result containing both stream and history task
        return new StreamingTurnResult(responseStream, historyCompletionSource.Task);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Simply delegate to ExecuteStreamingTurnAsync and return the stream part
        var result = await ExecuteStreamingTurnAsync(messages, options, cancellationToken);
        await foreach (var update in result.ResponseStream.WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Core agentic loop that handles streaming with tool execution
    /// </summary>
    private async IAsyncEnumerable<ChatResponseUpdate> RunAgenticLoopAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletionSource,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turnHistory = new List<ChatMessage>();
        
        // Convert BaseEvent stream to ChatResponseUpdate stream for IChatClient compatibility
        await foreach (var baseEvent in RunAgenticLoopCore(messages, options, turnHistory, historyCompletionSource, cancellationToken))
        {
            // Only convert events that map to the chat protocol
            switch (baseEvent)
            {
                case TextMessageContentEvent textEvent:
                    yield return new ChatResponseUpdate
                    {
                        Contents = [new TextContent(textEvent.Delta)]
                    };
                    break;
                    
                // StepStartedEvent is for UI observability only - ignore in chat adapter
                case StepStartedEvent:
                    break;
                    
                // ToolCallStartEvent is for UI observability only - ignore in chat adapter  
                case ToolCallStartEvent:
                    break;
                    
                case ToolCallEndEvent:
                    // ToolCallEndEvent is for UI notification only
                    break;
            }
        }
    }
    
    private async IAsyncEnumerable<BaseEvent> RunAgenticLoopCore(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        List<ChatMessage> turnHistory,
        TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletionSource,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Generate a message ID for this turn
        var messageId = Guid.NewGuid().ToString();
        
        // Collect all response updates to build final history
        var responseUpdates = new List<ChatResponseUpdate>();
        
        // Prepare messages using MessageProcessor
        var conversation = new Conversation(this);
        messages.ToList().ForEach(m => conversation.AddMessage(m));
        
        var (effectiveMessages, effectiveOptions) = await _messageProcessor.PrepareMessagesAsync(
            messages, options, conversation, _name, cancellationToken);
        
        var currentMessages = effectiveMessages.ToList();
        
        // Main agentic loop
        for (int iteration = 0; iteration < _maxFunctionCalls; iteration++)
        {
            var toolRequests = new List<FunctionCallContent>();
            var assistantContents = new List<AIContent>();
            bool streamFinished = false;
            
            // Run turn and collect events
            await foreach (var update in _agentTurn.RunAsync(currentMessages, effectiveOptions, cancellationToken))
            {
                // Store update for building final history
                responseUpdates.Add(update);
                
                // Process contents and emit appropriate BaseEvent objects
                if (update.Contents != null)
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                        {
                            // Emit step started event for reasoning (visible to UI, NOT saved to history)
                            yield return new StepStartedEvent
                            {
                                StepId = Guid.NewGuid().ToString(),
                                StepName = "Reasoning",
                                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                Type = "STEP_STARTED"
                            };
                            
                            // Emit the reasoning text as content (for UI visibility)
                            yield return new TextMessageContentEvent 
                            { 
                                MessageId = messageId, 
                                Delta = reasoning.Text,
                                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                Type = "TEXT_MESSAGE_CONTENT"
                            };
                            
                            // CRITICAL: Do NOT add reasoning to assistantContents (not saved to history)
                        }
                        else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                        {
                            // Regular text content - add to history
                            assistantContents.Add(textContent);
                            
                            // Emit text content event
                            yield return new TextMessageContentEvent 
                            { 
                                MessageId = messageId, 
                                Delta = textContent.Text,
                                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                Type = "TEXT_MESSAGE_CONTENT"
                            };
                        }
                        else if (content is FunctionCallContent functionCall)
                        {
                            toolRequests.Add(functionCall);
                            assistantContents.Add(functionCall);
                        }
                    }
                }
                
                // Check for stream completion
                if (update.FinishReason != null)
                {
                    streamFinished = true;
                }
            }
            
            // If there are tool requests, execute them
            if (toolRequests.Count > 0)
            {
                // Create assistant message with tool calls
                var assistantMessage = new ChatMessage(ChatRole.Assistant, assistantContents);
                currentMessages.Add(assistantMessage);
                turnHistory.Add(assistantMessage);
                
                // Emit tool call start events
                foreach (var toolRequest in toolRequests)
                {
                    yield return new ToolCallStartEvent 
                    { 
                        ToolCallId = toolRequest.CallId, 
                        ToolCallName = toolRequest.Name,
                        ParentMessageId = messageId,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Type = "TOOL_CALL_START"
                    };
                }
                
                // Execute tools
                var toolResultMessage = await _toolScheduler.ExecuteToolsAsync(
                    currentMessages, toolRequests, effectiveOptions, cancellationToken);
                
                // Add tool results to history
                currentMessages.Add(toolResultMessage);
                turnHistory.Add(toolResultMessage);
                
                // Emit tool call end events
                foreach (var content in toolResultMessage.Contents)
                {
                    if (content is FunctionResultContent result)
                    {
                        yield return new ToolCallEndEvent 
                        { 
                            ToolCallId = result.CallId,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Type = "TOOL_CALL_END"
                        };
                    }
                }
                
                // Update response history for final assembly
                var toolUpdate = new ChatResponseUpdate
                {
                    Contents = toolResultMessage.Contents.ToList()
                };
                responseUpdates.Add(toolUpdate);
                
                // Update options for next iteration to allow the model to choose not to call tools
                effectiveOptions = effectiveOptions == null
                    ? new ChatOptions { ToolMode = ChatToolMode.Auto }
                    : new ChatOptions
                    {
                        Tools = effectiveOptions.Tools,
                        ToolMode = ChatToolMode.Auto,
                        AllowMultipleToolCalls = effectiveOptions.AllowMultipleToolCalls,
                        MaxOutputTokens = effectiveOptions.MaxOutputTokens,
                        Temperature = effectiveOptions.Temperature,
                        TopP = effectiveOptions.TopP,
                        FrequencyPenalty = effectiveOptions.FrequencyPenalty,
                        PresencePenalty = effectiveOptions.PresencePenalty,
                        ResponseFormat = effectiveOptions.ResponseFormat,
                        Seed = effectiveOptions.Seed,
                        StopSequences = effectiveOptions.StopSequences,
                        ModelId = effectiveOptions.ModelId,
                        AdditionalProperties = effectiveOptions.AdditionalProperties
                    };
                
                // Continue to next iteration
            }
            else if (streamFinished)
            {
                // No tools called and stream finished - we're done
                if (assistantContents.Any())
                {
                    var assistantMessage = new ChatMessage(ChatRole.Assistant, assistantContents);
                    turnHistory.Add(assistantMessage);
                }
                break;
            }
        }
        
        // Build the complete history including the final assistant message
        if (responseUpdates.Any())
        {
            var finalResponse = ConstructChatResponseFromUpdates(responseUpdates);
            var finalAssistantMessage = finalResponse.Messages.First();
            
            // Only add if we don't already have this assistant message
            // (in case it was already added during tool execution)
            if (!turnHistory.Any() || turnHistory.Last().Role != ChatRole.Assistant || 
                !turnHistory.Last().Contents.SequenceEqual(finalAssistantMessage.Contents))
            {
                turnHistory.Add(finalAssistantMessage);
            }
        }
        
        // Set the final complete history
        historyCompletionSource.SetResult(turnHistory);
    }

    /// <summary>
    /// Constructs a final ChatResponse from collected streaming updates
    /// </summary>
    private static ChatResponse ConstructChatResponseFromUpdates(List<ChatResponseUpdate> updates)
    {
        // Collect all content from the updates
        var allContents = new List<AIContent>();
        ChatFinishReason? finishReason = null;
        string? modelId = null;
        string? responseId = null;
        DateTimeOffset? createdAt = null;
        
        foreach (var update in updates)
        {
            if (update.Contents != null)
            {
                allContents.AddRange(update.Contents);
            }
            
            if (update.FinishReason != null)
                finishReason = update.FinishReason;
                
            if (update.ModelId != null)
                modelId = update.ModelId;
                
            if (update.ResponseId != null)
                responseId = update.ResponseId;
                
            if (update.CreatedAt != null)
                createdAt = update.CreatedAt;
        }
        
        // Create a ChatMessage from the collected content
        var chatMessage = new ChatMessage(ChatRole.Assistant, allContents)
        {
            MessageId = responseId
        };
        
        return new ChatResponse(chatMessage)
        {
            FinishReason = finishReason,
            ModelId = modelId,
            CreatedAt = createdAt
        };
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



    #endregion



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


#region AGUI Event Handling

/// <summary>
/// Handles all AGUI (Agent-GUI) protocol logic including event conversion, streaming, and WebSocket communication.
/// Implements the IAGUIAgent interface and provides SSE and WebSocket streaming capabilities.
/// </summary>
public class AGUIEventHandler : IAGUIAgent
{
    private readonly Agent _agent;
    private readonly IChatClient _baseClient;
    private readonly MessageProcessor _messageProcessor;
    private readonly string _agentName;
    private readonly AgentConfig? _config;
    private readonly AGUIEventConverter _eventConverter;

    public AGUIEventHandler(
        Agent agent,
        IChatClient baseClient,
        MessageProcessor messageProcessor,
        string agentName,
        AgentConfig? config = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _messageProcessor = messageProcessor ?? throw new ArgumentNullException(nameof(messageProcessor));
        _agentName = agentName ?? throw new ArgumentNullException(nameof(agentName));
        _config = config;
        _eventConverter = new AGUIEventConverter();
    }

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
            var chatOptions = _eventConverter.ConvertToExtensionsAIChatOptions(input, _config?.Provider?.DefaultChatOptions);

            // Generate message ID for this response
            var messageId = Guid.NewGuid().ToString();

            // Emit message start
            await events.WriteAsync(AGUIEventConverter.LifecycleEvents.CreateTextMessageStart(messageId), cancellationToken);

            // Use the agent's native BaseEvent stream directly
            await foreach (var baseEvent in _agent.StreamEventsAsync(messages, chatOptions, cancellationToken))
            {
                // Write native events directly to the channel
                await events.WriteAsync(baseEvent, cancellationToken);
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
    /// Serializes any AG-UI event to JSON using the correct polymorphic serialization.
    /// Implements the functionality previously in EventHelpers.SerializeEvent.
    /// </summary>
    /// <param name="aguiEvent">The AG-UI event to serialize</param>
    /// <returns>JSON string with proper polymorphic serialization</returns>
    public string SerializeEvent(BaseEvent aguiEvent)
    {
        return EventSerialization.SerializeEvent(aguiEvent);
    }
}

#endregion

#region Capability Manager

/// <summary>
/// Manages the agent's extended capabilities, such as Audio and MCP.
/// </summary>
public class CapabilityManager
{
    private readonly Dictionary<string, object> _capabilities = new();

    /// <summary>
    /// Gets a capability by name and type.
    /// </summary>
    /// <typeparam name="T">The type of capability to retrieve.</typeparam>
    /// <param name="name">The name of the capability.</param>
    /// <returns>The capability instance if found, otherwise null.</returns>
    public T? GetCapability<T>(string name) where T : class
        => _capabilities.TryGetValue(name, out var capability) ? capability as T : null;

    /// <summary>
    /// Adds or updates a capability.
    /// </summary>
    /// <param name="name">The name of the capability.</param>
    /// <param name="capability">The capability instance.</param>
    public void AddCapability(string name, object capability)
        => _capabilities[name] = capability ?? throw new ArgumentNullException(nameof(capability));

    /// <summary>
    /// Removes a capability by name.
    /// </summary>
    /// <param name="name">The name of the capability to remove.</param>
    /// <returns>True if the capability was removed, false if it wasn't found.</returns>
    public bool RemoveCapability(string name)
        => _capabilities.Remove(name);
}

#endregion

#region 

/// <summary>
/// Handles all function calling logic, including multi-turn execution and filter pipelines.
/// </summary>
public class FunctionCallProcessor
{
    private readonly ScopedFilterManager? _scopedFilterManager;
    private readonly IAiFunctionFilter? _permissionFilter;
    private readonly IReadOnlyList<IAiFunctionFilter> _aiFunctionFilters;
    private readonly ContinuationPermissionManager? _continuationPermissionManager;
    private readonly int _maxFunctionCalls;

    public FunctionCallProcessor(ScopedFilterManager? scopedFilterManager, IAiFunctionFilter? permissionFilter, IReadOnlyList<IAiFunctionFilter>? aiFunctionFilters, ContinuationPermissionManager? continuationPermissionManager, int maxFunctionCalls)
    {
        _scopedFilterManager = scopedFilterManager;
        _permissionFilter = permissionFilter;
        _aiFunctionFilters = aiFunctionFilters ?? new List<IAiFunctionFilter>();
        _continuationPermissionManager = continuationPermissionManager;
        _maxFunctionCalls = maxFunctionCalls;
    }


    /// <summary>
    /// Processes the function calls and returns the messages to add to the conversation
    /// </summary>
    public async Task<IList<ChatMessage>> ProcessFunctionCallsAsync(
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

            // Get scoped filters for this function
            var scopedFilters = _scopedFilterManager?.GetApplicableFilters(functionCall.Name)
                                ?? Enumerable.Empty<IAiFunctionFilter>();

            // Combine scoped filters with general AI function filters
            var allFilters = _aiFunctionFilters.Concat(scopedFilters);

            // Wrap all standard filters first.
            foreach (var filter in allFilters.Reverse())
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

    /// <summary>
    /// Prepares the various chat message lists after a response from the inner client and before invoking functions
    /// </summary>
}

#endregion 

#region MessageProcessor

/// <summary>
/// Handles all pre-processing of chat messages and options before sending to the LLM.
/// </summary>
public class MessageProcessor
{
    private readonly IReadOnlyList<IPromptFilter> _promptFilters;
    private readonly string? _systemInstructions;
    private readonly ChatOptions? _defaultOptions;

    public MessageProcessor(string? systemInstructions, ChatOptions? defaultOptions, IReadOnlyList<IPromptFilter> promptFilters)
    {
        _systemInstructions = systemInstructions;
        _defaultOptions = defaultOptions;
        _promptFilters = promptFilters ?? new List<IPromptFilter>();
    }

    /// <summary>
    /// Gets the system instructions configured for this processor.
    /// </summary>
    public string? SystemInstructions => _systemInstructions;

    /// <summary>
    /// Gets the default chat options configured for this processor.
    /// </summary>
    public ChatOptions? DefaultOptions => _defaultOptions;

    /// <summary>
    /// Prepares the final list of messages and chat options for the LLM call.
    /// </summary>
    public async Task<(IEnumerable<ChatMessage> messages, ChatOptions? options)> PrepareMessagesAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        Conversation conversation,
        string agentName,
        CancellationToken cancellationToken)
    {
        var effectiveMessages = PrependSystemInstructions(messages);
        var effectiveOptions = MergeOptions(options);

        effectiveMessages = await ApplyPromptFiltersAsync(effectiveMessages, effectiveOptions, conversation, agentName, cancellationToken);

        return (effectiveMessages, effectiveOptions);
    }

    /// <summary>
    /// Prepends system instructions to the message list if configured.
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
    /// Merges provided options with default options.
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
    /// Applies the registered prompt filters pipeline.
    /// </summary>
    private async Task<IEnumerable<ChatMessage>> ApplyPromptFiltersAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        Conversation conversation,
        string agentName,
        CancellationToken cancellationToken)
    {
        if (!_promptFilters.Any())
        {
            return messages;
        }

        // Create filter context
        var context = new PromptFilterContext(messages, options, conversation, agentName, cancellationToken);

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

    /// <summary>
    /// Merges two dictionaries, with the second taking precedence.
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
}

#endregion

#region AgentTurn
/// <summary>
/// Manages a single streaming call to the LLM and translates the raw output into TurnEvents
/// </summary>
public class AgentTurn
{
    private readonly IChatClient _baseClient;

    /// <summary>
    /// Initializes a new instance of AgentTurn
    /// </summary>
    /// <param name="baseClient">The underlying chat client to use for LLM calls</param>
    public AgentTurn(IChatClient baseClient)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
    }

    /// <summary>
    /// Runs a single turn with the LLM and yields ChatResponseUpdates representing the response
    /// </summary>
    /// <param name="messages">The conversation history to send to the LLM</param>
    /// <param name="options">Optional chat options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of ChatResponseUpdates representing the LLM's response</returns>
    public async IAsyncEnumerable<ChatResponseUpdate> RunAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in RunAsyncCore(messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> RunAsyncCore(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Get the streaming response from the base client
        var stream = _baseClient.GetStreamingResponseAsync(messages, options, cancellationToken);

        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        Exception? errorToYield = null;

        try
        {
            enumerator = stream.GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errorToYield = ex;
                    break;
                }

                if (!hasNext)
                    break;

                var update = enumerator.Current;

                // Yield the update directly
                yield return update;
            }
        }
        finally
        {
            if (enumerator != null)
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore disposal errors
                }
            }
        }

        // If there was an error, throw it after cleanup
        if (errorToYield != null)
        {
            throw errorToYield;
        }
    }
}

#endregion

#region 

/// <summary>
/// Represents the result of a streaming turn, providing both the stream and the final turn history
/// </summary>
public class StreamingTurnResult
{
    /// <summary>
    /// The stream of response updates that can be consumed by the caller
    /// </summary>
    public IAsyncEnumerable<ChatResponseUpdate> ResponseStream { get; }

    /// <summary>
    /// Task that completes with the final turn history once streaming is done
    /// </summary>
    public Task<IReadOnlyList<ChatMessage>> FinalHistory { get; }

    /// <summary>
    /// Initializes a new instance of StreamingTurnResult
    /// </summary>
    /// <param name="responseStream">The stream of response updates</param>
    /// <param name="finalHistory">Task that provides the final turn history</param>
    public StreamingTurnResult(
        IAsyncEnumerable<ChatResponseUpdate> responseStream,
        Task<IReadOnlyList<ChatMessage>> finalHistory)
    {
        ResponseStream = responseStream ?? throw new ArgumentNullException(nameof(responseStream));
        FinalHistory = finalHistory ?? throw new ArgumentNullException(nameof(finalHistory));
    }
}

#endregion

#region Tool Scheduler

/// <summary>
/// Responsible for executing tools and running the associated IAiFunctionFilter pipeline
/// </summary>
public class ToolScheduler
{
    private readonly FunctionCallProcessor _functionCallProcessor;

    /// <summary>
    /// Initializes a new instance of ToolScheduler
    /// </summary>
    /// <param name="functionCallProcessor">The function call processor to use for tool execution</param>
    public ToolScheduler(FunctionCallProcessor functionCallProcessor)
    {
        _functionCallProcessor = functionCallProcessor ?? throw new ArgumentNullException(nameof(functionCallProcessor));
    }

    /// <summary>
    /// Executes the requested tools in parallel and returns the tool response message
    /// </summary>
    /// <param name="currentHistory">The current conversation history</param>
    /// <param name="toolRequests">The tool call requests to execute</param>
    /// <param name="options">Optional chat options containing tool definitions</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A chat message containing the tool execution results</returns>
    public async Task<ChatMessage> ExecuteToolsAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        // For single tool calls, use sequential execution (no parallelization overhead)
        if (toolRequests.Count <= 1)
        {
            return await ExecuteSequentiallyAsync(currentHistory, toolRequests, options, cancellationToken);
        }

        // For multiple tool calls, execute in parallel for better performance
        return await ExecuteInParallelAsync(currentHistory, toolRequests, options, cancellationToken);
    }

    /// <summary>
    /// Executes tools sequentially (used for single tools or as fallback)
    /// </summary>
    private async Task<ChatMessage> ExecuteSequentiallyAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        // Use the existing FunctionCallProcessor to execute the tools sequentially
        var resultMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(
            currentHistory, options, toolRequests, cancellationToken);

        // Combine all tool results into a single message
        var allContents = new List<AIContent>();
        foreach (var message in resultMessages)
        {
            allContents.AddRange(message.Contents);
        }

        return new ChatMessage(ChatRole.Tool, allContents);
    }

    /// <summary>
    /// Executes tools in parallel for improved performance with multiple independent tools
    /// </summary>
    private async Task<ChatMessage> ExecuteInParallelAsync(
        List<ChatMessage> currentHistory,
        List<FunctionCallContent> toolRequests,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        // Create tasks for each tool execution
        var executionTasks = toolRequests.Select(async toolRequest =>
        {
            try
            {
                // Execute each tool call individually through the processor
                var singleToolList = new List<FunctionCallContent> { toolRequest };
                var resultMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(
                    currentHistory, options, singleToolList, cancellationToken);

                return (Success: true, Messages: resultMessages, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                // Capture any errors for aggregation
                return (Success: false, Messages: new List<ChatMessage>(), Error: ex);
            }
        }).ToArray();

        // Wait for all tasks to complete
        var results = await Task.WhenAll(executionTasks);

        // Aggregate results and handle errors
        var allContents = new List<AIContent>();
        var errors = new List<Exception>();

        foreach (var result in results)
        {
            if (result.Success)
            {
                foreach (var message in result.Messages)
                {
                    allContents.AddRange(message.Contents);
                }
            }
            else if (result.Error != null)
            {
                errors.Add(result.Error);
            }
        }

        // If there were any errors, include them in the response
        if (errors.Count > 0)
        {
            var errorMessage = $"Some tool executions failed: {string.Join("; ", errors.Select(e => e.Message))}";
            allContents.Add(new TextContent($"⚠️ Tool Execution Errors: {errorMessage}"));
        }

        return new ChatMessage(ChatRole.Tool, allContents);
    }
}

#endregion