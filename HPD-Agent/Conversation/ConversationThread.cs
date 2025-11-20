using Microsoft.Extensions.AI;
using System.Text.Json;

namespace HPD.Agent;

/// <summary>
/// Factory interface for creating ChatMessageStore instances from serialized state.
/// Enables AOT-friendly deserialization without reflection.
/// </summary>
/// <remarks>
/// Implement this interface for each message store type, then register via:
/// <code>
/// ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());
/// </code>
/// </remarks>
internal interface IConversationMessageStoreFactory
{
    /// <summary>
    /// Gets the store type name used for matching during deserialization.
    /// Should match the Type.FullName or a stable identifier for the store type.
    /// </summary>
    string StoreTypeName { get; }

    /// <summary>
    /// Creates a message store instance from serialized state.
    /// </summary>
    /// <param name="state">Serialized store state (JsonElement)</param>
    /// <param name="options">Optional JSON serializer options</param>
    /// <returns>Initialized message store with restored state</returns>
    ChatMessageStore CreateFromSnapshot(JsonElement state, JsonSerializerOptions? options);
}

/// <summary>
/// PROTOCOL-AGNOSTIC conversation thread for managing conversation state.
/// This is the core implementation that can be wrapped by protocol-specific adapters.
/// INTERNAL: Use HPD.Agent.Microsoft.ConversationThread for protocol-specific APIs.
///
/// <para><b>Architecture:</b></para>
/// <para>
/// - Uses ChatMessageStore for message storage, cache-aware reduction, token counting
/// - Handles: metadata, timestamps, display names, execution state, history reduction state
/// - Protocol-agnostic: No dependencies on Microsoft.Agents.AI or other protocols
/// </para>
///
/// <para><b>Thread Safety:</b></para>
/// <para>
/// - All public methods are thread-safe for concurrent reads
/// - Writes are serialized internally by the MessageStore
/// - Metadata dictionary uses locking for concurrent access
/// </para>
///
/// <para><b>Storage:</b></para>
/// <para>
/// - InMemoryConversationMessageStore: Fast sync access, no I/O
/// - DatabaseConversationMessageStore: Async required, may involve I/O
/// </para>
///
/// <para><b>API Design:</b></para>
/// <para>
/// - Public API uses "push" pattern: AddMessagesAsync() to update state
/// - Internal API uses "pull" pattern: GetMessagesAsync() for efficient snapshots
/// - This prevents users from working with stale data while giving framework flexibility
/// </para>
///
/// <para><b>Native AOT Support:</b></para>
/// <para>
/// Register message store factories at application startup for AOT-friendly deserialization:
/// <code>
/// ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());
/// </code>
/// </para>
/// </summary>
internal sealed class ConversationThread
{
    // Metadata key constants
    private const string METADATA_KEY_DISPLAY_NAME = "DisplayName";

    // AOT-friendly factory registration
    private static readonly Dictionary<string, IConversationMessageStoreFactory> _storeFactories = new();
    private static readonly object _factoryLock = new();

    private readonly ChatMessageStore _messageStore;
    private readonly Dictionary<string, object> _metadata = new();
    private string? _serviceThreadId;

    /// <summary>
    /// Unique identifier for this conversation thread
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// When this thread was created
    /// </summary>
    public DateTime CreatedAt { get; }

    /// <summary>
    /// Last time this thread was updated
    /// </summary>
    public DateTime LastActivity { get; private set; }

    /// <summary>
    /// Display name for this conversation (UI-friendly).
    /// If not set, falls back to first user message via GetDisplayNameAsync().
    /// </summary>
    public string? DisplayName
    {
        get => _metadata.TryGetValue(METADATA_KEY_DISPLAY_NAME, out var name)
            ? name?.ToString()
            : null;
        set
        {
            if (value == null)
                _metadata.Remove(METADATA_KEY_DISPLAY_NAME);
            else
                AddMetadata(METADATA_KEY_DISPLAY_NAME, value);
        }
    }

    /// <summary>
    /// Read-only view of metadata associated with this thread
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata => _metadata.AsReadOnly();

    /// <summary>
    /// Indicates whether message operations require async I/O.
    /// True for database/network stores, false for in-memory.
    /// </summary>
    public bool RequiresAsyncAccess => _messageStore is not InMemoryConversationMessageStore;

