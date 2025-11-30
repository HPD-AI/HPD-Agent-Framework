// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Extension methods for ChatOptions to support immutable-style updates.
/// </summary>
/// <remarks>
/// <para><b>Why This Exists:</b></para>
/// <para>
/// ChatOptions is a class, not a record, so we cannot use <c>with</c> expressions.
/// This extension centralizes property copying to avoid silent property loss when
/// ChatOptions evolves with new properties.
/// </para>
///
/// <para><b>Maintenance:</b></para>
/// <para>
/// When Microsoft.Extensions.AI adds new properties to ChatOptions,
/// update this file to copy them. This is the single location to update.
/// </para>
/// </remarks>
internal static class ChatOptionsExtensions
{
    /// <summary>
    /// Creates a shallow copy of ChatOptions with modified tools.
    /// </summary>
    /// <param name="options">Source options to copy</param>
    /// <param name="tools">New tools list to use</param>
    /// <returns>New ChatOptions instance with updated tools</returns>
    public static ChatOptions WithTools(this ChatOptions options, IList<AITool> tools)
    {
        return new ChatOptions
        {
            ModelId = options.ModelId,
            Tools = tools,
            ToolMode = options.ToolMode,
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            TopP = options.TopP,
            TopK = options.TopK,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            StopSequences = options.StopSequences,
            ResponseFormat = options.ResponseFormat,
            Seed = options.Seed,
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            Instructions = options.Instructions,
            RawRepresentationFactory = options.RawRepresentationFactory,
            AdditionalProperties = options.AdditionalProperties,
            ConversationId = options.ConversationId
        };
    }

    /// <summary>
    /// Creates a shallow copy of ChatOptions with a modified conversation ID.
    /// </summary>
    /// <param name="options">Source options to copy</param>
    /// <param name="conversationId">New conversation ID to use</param>
    /// <returns>New ChatOptions instance with updated conversation ID</returns>
    public static ChatOptions WithConversationId(this ChatOptions options, string? conversationId)
    {
        return new ChatOptions
        {
            ModelId = options.ModelId,
            Tools = options.Tools,
            ToolMode = options.ToolMode,
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            TopP = options.TopP,
            TopK = options.TopK,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            StopSequences = options.StopSequences,
            ResponseFormat = options.ResponseFormat,
            Seed = options.Seed,
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            Instructions = options.Instructions,
            RawRepresentationFactory = options.RawRepresentationFactory,
            AdditionalProperties = options.AdditionalProperties,
            ConversationId = conversationId
        };
    }
}
