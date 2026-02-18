namespace HPD.Agent.Hosting.Data;

/// <summary>
/// Data transfer object for a chat message.
/// </summary>
/// <param name="Id">Message identifier (e.g., "msg-0", "msg-1")</param>
/// <param name="Role">Message role (user, assistant, system)</param>
/// <param name="Content">Message text content</param>
/// <param name="Timestamp">When this message was created (ISO 8601 format)</param>
public record MessageDto(
    string Id,
    string Role,
    string Content,
    string Timestamp);
