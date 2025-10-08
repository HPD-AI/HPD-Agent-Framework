using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using HPD_Agent.TextExtraction;
using System.Diagnostics;
using HPD_Agent.Orchestration;

/// <summary>
/// Clean conversation management built on Microsoft.Extensions.AI
/// Coordinates ConversationThread (state) and Agent execution (direct for single-agent, via IOrchestrator for multi-agent).
/// Similar to Microsoft's pattern where Agent + Thread are composed by user code.
/// </summary>
public class Conversation
{
    private readonly ConversationThread _thread;
    private readonly IReadOnlyList<Agent> _agents;
    private IOrchestrator? _defaultOrchestrator;

    // OpenTelemetry Activity Source for conversation telemetry
    private static readonly ActivitySource ActivitySource = new("HPD.Conversation");

    /// <summary>
    /// Gets the conversation thread (state container)
    /// Provides access to message history, metadata, and serialization
    /// </summary>
    public ConversationThread Thread => _thread;

    /// <summary>
    /// Gets or sets the default orchestrator for multi-agent scenarios.
    /// When set, this orchestrator will be used if no orchestrator is provided to Send methods.
    /// </summary>
    public IOrchestrator? DefaultOrchestrator
    {
        get => _defaultOrchestrator;
        set => _defaultOrchestrator = value;
    }

    // Convenient pass-through properties to thread
    public string Id => _thread.Id;
    public DateTime CreatedAt => _thread.CreatedAt;
    public DateTime LastActivity => _thread.LastActivity;
    public IReadOnlyList<ChatMessage> Messages => _thread.Messages;
    public IReadOnlyDictionary<string, object> Metadata => _thread.Metadata;

    /// <summary>Gets the primary agent in this conversation, or null if no agents are present.</summary>
    public Agent? PrimaryAgent => _agents.FirstOrDefault();

    /// <summary>Add metadata key/value to this conversation thread.</summary>
    public void AddMetadata(string key, object value) => _thread.AddMetadata(key, value);

    /// <summary>
    /// Creates a conversation with a single agent and a new thread
    /// </summary>
    public Conversation(Agent agent)
    {
        _thread = new ConversationThread();
        _agents = new[] { agent ?? throw new ArgumentNullException(nameof(agent)) };
        _defaultOrchestrator = null;
    }

    /// <summary>
    /// Creates a conversation with a single agent and custom thread (for reusing state)
    /// </summary>
    public Conversation(Agent agent, ConversationThread thread)
    {
        _thread = thread ?? throw new ArgumentNullException(nameof(thread));
        _agents = new[] { agent ?? throw new ArgumentNullException(nameof(agent)) };
        _defaultOrchestrator = null;
    }

    /// <summary>
    /// Creates a conversation with multiple agents and a new thread
    /// </summary>
    public Conversation(IEnumerable<Agent> agents, IOrchestrator? orchestrator = null)
    {
        _thread = new ConversationThread();
        _agents = agents?.ToList() ?? throw new ArgumentNullException(nameof(agents));
        _defaultOrchestrator = orchestrator;
    }

    /// <summary>
    /// Creates a conversation with multiple agents and custom thread (for reusing state)
    /// </summary>
    public Conversation(IEnumerable<Agent> agents, ConversationThread thread, IOrchestrator? orchestrator = null)
    {
        _thread = thread ?? throw new ArgumentNullException(nameof(thread));
        _agents = agents?.ToList() ?? throw new ArgumentNullException(nameof(agents));
        _defaultOrchestrator = orchestrator;
    }

    /// <summary>
    /// Creates a conversation within a project (stores project in thread metadata)
    /// </summary>
    public Conversation(Project project, IEnumerable<Agent> agents, IOrchestrator? orchestrator = null)
    {
        _thread = new ConversationThread();
        _thread.AddMetadata("Project", project);
        _agents = agents?.ToList() ?? throw new ArgumentNullException(nameof(agents));
        _defaultOrchestrator = orchestrator;
    }

    /// <summary>
    /// Add a single message to the conversation thread
    /// </summary>
    public void AddMessage(ChatMessage message) => _thread.AddMessage(message);





    /// <summary>
    /// Send a message using AGUI protocol input format.
    /// </summary>
    /// <param name="aguiInput">AGUI protocol input containing thread, messages, tools, and context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Conversation turn result with response and metadata</returns>
    public Task<ConversationTurnResult> SendAsync(
        RunAgentInput aguiInput,
        CancellationToken cancellationToken = default)
    {
        return SendAsyncAGUI(aguiInput, cancellationToken);
    }

