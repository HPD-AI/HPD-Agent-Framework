using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HPD.Agent.Providers;
using CoreAgent = HPD.Agent.Agent;

namespace HPD.Agent.Microsoft;

/// <summary>
/// Microsoft.Agents.AI protocol adapter for HPD-Agent.
/// Wraps the protocol-agnostic core agent and provides Microsoft protocol compatibility.
/// </summary>
public sealed class Agent : AIAgent
{
    private readonly CoreAgent _core;

    /// <summary>
    /// Initializes a new Microsoft protocol agent instance
    /// </summary>
    public Agent(
        AgentConfig config,
        IChatClient baseClient,
        ChatOptions? mergedOptions,
        List<IPromptFilter> promptFilters,
        ScopedFilterManager scopedFilterManager,
        HPD.Agent.ErrorHandling.IProviderErrorHandler providerErrorHandler,
        IProviderRegistry providerRegistry,
        HPD_Agent.Skills.SkillScopingManager? skillScopingManager = null,
        IReadOnlyList<IPermissionFilter>? permissionFilters = null,
        IReadOnlyList<IAiFunctionFilter>? aiFunctionFilters = null,
        IReadOnlyList<IMessageTurnFilter>? messageTurnFilters = null)
    {
        _core = new CoreAgent(
            config,
            baseClient,
            mergedOptions,
            promptFilters,
            scopedFilterManager,
            providerErrorHandler,
            providerRegistry,
            skillScopingManager,
            permissionFilters,
            aiFunctionFilters,
            messageTurnFilters);
    }

    /// <summary>
    /// Agent name (delegated to core)
    /// </summary>
    public override string Name => _core.Name;

    /// <summary>
    /// System instructions (delegated to core)
    /// </summary>
    public string? SystemInstructions => _core.SystemInstructions;

    /// <summary>
    /// Default chat options (delegated to core)
    /// </summary>
    public ChatOptions? DefaultOptions => _core.DefaultOptions;

    /// <summary>
    /// Runs the agent with messages and an explicit thread for state management (non-streaming).
    /// </summary>
    /// <param name="messages">Messages to process</param>
    /// <param name="thread">Thread for conversation state</param>
    /// <param name="options">Optional chat options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Agent run response with final messages</returns>
    public override async Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Create thread if not provided, cast to ConversationThread
        var conversationThread = (thread as ConversationThread) ?? new ConversationThread();

        // Convert AgentRunOptions to ChatOptions (for now, create empty ChatOptions)
        var chatOptions = options != null ? new ChatOptions() : null;

        // Get current messages once
        var currentMessages = await conversationThread.GetMessagesAsync(cancellationToken);

        // Add workflow messages to thread state
        foreach (var msg in messages)
        {
            if (!currentMessages.Contains(msg))
            {
                await conversationThread.AddMessageAsync(msg, cancellationToken);
            }
        }

        // Refresh messages after adding new ones
        currentMessages = await conversationThread.GetMessagesAsync(cancellationToken);

        IReadOnlyList<ChatMessage> finalHistory;
        try
        {
            // Call core agent directly and track history from events
            var turnHistory = new List<ChatMessage>();
            var historyCompletionSource = new TaskCompletionSource<IReadOnlyList<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);

            var internalStream = _core.RunAsync(
                currentMessages,
                chatOptions,
                cancellationToken);

            // Consume stream and track messages (non-streaming path)
            await foreach (var internalEvent in internalStream.WithCancellation(cancellationToken))
            {
                // Track history completion events
                if (internalEvent is InternalAgentTurnFinishedEvent turnFinished)
                {
                    // Get the final conversation state when agent turn completes
                    var finalMessages = await conversationThread.GetMessagesAsync(cancellationToken);
                    historyCompletionSource.TrySetResult(finalMessages.ToList());
                }
            }

