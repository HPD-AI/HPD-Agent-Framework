using Microsoft.Extensions.AI;
using System.Collections.Concurrent;
using System.Text.Json;

namespace HPD.Agent;

/// <summary>
/// Conversation thread for managing conversation state.
/// Stores messages, metadata, and execution state for checkpointing.
/// </summary>
/// <remarks>
/// <para><b>Architecture:</b></para>
/// <para>
/// - Messages stored directly in a List (simple, no abstraction overhead)
/// - Handles: metadata, timestamps, display names, execution state, history reduction state
/// - Protocol-agnostic: No dependencies on Microsoft.Agents.AI or other protocols
/// </para>
///
/// <para><b>Thread Safety:</b></para>
/// <para>
/// - All public methods are thread-safe for concurrent reads
/// - Metadata dictionary uses locking for concurrent access
/// </para>
/// </remarks>
public sealed class ConversationThread
{
    // Metadata key constants
    private const string METADATA_KEY_DISPLAY_NAME = "DisplayName";

    private readonly List<ChatMessage> _messages = new();
    private readonly Dictionary<string, object> _metadata = new();
    private readonly Dictionary<string, string> _middlewarePersistentState = new();
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
    public string? ConversationId { get; set; }

    /// <summary>
    /// Current agent execution state for checkpointing and resumption.
    /// Null when thread is idle (no agent run in progress).
    /// </summary>
    public AgentLoopState? ExecutionState { get; set; }

    /// <summary>
    /// Persistent state for middlewares that need to cache data across runs.
    /// Uses type-safe keys generated from middleware state types.
    /// </summary>
    internal IReadOnlyDictionary<string, string> MiddlewarePersistentState
        => _middlewarePersistentState;