    /// <summary>
    /// Send a message in the conversation.
    /// For multi-agent scenarios, uses the provided orchestrator or falls back to DefaultOrchestrator.
    /// </summary>
    /// <param name="message">The message to send</param>
    /// <param name="options">Optional chat options</param>
    /// <param name="orchestrator">Optional orchestrator for multi-agent scenarios (falls back to DefaultOrchestrator)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<ConversationTurnResult> SendAsync(
        string message,
        ChatOptions? options = null,
        IOrchestrator? orchestrator = null,
        CancellationToken cancellationToken = default)
    {

        using var activity = ActivitySource.StartActivity("conversation.turn");
        var startTime = DateTimeOffset.UtcNow;

        // Set telemetry tags
        activity?.SetTag("conversation.id", Id);
        activity?.SetTag("conversation.message_count", _thread.MessageCount);
        activity?.SetTag("conversation.agent_count", _agents.Count);
        activity?.SetTag("conversation.primary_agent", PrimaryAgent?.Config?.Name);

        try
        {
            var userMessage = new ChatMessage(ChatRole.User, message);
            _thread.AddMessage(userMessage);

            // Inject context
            options = InjectProjectContextIfNeeded(options);

            OrchestrationResult orchestrationResult;

            // INLINE ROUTING LOGIC: Single vs Multi-agent
            if (_agents.Count == 0)
            {
                throw new InvalidOperationException("No agents configured for this conversation");
            }
            else if (_agents.Count == 1)
            {
                // SINGLE-AGENT PATH: Direct execution
                var agent = _agents[0];
                var sw = Stopwatch.StartNew();
                var streamingResult = await agent.ExecuteStreamingTurnAsync(
                    _thread.Messages, options, cancellationToken: cancellationToken);

                // Consume stream
                await foreach (var _ in streamingResult.EventStream.WithCancellation(cancellationToken))
                {
                    // Just consume events
                }

                // Get final history
                var finalHistory = await streamingResult.FinalHistory;
                sw.Stop();

                // Build OrchestrationResult
                var response = new ChatResponse(finalHistory.ToList());
                var singleAgentUsage = CreateTokenUsage(response);

                orchestrationResult = new OrchestrationResult
                {
                    Response = response,
                    PrimaryAgent = agent,
                    RunId = Id,
                    Status = OrchestrationStatus.Completed,
                    ExecutionCount = 1,
                    ExecutionTimeMs = (int)sw.ElapsedMilliseconds,
                    AggregatedUsage = singleAgentUsage,
                    ExecutionOrder = new[] { agent.Name },
                    Metadata = new OrchestrationMetadata
                    {
                        StrategyName = "SingleAgent",
                        DecisionDuration = TimeSpan.Zero,
                        Context = OrchestrationHelpers.PackageReductionMetadata(streamingResult.Reduction)
                    }
                };
            }
            else
            {
                // MULTI-AGENT PATH: Use orchestrator
                var effectiveOrchestrator = orchestrator ?? _defaultOrchestrator;
                if (effectiveOrchestrator == null)
                {
                    throw new InvalidOperationException(
                        $"Multi-agent conversations ({_agents.Count} agents) require an orchestrator. " +
                        $"Set DefaultOrchestrator or pass an orchestrator parameter.");
                }

                orchestrationResult = await effectiveOrchestrator.OrchestrateAsync(
                    _thread.Messages, _agents, Id, options, cancellationToken);
            }

            activity?.SetTag("conversation.orchestration_strategy", orchestrationResult.Metadata.StrategyName);
            activity?.SetTag("orchestration.status", orchestrationResult.Status.ToString().ToLowerInvariant());
            activity?.SetTag("orchestration.execution_count", orchestrationResult.ExecutionCount);
            activity?.SetTag("orchestration.execution_time_ms", orchestrationResult.ExecutionTimeMs);
            if (orchestrationResult.AggregatedUsage != null)
            {
                activity?.SetTag("orchestration.aggregated_tokens", orchestrationResult.AggregatedUsage.TotalTokens);
            }
            if (orchestrationResult.ExecutionOrder != null)
            {
                activity?.SetTag("orchestration.execution_order", string.Join(" -> ", orchestrationResult.ExecutionOrder));
            }

            // Apply reduction BEFORE adding response to history
            ApplyReductionIfPresent(orchestrationResult);

            // Store token counts from response (BAML-inspired pattern)
            StoreTokenCounts(orchestrationResult.Response, userMessage);

            // Commit response to history
            _thread.AddMessages(orchestrationResult.Response.Messages);

            // Record telemetry metrics
            var duration = DateTimeOffset.UtcNow - startTime;
            var tokenUsage = CreateTokenUsage(orchestrationResult.Response);

            activity?.SetTag("conversation.duration_ms", duration.TotalMilliseconds);
            activity?.SetTag("conversation.responding_agent", orchestrationResult.PrimaryAgent?.Name);
            activity?.SetTag("conversation.tokens_used", tokenUsage?.TotalTokens ?? 0);
            activity?.SetTag("conversation.success", true);

            return new ConversationTurnResult
            {
                Response = orchestrationResult.Response,
                TurnHistory = ExtractTurnHistory(userMessage, orchestrationResult.Response),
                RespondingAgent = orchestrationResult.PrimaryAgent!,
                UsedOrchestrator = orchestrator,
                Duration = duration,
                OrchestrationMetadata = orchestrationResult.Metadata,
                Usage = tokenUsage,
                RequestId = Guid.NewGuid().ToString(),
                ActivityId = System.Diagnostics.Activity.Current?.Id
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

    // Helper method for AGUI input path
    private async Task<ConversationTurnResult> SendAsyncAGUI(RunAgentInput aguiInput, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("conversation.turn");
        var startTime = DateTimeOffset.UtcNow;

        activity?.SetTag("conversation.id", Id);
        activity?.SetTag("conversation.input_format", "agui");
        activity?.SetTag("conversation.thread_id", aguiInput.ThreadId);
        activity?.SetTag("conversation.run_id", aguiInput.RunId);

        try
        {
            var agent = PrimaryAgent ?? throw new InvalidOperationException("No agent configured for this conversation");

            // ✅ IMPORTANT: Add the new user message from aguiInput to conversation thread
            // This ensures the agent sees the full conversation history
            var newUserMessage = aguiInput.Messages.LastOrDefault(m => m.Role == "user");
            if (newUserMessage != null)
            {
                _thread.AddMessage(new ChatMessage(ChatRole.User, newUserMessage.Content ?? ""));
            }

            // ✅ Create new RunAgentInput using server-side _thread as source of truth
            // This ensures the agent uses the conversation history managed by Conversation, not frontend messages
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
            var streamResult = await agent.ExecuteStreamingTurnAsync(serverSideInput, cancellationToken);

            // Consume stream (non-streaming path)
            await foreach (var evt in streamResult.EventStream.WithCancellation(cancellationToken))
            {
                // Events consumed but not exposed in non-streaming path
            }

            // Wait for final history
            var finalHistory = await streamResult.FinalHistory;

            // Build response from final history
            var assistantMessages = finalHistory.Where(m => m.Role == ChatRole.Assistant).ToList();
            var response = assistantMessages.Count > 0
                ? new ChatResponse(assistantMessages)
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, ""));

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

            return new ConversationTurnResult
            {
                Response = response,
                TurnHistory = finalHistory,
                RespondingAgent = agent,
                UsedOrchestrator = null,
                Duration = duration,
                OrchestrationMetadata = new OrchestrationMetadata
                {
                    StrategyName = "AGUI",
                    DecisionDuration = TimeSpan.Zero
                },
                Usage = CreateTokenUsage(response),
                RequestId = aguiInput.RunId,
                ActivityId = System.Diagnostics.Activity.Current?.Id
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

    // Helper method to avoid code duplication
    private ChatOptions? InjectProjectContextIfNeeded(ChatOptions? options)
    {
        options ??= new ChatOptions();
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        
        // Always inject conversation ID
        options.AdditionalProperties["ConversationId"] = Id;
        
        // Inject project if available
        if (Metadata.TryGetValue("Project", out var obj) && obj is Project project)
        {
            options.AdditionalProperties["Project"] = project;
        }
        return options;
    }

    /// <summary>
    /// Stream a conversation turn using AGUI protocol input format.
    /// Returns both event stream and final conversation result.
    /// </summary>
    /// <param name="aguiInput">AGUI protocol input containing thread, messages, tools, and context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Streaming result with event stream and final metadata</returns>
    public Task<ConversationStreamingResult> SendStreamingAsync(
        RunAgentInput aguiInput,
        CancellationToken cancellationToken = default)
    {
        return SendStreamingAsyncAGUI(aguiInput, cancellationToken);
    }

    /// <summary>
    /// Stream a conversation turn and return both event stream and final metadata.
    /// For multi-agent scenarios, uses the provided orchestrator or falls back to DefaultOrchestrator.
    /// </summary>
    /// <param name="message">The user message to send</param>
    /// <param name="options">Chat options</param>
    /// <param name="orchestrator">Optional orchestrator for multi-agent scenarios (falls back to DefaultOrchestrator)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Streaming result with event stream and final metadata</returns>
    public Task<ConversationStreamingResult> SendStreamingAsync(
        string message,
        ChatOptions? options = null,
        IOrchestrator? orchestrator = null,
        CancellationToken cancellationToken = default)
    {

        // Create a channel to allow multiple consumers of the event stream
        var channel = System.Threading.Channels.Channel.CreateUnbounded<BaseEvent>();
        var writer = channel.Writer;
        var reader = channel.Reader;

        // Create TaskCompletionSource for the final result
        var resultTcs = new TaskCompletionSource<ConversationTurnResult>();

        // Start a task to produce events and build the final result
        _ = Task.Run(async () =>
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // ✅ NEW: Capture definitive metadata from streaming execution
                var metadata = await SendStreamingEventsAsync(message, writer, options, orchestrator, cancellationToken);

                // Close the writer to signal completion
                writer.Complete();

                // ✅ Build result from definitive metadata (no more TakeLast(10) guessing!)
                resultTcs.SetResult(new ConversationTurnResult
                {
                    Response = metadata.Response,
                    TurnHistory = metadata.FinalHistory,
                    RespondingAgent = metadata.RespondingAgent,
                    UsedOrchestrator = orchestrator,
                    Duration = DateTime.UtcNow - startTime,
                    OrchestrationMetadata = metadata.OrchestrationMetadata,
                    Usage = CreateTokenUsage(metadata.Response),
                    RequestId = Guid.NewGuid().ToString(),
                    ActivityId = System.Diagnostics.Activity.Current?.Id
                });
            }
            catch (Exception ex)
            {
                writer.Complete(ex);
                resultTcs.SetException(ex);
            }
        }, cancellationToken);

        // Create an async enumerable from the channel reader
        async IAsyncEnumerable<BaseEvent> eventStream([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                yield return evt;
            }
        }

        return Task.FromResult(new ConversationStreamingResult
        {
            EventStream = eventStream(cancellationToken),
            FinalResult = resultTcs.Task
        });
    }

    // Helper method for AGUI streaming path
    private async Task<ConversationStreamingResult> SendStreamingAsyncAGUI(RunAgentInput aguiInput, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var agent = PrimaryAgent ?? throw new InvalidOperationException("No agent configured for this conversation");

        // ✅ IMPORTANT: Add the new user message from aguiInput to conversation thread
        // This ensures the agent sees the full conversation history
        var newUserMessage = aguiInput.Messages.LastOrDefault(m => m.Role == "user");
        if (newUserMessage != null)
        {
            _thread.AddMessage(new ChatMessage(ChatRole.User, newUserMessage.Content ?? ""));
        }

        // ✅ Create new RunAgentInput using server-side _thread as source of truth
        // This ensures the agent uses the conversation history managed by Conversation, not frontend messages
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
        var streamResult = await agent.ExecuteStreamingTurnAsync(serverSideInput, cancellationToken);

        // Create task to build final result after streaming completes
        var finalResultTask = Task.Run(async () =>
        {
            // Wait for final history from agent
            var finalHistory = await streamResult.FinalHistory;

            // Build response from final history
            var assistantMessages = finalHistory.Where(m => m.Role == ChatRole.Assistant).ToList();
            var response = assistantMessages.Count > 0
                ? new ChatResponse(assistantMessages)
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, ""));

            // Update conversation thread
            foreach (var msg in finalHistory)
            {
                if (!_thread.Messages.Contains(msg))
                {
                    _thread.AddMessage(msg);
                }
            }

            return new ConversationTurnResult
            {
                Response = response,
                TurnHistory = finalHistory,
                RespondingAgent = agent,
                UsedOrchestrator = null,
                Duration = DateTime.UtcNow - startTime,
                OrchestrationMetadata = new OrchestrationMetadata
                {
                    StrategyName = "AGUI",
                    DecisionDuration = TimeSpan.Zero
                },
                Usage = CreateTokenUsage(response),
                RequestId = aguiInput.RunId,
                ActivityId = System.Diagnostics.Activity.Current?.Id
            };
        }, cancellationToken);

        return new ConversationStreamingResult
        {
            EventStream = streamResult.EventStream,
            FinalResult = finalResultTask
        };
    }

    /// <summary>
    /// Stream a conversation turn with default console display formatting.
    /// Provides a user-friendly experience with automatic event formatting and output.
    /// For advanced event control, use SendStreamingAsync instead.
    /// </summary>
    /// <param name="message">The user message to send</param>
    /// <param name="outputHandler">Optional custom output handler. Defaults to Console.Write</param>
    /// <param name="options">Chat options</param>
    /// <param name="orchestrator">Optional orchestrator for multi-agent scenarios (falls back to DefaultOrchestrator)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Final conversation turn result with all metadata</returns>
    public async Task<ConversationTurnResult> SendStreamingWithOutputAsync(
        string message,
        Action<string>? outputHandler = null,
        ChatOptions? options = null,
        IOrchestrator? orchestrator = null,
        CancellationToken cancellationToken = default)
    {
        outputHandler ??= Console.Write;

        var result = await SendStreamingAsync(message, options, orchestrator, cancellationToken);
        
        // Stream events to output handler
        await foreach (var evt in result.EventStream.WithCancellation(cancellationToken))
        {
            var formattedOutput = FormatEventForDisplay(evt);
            if (!string.IsNullOrEmpty(formattedOutput))
            {
                outputHandler(formattedOutput);
            }
        }
        
        // Return the final result with all metadata
        return await result.FinalResult;
    }

    /// <summary>
    /// Formats a BaseEvent for display with clean text output plus reasoning steps
    /// </summary>
    private static string FormatEventForDisplay(BaseEvent evt)
    {
        return evt switch
        {
            // Official AG-UI thinking events
            ThinkingStartEvent => $"\n💭 Thinking...\n",
            ThinkingTextMessageContentEvent thinkingContent => thinkingContent.Delta,
            ThinkingEndEvent => "",
            // Regular text content
            TextMessageContentEvent text => text.Delta,
            // Fallback for generic steps
            StepStartedEvent step => $"\n[Step: {step.StepName}]\n",
            _ => "" // Only show thinking and assistant text, ignore other events
        };
    }

    /// <summary>
    /// Stream a conversation turn with full event transparency (advanced users).
    /// Writes events to the provided channel and returns definitive metadata for result construction.
    /// </summary>
    internal async Task<StreamingTurnMetadata> SendStreamingEventsAsync(
        string message,
        System.Threading.Channels.ChannelWriter<BaseEvent> eventWriter,
        ChatOptions? options = null,
        IOrchestrator? orchestrator = null,
        CancellationToken cancellationToken = default)
    {
        var userMessage = new ChatMessage(ChatRole.User, message);
        _thread.AddMessage(userMessage);

        // Inject project context
        options = InjectProjectContextIfNeeded(options);

        // Inject conversation ID into ChatOptions for plugin access
        // This is more reliable than AsyncLocal when ExecutionContext may not flow through Microsoft.Extensions.AI
        options ??= new ChatOptions();
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties["ConversationId"] = Id;

        // Set conversation context for AsyncLocal access by plugins (backup mechanism)
        ConversationContext.SetConversationId(Id);
        try
        {
            if (_agents.Count == 0)
            {
                throw new InvalidOperationException("No agents configured for this conversation");
            }
            else if (_agents.Count == 1)
            {
                // DIRECT PATH - Single agent
                var agent = _agents[0];
                var result = await agent.ExecuteStreamingTurnAsync(_thread.Messages, options, cancellationToken: cancellationToken);

                // Stream the events
                await foreach (var evt in result.EventStream.WithCancellation(cancellationToken))
                {
                    await eventWriter.WriteAsync(evt, cancellationToken);
                }

                // Wait for final history and update conversation
                var finalHistory = await result.FinalHistory;

                // Check for reduction metadata and apply BEFORE adding new messages
                if (result.Reduction != null)
                {
                    _thread.ApplyReduction(result.Reduction.SummaryMessage, result.Reduction.MessagesRemovedCount);
                }

                foreach (var msg in finalHistory)
                {
                    _thread.AddMessage(msg);
                }

                // Build response from final history
                var assistantMessages = finalHistory.Where(m => m.Role == ChatRole.Assistant).ToList();
                var response = assistantMessages.Count > 0
                    ? new ChatResponse(assistantMessages)
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, ""));

                // Return definitive metadata instead of letting caller reconstruct
                return new StreamingTurnMetadata
                {
                    Response = response,
                    FinalHistory = finalHistory,
                    RespondingAgent = agent,
                    OrchestrationMetadata = new OrchestrationMetadata
                    {
                        StrategyName = "SingleAgent",
                        DecisionDuration = TimeSpan.Zero
                    },
                    UserMessage = userMessage
                };
            }
            else
            {
                // ORCHESTRATED PATH - Multi-agent
                var effectiveOrchestrator = orchestrator ?? DefaultOrchestrator;
                if (effectiveOrchestrator == null)
                {
                    throw new InvalidOperationException(
                        $"Multi-agent conversations ({_agents.Count} agents) require an orchestrator. Set DefaultOrchestrator or pass an orchestrator parameter.");
                }

                // Use the orchestrator's streaming method
                var orchestrationResult = await effectiveOrchestrator.OrchestrateStreamingAsync(
                    _thread.Messages, _agents, this.Id, options, cancellationToken);

                // Stream all orchestration and agent events
                await foreach (var evt in orchestrationResult.EventStream.WithCancellation(cancellationToken))
                {
                    await eventWriter.WriteAsync(evt, cancellationToken);
                }

                // Wait for final result and update conversation
                var finalResult = await orchestrationResult.FinalResult;

                // Apply reduction from orchestration metadata BEFORE adding response
                ApplyReductionIfPresent(finalResult);

                foreach (var msg in finalResult.Response.Messages)
                {
                    _thread.AddMessage(msg);
                }

                // Return definitive metadata from orchestration
                return new StreamingTurnMetadata
                {
                    Response = finalResult.Response,
                    FinalHistory = ExtractTurnHistory(userMessage, finalResult.Response),
                    RespondingAgent = finalResult.PrimaryAgent,
                    OrchestrationMetadata = finalResult.Metadata,
                    UserMessage = userMessage
                };
            }
        }
        finally
        {
            // Clear conversation context after turn execution
            ConversationContext.Clear();
        }
    }

    /// <summary>
    /// Extracts reduction metadata from OrchestrationMetadata.Context and applies to storage.
    /// </summary>
    private void ApplyReductionIfPresent(OrchestrationResult result)
    {
        var context = result.Metadata.Context;

        if (context.TryGetValue("SummaryMessage", out var summaryObj) &&
            summaryObj is ChatMessage summary &&
            context.TryGetValue("MessagesRemovedCount", out var countObj) &&
            countObj is int count)
        {
            // Delegate to thread's ApplyReduction method
            _thread.ApplyReduction(summary, count);
        }
    }

    /// <summary>
    /// Extracts the turn history from user message and response.
    /// </summary>
    private IReadOnlyList<ChatMessage> ExtractTurnHistory(ChatMessage userMessage, ChatResponse response)
    {
        var turnMessages = new List<ChatMessage> { userMessage };
        turnMessages.AddRange(response.Messages);
        return turnMessages.AsReadOnly();
    }



    #region Conversation Helpers

    /// <summary>
    /// Gets a human-readable display name for this conversation.
    /// Delegates to ConversationThread.GetDisplayName.
    /// </summary>
    /// <param name="maxLength">Maximum length for the display name</param>
    /// <returns>Human-readable conversation name</returns>
    public string GetDisplayName(int maxLength = 30) => _thread.GetDisplayName(maxLength);

    /// <summary>
    /// Creates TokenUsage from ChatResponse.Usage if available
    /// </summary>
    private static TokenUsage? CreateTokenUsage(ChatResponse response)
    {
        if (response.Usage == null)
            return null;

        return new TokenUsage
        {
            PromptTokens = (int)(response.Usage.InputTokenCount ?? 0),
            CompletionTokens = (int)(response.Usage.OutputTokenCount ?? 0),
            TotalTokens = (int)(response.Usage.TotalTokenCount ?? 0),
            ModelId = response.ModelId
            // EstimatedCost is intentionally left null - cost calculation should be handled by business logic layer
        };
    }

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

    /// <summary>
    /// Stores token counts from ChatResponse into messages for token-aware reduction.
    /// Follows BAML's pattern of capturing provider-returned token counts.
    /// This enables accurate token budgeting without custom tokenizers.
    /// </summary>
    private void StoreTokenCounts(ChatResponse response, ChatMessage userMessage)
    {
        if (response.Usage == null)
            return;

        // Store input tokens on the user message
        if (response.Usage.InputTokenCount.HasValue)
        {
            userMessage.SetTokenCount((int)response.Usage.InputTokenCount.Value);
        }

        // Store output tokens on assistant messages
        // Note: For multi-message responses, we distribute tokens across messages
        var assistantMessages = response.Messages.Where(m => m.Role == ChatRole.Assistant).ToList();
        if (assistantMessages.Count > 0 && response.Usage.OutputTokenCount.HasValue)
        {
            var outputTokens = (int)response.Usage.OutputTokenCount.Value;

            if (assistantMessages.Count == 1)
            {
                // Single message - assign all output tokens
                assistantMessages[0].SetTokenCount(outputTokens);
            }
            else
            {
                // Multiple messages - distribute proportionally by content length
                var totalLength = assistantMessages.Sum(m =>
                    m.Contents.OfType<TextContent>().Sum(c => c.Text?.Length ?? 0));

                foreach (var msg in assistantMessages)
                {
                    var msgLength = msg.Contents.OfType<TextContent>().Sum(c => c.Text?.Length ?? 0);
                    if (totalLength > 0)
                    {
                        var proportion = (double)msgLength / totalLength;
                        msg.SetTokenCount((int)(outputTokens * proportion));
                    }
                }
            }
        }
    }
    
    
    

    #endregion

}