            // Get final history from completion source or current thread state
            finalHistory = historyCompletionSource.Task.IsCompletedSuccessfully 
                ? await historyCompletionSource.Task
                : (await conversationThread.GetMessagesAsync(cancellationToken)).ToList();
        }
        finally
        {
            // Context is cleared automatically per function call in Agent.CurrentFunctionContext
        }

        // Refresh messages after turn completes and before checking final history
        currentMessages = await conversationThread.GetMessagesAsync(cancellationToken);

        // Update thread with final messages
        foreach (var msg in finalHistory)
        {
            if (!currentMessages.Contains(msg))
            {
                await conversationThread.AddMessageAsync(msg, cancellationToken);
            }
        }

        // Convert to AgentRunResponse
        var response = new AgentRunResponse();
        foreach (var msg in finalHistory)
        {
            response.Messages.Add(msg);
        }

        return response;
    }

    /// <summary>
    /// Runs the agent with messages and an explicit thread for state management (streaming).
    /// This is the primary method for streaming agent execution.
    /// </summary>
    /// <param name="messages">Messages to process</param>
    /// <param name="thread">Thread for conversation state</param>
    /// <param name="options">Optional chat options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Streaming agent run response updates (extended with HPD-specific event data)</returns>
    public override async IAsyncEnumerable<ExtendedAgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Create thread if not provided, cast to ConversationThread
        var conversationThread = (thread as ConversationThread) ?? new ConversationThread();

        // Convert AgentRunOptions to ChatOptions (for now, create empty ChatOptions)
        var chatOptions = options != null ? new ChatOptions() : null;

        // Get current messages once
        var currentMessages = await conversationThread.GetMessagesAsync(cancellationToken);

        // Add workflow messages to thread state
        foreach (var msg in messages)
        {
            if (!currentMessages.Contains(msg))
            {
                await conversationThread.AddMessageAsync(msg, cancellationToken);
            }
        }

        // Refresh messages after adding new ones
        currentMessages = await conversationThread.GetMessagesAsync(cancellationToken);

        IReadOnlyList<ChatMessage> finalHistory;
        try
        {
            // Call core agent and adapt events to Microsoft protocol
            var internalStream = _core.RunAsync(
                currentMessages,
                chatOptions,
                cancellationToken);

            // Use EventStreamAdapter pattern for protocol conversion
            var agentsAIStream = EventStreamAdapter.ToAgentsAI(internalStream, conversationThread.Id, _core.Name, cancellationToken);

            await foreach (var update in agentsAIStream)
            {
                yield return update;
            }

            // Get final history (would need to track from events in real implementation)
            finalHistory = new List<ChatMessage>();
        }
        finally
        {
            // Context is cleared automatically per function call in Agent.CurrentFunctionContext
        }

        // Update thread with final messages
        var refreshedMessages = await conversationThread.GetMessagesAsync(cancellationToken);
        foreach (var msg in finalHistory)
        {
            if (!refreshedMessages.Contains(msg))
            {
                await conversationThread.AddMessageAsync(msg, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Creates a new conversation thread for this agent.
    /// </summary>
    /// <returns>A new ConversationThread instance</returns>
    public override AgentThread GetNewThread()
    {
        return _core.CreateThread();
    }

    /// <summary>
    /// Creates a new conversation thread (convenience method delegated to core).
    /// </summary>
    /// <returns>A new ConversationThread instance</returns>
    public ConversationThread CreateThread()
    {
        return _core.CreateThread();
    }

    /// <summary>
    /// Sends a filter response to the core agent (for permission handling, etc.)
    /// </summary>
    /// <param name="filterId">The filter ID to respond to</param>
    /// <param name="response">The response event</param>
    public void SendFilterResponse(string filterId, InternalAgentEvent response)
    {
        _core.SendFilterResponse(filterId, response);
    }

    /// <summary>
    /// Deserializes a conversation thread from its JSON representation.
    /// </summary>
    /// <param name="serializedThread">The JSON element containing the serialized thread state.</param>
    /// <param name="jsonSerializerOptions">Optional JSON serialization options.</param>
    /// <returns>A restored ConversationThread instance</returns>
    public override AgentThread DeserializeThread(JsonElement serializedThread, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var snapshot = JsonSerializer.Deserialize<ConversationThreadSnapshot>(serializedThread, jsonSerializerOptions);
        if (snapshot == null)
        {
            throw new JsonException("Failed to deserialize ConversationThreadSnapshot from JSON.");
        }
        return ConversationThread.Deserialize(snapshot);
    }
}

/// <summary>
/// Adapts protocol-agnostic internal agent events to Microsoft.Agents.AI protocol format.
/// </summary>
internal static class EventStreamAdapter
{
    /// <summary>
    /// Adapts internal events to Microsoft.Agents.AI protocol (ExtendedAgentRunResponseUpdate).
    /// Converts internal events to the Agents.AI protocol format, preserving all HPD-specific event data.
    /// Returns ExtendedAgentRunResponseUpdate which includes turn boundaries, permissions, filters, etc.
    /// </summary>
    public static async IAsyncEnumerable<ExtendedAgentRunResponseUpdate> ToAgentsAI(
        IAsyncEnumerable<InternalAgentEvent> internalStream,
        string threadId,
        string agentName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var internalEvent in internalStream.WithCancellation(cancellationToken))
        {
            ExtendedAgentRunResponseUpdate? update = internalEvent switch
            {
                // Text content
                InternalTextDeltaEvent text => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(text.Text)],
                    CreatedAt = DateTimeOffset.UtcNow,
                    MessageId = text.MessageId,
                    OriginalInternalEvent = text
                },

                // Reasoning content (for o1, DeepSeek-R1, etc.)
                InternalReasoningDeltaEvent reasoning => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    Role = ChatRole.Assistant,
                    Contents = [new TextReasoningContent(reasoning.Text)],
                    CreatedAt = DateTimeOffset.UtcNow,
                    MessageId = reasoning.MessageId,
                    OriginalInternalEvent = reasoning
                },

                // Tool call start
                InternalToolCallStartEvent toolCall => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent(toolCall.CallId, toolCall.Name)],
                    CreatedAt = DateTimeOffset.UtcNow,
                    MessageId = toolCall.MessageId,
                    OriginalInternalEvent = toolCall
                },

                // Tool call arguments
                InternalToolCallArgsEvent toolArgs => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ToolData = new ToolCallData
                    {
                        CallId = toolArgs.CallId,
                        ArgsJson = toolArgs.ArgsJson
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalToolCallArgsEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = toolArgs
                },

                // Tool call end
                InternalToolCallEndEvent toolEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ToolData = new ToolCallData
                    {
                        CallId = toolEnd.CallId,
                        IsToolEnd = true
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalToolCallEndEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = toolEnd
                },

                // Tool call result
                InternalToolCallResultEvent toolResult => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    Role = ChatRole.Tool,
                    Contents = [new FunctionResultContent(toolResult.CallId, toolResult.Result)],
                    CreatedAt = DateTimeOffset.UtcNow,
                    OriginalInternalEvent = toolResult
                },

                // Message turn started
                InternalMessageTurnStartedEvent msgTurnStart => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    TurnBoundary = new TurnBoundaryData
                    {
                        Type = TurnBoundaryType.MessageTurnStart,
                        MessageTurnId = msgTurnStart.MessageTurnId,
                        ConversationId = msgTurnStart.ConversationId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalMessageTurnStartedEvent),
                        MessageTurnId = msgTurnStart.MessageTurnId,
                        ConversationId = msgTurnStart.ConversationId,
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = msgTurnStart
                },

                // Message turn finished
                InternalMessageTurnFinishedEvent msgTurnEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    TurnBoundary = new TurnBoundaryData
                    {
                        Type = TurnBoundaryType.MessageTurnEnd,
                        MessageTurnId = msgTurnEnd.MessageTurnId,
                        ConversationId = msgTurnEnd.ConversationId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalMessageTurnFinishedEvent),
                        MessageTurnId = msgTurnEnd.MessageTurnId,
                        ConversationId = msgTurnEnd.ConversationId,
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = msgTurnEnd
                },

                // Message turn error
                InternalMessageTurnErrorEvent msgTurnError => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ErrorData = new ErrorEventData
                    {
                        Message = msgTurnError.Message,
                        Exception = msgTurnError.Exception,
                        ExceptionType = msgTurnError.Exception?.GetType().Name,
                        StackTrace = msgTurnError.Exception?.StackTrace
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalMessageTurnErrorEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = msgTurnError
                },

                // Agent turn started
                InternalAgentTurnStartedEvent agentTurnStart => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    TurnBoundary = new TurnBoundaryData
                    {
                        Type = TurnBoundaryType.AgentTurnStart,
                        Iteration = agentTurnStart.Iteration
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalAgentTurnStartedEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = agentTurnStart
                },

                // Agent turn finished
                InternalAgentTurnFinishedEvent agentTurnEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    TurnBoundary = new TurnBoundaryData
                    {
                        Type = TurnBoundaryType.AgentTurnEnd,
                        Iteration = agentTurnEnd.Iteration
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalAgentTurnFinishedEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = agentTurnEnd
                },

                // Text message start
                InternalTextMessageStartEvent textStart => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.TextMessageStart,
                        MessageId = textStart.MessageId,
                        Role = textStart.Role
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalTextMessageStartEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = textStart
                },

                // Text message end
                InternalTextMessageEndEvent textEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.TextMessageEnd,
                        MessageId = textEnd.MessageId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalTextMessageEndEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = textEnd
                },

                // Reasoning start
                InternalReasoningStartEvent reasoningStart => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.ReasoningStart,
                        MessageId = reasoningStart.MessageId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalReasoningStartEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = reasoningStart
                },

                // Reasoning message start
                InternalReasoningMessageStartEvent reasoningMsgStart => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.ReasoningMessageStart,
                        MessageId = reasoningMsgStart.MessageId,
                        Role = reasoningMsgStart.Role
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalReasoningMessageStartEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = reasoningMsgStart
                },

                // Reasoning message end
                InternalReasoningMessageEndEvent reasoningMsgEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.ReasoningMessageEnd,
                        MessageId = reasoningMsgEnd.MessageId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalReasoningMessageEndEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = reasoningMsgEnd
                },

                // Reasoning end
                InternalReasoningEndEvent reasoningEnd => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    MessageBoundary = new MessageBoundaryData
                    {
                        Type = MessageBoundaryType.ReasoningEnd,
                        MessageId = reasoningEnd.MessageId
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalReasoningEndEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = reasoningEnd
                },

                // Permission request
                InternalPermissionRequestEvent permReq => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    PermissionData = new PermissionEventData
                    {
                        Type = PermissionEventType.Request,
                        PermissionId = permReq.PermissionId,
                        FunctionName = permReq.FunctionName,
                        Description = permReq.Description,
                        CallId = permReq.CallId,
                        Arguments = permReq.Arguments
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalPermissionRequestEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = permReq
                },

                // Permission response
                InternalPermissionResponseEvent permResp => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    PermissionData = new PermissionEventData
                    {
                        Type = PermissionEventType.Response,
                        PermissionId = permResp.PermissionId,
                        Approved = permResp.Approved,
                        Reason = permResp.Reason,
                        Choice = permResp.Choice
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalPermissionResponseEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = permResp
                },

                // Permission approved
                InternalPermissionApprovedEvent permApproved => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    PermissionData = new PermissionEventData
                    {
                        Type = PermissionEventType.Approved,
                        PermissionId = permApproved.PermissionId,
                        Approved = true
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalPermissionApprovedEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = permApproved
                },

                // Permission denied
                InternalPermissionDeniedEvent permDenied => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    PermissionData = new PermissionEventData
                    {
                        Type = PermissionEventType.Denied,
                        PermissionId = permDenied.PermissionId,
                        Approved = false,
                        Reason = permDenied.Reason
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalPermissionDeniedEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = permDenied
                },

                // Continuation request
                InternalContinuationRequestEvent contReq => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ContinuationData = new ContinuationEventData
                    {
                        Type = ContinuationEventType.Request,
                        ContinuationId = contReq.ContinuationId,
                        CurrentIteration = contReq.CurrentIteration,
                        MaxIterations = contReq.MaxIterations
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalContinuationRequestEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = contReq
                },

                // Continuation response
                InternalContinuationResponseEvent contResp => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ContinuationData = new ContinuationEventData
                    {
                        Type = ContinuationEventType.Response,
                        ContinuationId = contResp.ContinuationId,
                        Approved = contResp.Approved,
                        ExtensionAmount = contResp.ExtensionAmount
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalContinuationResponseEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = contResp
                },

                // Clarification request
                InternalClarificationRequestEvent clarReq => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ClarificationData = new ClarificationEventData
                    {
                        Type = ClarificationEventType.Request,
                        RequestId = clarReq.RequestId,
                        AgentName = clarReq.AgentName,
                        Question = clarReq.Question,
                        Options = clarReq.Options
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalClarificationRequestEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = clarReq
                },

                // Clarification response
                InternalClarificationResponseEvent clarResp => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    ClarificationData = new ClarificationEventData
                    {
                        Type = ClarificationEventType.Response,
                        RequestId = clarResp.RequestId,
                        Question = clarResp.Question,
                        Answer = clarResp.Answer
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalClarificationResponseEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = clarResp
                },

                // Filter progress
                InternalFilterProgressEvent filterProgress => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    FilterData = new FilterEventData
                    {
                        Type = FilterEventType.Progress,
                        SourceName = filterProgress.SourceName,
                        ProgressMessage = filterProgress.Message,
                        PercentComplete = filterProgress.PercentComplete
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalFilterProgressEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = filterProgress
                },

                // Filter error
                InternalFilterErrorEvent filterError => new ExtendedAgentRunResponseUpdate
                {
                    AgentId = threadId,
                    AuthorName = agentName,
                    FilterData = new FilterEventData
                    {
                        Type = FilterEventType.Error,
                        SourceName = filterError.SourceName,
                        ErrorMessage = filterError.ErrorMessage,
                        Exception = filterError.Exception
                    },
                    EventMetadata = new EventMetadata
                    {
                        EventType = nameof(InternalFilterErrorEvent),
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    OriginalInternalEvent = filterError
                },

                // Unknown event types - return null to filter out
                _ => null
            };

            if (update != null)
            {
                yield return update;
            }
        }
    }
}

