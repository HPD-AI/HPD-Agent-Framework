// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.ClientTools;

/// <summary>
/// Defines a plugin container with multiple tools.
/// Similar to [Collapse] attribute for C# plugins.
/// </summary>
/// <remarks>
/// <para><b>Important:</b> If <paramref name="StartCollapsed"/> is true, <paramref name="Description"/> is REQUIRED.</para>
/// <para>The description tells the LLM when to expand this container. Without a description, the LLM won't know what tools are inside.</para>
/// </remarks>
/// <param name="Name">Unique name for the plugin container</param>
/// <param name="Description">Description shown to LLM (REQUIRED if StartCollapsed=true)</param>
/// <param name="Tools">Tools contained in this plugin</param>
/// <param name="Skills">Optional skills that reference tools in this or other plugins</param>
/// <param name="FunctionResult">Ephemeral instructions returned in function result when container is expanded</param>
/// <param name="SystemPrompt">Persistent instructions injected into system prompt after expansion</param>
/// <param name="StartCollapsed">Whether container starts collapsed (default: true)</param>
public record ClientToolGroupDefinition(
    string Name,
    string? Description,
    IReadOnlyList<ClientToolDefinition> Tools,
    IReadOnlyList<ClientSkillDefinition>? Skills = null,
    string? FunctionResult = null,
    string?SystemPrompt = null,
    bool StartCollapsed = true
)
{
    /// <summary>
    /// Validates the plugin definition.
    /// Throws if StartCollapsed=true but no Description provided.
    /// </summary>
    /// <exception cref="ArgumentException">If validation fails</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Plugin name is required", nameof(Name));

        if (Tools == null || Tools.Count == 0)
            throw new ArgumentException("Plugin must contain at least one tool", nameof(Tools));

        if (StartCollapsed && string.IsNullOrWhiteSpace(Description))
            throw new ArgumentException(
                $"Plugin '{Name}' has StartCollapsed=true but no Description. " +
                "A description is required for collapsed plugins so the LLM knows when to expand them.",
                nameof(Description));

        // Validate each tool
        foreach (var tool in Tools)
        {
            tool.Validate();
        }

        // Validate skills if present
        if (Skills != null)
        {
            foreach (var skill in Skills)
            {
                skill.Validate();
            }
        }
    }

    /// <summary>
    /// Validates skill references against registered plugins.
    /// Call this after all plugins are registered to validate cross-plugin references.
    /// </summary>
    /// <param name="RegisteredToolGroups">All registered plugins by name</param>
    /// <exception cref="ArgumentException">If a skill references a non-existent tool</exception>
    public void ValidateSkillReferences(IReadOnlyDictionary<string, ClientToolGroupDefinition> RegisteredToolGroups)
    {
        if (Skills == null) return;

        foreach (var skill in Skills)
        {
            skill.ValidateReferences(Name, RegisteredToolGroups);
        }
    }
}
