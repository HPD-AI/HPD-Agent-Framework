using Microsoft.Extensions.AI;
using HPD.Agent.Internal.Filters;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using CoreAgent = HPD.Agent.Agent;

namespace HPD.Agent.AGUI;

/// <summary>
/// AGUI protocol adapter for HPD-Agent.
/// Wraps the protocol-agnostic core agent and provides AGUI protocol compatibility.
/// </summary>
public sealed class Agent
{
    private readonly CoreAgent _core;
    private readonly AGUIEventConverter _converter;

    /// <summary>
    /// Initializes a new AGUI protocol agent instance.
    /// Internal constructor - use AgentBuilder to create agents.
    /// </summary>
    internal Agent(
        AgentConfig config,
        IChatClient baseClient,
        ChatOptions? mergedOptions,
        List<IPromptFilter> promptFilters,
        ScopedFilterManager scopedFilterManager,
        HPD.Agent.ErrorHandling.IProviderErrorHandler providerErrorHandler,
        IReadOnlyList<IPermissionFilter>? permissionFilters = null,
        IReadOnlyList<IAiFunctionFilter>? aiFunctionFilters = null,
        IReadOnlyList<IMessageTurnFilter>? messageTurnFilters = null,
        IServiceProvider? serviceProvider = null)
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
            serviceProvider);

        _converter = new AGUIEventConverter();
    }

    /// <summary>
    /// Agent name (delegated to core)
    /// </summary>
    public string Name => _core.Name;

    /// <summary>
    /// Runs the agent with AGUI protocol input and streams events to the provided channel.
    /// This is the primary AGUI protocol entry point.
    /// </summary>
    /// <param name="input">AGUI run agent input containing messages, tools, and context</param>
    /// <param name="events">Channel writer for streaming AGUI BaseEvent events</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task RunAsync(
        RunAgentInput input,
        ChannelWriter<BaseEvent> events,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Convert AGUI input to Extensions.AI format (ensure non-null)
            var messageList = _converter.ConvertToExtensionsAI(input);
            var messages = messageList != null ? messageList.ToList() : new List<ChatMessage>();

            // Convert AGUI tools to ChatOptions (if needed)
            var chatOptions = _converter.ConvertToExtensionsAIChatOptions(
                input,
                existingOptions: null,
                enableFrontendToolScoping: false);

            // ═══════════════════════════════════════════════════════
            // ✅ CHECKPOINT SUPPORT: Create/load conversation thread
            // ═══════════════════════════════════════════════════════

            ConversationThread? conversationThread = null;
            IAsyncEnumerable<InternalAgentEvent> internalStream;

            var config = _core.Config;
            if (config?.Checkpointer != null)
            {
                // Load thread from checkpointer (may have checkpoint)
                conversationThread = await config.Checkpointer.LoadThreadAsync(
                    input.ThreadId, cancellationToken);

                // Create new thread if not found
                if (conversationThread == null)
                {
                    conversationThread = new ConversationThread();
                }

                // Validate resume semantics
                var hasMessages = messages?.Any() ?? false;
                var hasCheckpoint = conversationThread.ExecutionState != null;

                if (hasCheckpoint && hasMessages)
                {
                    throw new InvalidOperationException(
                        $"Cannot add new messages when resuming mid-execution. " +
                        $"Thread '{input.ThreadId}' is at iteration {conversationThread.ExecutionState?.Iteration ?? 0}.\n\n" +
                        $"To resume execution, send RunAgentInput with empty Messages array.");
                }

                // Call core agent with thread support
                internalStream = _core.RunAsync(
                    messages ?? new List<ChatMessage>(),
                    chatOptions,
                    conversationThread,
                    cancellationToken);
            }
            else
            {
                // No checkpointer - use original code path
                internalStream = _core.RunAsync(
                    messages ?? new List<ChatMessage>(),
                    chatOptions,
                    cancellationToken);
            }

            // Convert internal events to AGUI protocol using EventStreamAdapter
            var aguiStream = EventStreamAdapter.ToAGUI(
                internalStream,
                input.ThreadId,
                input.RunId,
                cancellationToken);

            // Stream AGUI events to the provided channel
            await foreach (var aguiEvent in aguiStream)
            {
                await events.WriteAsync(aguiEvent, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Emit error event on failure
            var errorEvent = EventSerialization.CreateRunError(ex.Message);
            await events.WriteAsync(errorEvent, cancellationToken);
            throw;
        }
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
}

/// <summary>
/// Adapts protocol-agnostic internal agent events to AGUI protocol format.
/// </summary>
internal static class EventStreamAdapter
{
    /// <summary>
    /// Adapts internal events to AGUI protocol format.
    /// Maps internal events to AGUI lifecycle events (Run, Step, Tool, Content).
    /// </summary>
    public static async IAsyncEnumerable<BaseEvent> ToAGUI(
        IAsyncEnumerable<InternalAgentEvent> internalStream,
        string threadId,
        string runId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var internalEvent in internalStream.WithCancellation(cancellationToken))
        {
            BaseEvent? aguiEvent = internalEvent switch
            {
                // MESSAGE TURN → RUN events
                InternalMessageTurnStartedEvent => EventSerialization.CreateRunStarted(threadId, runId),
                InternalMessageTurnFinishedEvent => EventSerialization.CreateRunFinished(threadId, runId),
                InternalMessageTurnErrorEvent e => EventSerialization.CreateRunError(e.Message),

                // AGENT TURN → STEP events
                InternalAgentTurnStartedEvent e => EventSerialization.CreateStepStarted(
                    stepId: $"step_{e.Iteration}",
                    stepName: $"Iteration {e.Iteration}",
                    description: null),
                InternalAgentTurnFinishedEvent e => EventSerialization.CreateStepFinished(
                    stepId: $"step_{e.Iteration}",
                    stepName: $"Iteration {e.Iteration}",
                    result: null),

                // TEXT CONTENT events
                InternalTextMessageStartEvent e => EventSerialization.CreateTextMessageStart(e.MessageId, e.Role),
                InternalTextDeltaEvent e => EventSerialization.CreateTextMessageContent(e.MessageId, e.Text),
                InternalTextMessageEndEvent e => EventSerialization.CreateTextMessageEnd(e.MessageId),

                // REASONING events
                InternalReasoningStartEvent e => EventSerialization.CreateReasoningStart(e.MessageId),
                InternalReasoningMessageStartEvent e => EventSerialization.CreateReasoningMessageStart(e.MessageId, e.Role),
                InternalReasoningDeltaEvent e => EventSerialization.CreateReasoningMessageContent(e.MessageId, e.Text),
                InternalReasoningMessageEndEvent e => EventSerialization.CreateReasoningMessageEnd(e.MessageId),
                InternalReasoningEndEvent e => EventSerialization.CreateReasoningEnd(e.MessageId),

                // TOOL events
                InternalToolCallStartEvent e => EventSerialization.CreateToolCallStart(e.CallId, e.Name, e.MessageId),
                InternalToolCallArgsEvent e => EventSerialization.CreateToolCallArgs(e.CallId, e.ArgsJson),
                InternalToolCallEndEvent e => EventSerialization.CreateToolCallEnd(e.CallId),
                InternalToolCallResultEvent e => EventSerialization.CreateToolCallResult(e.CallId, e.Result),

                _ => null // Unknown event type
            };

            if (aguiEvent != null)
            {
                yield return aguiEvent;
            }
        }
    }
}