#region Extended Event Model Classes

/// <summary>
/// Extended Agent Run Response Update with HPD-specific event data
/// </summary>
public class ExtendedAgentRunResponseUpdate : AgentRunResponseUpdate
{
    /// <summary>
    /// Metadata about the event itself (type, conversation ID, turn ID)
    /// </summary>
    public EventMetadata? EventMetadata { get; set; }

    /// <summary>
    /// Turn boundary information (start/end of agent turns and message turns)
    /// </summary>
    public TurnBoundaryData? TurnBoundary { get; set; }

    /// <summary>
    /// Message boundary information (start/end of text/reasoning messages)
    /// </summary>
    public MessageBoundaryData? MessageBoundary { get; set; }

    /// <summary>
    /// Tool call details (arguments JSON, completion markers)
    /// </summary>
    public ToolCallData? ToolData { get; set; }

    /// <summary>
    /// Permission-related event data (requests, approvals, denials)
    /// </summary>
    public PermissionEventData? PermissionData { get; set; }

    /// <summary>
    /// Continuation-related event data (iteration limit requests)
    /// </summary>
    public ContinuationEventData? ContinuationData { get; set; }

    /// <summary>
    /// Clarification-related event data (requests and responses)
    /// </summary>
    public ClarificationEventData? ClarificationData { get; set; }

