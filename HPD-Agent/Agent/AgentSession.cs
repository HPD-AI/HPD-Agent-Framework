using Microsoft.Extensions.AI;
using System.Collections.Concurrent;
using System.Text.Json;

namespace HPD.Agent;

/// <summary>
/// Agent session for managing conversation state and execution context.
/// Stores messages, metadata, and execution state for checkpointing and resumption.
/// </summary>
/// <remarks>
/// <para><b>What is an AgentSession?</b></para>
/// <para>
/// An AgentSession represents everything needed to resume an agent:
/// - Messages (conversation history)
/// - Execution state (iteration count, pending tool calls)
/// - Middleware state (what middleware knows)
/// - Checkpoint metadata (for history/time-travel)
/// </para>
///
/// <para><b>Architecture:</b></para>
/// <para>
/// - Messages stored directly in a List (simple, no abstraction overhead)
/// - Handles: metadata, timestamps, display names, execution state, history reduction state
/// </para>
///
/// <para><b>Thread Safety:</b></para>
/// <para>
/// - All public methods are thread-safe for concurrent reads
/// - Metadata dictionary uses locking for concurrent access
/// </para>
/// </remarks>
public class AgentSession
{
    // Metadata key constants
    private const string METADATA_KEY_DISPLAY_NAME = "DisplayName";

    private readonly List<ChatMessage> _messages = new();
    private readonly Dictionary<string, object> _metadata = new();
    private readonly Dictionary<string, string> _middlewarePersistentState = new();
    private string? _serviceThreadId;

    /// <summary>
    /// Unique identifier for this session
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// When this session was created
    /// </summary>
    public DateTime CreatedAt { get; }

    /// <summary>
    /// Last time this session was updated
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
    /// Read-only view of metadata associated with this session
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
    /// Null when session is idle (no agent run in progress).
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

