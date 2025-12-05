using Microsoft.Extensions.AI;
using System.Text.Json;
using HPD.Agent;

/// <summary>
/// In-memory implementation of ChatMessageStore.
/// Messages are stored in a List in local memory.
/// This is the default storage mechanism for conversation threads.
/// </summary>
public sealed class InMemoryConversationMessageStore : ChatMessageStore
{
    private readonly List<ChatMessage> _messages = new();

    /// <summary>
    /// Read-only view of messages for inspection (non-async accessor).
    /// For synchronous access to messages in memory.
    /// Returns a snapshot (copy) to prevent mutation of internal state.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages => new List<ChatMessage>(_messages).AsReadOnly();

    /// <summary>
    /// Number of messages currently stored.
    /// </summary>
    public int Count => _messages.Count;

    /// <summary>
    /// Creates an empty in-memory message store.
    /// </summary>
    public InMemoryConversationMessageStore()
    {
    }

    /// <summary>
    /// Creates an in-memory message store from serialized state.
    /// Enables restoration of conversation history from persistence.
    /// </summary>
    /// <param name="serializedState">Previously serialized store state</param>
    /// <param name="jsonSerializerOptions">Optional JSON serialization options</param>
    public InMemoryConversationMessageStore(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        if (serializedState.ValueKind is JsonValueKind.Object)
        {
            // Extract messages array directly instead of deserializing wrapper (AOT-compatible)
            if (serializedState.TryGetProperty("messages", out var messagesElement) ||
                serializedState.TryGetProperty("Messages", out messagesElement))
            {
                var messages = messagesElement.Deserialize(HPDJsonContext.Default.ListChatMessage);
                if (messages != null)
                {
                    _messages.AddRange(messages);
                }
            }
        }
    }

    #region Abstract Storage Implementation

    protected override Task<List<ChatMessage>> LoadMessagesAsync(CancellationToken cancellationToken = default)
    {
        // In-memory: just return the list (no I/O, but keep interface consistent)
        return Task.FromResult(_messages);
    }

    protected override Task SaveMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        _messages.Clear();
        _messages.AddRange(messages);
        return Task.CompletedTask;
    }

    protected override Task AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Add(message);
        return Task.CompletedTask;
    }

    public override Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _messages.Clear();
        return Task.CompletedTask;
    }

    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        // Serialize messages directly using HPDJsonContext (AOT-compatible)
        var messagesList = _messages.ToList();
        var element = JsonSerializer.SerializeToElement(messagesList, HPDJsonContext.Default.ListChatMessage);
        
        // Wrap in object with "Messages" property for backwards compatibility
        var jsonObject = new System.Text.Json.Nodes.JsonObject
        {
            ["Messages"] = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText())
        };
        
        return JsonSerializer.SerializeToElement(jsonObject, HPDJsonContext.Default.JsonObject);
    }

    #endregion

    #region Serialization

    private sealed class StoreState
    {
        public List<ChatMessage> Messages { get; set; } = new();
    }

    #endregion
}

/// <summary>
/// Factory for creating InMemoryConversationMessageStore instances from serialized state.
/// Enables AOT-friendly deserialization without reflection.
/// </summary>
/// <remarks>
/// Register this factory at application startup:
/// <code>
/// ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());
/// </code>
/// </remarks>
internal sealed class InMemoryConversationMessageStoreFactory : IConversationMessageStoreFactory
{
    /// <inheritdoc/>
    public string StoreTypeName => typeof(InMemoryConversationMessageStore).AssemblyQualifiedName!;

    /// <inheritdoc/>
    public ChatMessageStore CreateFromSnapshot(JsonElement state, JsonSerializerOptions? options)
    {
        return new InMemoryConversationMessageStore(state, options);
    }
}