    /// <summary>
    /// Filter-related event data (progress, errors, custom events)
    /// </summary>
    public FilterEventData? FilterData { get; set; }

    /// <summary>
    /// Error information (from InternalMessageTurnErrorEvent)
    /// </summary>
    public ErrorEventData? ErrorData { get; set; }

    /// <summary>
    /// The original internal event that generated this update (for debugging/diagnostics)
    /// </summary>
    [JsonIgnore]
    public InternalAgentEvent? OriginalInternalEvent { get; set; }

    /// <summary>
    /// Helper property to check if this update represents a turn boundary
    /// </summary>
    [JsonIgnore]
    public bool IsTurnBoundary => TurnBoundary != null;

    /// <summary>
    /// Helper property to check if this update represents a message boundary
    /// </summary>
    [JsonIgnore]
    public bool IsMessageBoundary => MessageBoundary != null;

    /// <summary>
    /// Helper property to check if this update contains permission data
    /// </summary>
    [JsonIgnore]
    public bool IsPermissionEvent => PermissionData != null;

    /// <summary>
    /// Helper property to check if this update contains filter data
    /// </summary>
    [JsonIgnore]
    public bool IsFilterEvent => FilterData != null;

    /// <summary>
    /// Helper property to check if this update contains error data
    /// </summary>
    [JsonIgnore]
    public bool IsErrorEvent => ErrorData != null;
}

