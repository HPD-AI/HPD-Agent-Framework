using Microsoft.Extensions.AI;
using System.Text.Json;
using Json.Schema;
using Json.Schema.Generation;

/// <summary>
/// Wraps external tools (MCP, Frontend) with plugin scoping metadata at runtime.
/// Unlike C# plugins which get metadata from the source generator, external tools
/// need runtime wrapping to support the plugin scoping architecture.
/// </summary>
public static class ExternalToolScopingWrapper
{
    /// <summary>
    /// Wraps a group of MCP tools from one server with a container function.
    /// Creates a template description listing the available function names.
    /// </summary>
    /// <param name="serverName">Name of the MCP server (e.g., "filesystem", "github")</param>
    /// <param name="tools">Tools from this MCP server</param>
    /// <param name="maxFunctionNamesInDescription">Maximum number of function names to include in description (default: 10)</param>
    /// <param name="postExpansionInstructions">Optional instructions shown to the agent after plugin expansion</param>
    /// <param name="customDescription">Optional custom description from JSON config. If provided, replaces auto-generated description.</param>
    /// <returns>Container function and scoped tools with metadata</returns>
    public static (AIFunction container, List<AIFunction> scopedTools) WrapMCPServerTools(
        string serverName,
        List<AIFunction> tools,
        int maxFunctionNamesInDescription = 10,
        string? postExpansionInstructions = null,
        string? customDescription = null)
    {
        if (string.IsNullOrEmpty(serverName))
            throw new ArgumentException("Server name cannot be null or empty", nameof(serverName));

        if (tools == null || tools.Count == 0)
            throw new ArgumentException("Tools list cannot be null or empty", nameof(tools));

        var containerName = $"MCP_{serverName}";
        var allFunctionNames = tools.Select(t => t.Name).ToList();

        // Build function list suffix (used for all description types)
        var displayedNames = allFunctionNames.Take(maxFunctionNamesInDescription);
        var functionNamesList = string.Join(", ", displayedNames);
        var moreCount = allFunctionNames.Count > maxFunctionNamesInDescription
            ? $" and {allFunctionNames.Count - maxFunctionNamesInDescription} more"
            : "";
        var functionSuffix = $"Contains {allFunctionNames.Count} functions: {functionNamesList}{moreCount}";

        // Build description: user-provided or auto-generated, always with function list
        string description;
        if (!string.IsNullOrWhiteSpace(customDescription))
        {
            // User-provided description from JSON config + function list
            description = $"{customDescription}. {functionSuffix}";
        }
        else
        {
            // Auto-generated description with function list
            description = $"MCP Server '{serverName}'. {functionSuffix}";
        }

        var fullFunctionList = string.Join(", ", allFunctionNames);

        // Build return message with optional post-expansion instructions
        var returnMessage = $"{serverName} server expanded. Available functions: {fullFunctionList}";
        if (!string.IsNullOrEmpty(postExpansionInstructions))
        {
            returnMessage += $"\n\n{postExpansionInstructions}";
        }

        // Create container function
        var container = HPDAIFunctionFactory.Create(
            async (arguments, cancellationToken) =>
            {
                return returnMessage;
            },
            new HPDAIFunctionFactoryOptions
            {
                Name = containerName,
                Description = description,
                RequiresPermission = false, // Container expansion doesn't need permission
                Validator = _ => new List<ValidationError>(), // No validation needed
                SchemaProvider = () => CreateEmptyContainerSchema(),
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["PluginName"] = containerName,
                    ["FunctionNames"] = allFunctionNames.ToArray(),
                    ["FunctionCount"] = allFunctionNames.Count,
                    ["SourceType"] = "MCP",
                    ["MCPServerName"] = serverName
                }
            });

        // Add metadata to individual tools
        var scopedTools = tools.Select(tool => AddParentPluginMetadata(tool, containerName, "MCP")).ToList();

