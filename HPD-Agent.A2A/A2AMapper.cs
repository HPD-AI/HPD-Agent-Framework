using A2A;
using Microsoft.Extensions.AI;
using System;
using System.Linq;

namespace HPD.Agent.A2A;

/// <summary>
/// Mapper for converting between A2A protocol types and HPD-Agent types.
/// Refactored to work with core agent (no Microsoft adapter dependency).
/// </summary>
public static class A2AMapper
{
    /// <summary>
    /// Converts an A2A Message to an HPD-Agent ChatMessage.
    /// </summary>
    public static ChatMessage ToHpdChatMessage(Message a2aMessage)
    {
        var textContent = a2aMessage.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? string.Empty;
        var role = a2aMessage.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant;

        // This assumes simple text content for now.
        // It can be expanded to handle other Part types like FilePart.
        return new ChatMessage(role, textContent);
    }

    /// <summary>
    /// Converts response text from HPD-Agent into an A2A Artifact.
    /// Updated to accept string directly instead of AgentRunResponse.
    /// </summary>
    public static Artifact ToA2AArtifact(string responseText)
    {
        return new Artifact
        {
            ArtifactId = Guid.NewGuid().ToString(),
            Parts = [new TextPart { Text = responseText ?? "No response." }]
        };
    }
}
