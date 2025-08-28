// HPD-Agent/Agent/AGUIEventHandler.cs

using Microsoft.Extensions.AI;
using System.Threading.Channels;

/// <summary>
/// Handles all AGUI (Agent-GUI) protocol logic including event conversion, streaming, and WebSocket communication.
/// Implements the IAGUIAgent interface and provides SSE and WebSocket streaming capabilities.
/// </summary>
public class AGUIEventHandler : IAGUIAgent
{
    private readonly IChatClient _baseClient;
    private readonly MessageProcessor _messageProcessor;
    private readonly string _agentName;
    private readonly AgentConfig? _config;
    private readonly AGUIEventConverter _eventConverter;
    
    public AGUIEventHandler(
        IChatClient baseClient, 
        MessageProcessor messageProcessor, 
        string agentName, 
        AgentConfig? config = null)
    {
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
            
            // Use MessageProcessor for message preparation
            var conversation = new Conversation();
            messages.ToList().ForEach(m => conversation.AddMessage(m));
            var (effectiveMessages, effectiveOptions) = await _messageProcessor.PrepareMessagesAsync(
                messages, chatOptions, conversation, _agentName, cancellationToken);

            // Generate message ID for this response
            var messageId = Guid.NewGuid().ToString();

            // Emit message start
            await events.WriteAsync(AGUIEventConverter.LifecycleEvents.CreateTextMessageStart(messageId), cancellationToken);

            // Use the base client directly for streaming
            await foreach (var update in _baseClient.GetStreamingResponseAsync(
                effectiveMessages, effectiveOptions, cancellationToken))
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
        // Convert AGUI input to Extensions.AI format and create a self-generated stream
        var messages = _eventConverter.ConvertToExtensionsAI(input);
        var chatOptions = _eventConverter.ConvertToExtensionsAIChatOptions(input, _config?.Provider?.DefaultChatOptions);
        
        var selfGeneratedStream = _baseClient.GetStreamingResponseAsync(
            messages, chatOptions, cancellationToken);
            
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
        // Convert AGUI input to Extensions.AI format and create a self-generated stream
        var messages = _eventConverter.ConvertToExtensionsAI(input);
        var chatOptions = _eventConverter.ConvertToExtensionsAIChatOptions(input, _config?.Provider?.DefaultChatOptions);
        
        var selfGeneratedStream = _baseClient.GetStreamingResponseAsync(
            messages, chatOptions, cancellationToken);
            
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

    /// <summary>
    /// Converts ChatResponseUpdate to AG-UI events using simple defaults.
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