        return (container, scopedTools);
    }

    /// <summary>
    /// Wraps all frontend tools in a single container function.
    /// Frontend tools are AGUI tools executed by the frontend (human-in-the-loop).
    /// </summary>
    /// <param name="tools">Frontend tools to wrap</param>
    /// <param name="maxFunctionNamesInDescription">Maximum number of function names to include in description (default: 10)</param>
    /// <param name="postExpansionInstructions">Optional instructions shown to the agent after plugin expansion</param>
    /// <returns>Container function and scoped tools with metadata</returns>
    public static (AIFunction container, List<AIFunction> scopedTools) WrapFrontendTools(
        List<AIFunction> tools,
        int maxFunctionNamesInDescription = 10,
        string? postExpansionInstructions = null)
    {
        if (tools == null || tools.Count == 0)
            throw new ArgumentException("Tools list cannot be null or empty", nameof(tools));

        var containerName = "FrontendTools";
        var allFunctionNames = tools.Select(t => t.Name).ToList();

        // Generate template description with function names
        var displayedNames = allFunctionNames.Take(maxFunctionNamesInDescription);
        var functionNamesList = string.Join(", ", displayedNames);
        var moreCount = allFunctionNames.Count > maxFunctionNamesInDescription
            ? $" and {allFunctionNames.Count - maxFunctionNamesInDescription} more"
            : "";

        var description = $"Frontend UI tools for user interaction. Contains {allFunctionNames.Count} functions: {functionNamesList}{moreCount}";
        var fullFunctionList = string.Join(", ", allFunctionNames);

        // Build return message with optional post-expansion instructions
        var returnMessage = $"Frontend tools expanded. Available functions: {fullFunctionList}";
        if (!string.IsNullOrEmpty(postExpansionInstructions))
        {
            returnMessage += $"\n\n{postExpansionInstructions}";
        }

        // Create container function
        var container = HPDAIFunctionFactory.Create(
            async (arguments, cancellationToken) =>
            {
                return returnMessage;
            },
            new HPDAIFunctionFactoryOptions
            {
                Name = containerName,
                Description = description,
                RequiresPermission = false, // Container expansion doesn't need permission
                Validator = _ => new List<ValidationError>(),
                SchemaProvider = () => CreateEmptyContainerSchema(),
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["PluginName"] = containerName,
                    ["FunctionNames"] = allFunctionNames.ToArray(),
                    ["FunctionCount"] = allFunctionNames.Count,
                    ["SourceType"] = "Frontend"
                }
            });

        // Add metadata to individual tools
        var scopedTools = tools.Select(tool => AddParentPluginMetadata(tool, containerName, "Frontend")).ToList();

        return (container, scopedTools);
    }

    /// <summary>
    /// Adds ParentPlugin metadata to an existing AIFunction by wrapping it.
    /// This is necessary because AIFunction.AdditionalProperties is read-only,
    /// so we create a new function that delegates to the original.
    /// </summary>
    /// <param name="tool">Original tool to wrap</param>
    /// <param name="parentPluginName">Parent container name</param>
    /// <param name="sourceType">Source type (MCP, Frontend)</param>
    /// <returns>New AIFunction with metadata</returns>
    private static AIFunction AddParentPluginMetadata(AIFunction tool, string parentPluginName, string sourceType)
    {
        // Check if tool already has scoping metadata (avoid double-wrapping)
        if (tool.AdditionalProperties?.ContainsKey("ParentPlugin") == true)
        {
            return tool;
        }

        // Wrap the existing tool with metadata
        // This delegates invocation to the original tool while adding metadata
        return HPDAIFunctionFactory.Create(
            async (args, ct) => await tool.InvokeAsync(args, ct),
            new HPDAIFunctionFactoryOptions
            {
                Name = tool.Name,
                Description = tool.Description,
                SchemaProvider = () => tool.JsonSchema,
                RequiresPermission = true, // Preserve permission requirement from original tool
                Validator = _ => new List<ValidationError>(), // Original tool handles validation
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["ParentPlugin"] = parentPluginName,
                    ["PluginName"] = parentPluginName,
                    ["IsContainer"] = false,
                    ["SourceType"] = sourceType
                }
            });
    }

    /// <summary>
    /// Creates an empty JSON schema for container functions (no parameters).
    /// Container functions don't take arguments - they just trigger expansion.
    /// </summary>
    private static JsonElement CreateEmptyContainerSchema()
    {
        var schema = new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .Properties(new Dictionary<string, JsonSchema>())
            .Build();
        return JsonSerializer.SerializeToElement(schema, HPDJsonContext.Default.JsonSchema);
    }
}
