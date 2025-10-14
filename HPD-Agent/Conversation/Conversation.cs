using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using Microsoft.Agents.AI;

/// <summary>
/// Clean conversation management built on Microsoft.Extensions.AI
/// Coordinates ConversationThread (state) and Agent execution.
/// Implements AIAgent to be compatible with Microsoft.Agents.AI workflows.
/// Multi-agent orchestration is handled by Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder.
/// Similar to Microsoft's pattern where Agent + Thread are composed by user code.
/// </summary>
public class Conversation : AIAgent
{
    private readonly ConversationThread _thread;
    private readonly Agent _agent;

    // OpenTelemetry Activity Source for conversation telemetry
    private static readonly ActivitySource ActivitySource = new("HPD.Conversation");

    /// <summary>
    /// Gets the conversation thread (state container)
    /// Provides access to message history, metadata, and serialization
    /// </summary>
    public ConversationThread Thread => _thread;

    // Convenient pass-through properties to thread
    public new string Id => _thread.Id;  // Use 'new' to hide inherited AIAgent.Id
    public DateTime CreatedAt => _thread.CreatedAt;
    public DateTime LastActivity => _thread.LastActivity;
    public IReadOnlyList<ChatMessage> Messages => _thread.Messages;
    public IReadOnlyDictionary<string, object> Metadata => _thread.Metadata;

    /// <summary>Gets the agent in this conversation.</summary>
    public Agent Agent => _agent;

    /// <summary>Add metadata key/value to this conversation thread.</summary>
    public void AddMetadata(string key, object value) => _thread.AddMetadata(key, value);

    /// <summary>
    /// Extracts and merges chat options from agent run options with conversation context.
    /// Since we're using only the abstractions library, we create new ChatOptions and inject conversation context.
    /// </summary>
    private ChatOptions ExtractAndMergeChatOptions(
        AgentRunOptions? workflowOptions,
        Dictionary<string, object>? conversationContext = null)
    {
        // Create new ChatOptions since we don't have access to ChatClientAgentRunOptions
        var chatOptions = new ChatOptions();

        // Inject conversation context into AdditionalProperties
        if (conversationContext != null)
        {
            chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            foreach (var kvp in conversationContext)
            {
                chatOptions.AdditionalProperties[kvp.Key] = kvp.Value;
            }
        }

        return chatOptions;
    }

    #region AIAgent Implementation

    /// <summary>
    /// Gets the name of this conversation (derived from agent or conversation ID).
    /// </summary>
    public override string? Name => _agent?.Config?.Name ?? $"Conversation-{Id}";

    /// <summary>
    /// Gets the description of this conversation (derived from agent).
    /// </summary>
    public override string? Description => _agent?.Config?.SystemInstructions;

    /// <summary>
    /// Creates a new thread compatible with this agent.
    /// </summary>
    public override AgentThread GetNewThread()
    {
        return new ConversationThread();
    }

    /// <summary>
    /// Deserializes a thread from its JSON representation.
    /// </summary>
    public override AgentThread DeserializeThread(JsonElement serializedThread, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        // Deserialize the JsonElement to ConversationThreadSnapshot using source-generated context
        var snapshot = serializedThread.Deserialize(ConversationJsonContext.Default.ConversationThreadSnapshot);
        if (snapshot == null)
        {
            throw new InvalidOperationException("Failed to deserialize ConversationThreadSnapshot from JsonElement");
        }

        return ConversationThread.Deserialize(snapshot);
    }

