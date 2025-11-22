using Microsoft.Extensions.AI;
using HPD.Agent.Internal.Filters;
using Microsoft.Agents.AI;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HPD.Agent.Providers;
using CoreAgent = HPD.Agent.AgentCore;
using MicrosoftThread = HPD.Agent.Microsoft.ConversationThread;

namespace HPD.Agent.Microsoft;

/// <summary>
/// Microsoft.Agents.AI protocol adapter for HPD-Agent.
/// Wraps the protocol-agnostic core agent and provides a clean, optimized API for HPD use cases.
/// </summary>
public sealed class Agent : AIAgent
{
    private readonly CoreAgent _core;
    private readonly Func<AIContextProviderFactoryContext, AIContextProvider>? _contextProviderFactory;

    /// <summary>
    /// Initializes a new Microsoft protocol agent by wrapping an existing core agent.
    /// Internal constructor - use AgentBuilder extension methods to create agents.
    /// </summary>
    /// <param name="coreAgent">The protocol-agnostic core agent to wrap</param>
    /// <param name="contextProviderFactory">Optional factory for creating AIContextProvider instances</param>
    internal Agent(
        CoreAgent coreAgent,
        Func<AIContextProviderFactoryContext, AIContextProvider>? contextProviderFactory = null)
    {
        _core = coreAgent ?? throw new ArgumentNullException(nameof(coreAgent));
        _contextProviderFactory = contextProviderFactory;
    }

    /// <summary>
    /// Initializes a new Microsoft protocol agent instance (legacy constructor).
    /// Internal constructor - use AgentBuilder extension methods to create agents.
    /// </summary>
    internal Agent(
        AgentConfig config,
        IChatClient baseClient,
        ChatOptions? mergedOptions,
        List<IPromptFilter> promptFilters,
        ScopedFilterManager scopedFilterManager,
        ErrorHandling.IProviderErrorHandler providerErrorHandler,
        IReadOnlyList<IPermissionFilter>? permissionFilters = null,
        IReadOnlyList<IAiFunctionFilter>? aiFunctionFilters = null,
        IReadOnlyList<IMessageTurnFilter>? messageTurnFilters = null,
        IReadOnlyList<IIterationFilter>? iterationFilters = null,
        IServiceProvider? serviceProvider = null,
        IEnumerable<IAgentEventObserver>? observers = null,
        Func<AIContextProviderFactoryContext, AIContextProvider>? contextProviderFactory = null)
    {
        _core = new CoreAgent(
            config,
            baseClient,
            mergedOptions,
            promptFilters,
            scopedFilterManager,
            providerErrorHandler,
            permissionFilters,
            aiFunctionFilters,
            messageTurnFilters,
            iterationFilters,
            serviceProvider,
            observers);

        _contextProviderFactory = contextProviderFactory;
    }

    /// <inheritdoc/>
    public override string? Name => _core.Name;

    /// <inheritdoc/>
    public override string? Description => null; // HPD core doesn't have description

    /// <summary>
    /// System instructions (delegated to core)
    /// </summary>
    public string? SystemInstructions => _core.SystemInstructions;

    /// <summary>
    /// Default chat options (delegated to core)
    /// </summary>
    public ChatOptions? DefaultOptions => _core.DefaultOptions;

    /// <inheritdoc/>
    public override async Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Create thread if not provided, cast to Microsoft ConversationThread
        var conversationThread = (thread as MicrosoftThread) ?? new MicrosoftThread();

        // Convert AgentRunOptions to ChatOptions (for now, create empty ChatOptions)
        var chatOptions = options != null ? new ChatOptions() : null;

        IList<ChatMessage>? aiContextMessages = null;
        Exception? invokeException = null;

