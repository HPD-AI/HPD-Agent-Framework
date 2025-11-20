using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreThread = HPD.Agent.ConversationThread;

namespace HPD.Agent.Microsoft;

/// <summary>
/// Microsoft.Agents.AI protocol adapter for HPD-Agent ConversationThread.
/// Wraps the protocol-agnostic core thread and provides Microsoft protocol compatibility.
/// </summary>
public sealed class ConversationThread : AgentThread
{
    private readonly CoreThread _core;

    /// <summary>
    /// Unique identifier for this conversation thread (delegated to core)
    /// </summary>
    public string Id => _core.Id;

    /// <summary>
    /// When this thread was created (delegated to core)
    /// </summary>
    public DateTime CreatedAt => _core.CreatedAt;

    /// <summary>
    /// Last time this thread was updated (delegated to core)
    /// </summary>
    public DateTime LastActivity => _core.LastActivity;

    /// <summary>
    /// Display name for this conversation (UI-friendly).
    /// Delegated to core.
    /// </summary>
    public string? DisplayName
    {
        get => _core.DisplayName;
        set => _core.DisplayName = value;
    }

    /// <summary>
    /// Read-only view of metadata associated with this thread (delegated to core)
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata => _core.Metadata;

    /// <summary>
    /// Indicates whether message operations require async I/O (delegated to core)
    /// </summary>
    public bool RequiresAsyncAccess => _core.RequiresAsyncAccess;

    /// <summary>
    /// Direct access to the message store for advanced operations (delegated to core)
    /// </summary>
    public HPD.Agent.ChatMessageStore MessageStore => _core.MessageStore;

    /// <summary>
    /// Optional service thread ID for hybrid scenarios (delegated to core)
    /// </summary>
    public string? ServiceThreadId
    {
        get => _core.ServiceThreadId;
        set => _core.ServiceThreadId = value;
    }

    /// <summary>
    /// Conversation identifier for server-side history tracking optimization (delegated to core)
    /// </summary>
    public string? ConversationId
    {
        get => _core.ConversationId;
        set => _core.ConversationId = value;
    }

    /// <summary>
    /// Optional AI context provider for protocol-specific enrichment.
    /// Used by Microsoft.Agents.AI protocol for memory/RAG injection before LLM calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is automatically set via factory pattern when using Microsoft protocol:
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithContextProviderFactory(ctx => new MyMemoryProvider())
    ///     .BuildMicrosoftAgent();
    ///
    /// var thread = agent.CreateThread();  // Provider created automatically
    /// </code>
    /// </para>
    /// <para>
    /// The provider's <see cref="AIContextProvider.InvokingAsync"/> method is called before each LLM
    /// invocation to inject additional context (memories, RAG documents, dynamic tools, etc.).
    /// The provider's <see cref="AIContextProvider.InvokedAsync"/> method is called after the LLM
    /// responds to enable learning and state updates.
    /// </para>
    /// </remarks>
    [JsonIgnore]  // Don't serialize the instance, only its state
    public AIContextProvider? AIContextProvider { get; set; }

    /// <summary>
    /// Current agent execution state for checkpointing and resumption (delegated to core)
    /// </summary>
    public AgentLoopState? ExecutionState
    {
        get => _core.ExecutionState;
        internal set => _core.ExecutionState = value;
    }

    /// <summary>
    /// Last successful history reduction state for cache-aware reduction (delegated to core)
    /// </summary>
    public HistoryReductionState? LastReduction
    {
        get => _core.LastReduction;
        set => _core.LastReduction = value;
    }

    /// <summary>
    /// Creates a new Microsoft protocol conversation thread with default in-memory storage.
    /// </summary>
    public ConversationThread()
        : this(new CoreThread())
    {
    }

    /// <summary>
    /// Creates a new Microsoft protocol conversation thread with custom message store.
    /// </summary>
    /// <param name="messageStore">Message store implementation (in-memory, database, etc.)</param>
    public ConversationThread(HPD.Agent.ChatMessageStore messageStore)
        : this(new CoreThread(messageStore))
    {
    }

    /// <summary>
    /// Internal constructor that wraps an existing core thread.
    /// </summary>
    /// <param name="core">Core thread to wrap</param>
    internal ConversationThread(CoreThread core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
    }

    /// <summary>
    /// Internal accessor for the wrapped core thread.
    /// Used by framework internals to access the protocol-agnostic core.
    /// </summary>
    internal CoreThread Core => _core;

    #region Message Operations (Delegated to Core)

    /// <summary>
    /// INTERNAL: Get messages from this thread. For internal agent framework use only.
    /// </summary>
    internal Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        return _core.GetMessagesAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the number of messages in this conversation thread.
    /// </summary>
    public Task<int> GetMessageCountAsync(CancellationToken cancellationToken = default)
    {
        return _core.GetMessageCountAsync(cancellationToken);
    }