/// <summary>
/// Metadata about the event itself
/// </summary>
public class EventMetadata
{
    /// <summary>
    /// The type of internal event that generated this update
    /// </summary>
    public string? EventType { get; set; }

    /// <summary>
    /// The conversation ID (for message turn events)
    /// </summary>
    public string? ConversationId { get; set; }

    /// <summary>
    /// The message turn ID (for message turn events)
    /// </summary>
    public string? MessageTurnId { get; set; }

    /// <summary>
    /// Timestamp when the event occurred
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Turn boundary information
/// </summary>
public class TurnBoundaryData
{
    /// <summary>
    /// Type of turn boundary (MessageTurnStart, MessageTurnEnd, AgentTurnStart, AgentTurnEnd)
    /// </summary>
    public TurnBoundaryType Type { get; set; }

    /// <summary>
    /// The iteration number (for agent turn events)
    /// </summary>
    public int? Iteration { get; set; }

    /// <summary>
    /// The message turn ID (for message turn events)
    /// </summary>
    public string? MessageTurnId { get; set; }

    /// <summary>
    /// The conversation ID (for message turn events)
    /// </summary>
    public string? ConversationId { get; set; }
}

/// <summary>
/// Types of turn boundaries
/// </summary>
public enum TurnBoundaryType
{
    MessageTurnStart,
    MessageTurnEnd,
    AgentTurnStart,
    AgentTurnEnd
}

/// <summary>
/// Message boundary information
/// </summary>
public class MessageBoundaryData
{
    /// <summary>
    /// Type of message boundary (TextStart, TextEnd, ReasoningStart, ReasoningEnd, etc.)
    /// </summary>
    public MessageBoundaryType Type { get; set; }