    /// <summary>
    /// Creates a new agent session.
    /// </summary>
    public AgentSession()
    {
        Id = Guid.NewGuid().ToString();
        CreatedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a new agent session with a specific ID.
    /// </summary>
    /// <param name="sessionId">The session identifier</param>
    public AgentSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Id = sessionId;
        CreatedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates an agent session with specific ID and timestamps (for deserialization).
    /// </summary>
    internal AgentSession(string id, DateTime createdAt, DateTime lastActivity)
    {
        Id = id;
        CreatedAt = createdAt;
        LastActivity = lastActivity;
    }

    #region Message API

    /// <summary>
    /// Gets a read-only view of all messages in this session.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages => _messages.AsReadOnly();

    /// <summary>
    /// Gets the number of messages in this session.
    /// </summary>
    public int MessageCount => _messages.Count;

    /// <summary>
    /// Adds a message to the session.
    /// </summary>
    public void AddMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Add(message);
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Adds multiple messages to the session.
    /// </summary>
    public void AddMessages(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages.AddRange(messages);
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Adds a message to the session (async for API consistency).
    /// </summary>
    public Task AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        AddMessage(message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds multiple messages to the session (async for API consistency).
    /// </summary>
    public Task AddMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        AddMessages(messages);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets messages from this session (async for API consistency).
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
    /// Clear all messages from this session.
    /// </summary>
    public void Clear()
    {
        _messages.Clear();
        _metadata.Clear();
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Clear all messages from this session (async for API consistency).
    /// </summary>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Clear();
        return Task.CompletedTask;
    }

    #endregion

    #region Metadata Operations

    /// <summary>
    /// Add metadata key/value pair to this session.
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
    /// Get a display name for this session based on first user message.
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
    /// Get a display name for this session (async for API consistency).
    /// </summary>
    public Task<string> GetDisplayNameAsync(int maxLength = 30, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetDisplayName(maxLength));
    }

    /// <summary>
    /// Convert this session to a lightweight snapshot (messages + metadata).
    /// Always works, regardless of whether ExecutionState is set.
    /// </summary>
    /// <remarks>
    /// Use this for normal session persistence after a turn completes.
    /// For crash recovery during execution, use <see cref="ToCheckpoint"/> instead.
    /// </remarks>
    public SessionSnapshot ToSnapshot()
    {
        return new SessionSnapshot
        {
            SessionId = Id,
            Messages = _messages.ToList(),
            Metadata = _metadata.ToDictionary(kv => kv.Key, kv => kv.Value),
            MiddlewarePersistentState = _middlewarePersistentState.Count > 0
                ? _middlewarePersistentState.ToDictionary(kv => kv.Key, kv => kv.Value)
                : null,
            CreatedAt = CreatedAt,
            LastActivity = LastActivity,
            ServiceThreadId = ServiceThreadId,
            ConversationId = ConversationId,
        };
    }

    /// <summary>
    /// Convert this session to an execution checkpoint for crash recovery.
    /// Only works when ExecutionState is set (during agent execution).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This creates an <see cref="ExecutionCheckpoint"/> which contains only ExecutionState.
    /// Messages are stored in ExecutionState.CurrentMessages (no duplication).
    /// </para>
    /// <para>
    /// Use this for crash recovery during execution.
    /// For normal session persistence after completion, use <see cref="ToSnapshot"/> instead.
    /// </para>
    /// </remarks>
    /// <param name="checkpointId">Optional checkpoint ID. If not provided, a new GUID is generated.</param>
    /// <exception cref="InvalidOperationException">Thrown when ExecutionState is null.</exception>
    public ExecutionCheckpoint ToExecutionCheckpoint(string? checkpointId = null)
    {
        if (ExecutionState == null)
            throw new InvalidOperationException(
                "Cannot create ExecutionCheckpoint without ExecutionState. " +
                "Use ToSnapshot() for saving session state after completion.");

        return new ExecutionCheckpoint
        {
            SessionId = Id,
            ExecutionCheckpointId = checkpointId ?? Guid.NewGuid().ToString(),
            ExecutionState = ExecutionState,
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// [DEPRECATED] Use <see cref="ToExecutionCheckpoint"/> instead.
    /// </summary>
    [Obsolete("Use ToExecutionCheckpoint() for crash recovery or ToSnapshot() for persistence")]
    public SessionCheckpoint ToCheckpoint()
    {
        if (ExecutionState == null)
            throw new InvalidOperationException(
                "Cannot create SessionCheckpoint without ExecutionState. " +
                "Use ToSnapshot() for saving session state after completion.");

        return new SessionCheckpoint
        {
            SessionId = Id,
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
    /// Create an AgentSession from a snapshot (messages + metadata only).
    /// ExecutionState will be null.
    /// </summary>
    public static AgentSession FromSnapshot(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var session = new AgentSession(
            snapshot.SessionId,
            snapshot.CreatedAt,
            snapshot.LastActivity);

        if (snapshot.Messages != null)
            session._messages.AddRange(snapshot.Messages);

        if (snapshot.Metadata != null)
        {
            foreach (var (key, value) in snapshot.Metadata)
                session._metadata[key] = value;
        }

        session._serviceThreadId = snapshot.ServiceThreadId;
        session.ConversationId = snapshot.ConversationId;

        if (snapshot.MiddlewarePersistentState != null)
        {
            foreach (var (key, value) in snapshot.MiddlewarePersistentState)
                session._middlewarePersistentState[key] = value;
        }

        // ExecutionState stays null for snapshots
        return session;
    }

    /// <summary>
    /// Create an AgentSession from an execution checkpoint (for crash recovery).
    /// Messages are restored from ExecutionState.CurrentMessages.
    /// </summary>
    public static AgentSession FromExecutionCheckpoint(ExecutionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var session = new AgentSession(
            checkpoint.SessionId,
            checkpoint.CreatedAt,
            DateTime.UtcNow); // LastActivity = now (we're resuming)

        // Restore messages from ExecutionState (single source of truth)
        if (checkpoint.ExecutionState.CurrentMessages != null)
        {
            session._messages.AddRange(checkpoint.ExecutionState.CurrentMessages);
        }

        // Restore ExecutionState for resumption
        session.ExecutionState = checkpoint.ExecutionState;

        return session;
    }

    /// <summary>
    /// [DEPRECATED] Use <see cref="FromExecutionCheckpoint"/> instead.
    /// </summary>
    [Obsolete("Use FromExecutionCheckpoint() for crash recovery or FromSnapshot() for persistence")]
    public static AgentSession FromCheckpoint(SessionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        // Use FromSnapshot for the base properties
        var session = FromSnapshot(checkpoint);

        // Add ExecutionState from checkpoint
        session.ExecutionState = checkpoint.ExecutionState;
        return session;
    }
}

//──────────────────────────────────────────────────────────────────
// SESSION SNAPSHOT: Lightweight session state (messages + metadata)
//──────────────────────────────────────────────────────────────────

/// <summary>
/// Lightweight snapshot of session state (messages, metadata, middleware persistent state).
/// Used for saving conversation state after a turn completes (~20KB).
/// </summary>
/// <remarks>
/// <para>
/// Use this for normal session persistence after successful completion.
/// For crash recovery during execution, use <see cref="ExecutionCheckpoint"/> instead.
/// </para>
/// <para>
/// <strong>Key difference from ExecutionCheckpoint:</strong>
/// SessionSnapshot stores messages directly, while ExecutionCheckpoint stores them
/// inside ExecutionState.CurrentMessages. This separation eliminates duplication.
/// </para>
/// </remarks>
public record SessionSnapshot
{
    /// <summary>
    /// Gets the identifier of the session this snapshot represents.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Unique identifier for this snapshot (for history tracking).
    /// </summary>
    public string? SessionSnapshotId { get; init; }

    /// <summary>
    /// Gets the ordered list of chat messages captured in the snapshot.
    /// May be empty but is never <c>null</c> for a valid snapshot.
    /// </summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Gets the session metadata persisted with this snapshot.
    /// Keys are metadata names and values are the serialized objects stored.
    /// </summary>
    public required Dictionary<string, object> Metadata { get; init; }

    /// <summary>
    /// Gets optional persistent state maintained by middleware components.
    /// This is a nullable dictionary of string keys and string values.
    /// </summary>
    public Dictionary<string, string>? MiddlewarePersistentState { get; init; }

    /// <summary>
    /// Gets the UTC date/time when this session was created.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// Gets the UTC date/time of the last activity recorded in this snapshot.
    /// </summary>
    public required DateTime LastActivity { get; init; }

    /// <summary>
    /// Gets an optional service-specific thread identifier. May be <c>null</c>.
    /// </summary>
    public string? ServiceThreadId { get; init; }

    /// <summary>
    /// Gets an optional conversation identifier associated with this snapshot.
    /// May be <c>null</c> when not applicable.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Version for schema evolution.
    /// </summary>
    public int Version { get; init; } = 1;
}

//──────────────────────────────────────────────────────────────────
// EXECUTION CHECKPOINT: For crash recovery during execution (~100KB)
//──────────────────────────────────────────────────────────────────

/// <summary>
/// Checkpoint for crash recovery during agent execution.
/// Contains only ExecutionState - messages are inside ExecutionState.CurrentMessages.
/// </summary>
/// <remarks>
/// <para>
/// This type is separate from <see cref="SessionSnapshot"/> to avoid message duplication.
/// Messages exist only in ExecutionState.CurrentMessages, not stored separately.
/// </para>
/// <para>
/// <strong>When to use:</strong>
/// <list type="bullet">
/// <item>During agent execution for crash recovery (DurableExecution)</item>
/// <item>For time-travel debugging and resumption</item>
/// </list>
/// </para>
/// <para>
/// <strong>When NOT to use:</strong>
/// <list type="bullet">
/// <item>After turn completes - use <see cref="SessionSnapshot"/> instead</item>
/// <item>For normal conversation persistence</item>
/// </list>
/// </para>
/// </remarks>
public record ExecutionCheckpoint
{
    /// <summary>
    /// Session ID this checkpoint belongs to.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Unique identifier for this execution checkpoint.
    /// </summary>
    public required string ExecutionCheckpointId { get; init; }

    /// <summary>
    /// Agent execution state containing all runtime data including messages.
    /// Messages are in ExecutionState.CurrentMessages (single source of truth).
    /// </summary>
    public required AgentLoopState ExecutionState { get; init; }

    /// <summary>
    /// When this checkpoint was created (UTC).
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// Version for schema evolution.
    /// </summary>
    public int Version { get; init; } = 1;
}

//──────────────────────────────────────────────────────────────────
// LEGACY: SessionCheckpoint (deprecated, kept for backward compat)
//──────────────────────────────────────────────────────────────────

/// <summary>
/// [DEPRECATED] Use <see cref="ExecutionCheckpoint"/> for crash recovery
/// or <see cref="SessionSnapshot"/> for conversation persistence.
/// </summary>
/// <remarks>
/// This type is kept temporarily for migration purposes.
/// It will be removed in a future version.
/// </remarks>
[Obsolete("Use ExecutionCheckpoint for crash recovery or SessionSnapshot for persistence")]
public record SessionCheckpoint : SessionSnapshot
{
    /// <summary>
    /// Agent execution state (iteration, middleware runtime state, etc.).
    /// </summary>
    public required AgentLoopState ExecutionState { get; init; }
}