    /// <summary>
    /// Direct access to the message store for advanced operations.
    /// Exposes cache-aware reduction and token counting capabilities.
    /// </summary>
    public ChatMessageStore MessageStore => _messageStore;

    /// <summary>
    /// Optional service thread ID for hybrid scenarios.
    /// Enables syncing to OpenAI Assistants, Azure AI, etc.
    /// This is stored separately from the local message history.
    /// </summary>
    public string? ServiceThreadId
    {
        get => _serviceThreadId;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _serviceThreadId = value;
                LastActivity = DateTime.UtcNow;
            }
            else
            {
                _serviceThreadId = null;
            }
        }
    }

    /// <summary>
    /// Conversation identifier for server-side history tracking optimization.
    /// Used by LLM services that manage conversation state server-side (e.g., OpenAI Assistants).
    /// When set, enables delta message sending - only new messages are sent to the LLM on subsequent turns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This ID is captured from the LLM's response (ChatResponseUpdate.ConversationId) and reused
    /// in subsequent calls to enable the service to track conversation history server-side.
    /// This provides significant token savings for multi-turn conversations.
    /// </para>
    /// <para>
    /// <b>Lifecycle:</b>
    /// - Initially null for new threads
    /// - Set automatically by Agent when LLM returns ConversationId
    /// - Persists across multiple agent runs on the same thread
    /// - Included in thread serialization/deserialization
    /// </para>
    /// </remarks>
    public string? ConversationId { get; set; }

    /// <summary>
    /// Current agent execution state for checkpointing and resumption.
    /// Null when thread is idle (no agent run in progress).
    /// This directly serializes the immutable AgentLoopState from the agent's main loop.
    /// PROTOCOL-AGNOSTIC: Works for Microsoft.Agents.AI, AGUI, and all future protocols.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This property enables durable agent execution with crash recovery:
    /// <code>
    /// // Agent runs and checkpoints state automatically
    /// await agent.RunAsync(messages, thread);
    /// // ... crash/restart ...
    ///
    /// // Resume from checkpoint (pass empty messages)
    /// var loadedThread = await checkpointer.LoadThreadAsync(threadId);
    /// if (loadedThread?.ExecutionState != null)
    /// {
    ///     await agent.RunAsync(Array.Empty&lt;ChatMessage&gt;(), loadedThread);
    /// }
    /// </code>
    /// </para>
    /// <para>
    /// The state is automatically set by the agent framework during execution when a checkpointer is configured.
    /// Only the agent framework and protocol adapters should modify this property.
    /// </para>
    /// </remarks>
    public AgentLoopState? ExecutionState { get; set; }

    /// <summary>
    /// Last successful history reduction state for cache-aware reduction.
    /// Persists across multiple agent runs to enable reduction cache hits.
    /// Null if history reduction has never been performed on this thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Purpose:</b> Enable cache-aware incremental history reduction without redundant LLM calls.
    /// </para>
    /// <para>
    /// <b>Lifecycle:</b>
    /// <list type="number">
    /// <item>Fresh run: Check if LastReduction.IsValidFor(currentMessageCount) → cache hit</item>
    /// <item>If valid: Reuse reduction via ApplyToMessages() (no LLM call!)</item>
    /// <item>If invalid: Run new reduction, update LastReduction</item>
    /// <item>Serialized with thread state for cross-session caching</item>
    /// </list>
    /// </para>
    /// </remarks>
    public HistoryReductionState? LastReduction { get; set; }

    /// <summary>
    /// Creates a new conversation thread with default in-memory storage.
    /// </summary>
    public ConversationThread()
        : this(new InMemoryConversationMessageStore())
    {
    }

    /// <summary>
    /// Creates a new conversation thread with custom message store.
    /// </summary>
    /// <param name="messageStore">Message store implementation (in-memory, database, etc.)</param>
    public ConversationThread(ChatMessageStore messageStore)
    {
        ArgumentNullException.ThrowIfNull(messageStore);
        _messageStore = messageStore;
        Id = Guid.NewGuid().ToString();
        CreatedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a conversation thread with specific ID and message store (for deserialization).
    /// </summary>
    /// <param name="id">Thread identifier</param>
    /// <param name="createdAt">Creation timestamp</param>
    /// <param name="lastActivity">Last activity timestamp</param>
    /// <param name="messageStore">Message store implementation</param>
    private ConversationThread(string id, DateTime createdAt, DateTime lastActivity, ChatMessageStore messageStore)
    {
        ArgumentNullException.ThrowIfNull(messageStore);
        _messageStore = messageStore;
        Id = id;
        CreatedAt = createdAt;
        LastActivity = lastActivity;
    }

    #region AOT-Friendly Factory Registration

    /// <summary>
    /// Registers a factory for AOT-friendly message store deserialization.
    /// Call this at application startup for each custom message store type.
    /// </summary>
    /// <param name="factory">Factory implementation for a specific store type</param>
    /// <remarks>
    /// This enables Native AOT compilation by avoiding reflection-based deserialization.
    /// <code>
    /// // At application startup:
    /// ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());
    /// ConversationThread.RegisterStoreFactory(new DatabaseConversationMessageStoreFactory());
    /// </code>
    /// </remarks>
    public static void RegisterStoreFactory(IConversationMessageStoreFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_factoryLock)
        {
            _storeFactories[factory.StoreTypeName] = factory;
        }
    }

    #endregion

    #region Synchronous Message API

    /// <summary>
    /// Gets a snapshot of all messages in this conversation thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠️ Important:</b> This returns a snapshot at the time of access.
    /// If you access this property multiple times, you may get different results
    /// as messages are added to the thread.
    /// </para>
    /// <para>
    /// <b>Performance:</b> For in-memory stores (default), this is a fast synchronous
    /// operation with no I/O. For database stores, this will block on async I/O.
    /// Check <see cref="RequiresAsyncAccess"/> to determine operation characteristics.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ChatMessage> Messages
    {
        get
        {
            if (_messageStore is InMemoryConversationMessageStore inMemStore)
            {
                // Fast path: direct access to in-memory list
                return inMemStore.Messages;
            }

            // Slow path: blocks on async I/O for database stores
            return _messageStore.GetMessagesAsync()
                .GetAwaiter()
                .GetResult()
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>
    /// Gets the number of messages in this conversation thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Performance:</b> For in-memory stores (default), this is a fast synchronous
    /// operation. For database stores, this will block on async I/O.
    /// Check <see cref="RequiresAsyncAccess"/> to determine operation characteristics.
    /// </para>
    /// </remarks>
    public int MessageCount => _messageStore is InMemoryConversationMessageStore inMem
        ? inMem.Count
        : GetMessageCountSync();

    /// <summary>
    /// Adds a message to the conversation thread.
    /// </summary>
    /// <param name="message">The message to add</param>
    /// <remarks>
    /// <para>
    /// <b>Performance:</b> For in-memory stores (default), this is a fast synchronous
    /// operation with no I/O. For database stores, this will block on async I/O.
    /// Check <see cref="RequiresAsyncAccess"/> to determine operation characteristics.
    /// </para>
    /// </remarks>
    public void AddMessage(ChatMessage message)
    {
        _messageStore.AddMessagesAsync([message])
            .GetAwaiter()
            .GetResult();

        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Adds multiple messages to the conversation thread.
    /// </summary>
    /// <param name="messages">The messages to add</param>
    /// <remarks>
    /// <para>
    /// <b>Performance:</b> For in-memory stores (default), this is a fast synchronous
    /// operation with no I/O. For database stores, this will block on async I/O.
    /// Check <see cref="RequiresAsyncAccess"/> to determine operation characteristics.
    /// </para>
    /// </remarks>
    public void AddMessages(IEnumerable<ChatMessage> messages)
    {
        _messageStore.AddMessagesAsync(messages)
            .GetAwaiter()
            .GetResult();

        LastActivity = DateTime.UtcNow;
    }

    #endregion

    #region Asynchronous Message API

    /// <summary>
    /// INTERNAL: Get messages from this thread. For internal agent framework use only.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Read-only list of messages (snapshot, not live)</returns>
    internal async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        var messages = await _messageStore.GetMessagesAsync(cancellationToken);
        return messages.ToList().AsReadOnly();
    }

    /// <summary>
    /// INTERNAL: Synchronous message access for FFI/unmanaged code only.
    /// ⚠️ WARNING: This blocks on async I/O and may deadlock.
    /// Only use this for FFI/P-Invoke scenarios where async is not possible.
    /// </summary>
    internal IReadOnlyList<ChatMessage> GetMessagesSync()
    {
        return _messageStore.GetMessagesAsync().GetAwaiter().GetResult().ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets the number of messages in this conversation thread.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Total message count</returns>
    public async Task<int> GetMessageCountAsync(CancellationToken cancellationToken = default)
    {
        var messages = await _messageStore.GetMessagesAsync(cancellationToken);
        return messages.Count();
    }

    /// <summary>
    /// INTERNAL: Synchronous message count for FFI/unmanaged code only.
    /// ⚠️ WARNING: This blocks on async I/O and may deadlock.
    /// Only use this for FFI/P-Invoke scenarios where async is not possible.
    /// </summary>
    internal int GetMessageCountSync()
    {
        return _messageStore.GetMessagesAsync().GetAwaiter().GetResult().Count();
    }

    /// <summary>
    /// Add a single message to the thread.
    /// This is the primary way to update thread state from external code.
    /// </summary>
    /// <param name="message">The message to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        await _messageStore.AddMessagesAsync(new[] { message }, cancellationToken);
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Add multiple messages to the thread.
    /// </summary>
    public async Task AddMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        await _messageStore.AddMessagesAsync(messages, cancellationToken);
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Clear all messages from this thread.
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _messageStore.ClearAsync(cancellationToken);
        _metadata.Clear();
        LastActivity = DateTime.UtcNow;
    }

    #endregion

    #region Metadata Operations

    /// <summary>
    /// Add metadata key/value pair to this thread.
    /// </summary>
    public void AddMetadata(string key, object value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key cannot be null or whitespace", nameof(key));

        _metadata[key] = value;
        LastActivity = DateTime.UtcNow;
    }

    #endregion

    /// <summary>
    /// Get a display name for this thread based on first user message.
    /// Useful for UI display in conversation lists.
    /// </summary>
    /// <param name="maxLength">Maximum length of display name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Display name or "New Conversation" if no messages</returns>
    public async Task<string> GetDisplayNameAsync(int maxLength = 30, CancellationToken cancellationToken = default)
    {
        // Check for explicit display name in metadata first
        if (_metadata.TryGetValue(METADATA_KEY_DISPLAY_NAME, out var name) && !string.IsNullOrEmpty(name?.ToString()))
        {
            return name.ToString()!;
        }

        // Find first user message and extract text content
        var messages = await GetMessagesAsync(cancellationToken);
        var firstUserMessage = messages.FirstOrDefault(m => m.Role == ChatRole.User);
        if (firstUserMessage == null)
            return "New Conversation";

        var text = firstUserMessage.Text ?? string.Empty;
        if (text.Length <= maxLength)
            return text;

        return text.Substring(0, maxLength - 3) + "...";
    }

    #region Serialization

    /// <summary>
    /// Serialize this thread to a snapshot object.
    /// Delegates message storage serialization to the message store.
    /// </summary>
    public ConversationThreadSnapshot Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        return new ConversationThreadSnapshot
        {
            Id = Id,
            MessageStoreState = _messageStore.Serialize(jsonSerializerOptions),
            MessageStoreType = _messageStore.GetType().AssemblyQualifiedName!,
            Metadata = _metadata.ToDictionary(kv => kv.Key, kv => kv.Value),
            CreatedAt = CreatedAt,
            LastActivity = LastActivity,
            ServiceThreadId = ServiceThreadId,
            ConversationId = ConversationId,
            ExecutionStateJson = ExecutionState?.Serialize(),
            LastReductionState = LastReduction
        };
    }

    /// <summary>
    /// Deserialize a thread from a snapshot.
    /// Recreates the message store from its serialized state.
    /// Uses registered factories for AOT-friendly deserialization.
    /// </summary>
    /// <param name="snapshot">Serialized thread snapshot</param>
    /// <param name="options">Optional JSON serializer options</param>
    /// <returns>Deserialized conversation thread</returns>
    /// <remarks>
    /// For Native AOT support, register factories before deserializing:
    /// <code>
    /// ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());
    /// var thread = ConversationThread.Deserialize(snapshot);
    /// </code>
    /// Falls back to reflection if no factory is registered (non-AOT scenarios).
    /// </remarks>
    public static ConversationThread Deserialize(
        ConversationThreadSnapshot snapshot,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Deserialize message store
        ChatMessageStore messageStore;

        if (snapshot.MessageStoreState.HasValue && !string.IsNullOrEmpty(snapshot.MessageStoreType))
        {
            // Try factory first (AOT-friendly)
            IConversationMessageStoreFactory? factory = null;
            lock (_factoryLock)
            {
                _storeFactories.TryGetValue(snapshot.MessageStoreType, out factory);
            }

            if (factory != null)
            {
                // AOT-friendly path: Use registered factory
                messageStore = factory.CreateFromSnapshot(snapshot.MessageStoreState.Value, options);
            }
            else
            {
                // Fallback to reflection (non-AOT scenarios)
                // ⚠️ This will fail in Native AOT if factory not registered
                var storeType = Type.GetType(snapshot.MessageStoreType);
                if (storeType == null)
                {
                    throw new InvalidOperationException(
                        $"Cannot find message store type: {snapshot.MessageStoreType}. " +
                        $"For Native AOT, register a factory via ConversationThread.RegisterStoreFactory() before deserializing.");
                }

                try
                {
                    // Invoke constructor: new XxxMessageStore(JsonElement state, JsonSerializerOptions?)
                    messageStore = (ChatMessageStore)Activator.CreateInstance(
                        storeType,
                        snapshot.MessageStoreState.Value,
                        options)!;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to deserialize message store of type {snapshot.MessageStoreType}. " +
                        $"Ensure a factory is registered for Native AOT support.", ex);
                }
            }
        }
        else
        {
            // Fallback: default to in-memory if no store state
            messageStore = new InMemoryConversationMessageStore();
        }

        var thread = new ConversationThread(
            snapshot.Id,
            snapshot.CreatedAt,
            snapshot.LastActivity,
            messageStore);

        thread._serviceThreadId = snapshot.ServiceThreadId;
        thread.ConversationId = snapshot.ConversationId;

        foreach (var (key, value) in snapshot.Metadata)
        {
            thread._metadata[key] = value;
        }

        // Restore ExecutionState if present
        if (!string.IsNullOrEmpty(snapshot.ExecutionStateJson))
        {
            thread.ExecutionState = AgentLoopState.Deserialize(snapshot.ExecutionStateJson);
        }

        // Restore LastReduction if present
        thread.LastReduction = snapshot.LastReductionState;

        return thread;
    }

    #endregion
}

