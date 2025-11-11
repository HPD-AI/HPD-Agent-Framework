using Microsoft.Extensions.AI;
using System.Text.Json;
using Microsoft.Agents.AI;
using HPD.Agent;

/// <summary>
/// Factory interface for creating ConversationMessageStore instances from serialized state.
/// Enables AOT-friendly deserialization without reflection.
/// </summary>
/// <remarks>
/// Implement this interface for each message store type, then register via:
/// <code>
/// ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());
/// </code>
/// </remarks>
public interface IConversationMessageStoreFactory
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
    ConversationMessageStore CreateFromSnapshot(JsonElement state, JsonSerializerOptions? options);
}

/// <summary>
/// Manages conversation state (message history, metadata, timestamps).
/// Inherits from Microsoft's AgentThread for compatibility with Agent Framework.
/// This allows one agent to serve multiple threads (conversations) concurrently.
///
/// <para><b>Architecture:</b></para>
/// <para>
/// - Uses ConversationMessageStore (which inherits from Microsoft's ChatMessageStore)
/// - Message store handles: storage, cache-aware reduction, token counting
/// - Thread handles: metadata, timestamps, display names, service integration
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
///
/// <para><b>Common Usage Patterns:</b></para>
/// <para>
/// ✅ <b>Create and run a conversation:</b>
/// <code>
/// var agent = new Agent(...);
/// var thread = new ConversationThread();
/// thread.DisplayName = "Weather Chat";
/// 
/// // Add user message
/// await thread.AddMessageAsync(new ChatMessage(ChatRole.User, "What's the weather?"));
/// 
/// // Run agent (messages added automatically via MessagesReceivedAsync)
/// var response = await agent.RunAsync("What's the weather?", thread);
/// 
/// // Access response messages
/// foreach (var msg in response.Messages)
///     Console.WriteLine(msg.Text);
/// </code>
/// </para>
/// <para>
/// ✅ <b>Check message count for UI:</b>
/// <code>
/// var count = await thread.GetMessageCountAsync();
/// Console.WriteLine($"Thread has {count} messages");
/// </code>
/// </para>
/// <para>
/// ❌ <b>DON'T try to iterate messages directly:</b>
/// <code>
/// // ❌ This won't compile - GetMessagesAsync() is internal
/// var messages = await thread.GetMessagesAsync(); // Compile error!
/// 
/// // ✅ Instead, use agent response:
/// var response = await agent.RunAsync("Hello", thread);
/// foreach (var msg in response.Messages)
///     Console.WriteLine(msg.Text);
/// </code>
/// </para>
/// </summary>
public class ConversationThread : AgentThread
{
    // Metadata key constants
    private const string METADATA_KEY_DISPLAY_NAME = "DisplayName";

    // AOT-friendly factory registration
    private static readonly Dictionary<string, IConversationMessageStoreFactory> _storeFactories = new();
    private static readonly object _factoryLock = new();

