using HPD.Agent;
using HPD.Agent.Hosting.Data;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Hosting.Extensions;

/// <summary>
/// Extension methods for converting between domain objects and DTOs.
/// </summary>
public static class DtoMappingExtensions
{
    /// <summary>
    /// Convert a Session to a SessionDto.
    /// </summary>
    public static SessionDto ToDto(this Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SessionDto(
            session.Id,
            session.CreatedAt,
            session.LastActivity,
            session.Metadata.Count > 0 ? session.Metadata : null);
    }

    /// <summary>
    /// Convert a Branch to a BranchDto.
    /// </summary>
    public static BranchDto ToDto(this Branch branch, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return new BranchDto(
            branch.Id,
            sessionId,
            branch.GetDisplayName(),
            branch.Description,
            branch.ForkedFrom,
            branch.ForkedAtMessageIndex,
            branch.CreatedAt,
            branch.LastActivity,
            branch.MessageCount,
            branch.Tags,
            branch.Ancestors);
    }

    /// <summary>
    /// Convert a ChatMessage to a MessageDto.
    /// </summary>
    public static MessageDto ToDto(this ChatMessage message, int index, DateTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new MessageDto(
            $"msg-{index}",
            message.Role.Value,
            message.Text ?? "",
            timestamp.ToString("O"));
    }

    /// <summary>
    /// Convert ContentInfo to AssetDto.
    /// </summary>
    public static AssetDto ToDto(this ContentInfo asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        return new AssetDto(
            asset.Id,
            asset.ContentType,
            asset.SizeBytes,
            asset.CreatedAt.ToString("O"));
    }

    /// <summary>
    /// Convert StreamMessage to ChatMessage.
    /// </summary>
    public static ChatMessage ToChatMessage(this StreamMessage streamMessage)
    {
        ArgumentNullException.ThrowIfNull(streamMessage);

        var role = streamMessage.Role?.ToLowerInvariant() switch
        {
            "user" => ChatRole.User,
            "assistant" => ChatRole.Assistant,
            "system" => ChatRole.System,
            _ => ChatRole.User
        };

        return new ChatMessage(role, streamMessage.Content);
    }

    /// <summary>
    /// Convert StreamRunConfigDto to AgentRunConfig.
    /// Only maps serializable properties.
    /// </summary>
    public static AgentRunConfig? ToAgentRunConfig(this StreamRunConfigDto? dto)
    {
        if (dto == null) return null;

        var config = new AgentRunConfig();

        if (dto.ProviderKey != null)
            config.ProviderKey = dto.ProviderKey;

        if (dto.ModelId != null)
            config.ModelId = dto.ModelId;

        if (dto.AdditionalSystemInstructions != null)
            config.AdditionalSystemInstructions = dto.AdditionalSystemInstructions;

        if (dto.ContextOverrides != null)
            config.ContextOverrides = dto.ContextOverrides;

        if (dto.PermissionOverrides != null)
            config.PermissionOverrides = dto.PermissionOverrides;

        if (dto.CoalesceDeltas.HasValue)
            config.CoalesceDeltas = dto.CoalesceDeltas.Value;

        if (dto.SkipTools.HasValue)
            config.SkipTools = dto.SkipTools.Value;

        if (dto.RunTimeout != null && TimeSpan.TryParse(dto.RunTimeout, out var timeout))
            config.RunTimeout = timeout;

        // Map chat options
        if (dto.Chat != null)
        {
            var chatOptions = new ChatRunConfig();

            if (dto.Chat.Temperature.HasValue)
                chatOptions.Temperature = dto.Chat.Temperature.Value;

            if (dto.Chat.MaxOutputTokens.HasValue)
                chatOptions.MaxOutputTokens = dto.Chat.MaxOutputTokens.Value;

            if (dto.Chat.TopP.HasValue)
                chatOptions.TopP = dto.Chat.TopP.Value;

            if (dto.Chat.FrequencyPenalty.HasValue)
                chatOptions.FrequencyPenalty = dto.Chat.FrequencyPenalty.Value;

            if (dto.Chat.PresencePenalty.HasValue)
                chatOptions.PresencePenalty = dto.Chat.PresencePenalty.Value;

            config.Chat = chatOptions;
        }

        return config;
    }
}
