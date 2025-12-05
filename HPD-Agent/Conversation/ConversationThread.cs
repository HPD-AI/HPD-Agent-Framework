using Microsoft.Extensions.AI;
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
    /// Last successful history reduction state for cache-aware reduction.
    /// Persists across multiple agent runs to enable reduction cache hits.
    /// Null if history reduction has never been performed on this thread.
    /// </summary>
    public HistoryReductionStateData? LastReduction { get; set; }

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

    #region Serialization

    /// <summary>
    /// Serialize this thread to a snapshot object.
    /// </summary>
    public ConversationThreadSnapshot Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        return new ConversationThreadSnapshot
        {
            Id = Id,
            Messages = _messages.ToList(),
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
    /// </summary>
    public static ConversationThread Deserialize(
        ConversationThreadSnapshot snapshot,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var thread = new ConversationThread(
            snapshot.Id,
            snapshot.CreatedAt,
            snapshot.LastActivity);

        // Restore messages
        if (snapshot.Messages != null)
        {
            thread._messages.AddRange(snapshot.Messages);
        }

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
/// </summary>
public record ConversationThreadSnapshot
{
    public required string Id { get; init; }

    /// <summary>
    /// All messages in the conversation.
    /// </summary>
    public List<ChatMessage>? Messages { get; init; }

    public required Dictionary<string, object> Metadata { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime LastActivity { get; init; }
    public string? ServiceThreadId { get; init; }

    /// <summary>
    /// Conversation identifier for server-side history tracking.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Serialized AgentLoopState JSON (if mid-execution).
    /// </summary>
    public string? ExecutionStateJson { get; init; }

    /// <summary>
    /// Last successful history reduction state for cache-aware reduction.
    /// </summary>
    public HistoryReductionStateData? LastReductionState { get; init; }
}
