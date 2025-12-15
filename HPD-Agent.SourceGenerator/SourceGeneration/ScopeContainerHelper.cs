using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Shared helper for generating Collapse container descriptions and metadata.
/// Used by both HPDToolSourceGenerator and SkillCodeGenerator to avoid code duplication.
/// </summary>
internal static class CollapseContainerHelper
{
    /// <summary>
    /// Generates a Mermaid flowchart showing the invocation flow for a Collapsed container.
    /// Format: A[Invoke PluginName] --> B{Access Granted} B --> C[Function1] & D[Function2] & ...
    /// </summary>
    public static string GenerateMermaidFlow(string toolName, List<string> capabilities)
    {
        var functionNodes = string.Join(" & ", capabilities.Select((name, index) => $"{(char)('C' + index)}[{name}]"));
        return $"A[Invoke {toolName}] --> B{{{{Access Granted}}}} B -->|direct callable functions* after an initial invocation| {functionNodes}";
    }

    /// <summary>
    /// Generates the full container description including user description and Mermaid flow.
    /// </summary>
    public static string GenerateContainerDescription(string? userDescription, string toolName, List<string> capabilities)
    {
        var description = userDescription ?? string.Empty;
        var mermaidFlow = GenerateMermaidFlow(toolName, capabilities);
        return $"{description}. {mermaidFlow}";
    }

    /// <summary>
    /// Generates the return message shown after container expansion.
    /// </summary>
    public static string GenerateReturnMessage(string toolName, List<string> capabilities, string? postExpansionInstructions)
    {
        var capabilitiesList = string.Join(", ", capabilities);
        var returnMessage = $"{toolName} expanded. Available functions: {capabilitiesList}";

        if (!string.IsNullOrEmpty(postExpansionInstructions))
        {
            returnMessage += $"\n\n{postExpansionInstructions}";
        }

        return returnMessage;
    }
}