    /// <summary>
    /// Service discovery - chains through thread and agent
    /// </summary>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        return base.GetService(serviceType, serviceKey)
            ?? _thread.GetService(serviceType, serviceKey)
            ?? ((IChatClient)_agent).GetService(serviceType, serviceKey);
    }

    /// <summary>
    /// Runs the agent with messages (non-streaming, AIAgent interface).
    /// DIRECT DELEGATION to Agent - no wrapper methods, no duplicate messages.
    /// </summary>
    public override async Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ConversationThread targetThread;
        if (thread != null)
        {
            if (thread is not ConversationThread conversationThread)
            {
                throw new InvalidOperationException(
                    "The provided thread is not compatible with Conversation. " +
                    "Only ConversationThread instances can be used.");
            }
            targetThread = conversationThread;
        }
        else
        {
            targetThread = _thread;
        }

        // Add workflow messages to thread state
        foreach (var msg in messages)
        {
            if (!targetThread.Messages.Contains(msg))
            {
                targetThread.AddMessage(msg);
            }
        }

        // Extract workflow options and inject conversation context
        var conversationContextDict = BuildConversationContext();
        var chatOptions = ExtractAndMergeChatOptions(options, conversationContextDict);

        // Create ConversationExecutionContext for AsyncLocal context
        var executionContext = new ConversationExecutionContext(Id)
        {
            AgentName = _agent.Name
        };

        // Set AsyncLocal context for plugins (e.g., PlanMode) to access
        ConversationContext.Set(executionContext);

        IReadOnlyList<ChatMessage> finalHistory;
        try
        {
            // DIRECT CALL to Agent.ExecuteStreamingTurnAsync - no SendAsync wrapper!
            var streamResult = await _agent.ExecuteStreamingTurnAsync(
                targetThread.Messages,
                chatOptions,
                cancellationToken: cancellationToken);

            // Consume stream (non-streaming path)
            await foreach (var _ in streamResult.EventStream.WithCancellation(cancellationToken))
            {
                // Just consume events
            }

            // Get final history and apply reduction
            finalHistory = await streamResult.FinalHistory;
            var reductionMetadata = await streamResult.ReductionTask;

            if (reductionMetadata != null)
            {
                targetThread.ApplyReduction(reductionMetadata.SummaryMessage, reductionMetadata.MessagesRemovedCount);
            }
        }
        finally
        {
            // Clear context to prevent leaks
            ConversationContext.Clear();
        }

        // Build response from final history
        var response = new ChatResponse(finalHistory.ToList());

        // Update thread with response messages
        foreach (var msg in response.Messages)
        {
            if (!targetThread.Messages.Contains(msg))
            {
                targetThread.AddMessage(msg);
            }
        }

        // Notify thread of new messages if external thread provided
        if (thread != null && thread != _thread)
        {
            await NotifyThreadOfNewMessagesAsync(thread, response.Messages, cancellationToken);
        }

        return new AgentRunResponse(response)
        {
            AgentId = this.Id,
            ResponseId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            Usage = CreateUsageFromResponse(response)
        };
    }

    /// <summary>
    /// Runs the agent with messages (streaming, AIAgent interface).
    /// DIRECT DELEGATION to Agent - no wrapper methods, no duplicate messages.
    /// </summary>
    public override async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ConversationThread targetThread;
        if (thread != null)
        {
            if (thread is not ConversationThread conversationThread)
            {
                throw new InvalidOperationException(
                    "The provided thread is not compatible with Conversation. " +
                    "Only ConversationThread instances can be used.");
            }
            targetThread = conversationThread;
        }
        else
        {
            targetThread = _thread;
        }

        // Add workflow messages to thread state
        foreach (var msg in messages)
        {
            if (!targetThread.Messages.Contains(msg))
            {
                targetThread.AddMessage(msg);
            }
        }

        // Extract workflow options and inject conversation context
        var conversationContextDict = BuildConversationContext();
        var chatOptions = ExtractAndMergeChatOptions(options, conversationContextDict);

        // Create ConversationExecutionContext for AsyncLocal context
        var executionContext = new ConversationExecutionContext(Id)
        {
            AgentName = _agent.Name
        };

        // Set AsyncLocal context for plugins (e.g., PlanMode) to access
        ConversationContext.Set(executionContext);

        IReadOnlyList<ChatMessage> finalHistory;
        try
        {
            // DIRECT CALL to Agent.ExecuteStreamingTurnAsync - no SendStreamingAsync wrapper!
            var streamResult = await _agent.ExecuteStreamingTurnAsync(
                targetThread.Messages,
                chatOptions,
                cancellationToken: cancellationToken);

            // Convert BaseEvent stream to AgentRunResponseUpdate stream
            await foreach (var evt in streamResult.EventStream.WithCancellation(cancellationToken))
            {
                var update = ConvertBaseEventToAgentRunResponseUpdate(evt);
                if (update != null)
                {
                    yield return update;
                }
            }

            // Update thread with final history
            finalHistory = await streamResult.FinalHistory;
            var reductionMetadata = await streamResult.ReductionTask;

            if (reductionMetadata != null)
            {
                targetThread.ApplyReduction(reductionMetadata.SummaryMessage, reductionMetadata.MessagesRemovedCount);
            }
        }
        finally
        {
            // Clear context to prevent leaks
            ConversationContext.Clear();
        }

        foreach (var msg in finalHistory)
        {
            if (!targetThread.Messages.Contains(msg))
            {
                targetThread.AddMessage(msg);
            }
        }

        // Notify thread of new messages if external thread provided
        if (thread != null && thread != _thread)
        {
            await NotifyThreadOfNewMessagesAsync(thread, finalHistory, cancellationToken);
        }
    }

    /// <summary>
    /// Converts BaseEvent to AgentRunResponseUpdate with comprehensive AIContent mapping.
    /// Maps AG-UI protocol events to Microsoft.Extensions.AI content types:
    /// - TextMessageContentEvent → TextContent (assistant response)
    /// - ReasoningMessageContentEvent → TextReasoningContent (model thinking)
    /// - ToolCallStartEvent → FunctionCallContent (tool invocation)
    /// - ToolCallResultEvent → FunctionResultContent (tool result)
    /// - RunErrorEvent → ErrorContent (non-fatal errors)
    /// Metadata events (boundaries, steps, etc.) are filtered out as they don't represent message content.
    /// </summary>
    private AgentRunResponseUpdate? ConvertBaseEventToAgentRunResponseUpdate(BaseEvent evt)
    {
        return evt switch
        {
            // ✅ Text Content - Assistant's actual response
            TextMessageContentEvent textEvent => new AgentRunResponseUpdate
            {
                AgentId = this.Id,
                AuthorName = this.Name,
                Role = ChatRole.Assistant,
                Contents = [new TextContent(textEvent.Delta)],
                CreatedAt = DateTimeOffset.UtcNow,
                MessageId = textEvent.MessageId
            },

            // ✅ Reasoning Content - Model's thinking/reasoning process
            ReasoningMessageContentEvent reasoningEvent => new AgentRunResponseUpdate
            {
                AgentId = this.Id,
                AuthorName = this.Name,
                Role = ChatRole.Assistant,
                Contents = [new TextReasoningContent(reasoningEvent.Delta)],
                CreatedAt = DateTimeOffset.UtcNow,
                MessageId = reasoningEvent.MessageId
            },

            // ✅ Function Call - Tool invocation
            ToolCallStartEvent toolStart => new AgentRunResponseUpdate
            {
                AgentId = this.Id,
                AuthorName = this.Name,
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent(toolStart.ToolCallId, toolStart.ToolCallName)],
                CreatedAt = DateTimeOffset.UtcNow,
                MessageId = toolStart.ParentMessageId
            },

            // ✅ Function Result - Tool execution result
            ToolCallResultEvent toolResult => new AgentRunResponseUpdate
            {
                AgentId = this.Id,
                AuthorName = this.Name,
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent(toolResult.ToolCallId, toolResult.Result)],
                CreatedAt = DateTimeOffset.UtcNow,
                MessageId = Guid.NewGuid().ToString("N")
            },

            // ✅ Error Content - Non-fatal errors during execution
            RunErrorEvent errorEvent => new AgentRunResponseUpdate
            {
                AgentId = this.Id,
                AuthorName = this.Name,
                Role = ChatRole.Assistant,
                Contents = [new ErrorContent(errorEvent.Message)],
                CreatedAt = DateTimeOffset.UtcNow,
                MessageId = Guid.NewGuid().ToString("N")
            },

            // ❌ Filtered Events (Metadata/Boundaries - not message content):
            // - ReasoningStartEvent/EndEvent - reasoning boundaries
            // - TextMessageStartEvent/EndEvent - message boundaries
            // - ReasoningMessageStartEvent/EndEvent - reasoning message boundaries
            // - ToolCallEndEvent/ToolCallArgsEvent - tool call boundaries/streaming
            // - StepStartedEvent/StepFinishedEvent - agent iteration metadata
            // - RunStartedEvent/RunFinishedEvent - run boundaries
            // - StateSnapshotEvent/StateDeltaEvent - state management
            // - CustomEvent/RawEvent - custom protocol extensions
            // - TextMessageChunkEvent/ToolCallChunkEvent - alternate chunking (use delta events instead)
            _ => null
        };
    }

    /// <summary>
    /// Builds conversation context for injection into ChatOptions.
    /// Includes ConversationId and Project if available.
    /// </summary>
    private Dictionary<string, object> BuildConversationContext()
    {
        var context = new Dictionary<string, object>
        {
            ["ConversationId"] = Id
        };

        // Inject project if available in metadata
        if (Metadata.TryGetValue("Project", out var projectObj) && projectObj is Project project)
        {
            context["Project"] = project;
        }

        return context;
    }

    /// <summary>
    /// Creates UsageDetails from ChatResponse.Usage for AgentRunResponse.
    /// </summary>
    private static UsageDetails? CreateUsageFromResponse(ChatResponse response)
    {
        if (response.Usage == null)
            return null;

        return new UsageDetails
        {
            InputTokenCount = response.Usage.InputTokenCount,
            OutputTokenCount = response.Usage.OutputTokenCount,
            TotalTokenCount = response.Usage.TotalTokenCount
        };
    }

    #endregion

    /// <summary>
    /// Creates a conversation with an agent and a new thread
    /// </summary>
    public Conversation(Agent agent)
    {
        _thread = new ConversationThread();
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    /// <summary>
    /// Creates a conversation with an agent and custom thread (for reusing state)
    /// </summary>
    public Conversation(Agent agent, ConversationThread thread)
    {
        _thread = thread ?? throw new ArgumentNullException(nameof(thread));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    /// <summary>
    /// Creates a conversation within a project (stores project in thread metadata)
    /// </summary>
    public Conversation(Project project, Agent agent)
    {
        _thread = new ConversationThread();
        _thread.AddMetadata("Project", project);
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    /// <summary>
    /// Add a single message to the conversation thread
    /// </summary>
    public void AddMessage(ChatMessage message) => _thread.AddMessage(message);

    /// <summary>
    /// Runs the agent with AGUI protocol input (non-streaming, AIAgent-compatible overload).
    /// </summary>
    /// <param name="aguiInput">AGUI protocol input containing thread, messages, tools, and context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Agent run response</returns>
    public async Task<AgentRunResponse> RunAsync(
        RunAgentInput aguiInput,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.turn");
        var startTime = DateTimeOffset.UtcNow;

        activity?.SetTag("conversation.id", Id);
        activity?.SetTag("conversation.input_format", "agui");
        activity?.SetTag("conversation.thread_id", aguiInput.ThreadId);
        activity?.SetTag("conversation.run_id", aguiInput.RunId);

        try
        {
            // Add the new user message from aguiInput to conversation thread
            var newUserMessage = aguiInput.Messages.LastOrDefault(m => m.Role == "user");
            if (newUserMessage != null)
            {
                _thread.AddMessage(new ChatMessage(ChatRole.User, newUserMessage.Content ?? ""));
            }

            // Create new RunAgentInput using server-side _thread as source of truth
            var serverSideInput = new RunAgentInput
            {
                ThreadId = aguiInput.ThreadId,
                RunId = aguiInput.RunId,
                State = aguiInput.State,
                Messages = ConvertThreadToAGUIMessages(_thread.Messages),
                Tools = aguiInput.Tools,
                Context = aguiInput.Context,
                ForwardedProps = aguiInput.ForwardedProps
            };

            // Use agent's AGUI overload with server-side messages
            var streamResult = await _agent.ExecuteStreamingTurnAsync(serverSideInput, cancellationToken);

            // Consume stream (non-streaming path)
            await foreach (var evt in streamResult.EventStream.WithCancellation(cancellationToken))
            {
                // Events consumed but not exposed in non-streaming path
            }

            // Wait for final history and check for reduction
            var finalHistory = await streamResult.FinalHistory;
            var reductionMetadata = await streamResult.ReductionTask;

            // Apply reduction BEFORE adding new messages
            if (reductionMetadata != null)
            {
                _thread.ApplyReduction(reductionMetadata.SummaryMessage, reductionMetadata.MessagesRemovedCount);
            }

            // Build response from final history
            var response = new ChatResponse(finalHistory.ToList());

            // Update conversation thread
            foreach (var msg in response.Messages)
            {
                if (!_thread.Messages.Contains(msg))
                {
                    _thread.AddMessage(msg);
                }
            }

            var duration = DateTimeOffset.UtcNow - startTime;
            activity?.SetTag("conversation.duration_ms", duration.TotalMilliseconds);
            activity?.SetTag("conversation.success", true);

            return new AgentRunResponse(response)
            {
                AgentId = this.Id,
                ResponseId = aguiInput.RunId,
                CreatedAt = DateTimeOffset.UtcNow,
                Usage = CreateUsageFromResponse(response)
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

    /// <summary>
    /// Runs the agent with AGUI protocol input (streaming).
    /// Returns full BaseEvent stream for AGUI frontend compatibility.
    /// Includes ALL events: content, reasoning, tool calls, steps, boundaries, state.
    /// </summary>
    /// <param name="aguiInput">AGUI protocol input containing thread, messages, tools, and context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Full stream of BaseEvent (AGUI protocol events)</returns>
    public async IAsyncEnumerable<BaseEvent> RunStreamingAsync(
        RunAgentInput aguiInput,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.turn");
        var startTime = DateTimeOffset.UtcNow;

        activity?.SetTag("conversation.id", Id);
        activity?.SetTag("conversation.input_format", "agui");
        activity?.SetTag("conversation.thread_id", aguiInput.ThreadId);
        activity?.SetTag("conversation.run_id", aguiInput.RunId);

        // Add the new user message from aguiInput to conversation thread
        var newUserMessage = aguiInput.Messages.LastOrDefault(m => m.Role == "user");
        if (newUserMessage != null)
        {
            _thread.AddMessage(new ChatMessage(ChatRole.User, newUserMessage.Content ?? ""));
        }

        // Create new RunAgentInput using server-side _thread as source of truth
        var serverSideInput = new RunAgentInput
        {
            ThreadId = aguiInput.ThreadId,
            RunId = aguiInput.RunId,
            State = aguiInput.State,
            Messages = ConvertThreadToAGUIMessages(_thread.Messages),
            Tools = aguiInput.Tools,
            Context = aguiInput.Context,
            ForwardedProps = aguiInput.ForwardedProps
        };

        // Use agent's AGUI overload with server-side messages
        var streamResult = await _agent.ExecuteStreamingTurnAsync(serverSideInput, cancellationToken);

        // Stream ALL BaseEvent events (no filtering for AGUI protocol)
        await foreach (var evt in streamResult.EventStream.WithCancellation(cancellationToken))
        {
            yield return evt;
        }

        // Wait for final history and check for reduction
        var finalHistory = await streamResult.FinalHistory;
        var reductionMetadata = await streamResult.ReductionTask;

        // Apply reduction BEFORE adding new messages
        if (reductionMetadata != null)
        {
            _thread.ApplyReduction(reductionMetadata.SummaryMessage, reductionMetadata.MessagesRemovedCount);
        }

        // Update conversation thread
        foreach (var msg in finalHistory)
        {
            if (!_thread.Messages.Contains(msg))
            {
                _thread.AddMessage(msg);
            }
        }

        var duration = DateTimeOffset.UtcNow - startTime;
        activity?.SetTag("conversation.duration_ms", duration.TotalMilliseconds);
        activity?.SetTag("conversation.success", true);
    }

    // TODO: Potential convenience methods to add later (if needed):
    // - public async Task<AgentRunResponse> RunAsync(string message, ChatOptions? options = null)
    //   Simple string overload that creates ChatMessage internally
    // - public async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(string message, ChatOptions? options = null)
    //   Streaming string overload
    // - Extension methods to extract common data from AgentRunResponse:
    //   * response.GetText() - Extract text content
    //   * response.GetDuration() - Calculate duration from timestamps
    //   * response.GetAgent() - Resolve agent from AgentId
    //
    // For now, consumers can use:
    //   await conversation.RunAsync([new ChatMessage(ChatRole.User, "message")])
    //   or call via RunAsync(aguiInput) for AGUI protocol

    #region Conversation Helpers

    /// <summary>
    /// Gets a human-readable display name for this conversation.
    /// Delegates to ConversationThread.GetDisplayName.
    /// </summary>
    /// <param name="maxLength">Maximum length for the display name</param>
    /// <returns>Human-readable conversation name</returns>
    public string GetDisplayName(int maxLength = 30) => _thread.GetDisplayName(maxLength);

    /// <summary>
    /// Converts Extensions.AI ChatMessage collection to AGUI BaseMessage collection.
    /// This ensures server-side conversation thread is the source of truth for AG-UI protocol.
    /// Filters out tool-related messages since AG-UI handles tools via events, not message history.
    /// </summary>
    private static IReadOnlyList<BaseMessage> ConvertThreadToAGUIMessages(IEnumerable<ChatMessage> messages)
    {
        return messages
            .Where(m => !HasToolContent(m)) // Skip messages with tool calls/results
            .Select(AGUIEventConverter.ConvertChatMessageToBaseMessage)
            .ToList();
    }

    /// <summary>
    /// Checks if a message contains tool-related content (FunctionCallContent or FunctionResultContent).
    /// These messages should be excluded from AG-UI message history as tools are handled via events.
    /// </summary>
    private static bool HasToolContent(ChatMessage message)
    {
        return message.Contents.Any(c => c is FunctionCallContent or FunctionResultContent);
    }

    #endregion
}
