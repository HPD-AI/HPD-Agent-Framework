using Microsoft.Extensions.AI;
using System.Text.Json;

/// <summary>
/// Wraps external tools (MCP, Frontend) with plugin Collapsing metadata at runtime.
/// Unlike C# plugins which get metadata from the source generator, external tools
/// need runtime wrapping to support the plugin Collapsing architecture.
/// </summary>
public static class ExternalToolCollapsingWrapper
{
    /// <summary>
    /// Wraps a group of MCP tools from one server with a container function.
    /// Creates a template description listing the available function names.
    /// </summary>
    /// <param name="serverName">Name of the MCP server (e.g., "filesystem", "github")</param>
    /// <param name="tools">Tools from this MCP server</param>
    /// <param name="maxFunctionNamesInDescription">Maximum number of function names to include in description (default: 10)</param>
    /// <param name="FunctionResult">Ephemeral instructions returned in function result after expansion</param>
    /// <param name="SystemPrompt">Persistent instructions injected into system prompt after expansion</param>
    /// <param name="customDescription">Optional custom description from JSON config. If provided, replaces auto-generated description.</param>
    /// <returns>Container function and Collapsed tools with metadata</returns>
    public static (AIFunction container, List<AIFunction> CollapsedTools) WrapMCPServerTools(
        string serverName,
        List<AIFunction> tools,
        int maxFunctionNamesInDescription = 10,
        string? FunctionResult = null,
        string?SystemPrompt = null,
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

        // Build return message with optional ephemeral context (function result)
        var returnMessage = $"{serverName} server expanded. Available functions: {fullFunctionList}";
        if (!string.IsNullOrEmpty(FunctionResult))
        {
            returnMessage += $"\n\n{FunctionResult}";
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
                    ["MCPServerName"] = serverName,
                    // Dual-context architecture: FunctionResult for ephemeral, SystemPrompt for persistent
                    ["FunctionResult"] = FunctionResult,
                    ["SystemPrompt"] = SystemPrompt,
                    // Legacy key for backward compatibility with ContainerMiddleware
                    ["Instructions"] = SystemPrompt
                }
            });

        // Add metadata to individual tools
        var CollapsedTools = tools.Select(tool => AddParentPluginMetadata(tool, containerName, "MCP")).ToList();

        return (container, CollapsedTools);
    }

    /// <summary>
    /// Wraps all frontend tools in a single container function.
    /// Frontend tools are AGUI tools executed by the frontend (human-in-the-loop).
    /// </summary>
    /// <param name="tools">Frontend tools to wrap</param>
    /// <param name="maxFunctionNamesInDescription">Maximum number of function names to include in description (default: 10)</param>
    /// <param name="FunctionResult">Ephemeral instructions returned in function result after expansion</param>
    /// <param name="SystemPrompt">Persistent instructions injected into system prompt after expansion</param>
    /// <returns>Container function and Collapsed tools with metadata</returns>
    public static (AIFunction container, List<AIFunction> CollapsedTools) WrapFrontendTools(
        List<AIFunction> tools,
        int maxFunctionNamesInDescription = 10,
        string? FunctionResult = null,
        string?SystemPrompt = null)
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

        // Build return message with optional ephemeral context (function result)
        var returnMessage = $"Frontend tools expanded. Available functions: {fullFunctionList}";
        if (!string.IsNullOrEmpty(FunctionResult))
        {
            returnMessage += $"\n\n{FunctionResult}";
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
        var CollapsedTools = tools.Select(tool => AddParentPluginMetadata(tool, containerName, "Frontend")).ToList();

        return (container, CollapsedTools);
    }

    /// <summary>
    /// Wraps a group of frontend tools from one plugin with a container function.
    /// Uses "Frontend_" prefix to distinguish from other plugin types.
    /// Requires a description (since collapsed plugins need to tell the LLM what they contain).
    /// </summary>
    /// <param name="pluginName">Name of the frontend plugin (e.g., "ECommerce", "Settings")</param>
    /// <param name="description">Description of the plugin (REQUIRED - tells LLM when to expand)</param>
    /// <param name="tools">Tools in this plugin</param>
    /// <param name="maxFunctionNamesInDescription">Maximum number of function names to include in description (default: 10)</param>
    /// <param name="FunctionResult">Ephemeral instructions returned in function result after expansion</param>
    /// <param name="SystemPrompt">Persistent instructions injected into system prompt after expansion</param>
    /// <returns>Container function and Collapsed tools with metadata</returns>
    public static (AIFunction container, List<AIFunction> CollapsedTools) WrapFrontendPlugin(
        string pluginName,
        string description,
        List<AIFunction> tools,
        int maxFunctionNamesInDescription = 10,
        string? FunctionResult = null,
        string?SystemPrompt = null)
    {
        if (string.IsNullOrEmpty(pluginName))
            throw new ArgumentException("Plugin name cannot be null or empty", nameof(pluginName));

        if (string.IsNullOrEmpty(description))
            throw new ArgumentException(
                "Description is required for frontend plugins so the LLM knows when to expand them",
                nameof(description));

        if (tools == null || tools.Count == 0)
            throw new ArgumentException("Tools list cannot be null or empty", nameof(tools));

        var containerName = $"Frontend_{pluginName}";
        var allFunctionNames = tools.Select(t => t.Name).ToList();

        // Build function list suffix
        var displayedNames = allFunctionNames.Take(maxFunctionNamesInDescription);
        var functionNamesList = string.Join(", ", displayedNames);
        var moreCount = allFunctionNames.Count > maxFunctionNamesInDescription
            ? $" and {allFunctionNames.Count - maxFunctionNamesInDescription} more"
            : "";
        var functionSuffix = $"Contains {allFunctionNames.Count} functions: {functionNamesList}{moreCount}";

        // Build description: user-provided + function list
        var fullDescription = $"{description}. {functionSuffix}";
        var fullFunctionList = string.Join(", ", allFunctionNames);

        // Build return message with optional ephemeral context (function result)
        var returnMessage = $"{pluginName} plugin expanded. Available functions: {fullFunctionList}";
        if (!string.IsNullOrEmpty(FunctionResult))
        {
            returnMessage += $"\n\n{FunctionResult}";
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
                Description = fullDescription,
                RequiresPermission = false, // Container expansion doesn't need permission
                Validator = _ => new List<ValidationError>(), // No validation needed
                SchemaProvider = () => CreateEmptyContainerSchema(),
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsContainer"] = true,
                    ["PluginName"] = containerName,
                    ["FrontendPluginName"] = pluginName, // Original name without prefix
                    ["FunctionNames"] = allFunctionNames.ToArray(),
                    ["FunctionCount"] = allFunctionNames.Count,
                    ["SourceType"] = "FrontendPlugin",
                    // Dual-context architecture: FunctionResult for ephemeral, SystemPrompt for persistent
                    ["FunctionResult"] = FunctionResult,
                    ["SystemPrompt"] = SystemPrompt,
                    // Legacy key for backward compatibility with ContainerMiddleware
                    ["Instructions"] = SystemPrompt
                }
            });

        // Add metadata to individual tools
        var CollapsedTools = tools.Select(tool => AddParentPluginMetadata(tool, containerName, "FrontendPlugin")).ToList();

        return (container, CollapsedTools);
    }

    /// <summary>
    /// Adds ParentPlugin metadata to an existing AIFunction by wrapping it.
    /// This is necessary because AIFunction.AdditionalProperties is read-only,
    /// so we create a new function that delegates to the original.
    /// </summary>
    /// <param name="tool">Original tool to wrap</param>
    /// <param name="parentPluginName">Parent container name</param>
    /// <param name="sourceType">Source type (MCP, Frontend, FrontendPlugin)</param>
    /// <returns>New AIFunction with metadata</returns>
    private static AIFunction AddParentPluginMetadata(AIFunction tool, string parentPluginName, string sourceType)
    {
        // Check if tool already has Collapsing metadata (avoid double-wrapping)
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
        return global::Microsoft.Extensions.AI.AIJsonUtilities.CreateJsonSchema(
            null,
            serializerOptions: HPDJsonContext.Default.Options,
            inferenceOptions: new global::Microsoft.Extensions.AI.AIJsonSchemaCreateOptions
            {
                IncludeSchemaKeyword = false
            }
        );
    }
}
