using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;
using HPDChatMessageStore = HPD.Agent.ChatMessageStore;

namespace HPD.Agent.Microsoft;

/// <summary>
/// Adapter that bridges HPD's protocol-agnostic ChatMessageStore to Microsoft's ChatMessageStore.
/// Enables use of HPD message stores with Microsoft.Agents.AI framework APIs.
/// </summary>
/// <remarks>
/// <para>
/// This adapter implements the Adapter pattern to make HPD's core message store
/// compatible with Microsoft.Agents.AI's ChatMessageStore interface.
/// </para>
/// <para>
/// <b>Usage:</b>
/// <code>
/// // Wrap HPD store for use with Microsoft APIs
/// var hpdStore = new InMemoryConversationMessageStore();
/// var microsoftStore = new MicrosoftChatMessageStoreAdapter(hpdStore);
///
/// // Use with Microsoft.Agents.AI APIs that expect ChatMessageStore
/// await microsoftStore.AddMessagesAsync(messages);
/// </code>
/// </para>
/// <para>
/// <b>Note:</b> In most cases, you don't need to use this adapter directly.
/// The ConversationThread classes handle adaptation automatically.
/// </para>
/// </remarks>
public sealed class ChatMessageStore : global::Microsoft.Agents.AI.ChatMessageStore
{
    private readonly HPDChatMessageStore _hpdStore;

    /// <summary>
    /// Creates a new adapter wrapping an HPD ChatMessageStore.
    /// </summary>
    /// <param name="hpdStore">The HPD message store to wrap</param>
    /// <exception cref="ArgumentNullException">Thrown when hpdStore is null</exception>
    public ChatMessageStore(HPDChatMessageStore hpdStore)
    {
        _hpdStore = hpdStore ?? throw new ArgumentNullException(nameof(hpdStore));
    }

    /// <summary>
    /// Gets the wrapped HPD message store.
    /// </summary>
    public HPDChatMessageStore HpdStore => _hpdStore;

    /// <summary>
    /// Retrieves all messages from the HPD store.
    /// Delegates to the wrapped HPD ChatMessageStore.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of messages in chronological order</returns>
    public override Task<IEnumerable<ChatMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        return _hpdStore.GetMessagesAsync(cancellationToken);
    }

    /// <summary>
    /// Adds messages to the HPD store.
    /// Delegates to the wrapped HPD ChatMessageStore.
    /// </summary>
    /// <param name="messages">Messages to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public override Task AddMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        return _hpdStore.AddMessagesAsync(messages, cancellationToken);
    }

    /// <summary>
    /// Serializes the HPD store state to JSON.
    /// Delegates to the wrapped HPD ChatMessageStore.
    /// </summary>
    /// <param name="jsonSerializerOptions">Optional JSON serializer options</param>
    /// <returns>Serialized store state</returns>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        return _hpdStore.Serialize(jsonSerializerOptions);
    }

    /// <summary>
    /// Service discovery - supports retrieving the wrapped HPD store.
    /// </summary>
    /// <param name="serviceType">Type of service to retrieve</param>
    /// <param name="serviceKey">Optional service key</param>
    /// <returns>Service instance if found, null otherwise</returns>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        // Allow retrieval of the wrapped HPD store
        if (serviceKey == null && serviceType == typeof(HPDChatMessageStore))
        {
            return _hpdStore;
        }

        // Allow retrieval of the specific HPD store implementation type
        if (serviceKey == null && serviceType.IsInstanceOfType(_hpdStore))
        {
            return _hpdStore;
        }

        return base.GetService(serviceType, serviceKey);
    }
}