    /// <summary>
    /// The message ID
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>
    /// The role of the message (for start events)
    /// </summary>
    public string? Role { get; set; }
}

/// <summary>
/// Types of message boundaries
/// </summary>
public enum MessageBoundaryType
{
    TextMessageStart,
    TextMessageEnd,
    ReasoningStart,
    ReasoningMessageStart,
    ReasoningMessageEnd,
    ReasoningEnd
}

/// <summary>
/// Tool call details
/// </summary>
public class ToolCallData
{
    /// <summary>
    /// The tool call ID
    /// </summary>
    public string? CallId { get; set; }

    /// <summary>
    /// The tool arguments as JSON (from InternalToolCallArgsEvent)
    /// </summary>
    public string? ArgsJson { get; set; }

    /// <summary>
    /// Whether this represents a tool call end event
    /// </summary>
    public bool IsToolEnd { get; set; }
}

/// <summary>
/// Permission event data
/// </summary>
public class PermissionEventData
{
    /// <summary>
    /// Type of permission event
    /// </summary>
    public PermissionEventType Type { get; set; }

    /// <summary>
    /// The permission ID
    /// </summary>
    public string? PermissionId { get; set; }

    /// <summary>
    /// The function name requiring permission
    /// </summary>
    public string? FunctionName { get; set; }