    /// <summary>
    /// Add a single message to the thread.
    /// </summary>
    public Task AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        return _core.AddMessageAsync(message, cancellationToken);
    }

    /// <summary>
    /// Add multiple messages to the thread.
    /// </summary>
    public Task AddMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        return _core.AddMessagesAsync(messages, cancellationToken);
    }

    /// <summary>
    /// Clear all messages from this thread.
    /// </summary>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        return _core.ClearAsync(cancellationToken);
    }

    #endregion

    #region Synchronous Message API (Delegated to Core)

    /// <summary>
    /// Gets a snapshot of all messages (delegated to core).
    /// See core documentation for performance characteristics.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages => _core.Messages;

    /// <summary>
    /// Gets the number of messages (delegated to core).
    /// See core documentation for performance characteristics.
    /// </summary>
    public int MessageCount => _core.MessageCount;

    /// <summary>
    /// Adds a message to the thread (delegated to core).
    /// See core documentation for performance characteristics.
    /// </summary>
    public void AddMessage(ChatMessage message) => _core.AddMessage(message);

    /// <summary>
    /// Adds multiple messages to the thread (delegated to core).
    /// See core documentation for performance characteristics.
    /// </summary>
    public void AddMessages(IEnumerable<ChatMessage> messages) => _core.AddMessages(messages);

    #endregion

    #region Metadata Operations (Delegated to Core)

    /// <summary>
    /// Add metadata key/value pair to this thread.
    /// </summary>
    public void AddMetadata(string key, object value)
    {
        _core.AddMetadata(key, value);
    }

    #endregion

    /// <summary>
    /// Get a display name for this thread based on first user message.
    /// </summary>
    public Task<string> GetDisplayNameAsync(int maxLength = 30, CancellationToken cancellationToken = default)
    {
        return _core.GetDisplayNameAsync(maxLength, cancellationToken);
    }

    #region Microsoft Protocol: Service Discovery

    /// <summary>
    /// Service discovery - provides AgentThreadMetadata (Microsoft pattern)
    /// </summary>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey == null && serviceType == typeof(AgentThreadMetadata))
        {
            return new AgentThreadMetadata(Id);
        }

        return base.GetService(serviceType, serviceKey);
    }

    #endregion

    #region Microsoft Protocol: Serialization

    /// <summary>
    /// Serialize this thread to a JSON element (AgentThread override).
    /// Includes both core state and Microsoft-specific state (AIContextProvider).
    /// </summary>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var coreSnapshot = _core.Serialize(jsonSerializerOptions);

        var snapshot = new MicrosoftConversationThreadSnapshot
        {
            CoreState = coreSnapshot,
            AIContextProviderState = AIContextProvider?.Serialize(jsonSerializerOptions)
        };

        // Use source-generated JSON context for AOT compatibility
        return JsonSerializer.SerializeToElement(snapshot, MicrosoftConversationJsonContext.Default.MicrosoftConversationThreadSnapshot);
    }

    /// <summary>
    /// Deserialize a thread from a snapshot.
    /// Recreates both core state and Microsoft-specific state.
    /// </summary>
    /// <param name="serializedThreadState">Serialized thread snapshot (JsonElement)</param>
    /// <param name="jsonSerializerOptions">Optional JSON serializer options</param>
    /// <param name="contextProviderFactory">Optional factory for restoring AIContextProvider from serialized state</param>
    /// <returns>Deserialized Microsoft protocol conversation thread</returns>
    public static ConversationThread Deserialize(
        JsonElement serializedThreadState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        Func<JsonElement, JsonSerializerOptions?, AIContextProvider>? contextProviderFactory = null)
    {
        var snapshot = JsonSerializer.Deserialize(
            serializedThreadState,
            MicrosoftConversationJsonContext.Default.MicrosoftConversationThreadSnapshot);

        if (snapshot == null)
        {
            throw new JsonException("Failed to deserialize MicrosoftConversationThreadSnapshot from JSON.");
        }

        // Deserialize core thread
        var coreThread = CoreThread.Deserialize(snapshot.CoreState, jsonSerializerOptions);

        // Wrap in Microsoft adapter
        var thread = new ConversationThread(coreThread);

        // Restore AIContextProvider state if factory provided and state exists
        if (snapshot.AIContextProviderState.HasValue &&
            snapshot.AIContextProviderState.Value.ValueKind != JsonValueKind.Undefined &&
            snapshot.AIContextProviderState.Value.ValueKind != JsonValueKind.Null &&
            contextProviderFactory != null)
        {
            thread.AIContextProvider = contextProviderFactory(
                snapshot.AIContextProviderState.Value,
                jsonSerializerOptions);
        }

        return thread;
    }

    #endregion

    #region AgentThread Overrides

    /// <summary>
    /// Called when new messages are received (AgentThread override).
    /// Updates this thread's message list via core.
    /// </summary>
    protected override async Task MessagesReceivedAsync(IEnumerable<ChatMessage> newMessages, CancellationToken cancellationToken = default)
    {
        var existingMessages = await _core.GetMessagesAsync(cancellationToken);
        var messagesToAdd = newMessages.Where(m => !existingMessages.Contains(m)).ToList();

        if (messagesToAdd.Any())
        {
            await _core.AddMessagesAsync(messagesToAdd, cancellationToken);
        }
    }

    #endregion
}

/// <summary>
/// JSON source generation context for Microsoft ConversationThread serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(MicrosoftConversationThreadSnapshot))]
internal partial class MicrosoftConversationJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Serializable snapshot of a Microsoft protocol ConversationThread.
/// Contains both core state and Microsoft-specific state (AIContextProvider).
/// </summary>
internal record MicrosoftConversationThreadSnapshot
{
    /// <summary>
    /// Core thread state (protocol-agnostic)
    /// </summary>
    public required ConversationThreadSnapshot CoreState { get; init; }

    /// <summary>
    /// Serialized state of the AIContextProvider (Microsoft-specific, optional)
    /// </summary>
    public JsonElement? AIContextProviderState { get; init; }
}