/// <summary>
/// Primary return type for conversation turns.
/// BREAKING: SendAsync now returns this instead of ChatResponse.
/// </summary>
/// <summary>
/// Token usage and cost information for a conversation turn
/// </summary>
public record TokenUsage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
    public decimal? EstimatedCost { get; init; }
    public string? ModelId { get; init; }
}


/// <summary>
/// Result of a conversation turn with rich metadata for business decisions
/// </summary>
public record ConversationTurnResult
{
    public required ChatResponse Response { get; init; }
    public required IReadOnlyList<ChatMessage> TurnHistory { get; init; }
    public required Agent RespondingAgent { get; init; }
    public IOrchestrator? UsedOrchestrator { get; init; }
    public required TimeSpan Duration { get; init; }
    public required OrchestrationMetadata OrchestrationMetadata { get; init; }

    // NEW: Core business data for immediate decisions
    public TokenUsage? Usage { get; init; }
    public string RequestId { get; init; } = Guid.NewGuid().ToString();
    public string? ActivityId { get; init; }

    /// <summary>
    /// Convenience conversions for backward compatibility.
    /// </summary>
    public static implicit operator ChatResponse(ConversationTurnResult result)
        => result.Response;

    public static implicit operator ChatMessage(ConversationTurnResult result)
        => result.Response.Messages.FirstOrDefault() ?? new ChatMessage();

    public string Text => Response.Text;
}

/// <summary>
/// Streaming result for conversation turns, providing both event stream and final metadata.
/// </summary>
public record ConversationStreamingResult
{
    public required IAsyncEnumerable<BaseEvent> EventStream { get; init; }
    public required Task<ConversationTurnResult> FinalResult { get; init; }
}

/// <summary>
/// Internal metadata captured during streaming turn execution.
/// Used to build ConversationTurnResult from definitive sources instead of reconstructing from thread.
/// </summary>
internal record StreamingTurnMetadata
{
    public required ChatResponse Response { get; init; }
    public required IReadOnlyList<ChatMessage> FinalHistory { get; init; }
    public required Agent RespondingAgent { get; init; }
    public required OrchestrationMetadata OrchestrationMetadata { get; init; }
    public ChatMessage UserMessage { get; init; } = null!;
}

/// <summary>
/// JSON source generation context for AOT compatibility
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ConversationThreadSnapshot))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatRole))]
[JsonSerializable(typeof(DateTime))]
internal partial class ConversationJsonContext : JsonSerializerContext
{
}
