
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace HPD.Agent;

/// <summary>
/// Abstract base class for conversation message storage with token tracking capabilities.
/// PROTOCOL-AGNOSTIC: This class has no dependencies on any specific agent framework.
///
/// Key Features:
/// - Token counting: Tracks both provider-accurate and estimated token counts
/// - Template Method pattern: Derived classes implement storage backend (in-memory, database, etc.)
/// - Full history storage: Always stores complete message history (reduction is applied at runtime)
///
/// Architecture:
/// This provides:
/// 1. Token counting methods - SHARED across all implementations
/// 2. Abstract storage methods - IMPLEMENTED by derived classes
/// 3. Protocol-agnostic message store contract compatible with any framework
///
/// Derived classes must implement:
/// - LoadMessagesAsync(): Load all messages from storage
/// - SaveMessagesAsync(): Replace all messages in storage
/// - AppendMessageAsync(): Add a single message to storage
/// - ClearAsync(): Remove all messages from storage
///
/// Cache-aware history reduction is handled by HistoryReductionStateData in ConversationThread,
/// not by mutating the message store. Messages are reduced at runtime for LLM calls only.
///
/// </summary>
public abstract class ChatMessageStore
{
    #region Abstract Storage Methods - Derived Classes Must Implement

    /// <summary>
    /// Load all messages from storage.
    /// Implementation should return messages in chronological order (oldest first).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of all messages in chronological order</returns>
    protected abstract Task<List<ChatMessage>> LoadMessagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace all messages in storage with the provided list.
    /// Used by reduction to persist the reduced message set.
    /// </summary>
    /// <param name="messages">Messages to save (in chronological order)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    protected abstract Task SaveMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Append a single message to storage.
    /// Should maintain chronological order.
    /// </summary>
    /// <param name="message">Message to append</param>
    /// <param name="cancellationToken">Cancellation token</param>
    protected abstract Task AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove all messages from storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public abstract Task ClearAsync(CancellationToken cancellationToken = default);

    #endregion

    #region Message Store Public API

    /// <summary>
    /// Retrieves all messages in chronological order (oldest first).
    /// This is the primary method for accessing stored messages.
    /// Delegates to LoadMessagesAsync() implemented by derived classes.
    /// </summary>
    public virtual async Task<IEnumerable<ChatMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        // NOTE: We DON'T apply reduction at storage time.
        // Our architecture uses Agent-detected reduction + Conversation-applied pattern.
        // Reduction is applied at runtime for LLM calls only.
        return await LoadMessagesAsync(cancellationToken);
    }

    /// <summary>
    /// Adds new messages to the store.
    /// This is the primary method for adding messages.
    /// Delegates to AppendMessageAsync() implemented by derived classes.
    /// </summary>
    public virtual async Task AddMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages == null)
            throw new ArgumentNullException(nameof(messages));

        foreach (var message in messages)
        {
            await AppendMessageAsync(message, cancellationToken);
        }

        // NOTE: We DON'T apply reduction at storage time.
        // Our architecture uses Agent-detected reduction + Conversation-applied pattern.
    }

    /// <summary>
    /// Serializes the message store state to JSON.
    /// Must be implemented by derived classes to serialize their specific state.
    /// </summary>
    public abstract JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null);

    #endregion

}