        // Convert messages to list to ensure it's not null (fix CS8602)
        var messagesList = messages?.ToList() ?? new List<ChatMessage>();

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 1: AIContextProvider Pre-Invocation (Microsoft-specific enrichment)
        // ══════════════════════════════════════════════════════════════════════════
        if (conversationThread.AIContextProvider != null)
        {
            try
            {
                // Provider sees only NEW input messages (matches Microsoft's pattern)
                var invokingContext = new AIContextProvider.InvokingContext(messagesList);
                var aiContext = await conversationThread.AIContextProvider.InvokingAsync(
                    invokingContext,
                    cancellationToken);

                // Merge messages (provider messages BEFORE user input)
                if (aiContext.Messages is { Count: > 0 })
                {
                    aiContextMessages = aiContext.Messages;
                    messagesList = aiContext.Messages.Concat(messagesList).ToList();
                }

                // Merge tools
                if (aiContext.Tools is { Count: > 0 })
                {
                    chatOptions ??= new ChatOptions();
                    chatOptions.Tools ??= new List<AITool>();
                    foreach (var tool in aiContext.Tools)
                        chatOptions.Tools.Add(tool);
                }

                // Merge instructions
                if (!string.IsNullOrWhiteSpace(aiContext.Instructions))
                {
                    chatOptions ??= new ChatOptions();
                    chatOptions.Instructions = string.IsNullOrWhiteSpace(chatOptions.Instructions)
                        ? aiContext.Instructions
                        : $"{chatOptions.Instructions}\n{aiContext.Instructions}";
                }
            }
            catch
            {
                // TODO: Consider emitting error event for observability
                // For now, log and continue (don't fail the turn)
                // Could use: yield return new InternalFilterErrorEvent("AIContextProvider.InvokingAsync", ex.Message, ex);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 2: Checkpoint Loading & Validation (Durable Execution Support)
        // ══════════════════════════════════════════════════════════════════════════

        // Load checkpoint if checkpointer is configured and thread has an ID but no execution state yet
        var config = _core.Config;
        if (config?.Checkpointer != null && conversationThread.ExecutionState == null)
        {
            var loadedThread = await config.Checkpointer.LoadThreadAsync(
                conversationThread.Id, cancellationToken);

            if (loadedThread?.ExecutionState != null)
            {
                // Mutate existing thread instead of replacing it (preserves caller's reference)
                conversationThread.ExecutionState = loadedThread.ExecutionState;

                // Copy other checkpoint state if needed
                if (loadedThread.ConversationId != null)
                    conversationThread.ConversationId = loadedThread.ConversationId;
            }
        }

        // Validate resume semantics
        var hasMessages = messagesList.Any();
        var hasCheckpoint = conversationThread.ExecutionState != null;

        if (hasCheckpoint && hasMessages)
        {
            throw new InvalidOperationException(
                $"Cannot add new messages when resuming mid-execution. " +
                $"Thread '{conversationThread.Id}' is at iteration {conversationThread.ExecutionState?.Iteration ?? 0}.\n\n" +
                $"To resume execution:\n" +
                $"  await agent.RunAsync(Array.Empty<ChatMessage>(), thread);");
        }

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 3: Call core agent (with thread for full context)
        // ══════════════════════════════════════════════════════════════════════════
        // NOTE: We pass NEW messages only, but the core agent will automatically
        // load the thread's message history and merge it with the new messages.
        // This gives the LLM full conversation context while respecting the API pattern.
        IReadOnlyList<ChatMessage> turnMessages;
        try
        {
            // Call core agent with thread support (enables checkpointing + history)
            // Core agent will load thread history and merge with new messages
            var internalStream = _core.RunAsync(
                messagesList,  // NEW messages only (includes provider-enriched messages)
                chatOptions,
                conversationThread.Core,  // Pass the wrapped core thread
                cancellationToken);

            // Track messages built from events
            var turnMessageList = new List<ChatMessage>();
            var currentAssistantContents = new List<AIContent>();
            var currentToolResults = new List<ChatMessage>();

            // Consume stream and build messages from events (non-streaming path)
            await foreach (var internalEvent in internalStream.WithCancellation(cancellationToken))
            {
                // Build messages from content events
                switch (internalEvent)
                {
                    case InternalTextDeltaEvent textDelta:
                        currentAssistantContents.Add(new TextContent(textDelta.Text));
                        break;

                    case InternalReasoningEvent reasoning when reasoning.Phase == ReasoningPhase.Delta:
                        currentAssistantContents.Add(new TextReasoningContent(reasoning.Text ?? ""));
                        break;

                    case InternalToolCallStartEvent toolStart:
                        currentAssistantContents.Add(new FunctionCallContent(toolStart.CallId, toolStart.Name));
                        break;

                    case InternalToolCallResultEvent toolResult:
                        currentToolResults.Add(new ChatMessage(ChatRole.Tool, 
                            [new FunctionResultContent(toolResult.CallId, toolResult.Result)]));
                        break;

                    case InternalTextMessageEndEvent:
                    case InternalReasoningEvent { Phase: ReasoningPhase.MessageEnd }:
                    case InternalToolCallEndEvent:
                        // Message completed - add to turn messages if we have content
                        if (currentAssistantContents.Count > 0)
                        {
                            // Combine text and reasoning contents into a single text message
                            var combinedText = string.Join("", currentAssistantContents
                                .Where(c => c is TextContent || c is TextReasoningContent)
                                .Select(c => c is TextContent tc ? tc.Text : ((TextReasoningContent)c).Text));

                            var functionCalls = currentAssistantContents.OfType<FunctionCallContent>().ToList();

                            if (!string.IsNullOrEmpty(combinedText) && functionCalls.Count > 0)
                            {
                                // Both text and tool calls
                                var contents = new List<AIContent> { new TextContent(combinedText) };
                                contents.AddRange(functionCalls);
                                turnMessageList.Add(new ChatMessage(ChatRole.Assistant, contents));
                            }
                            else if (!string.IsNullOrEmpty(combinedText))
                            {
                                // Text only
                                turnMessageList.Add(new ChatMessage(ChatRole.Assistant, combinedText));
                            }
                            else if (functionCalls.Count > 0)
                            {
                                // Tool calls only
                                turnMessageList.Add(new ChatMessage(ChatRole.Assistant, functionCalls.Cast<AIContent>().ToList()));
                            }

                            currentAssistantContents.Clear();
                        }

                        // Add tool results if any
                        if (currentToolResults.Count > 0)
                        {
                            turnMessageList.AddRange(currentToolResults);
                            currentToolResults.Clear();
                        }
                        break;
                }
            }

            // Finalize any remaining content
            if (currentAssistantContents.Count > 0)
            {
                var combinedText = string.Join("", currentAssistantContents
                    .Where(c => c is TextContent || c is TextReasoningContent)
                    .Select(c => c is TextContent tc ? tc.Text : ((TextReasoningContent)c).Text));

                var functionCalls = currentAssistantContents.OfType<FunctionCallContent>().ToList();

                if (!string.IsNullOrEmpty(combinedText) || functionCalls.Count > 0)
                {
                    if (!string.IsNullOrEmpty(combinedText) && functionCalls.Count > 0)
                    {
                        var contents = new List<AIContent> { new TextContent(combinedText) };
                        contents.AddRange(functionCalls);
                        turnMessageList.Add(new ChatMessage(ChatRole.Assistant, contents));
                    }
                    else if (!string.IsNullOrEmpty(combinedText))
                    {
                        turnMessageList.Add(new ChatMessage(ChatRole.Assistant, combinedText));
                    }
                    else
                    {
                        turnMessageList.Add(new ChatMessage(ChatRole.Assistant, functionCalls.Cast<AIContent>().ToList()));
                    }
                }
            }

            if (currentToolResults.Count > 0)
            {
                turnMessageList.AddRange(currentToolResults);
            }

            // Note: Core agent now handles ALL message persistence (including assistant/tool messages)
            // We only need to track turnMessages for the response
            turnMessages = turnMessageList;
        }
        catch (Exception ex)
        {
            invokeException = ex;
            turnMessages = Array.Empty<ChatMessage>();

            // Call InvokedAsync with error before re-throwing
            if (conversationThread.AIContextProvider != null)
            {
                try
                {
                    await conversationThread.AIContextProvider.InvokedAsync(
                        new AIContextProvider.InvokedContext(messagesList, aiContextMessages)
                        {
                            ResponseMessages = null,
                            InvokeException = invokeException
                        },
                        cancellationToken);
                }
                catch
                {
                    // Ignore errors in error handler
                }
            }

            throw;
        }
        finally
        {
            // Context is cleared automatically per function call in Agent.CurrentFunctionContext
        }

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 4: AIContextProvider Post-Invocation (Microsoft-specific learning)
        // ══════════════════════════════════════════════════════════════════════════
        if (conversationThread.AIContextProvider != null && invokeException == null)
        {
            try
            {
                // Get just the new response messages (assistant messages from this turn)
                var responseMessages = turnMessages
                    .Where(m => m.Role == ChatRole.Assistant)
                    .ToList();

                await conversationThread.AIContextProvider.InvokedAsync(
                    new AIContextProvider.InvokedContext(messagesList, aiContextMessages)
                    {
                        ResponseMessages = responseMessages,
                        InvokeException = null
                    },
                    cancellationToken);
            }
            catch
            {
                // Ignore errors in post-processing
            }
        }

        // Convert to AgentRunResponse (only return messages from THIS turn)
        var response = new AgentRunResponse
        {
            AgentId = this.Id,
            Messages = turnMessages.ToList()
        };

        return response;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Create thread if not provided, cast to Microsoft ConversationThread
        var conversationThread = (thread as MicrosoftThread) ?? new MicrosoftThread();

        // Convert AgentRunOptions to ChatOptions (for now, create empty ChatOptions)
        var chatOptions = options != null ? new ChatOptions() : null;

        IList<ChatMessage>? aiContextMessages = null;
        Exception? invokeException = null;

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 1: AIContextProvider Pre-Invocation (Microsoft-specific enrichment)
        // ══════════════════════════════════════════════════════════════════════════
        if (conversationThread.AIContextProvider != null)
        {
            try
            {
                // Provider sees only NEW input messages (matches Microsoft's pattern)
                var invokingContext = new AIContextProvider.InvokingContext(messages);
                var aiContext = await conversationThread.AIContextProvider.InvokingAsync(
                    invokingContext,
                    cancellationToken);

                // Merge messages (provider messages BEFORE user input)
                if (aiContext.Messages is { Count: > 0 })
                {
                    aiContextMessages = aiContext.Messages;
                    messages = aiContext.Messages.Concat(messages).ToList();
                }

                // Merge tools
                if (aiContext.Tools is { Count: > 0 })
                {
                    chatOptions ??= new ChatOptions();
                    chatOptions.Tools ??= new List<AITool>();
                    foreach (var tool in aiContext.Tools)
                        chatOptions.Tools.Add(tool);
                }

                // Merge instructions
                if (!string.IsNullOrWhiteSpace(aiContext.Instructions))
                {
                    chatOptions ??= new ChatOptions();
                    chatOptions.Instructions = string.IsNullOrWhiteSpace(chatOptions.Instructions)
                        ? aiContext.Instructions
                        : $"{chatOptions.Instructions}\n{aiContext.Instructions}";
                }
            }
            catch
            {
                // TODO: Consider emitting error event for observability
                // For now, log and continue (don't fail the turn)
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 2: Call core agent and stream events (pass only NEW messages)
        // ══════════════════════════════════════════════════════════════════════════
        // NOTE: We pass messages (NEW messages only), not ALL messages from thread.
        // The core agent will automatically load thread history and merge it with new messages.
        // This gives the LLM full conversation context while respecting the API pattern.

        // Track the message count BEFORE the turn so we can identify new messages
        var currentMessages = await conversationThread.GetMessagesAsync(cancellationToken);
        var messageCountBeforeTurn = currentMessages.Count;

        IReadOnlyList<ChatMessage> turnMessages;
        Exception? streamingException = null;

        // Call core agent and adapt events to Microsoft protocol
        // Core agent will load thread history and merge with new messages
        var internalStream = _core.RunAsync(
            messages,  // NEW messages only (includes provider-enriched messages)
            chatOptions,
            conversationThread.Core,  // Pass the wrapped core thread
            cancellationToken);

        // Use EventStreamAdapter pattern for protocol conversion
        var agentsAIStream = EventStreamAdapter.ToAgentsAI(internalStream, this.Id, _core.Name, cancellationToken);

        // Stream events directly (core agent handles all message persistence)
        await foreach (var update in agentsAIStream.WithCancellation(cancellationToken))
        {
            yield return update;
        }

        // Get turn messages after streaming completes (only NEW messages from this turn)
        var allMessages = await conversationThread.GetMessagesAsync(cancellationToken);
        turnMessages = allMessages.Skip(messageCountBeforeTurn).ToList();

        // Handle any exceptions that occurred during streaming
        if (streamingException != null)
        {
            invokeException = streamingException;

            // Call InvokedAsync with error
            if (conversationThread.AIContextProvider != null)
            {
                try
                {
                    await conversationThread.AIContextProvider.InvokedAsync(
                        new AIContextProvider.InvokedContext(messages, aiContextMessages)
                        {
                            ResponseMessages = null,
                            InvokeException = invokeException
                        },
                        cancellationToken);
                }
                catch
                {
                    // Ignore errors in error handler
                }
            }

            throw streamingException;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 4: AIContextProvider Post-Invocation (Microsoft-specific learning)
        // ══════════════════════════════════════════════════════════════════════════
        if (conversationThread.AIContextProvider != null && invokeException == null)
        {
            try
            {
                // Get just the new response messages (assistant messages from this turn)
                var responseMessages = turnMessages
                    .Where(m => m.Role == ChatRole.Assistant)
                    .ToList();

                await conversationThread.AIContextProvider.InvokedAsync(
                    new AIContextProvider.InvokedContext(messages, aiContextMessages)
                    {
                        ResponseMessages = responseMessages,
                        InvokeException = null
                    },
                    cancellationToken);
            }
            catch
            {
                // Ignore errors in post-processing
            }
        }
    }

    /// <inheritdoc/>
    public override AgentThread GetNewThread()
    {
        var coreThread = _core.CreateThread();
        var thread = new MicrosoftThread(coreThread);

        // Apply AIContextProvider via factory (if configured)
        if (_contextProviderFactory != null)
        {
            thread.AIContextProvider = _contextProviderFactory(new AIContextProviderFactoryContext
            {
                SerializedState = default,  // New thread, no state to restore
                JsonSerializerOptions = null
            });
        }

        return thread;
    }

    /// <summary>
    /// Creates a new conversation thread (convenience method).
    /// If an AIContextProvider factory is configured, creates and attaches a provider instance.
    /// </summary>
    /// <returns>A new Microsoft ConversationThread instance</returns>
    public MicrosoftThread CreateThread()
    {
        var coreThread = _core.CreateThread();
        var thread = new MicrosoftThread(coreThread);

        // Apply AIContextProvider via factory (if configured)
        if (_contextProviderFactory != null)
        {
            thread.AIContextProvider = _contextProviderFactory(new AIContextProviderFactoryContext
            {
                SerializedState = default,  // New thread, no state to restore
                JsonSerializerOptions = null
            });
        }

        return thread;
    }

    /// <inheritdoc/>
    public override AgentThread DeserializeThread(JsonElement serializedThread, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        return MicrosoftThread.Deserialize(
            serializedThread,
            jsonSerializerOptions,
            contextProviderFactory: _contextProviderFactory != null
                ? (state, opts) => _contextProviderFactory(new AIContextProviderFactoryContext
                {
                    SerializedState = state,
                    JsonSerializerOptions = opts
                })
                : null);
    }
}

/// <summary>
/// Adapts protocol-agnostic internal agent events to Microsoft.Agents.AI protocol format.
/// </summary>
internal static class EventStreamAdapter
{
    /// <summary>
    /// Adapts internal events to Microsoft.Agents.AI protocol (AgentRunResponseUpdate).
    /// Converts internal events to the Agents.AI protocol format.
    /// </summary>
    public static async IAsyncEnumerable<AgentRunResponseUpdate> ToAgentsAI(
        IAsyncEnumerable<InternalAgentEvent> internalStream,
        string agentId,
        string agentName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var internalEvent in internalStream.WithCancellation(cancellationToken))
        {
            AgentRunResponseUpdate? update = internalEvent switch
            {
                // Text content
                InternalTextDeltaEvent text => new AgentRunResponseUpdate(new ChatResponseUpdate
                {
                    AuthorName = agentName,
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(text.Text)],
                    CreatedAt = DateTimeOffset.UtcNow
                })
                {
                    AgentId = agentId,
                    MessageId = text.MessageId
                },

                // Reasoning content (for o1, DeepSeek-R1, etc.)
                InternalReasoningEvent reasoning when reasoning.Phase == ReasoningPhase.Delta => new AgentRunResponseUpdate(new ChatResponseUpdate
                {
                    AuthorName = agentName,
                    Role = ChatRole.Assistant,
                    Contents = [new TextReasoningContent(reasoning.Text ?? "")],
                    CreatedAt = DateTimeOffset.UtcNow
                })
                {
                    AgentId = agentId,
                    MessageId = reasoning.MessageId
                },

                // Tool call start
                InternalToolCallStartEvent toolCall => new AgentRunResponseUpdate(new ChatResponseUpdate
                {
                    AuthorName = agentName,
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent(toolCall.CallId, toolCall.Name)],
                    CreatedAt = DateTimeOffset.UtcNow
                })
                {
                    AgentId = agentId,
                    MessageId = toolCall.MessageId
                },

                // Tool call result
                InternalToolCallResultEvent toolResult => new AgentRunResponseUpdate(new ChatResponseUpdate
                {
                    AuthorName = agentName,
                    Role = ChatRole.Tool,
                    Contents = [new FunctionResultContent(toolResult.CallId, toolResult.Result)],
                    CreatedAt = DateTimeOffset.UtcNow
                })
                {
                    AgentId = agentId
                },

                // Filter out events that don't map to standard AgentRunResponseUpdate
                // (turn boundaries, permissions, etc. are HPD-specific and not part of Microsoft's API)
                _ => null
            };

            if (update != null)
            {
                yield return update;
            }
        }
    }
}