    /// <summary>
    /// Sets middleware persistent state for a given key.
    /// </summary>
    internal void SetMiddlewarePersistentState(string key, string jsonValue)
    {
        _middlewarePersistentState[key] = jsonValue;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets middleware persistent state for a given key.
    /// </summary>
    internal string? GetMiddlewarePersistentState(string key)
    {
        return _middlewarePersistentState.TryGetValue(key, out var value) ? value : null;
    }

    // Branch tracking removed - now an application-level concern (see BranchManager)
    // Applications should use ConversationId to link threads to conversations

    /// <summary>
    /// Creates a new conversation thread.
    /// </summary>
    public ConversationThread()
    {
        Id = Guid.NewGuid().ToString();
        CreatedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a conversation thread with specific ID and timestamps (for deserialization).
    /// </summary>
    private ConversationThread(string id, DateTime createdAt, DateTime lastActivity)
    {
        Id = id;
        CreatedAt = createdAt;
        LastActivity = lastActivity;
    }

    #region Message API

    /// <summary>
    /// Gets a read-only view of all messages in this conversation thread.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages => _messages.AsReadOnly();

    /// <summary>
    /// Gets the number of messages in this conversation thread.
    /// </summary>
    public int MessageCount => _messages.Count;

    /// <summary>
    /// Adds a message to the conversation thread.
    /// </summary>
    public void AddMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Add(message);
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Adds multiple messages to the conversation thread.
    /// </summary>
    public void AddMessages(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages.AddRange(messages);
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Adds a message to the conversation thread (async for API consistency).
    /// </summary>
    public Task AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        AddMessage(message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds multiple messages to the conversation thread (async for API consistency).
    /// </summary>
    public Task AddMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        AddMessages(messages);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets messages from this thread (async for API consistency).
    /// </summary>
    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ChatMessage>>(_messages.AsReadOnly());
    }

    /// <summary>
    /// Gets the number of messages (async for API consistency).
    /// </summary>
    public Task<int> GetMessageCountAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_messages.Count);
    }

    /// <summary>
    /// Clear all messages from this thread.
    /// </summary>
    public void Clear()
    {
        _messages.Clear();
        _metadata.Clear();
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Clear all messages from this thread (async for API consistency).
    /// </summary>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Clear();
        return Task.CompletedTask;
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
    public string GetDisplayName(int maxLength = 30)
    {
        // Check for explicit display name in metadata first
        if (_metadata.TryGetValue(METADATA_KEY_DISPLAY_NAME, out var name) && !string.IsNullOrEmpty(name?.ToString()))
        {
            return name.ToString()!;
        }

        // Find first user message and extract text content
        var firstUserMessage = _messages.FirstOrDefault(m => m.Role == ChatRole.User);
        if (firstUserMessage == null)
            return "New Conversation";

        var text = firstUserMessage.Text ?? string.Empty;
        if (text.Length <= maxLength)
            return text;

        return text.Substring(0, maxLength - 3) + "...";
    }

    /// <summary>
    /// Get a display name for this thread (async for API consistency).
    /// </summary>
    public Task<string> GetDisplayNameAsync(int maxLength = 30, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetDisplayName(maxLength));
    }

    /// <summary>
    /// Convert this thread to a full execution checkpoint (includes ExecutionState).
    /// </summary>
    public ExecutionCheckpoint ToExecutionCheckpoint()
    {
        if (ExecutionState == null)
            throw new InvalidOperationException("Cannot create ExecutionCheckpoint without ExecutionState");

        return new ExecutionCheckpoint
        {
            ThreadId = Id,
            Messages = _messages.ToList(),
            Metadata = _metadata.ToDictionary(kv => kv.Key, kv => kv.Value),
            MiddlewarePersistentState = _middlewarePersistentState.Count > 0
                ? _middlewarePersistentState.ToDictionary(kv => kv.Key, kv => kv.Value)
                : null,
            ExecutionState = ExecutionState,
            CreatedAt = CreatedAt,
            LastActivity = LastActivity,
            ServiceThreadId = ServiceThreadId,
            ConversationId = ConversationId,
        };
    }

    /// <summary>
    /// Create a ConversationThread from a full execution checkpoint.
    /// </summary>
    public static ConversationThread FromExecutionCheckpoint(ExecutionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var thread = new ConversationThread(
            checkpoint.ThreadId,
            checkpoint.CreatedAt,
            checkpoint.LastActivity);

        if (checkpoint.Messages != null)
            thread._messages.AddRange(checkpoint.Messages);

        foreach (var (key, value) in checkpoint.Metadata)
            thread._metadata[key] = value;

        thread._serviceThreadId = checkpoint.ServiceThreadId;
        thread.ConversationId = checkpoint.ConversationId;

        if (checkpoint.MiddlewarePersistentState != null)
        {
            foreach (var (key, value) in checkpoint.MiddlewarePersistentState)
                thread._middlewarePersistentState[key] = value;
        }

        thread.ExecutionState = checkpoint.ExecutionState;
        return thread;
    }

}

//──────────────────────────────────────────────────────────────────
// EXECUTION CHECKPOINT: Full checkpoint for durable execution (~120KB+)
//──────────────────────────────────────────────────────────────────

/// <summary>
/// Full execution checkpoint including agent runtime state.
/// Used by DurableExecution for crash recovery and resumption.
/// </summary>
/// <remarks>
/// Contains all conversation state PLUS the heavyweight AgentLoopState (~100KB).
/// Only used when resuming mid-execution or saving checkpoints during agent runs.
/// </remarks>
public record ExecutionCheckpoint
{
    /// <summary>
    /// Gets the identifier of the conversation thread this checkpoint represents.
    /// </summary>
    public required string ThreadId { get; init; }

    /// <summary>
    /// Gets the ordered list of chat messages captured in the checkpoint.
    /// May be empty but is never <c>null</c> for a valid checkpoint.
    /// </summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Gets the conversation metadata persisted with this checkpoint.
    /// Keys are metadata names and values are the serialized objects stored.
    /// </summary>
    public required Dictionary<string, object> Metadata { get; init; }

    /// <summary>
    /// Gets optional persistent state maintained by middleware components.
    /// This is a nullable dictionary of string keys and string values.
    /// </summary>
    public Dictionary<string, string>? MiddlewarePersistentState { get; init; }

    /// <summary>
    /// Agent execution state (iteration, middleware runtime state, etc.).
    /// This is the heavyweight component (~100KB).
    /// </summary>
    public required AgentLoopState ExecutionState { get; init; }

    /// <summary>
    /// Gets the UTC date/time when this checkpoint was created.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// Gets the UTC date/time of the last activity recorded in this checkpoint.
    /// </summary>
    public required DateTime LastActivity { get; init; }

    /// <summary>
    /// Gets an optional service-specific thread identifier. May be <c>null</c>.
    /// </summary>
    public string? ServiceThreadId { get; init; }

    /// <summary>
    /// Gets an optional conversation identifier associated with this checkpoint.
    /// May be <c>null</c> when not applicable.
    /// </summary>
    public string? ConversationId { get; init; }

    // Branch tracking removed - application-level concern

    /// <summary>
    /// Version for schema evolution.
    /// </summary>
    public int Version { get; init; } = 1;
}