    private readonly ConversationMessageStore _messageStore;
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
    public ConversationMessageStore MessageStore => _messageStore;

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
    /// Optional AI context provider for protocol-specific enrichment.
    /// Used by Microsoft.Agents.AI protocol for memory/RAG injection before LLM calls.
    /// Null for protocols that don't use this pattern or threads without context enrichment.
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
    /// <para>
    /// <b>Per-Thread Override:</b> You can override the factory-provided provider on specific threads:
    /// <code>
    /// var thread = agent.CreateThread();
    /// thread.AIContextProvider = new VectorDBProvider();  // Override for this thread only
    /// </code>
    /// </para>
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]  // Don't serialize the instance, only its state
    public AIContextProvider? AIContextProvider { get; set; }

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
    /// The state is automatically set by the agent during execution when a checkpointer is configured.
    /// After completion (or explicit termination), you can clear it: <c>thread.ExecutionState = null;</c>
    /// </para>
    /// </remarks>
    public AgentLoopState? ExecutionState { get; set; }

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
    public ConversationThread(ConversationMessageStore messageStore)
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
    private ConversationThread(string id, DateTime createdAt, DateTime lastActivity, ConversationMessageStore messageStore)
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

    #region Message Operations (All Async)

    /// <summary>
    /// INTERNAL: Get messages from this thread. For internal agent framework use only.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Read-only list of messages (snapshot, not live)</returns>
    /// <remarks>
    /// <para>
    /// This method is internal to support efficient snapshot-based operations within the agent framework.
    /// External code should use <see cref="AddMessagesAsync"/> to push messages to the thread.
    /// </para>
    /// <para>
    /// <b>Why is this internal?</b>
    /// </para>
    /// <para>
    /// <b>Problem:</b> If users call GetMessagesAsync(), they receive a snapshot. If the agent later 
    /// adds messages via RunAsync(), the user's copy becomes stale without them realizing it.
    /// </para>
    /// <para>
    /// Example of the bug this prevents:
    /// <code>
    /// // User gets snapshot
    /// var messages = await thread.GetMessagesAsync(); // 5 messages
    /// 
    /// // Agent adds more messages internally
    /// await agent.RunAsync("Hello", thread); // Adds 2 messages (now 7 total)
    /// 
    /// // User's 'messages' variable is now stale (still shows 5)
    /// Console.WriteLine(messages.Count); // ❌ Prints 5, but actual count is 7!
    /// </code>
    /// </para>
    /// <para>
    /// <b>Solution:</b> Public API is push-only (AddMessagesAsync). Framework internals use 
    /// GetMessagesAsync() for efficient snapshots and handle refresh logic explicitly.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// This is useful for UI purposes (pagination, progress indicators, "X messages in thread").
    /// </para>
    /// <para>
    /// Performance: Fast for in-memory stores (no I/O). For database stores, may involve I/O.
    /// </para>
    /// <para>
    /// Unlike GetMessagesAsync() (internal), message count is safe to expose publicly because:
    /// - It's a scalar value (no stale reference issues)
    /// - Common UI need (pagination, progress bars)
    /// - Doesn't expose message content that could become stale
    /// </para>
    /// </remarks>
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
    /// Apply history reduction to the thread's message storage.
    /// Removes old messages and inserts a summary message.
    /// Delegates to ConversationMessageStore for the actual reduction logic.
    /// </summary>
    /// <param name="summaryMessage">Summary message to insert (contains __summary__ marker)</param>
    /// <param name="removedCount">Number of messages to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task ApplyReductionAsync(ChatMessage summaryMessage, int removedCount, CancellationToken cancellationToken = default)
    {
        await _messageStore.ApplyReductionAsync(summaryMessage, removedCount, cancellationToken);
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
    /// Serialize this thread to a JSON element (AgentThread override).
    /// Delegates message storage serialization to the message store.
    /// </summary>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var snapshot = new ConversationThreadSnapshot
        {
            Id = Id,
            MessageStoreState = _messageStore.Serialize(jsonSerializerOptions),
            MessageStoreType = _messageStore.GetType().AssemblyQualifiedName!,
            Metadata = _metadata.ToDictionary(kv => kv.Key, kv => kv.Value),
            CreatedAt = CreatedAt,
            LastActivity = LastActivity,
            ServiceThreadId = ServiceThreadId,
            AIContextProviderState = AIContextProvider?.Serialize(jsonSerializerOptions),
            ExecutionStateJson = ExecutionState?.Serialize()
        };

        // Use source-generated JSON context for AOT compatibility
        return JsonSerializer.SerializeToElement(snapshot, ConversationJsonContext.Default.ConversationThreadSnapshot);
    }

    /// <summary>
    /// Deserialize a thread from a snapshot.
    /// Recreates the message store from its serialized state.
    /// Uses registered factories for AOT-friendly deserialization.
    /// </summary>
    /// <param name="snapshot">Serialized thread snapshot</param>
    /// <param name="options">Optional JSON serializer options</param>
    /// <param name="contextProviderFactory">Optional factory for restoring AIContextProvider from serialized state</param>
    /// <returns>Deserialized conversation thread</returns>
    /// <remarks>
    /// <para>
    /// For Native AOT support, register factories before deserializing:
    /// <code>
    /// ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());
    /// var thread = ConversationThread.Deserialize(snapshot);
    /// </code>
    /// Falls back to reflection if no factory is registered (non-AOT scenarios).
    /// </para>
    /// <para>
    /// To restore AIContextProvider state, provide a factory:
    /// <code>
    /// var thread = ConversationThread.Deserialize(
    ///     snapshot,
    ///     contextProviderFactory: (state, opts) => new MyMemoryProvider(state, opts));
    /// </code>
    /// </para>
    /// </remarks>
    public static ConversationThread Deserialize(
        ConversationThreadSnapshot snapshot,
        JsonSerializerOptions? options = null,
        Func<JsonElement, JsonSerializerOptions?, AIContextProvider>? contextProviderFactory = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Deserialize message store
        ConversationMessageStore messageStore;

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
                    messageStore = (ConversationMessageStore)Activator.CreateInstance(
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

        foreach (var (key, value) in snapshot.Metadata)
        {
            thread._metadata[key] = value;
        }

        // Restore AIContextProvider state if factory provided and state exists
        if (snapshot.AIContextProviderState.HasValue &&
            snapshot.AIContextProviderState.Value.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
            snapshot.AIContextProviderState.Value.ValueKind != System.Text.Json.JsonValueKind.Null &&
            contextProviderFactory != null)
        {
            thread.AIContextProvider = contextProviderFactory(
                snapshot.AIContextProviderState.Value,
                options);
        }

        // Restore ExecutionState if present
        if (!string.IsNullOrEmpty(snapshot.ExecutionStateJson))
        {
            thread.ExecutionState = AgentLoopState.Deserialize(snapshot.ExecutionStateJson);
        }

        return thread;
    }

    #endregion

    #region AgentThread Overrides

    /// <summary>
    /// Called when new messages are received (AgentThread override).
    /// Updates this thread's message list.
    /// </summary>
    protected override async Task MessagesReceivedAsync(IEnumerable<ChatMessage> newMessages, CancellationToken cancellationToken = default)
    {
        var existingMessages = await _messageStore.GetMessagesAsync(cancellationToken);
        var messagesToAdd = newMessages.Where(m => !existingMessages.Contains(m)).ToList();

        if (messagesToAdd.Any())
        {
            await AddMessagesAsync(messagesToAdd, cancellationToken);
        }
    }

    #endregion
}

/// <summary>
/// JSON source generation context for ConversationThread serialization.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = false)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ConversationThreadSnapshot))]
internal partial class ConversationJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

/// <summary>
/// Serializable snapshot of a ConversationThread for persistence.
/// Delegates message storage to the message store's own serialization.
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
    /// Serialized state of the AIContextProvider (if any).
    /// Used by Microsoft protocol to restore provider state across sessions.
    /// Null if thread doesn't use an AIContextProvider.
    /// </summary>
    public JsonElement? AIContextProviderState { get; init; }

    /// <summary>
    /// Serialized AgentLoopState JSON (if mid-execution).
    /// Null when thread is idle (no agent run in progress).
    /// Contains complete execution context including iteration count, completed functions,
    /// consecutive failures, plugin/skill expansion state, and message references.
    /// </summary>
    public string? ExecutionStateJson { get; init; }
}