    /// <summary>
    /// Description of what the permission is for
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The tool call ID
    /// </summary>
    public string? CallId { get; set; }

    /// <summary>
    /// The function arguments
    /// </summary>
    public IDictionary<string, object?>? Arguments { get; set; }

    /// <summary>
    /// Whether permission was approved (for response/approved/denied events)
    /// </summary>
    public bool? Approved { get; set; }

    /// <summary>
    /// Reason for approval/denial
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// The permission choice (Ask, Allow, Deny)
    /// </summary>
    public PermissionChoice? Choice { get; set; }
}

/// <summary>
/// Types of permission events
/// </summary>
public enum PermissionEventType
{
    Request,
    Response,
    Approved,
    Denied
}

/// <summary>
/// Clarification event data for UI handlers
/// </summary>
public class ClarificationEventData
{
    /// <summary>
    /// Type of clarification event
    /// </summary>
    public ClarificationEventType Type { get; set; }

    /// <summary>
    /// The unique request ID
    /// </summary>
    public string? RequestId { get; set; }

    /// <summary>
    /// The name of the agent asking for clarification
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// The question being asked
    /// </summary>
    public string? Question { get; set; }

    /// <summary>
    /// Optional list of suggested answers/options
    /// </summary>
    public string[]? Options { get; set; }

    /// <summary>
    /// The user's answer (for response events)
    /// </summary>
    public string? Answer { get; set; }
}

/// <summary>
/// Types of clarification events
/// </summary>
public enum ClarificationEventType
{
    Request,
    Response
}

/// <summary>
/// Continuation event data
/// </summary>
public class ContinuationEventData
{
    /// <summary>
    /// Type of continuation event
    /// </summary>
    public ContinuationEventType Type { get; set; }

    /// <summary>
    /// The continuation ID
    /// </summary>
    public string? ContinuationId { get; set; }

    /// <summary>
    /// Current iteration number
    /// </summary>
    public int? CurrentIteration { get; set; }

    /// <summary>
    /// Maximum iterations allowed
    /// </summary>
    public int? MaxIterations { get; set; }

    /// <summary>
    /// Whether continuation was approved
    /// </summary>
    public bool? Approved { get; set; }

    /// <summary>
    /// How many additional iterations were granted
    /// </summary>
    public int? ExtensionAmount { get; set; }
}

/// <summary>
/// Types of continuation events
/// </summary>
public enum ContinuationEventType
{
    Request,
    Response
}

/// <summary>
/// Filter event data
/// </summary>
public class FilterEventData
{
    /// <summary>
    /// Type of filter event
    /// </summary>
    public FilterEventType Type { get; set; }

    /// <summary>
    /// The filter name
    /// </summary>
    public string? SourceName { get; set; }

    /// <summary>
    /// Progress message (for progress events)
    /// </summary>
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// Percent complete (0-100, for progress events)
    /// </summary>
    public int? PercentComplete { get; set; }

    /// <summary>
    /// Error message (for error events)
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Exception details (for error events)
    /// </summary>
    [JsonIgnore]
    public Exception? Exception { get; set; }

    /// <summary>
    /// Custom event type (for custom events)
    /// </summary>
    public string? CustomEventType { get; set; }

    /// <summary>
    /// Custom event data (for custom events)
    /// </summary>
    public IDictionary<string, object?>? CustomData { get; set; }
}

/// <summary>
/// Types of filter events
/// </summary>
public enum FilterEventType
{
    Progress,
    Error,
    Custom
}

/// <summary>
/// Error event data
/// </summary>
public class ErrorEventData
{
    /// <summary>
    /// Error message
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Exception details
    /// </summary>
    [JsonIgnore]
    public Exception? Exception { get; set; }

    /// <summary>
    /// Exception type name
    /// </summary>
    public string? ExceptionType { get; set; }

    /// <summary>
    /// Exception stack trace
    /// </summary>
    public string? StackTrace { get; set; }
}

#endregion