/// <summary>
/// Serializable snapshot of a ConversationThread for persistence.
/// Delegates message storage to the message store's own serialization.
/// PROTOCOL-AGNOSTIC: Contains no protocol-specific state.
/// </summary>
public record ConversationThreadSnapshot
{
    public required string Id { get; init; }

    /// <summary>
    /// Serialized state of the message store.
    /// Format depends on the store type:
    /// - InMemoryConversationMessageStore: Contains full message list
    /// - DatabaseConversationMessageStore: Contains just connection info + conversation ID
    /// </summary>
    public JsonElement? MessageStoreState { get; init; }

    /// <summary>
    /// Assembly-qualified type name of the message store.
    /// Used for deserialization to recreate the correct store type.
    /// </summary>
    public required string MessageStoreType { get; init; }

    public required Dictionary<string, object> Metadata { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime LastActivity { get; init; }
    public string? ServiceThreadId { get; init; }

    /// <summary>
    /// Conversation identifier for server-side history tracking.
    /// Enables delta message sending for LLM services that manage conversation state.
    /// Null if service doesn't support server-side history or hasn't returned an ID yet.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Serialized AgentLoopState JSON (if mid-execution).
    /// Null when thread is idle (no agent run in progress).
    /// Contains complete execution context including iteration count, completed functions,
    /// consecutive failures, plugin/skill expansion state, and message references.
    /// </summary>
    public string? ExecutionStateJson { get; init; }

    /// <summary>
    /// Last successful history reduction state for cache-aware reduction.
    /// Persists across agent runs to avoid redundant LLM calls for re-reduction.
    /// Null if history reduction has never been performed on this thread.
    /// </summary>
    public HistoryReductionState? LastReductionState { get; init; }
}
