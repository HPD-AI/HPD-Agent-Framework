// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Text.Json;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;

namespace HPD.Agent.FrontendTools;

/// <summary>
/// Middleware for frontend tool registration, invocation, and visibility management.
/// </summary>
/// <remarks>
/// <para><b>Lifecycle Hooks:</b></para>
/// <list type="bullet">
/// <item><c>BeforeMessageTurnAsync</c> - Process initial tool registration from AgentRunInput</item>
/// <item><c>BeforeIterationAsync</c> - Apply tool visibility based on current state</item>
/// <item><c>BeforeSequentialFunctionAsync</c> - Intercept frontend tool calls, emit request, wait for response</item>
/// </list>
///
/// <para><b>State Management:</b></para>
/// <para>
/// Uses <see cref="FrontendToolStateData"/> stored in <c>context.State.MiddlewareState.FrontendTool</c>.
/// State tracks registered tools, visibility, expanded containers, and pending augmentations.
/// </para>
/// </remarks>
public class FrontendToolMiddleware : IAgentMiddleware
{
    private readonly FrontendToolConfig _config;

    /// <summary>
    /// Creates a new FrontendToolMiddleware with optional configuration.
    /// </summary>
    /// <param name="config">Configuration for timeout, validation, etc.</param>
    public FrontendToolMiddleware(FrontendToolConfig? config = null)
    {
        _config = config ?? new FrontendToolConfig();
    }

    // ============================================
    // MESSAGE TURN LEVEL
    // ============================================

    /// <summary>
    /// Process initial plugin registration from AgentRunInput.
    /// Tools are always inside plugins - this is the only way to register frontend tools.
    ///
    /// Registration is ATOMIC: if any plugin fails validation (including cross-plugin
    /// skill references), NO plugins are registered. This prevents partial state.
    /// </summary>
    public Task BeforeMessageTurnAsync(AgentMiddlewareContext context, CancellationToken ct)
    {
        // Get AgentRunInput from context (if provided)
        var runInput = context.Properties.TryGetValue("AgentRunInput", out var input)
            ? input as AgentRunInput
            : null;

        if (runInput == null)
            return Task.CompletedTask;

        // Handle state persistence vs reset
        var existingState = context.State.MiddlewareState.FrontendTool;
        var state = runInput.ResetFrontendState || existingState == null
            ? new FrontendToolStateData()
            : existingState;

        // =============================================
        // PHASE 1: Register all plugins (tools only)
        // Build pending list without committing to state
        // =============================================
        var pendingPlugins = new List<FrontendPluginDefinition>();

        if (runInput.FrontendPlugins != null)
        {
            foreach (var plugin in runInput.FrontendPlugins)
            {
                // Validate plugin structure (name, description, tools)
                plugin.Validate();

                // Validate JSON Schema if configured
                if (_config.ValidateSchemaOnRegistration)
                {
                    foreach (var tool in plugin.Tools)
                    {
                        ValidateToolSchema(tool);
                    }
                }

                pendingPlugins.Add(plugin);
                state = state.WithRegisteredPlugin(plugin);
            }
        }

        // =============================================
        // PHASE 2: Validate ALL cross-plugin references
        // If any skill references a non-existent tool, fail here
        // =============================================
        foreach (var plugin in pendingPlugins)
        {
            if (plugin.Skills == null) continue;

            foreach (var skill in plugin.Skills)
            {
                skill.ValidateReferences(plugin.Name, state.RegisteredPlugins);
            }
        }

        // =============================================
        // PHASE 3: All validations passed - apply settings
        // =============================================

        // Set initial expanded plugins
        if (runInput.ExpandedContainers != null)
        {
            foreach (var pluginName in runInput.ExpandedContainers)
            {
                state = state.WithExpandedPlugin(pluginName);
            }
        }

        // Set initial hidden tools
        if (runInput.HiddenTools != null)
        {
            foreach (var tool in runInput.HiddenTools)
            {
                state = state.WithHiddenTool(tool);
            }
        }

        // Set context
        if (runInput.Context != null)
        {
            state = state.WithContext(runInput.Context);
        }

        // Set state
        if (runInput.State.HasValue)
        {
            state = state.WithState(runInput.State);
        }

        // =============================================
        // PHASE 4: Commit state (atomic - all or nothing)
        // =============================================
        context.UpdateState(s => s with
        {
            MiddlewareState = s.MiddlewareState.WithFrontendTool(state)
        });

        // Emit registration confirmation (optional - works without EventCoordinator)
        context.TryEmit(new FrontendPluginsRegisteredEvent(
            RegisteredPlugins: state.RegisteredPlugins.Keys.ToList(),
            TotalTools: state.RegisteredPlugins.Values.Sum(p => p.Tools.Count),
            Timestamp: DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates that a tool's JSON Schema is well-formed.
    /// Called during registration when ValidateSchemaOnRegistration is true.
    /// </summary>
    private static void ValidateToolSchema(FrontendToolDefinition tool)
    {
        try
        {
            // Verify the schema is valid JSON
            var schemaText = tool.ParametersSchema.GetRawText();

            // Basic structure validation - ensure it's an object schema
            if (tool.ParametersSchema.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Schema must be a JSON object");
            }
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new ArgumentException(
                $"Tool '{tool.Name}' has invalid JSON Schema: {ex.Message}",
                nameof(tool.ParametersSchema), ex);
        }
    }

    // ============================================
    // ITERATION LEVEL
    // ============================================

    /// <summary>
    /// Apply tool visibility based on current state.
    /// Converts frontend tool definitions to AIFunction and adds to context.Options.Tools.
    /// </summary>
    public Task BeforeIterationAsync(AgentMiddlewareContext context, CancellationToken ct)
    {
        var state = context.State.MiddlewareState.FrontendTool;
        if (state == null || state.RegisteredPlugins.Count == 0)
            return Task.CompletedTask;

        // Apply any pending augmentation from previous iteration
        if (state.PendingAugmentation != null)
        {
            state = ApplyPendingAugmentation(state);
            state = state.ClearPendingAugmentation();

            // Update state after augmentation
            context.UpdateState(s => s with
            {
                MiddlewareState = s.MiddlewareState.WithFrontendTool(state)
            });
        }

        // Convert plugins to AIFunctions
        var visibleAIFunctions = ConvertPluginsToAIFunctions(state);

        // Clone options and add frontend tools
        if (context.Options != null)
        {
            var existingTools = context.Options.Tools?.ToList() ?? new List<AITool>();
            existingTools.AddRange(visibleAIFunctions);
            var clonedOptions = context.Options.Clone();
            clonedOptions.Tools = existingTools;
            context.Options = clonedOptions;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies pending augmentation to state.
    /// </summary>
    private FrontendToolStateData ApplyPendingAugmentation(FrontendToolStateData state)
    {
        var aug = state.PendingAugmentation;
        if (aug == null) return state;

        // Remove plugins
        if (aug.RemovePlugins != null)
        {
            foreach (var pluginName in aug.RemovePlugins)
            {
                state = state.WithoutRegisteredPlugin(pluginName);
            }
        }

        // Inject plugins
        if (aug.InjectPlugins != null)
        {
            foreach (var plugin in aug.InjectPlugins)
            {
                plugin.Validate();
                state = state.WithRegisteredPlugin(plugin);
            }
        }

        // Expand plugins
        if (aug.ExpandPlugins != null)
        {
            foreach (var pluginName in aug.ExpandPlugins)
            {
                state = state.WithExpandedPlugin(pluginName);
            }
        }

        // Collapse plugins
        if (aug.CollapsePlugins != null)
        {
            foreach (var pluginName in aug.CollapsePlugins)
            {
                state = state.WithCollapsedPlugin(pluginName);
            }
        }

        // Hide tools
        if (aug.HideTools != null)
        {
            foreach (var toolName in aug.HideTools)
            {
                state = state.WithHiddenTool(toolName);
            }
        }

        // Show tools
        if (aug.ShowTools != null)
        {
            foreach (var toolName in aug.ShowTools)
            {
                state = state.WithVisibleTool(toolName);
            }
        }

        // Add context
        if (aug.AddContext != null)
        {
            state = state.WithContext(aug.AddContext);
        }

        // Remove context
        if (aug.RemoveContext != null)
        {
            foreach (var key in aug.RemoveContext)
            {
                state = state.WithoutContext(key);
            }
        }

        // Update state (full replacement)
        if (aug.UpdateState.HasValue)
        {
            state = state.WithState(aug.UpdateState);
        }
        // Patch state (merge) - simplified implementation
        else if (aug.PatchState.HasValue)
        {
            // For now, patch just replaces - could implement deep merge later
            state = state.WithState(aug.PatchState);
        }

        return state;
    }

    /// <summary>
    /// Converts frontend plugins to AIFunctions using ExternalToolScopingWrapper.
    /// </summary>
    private List<AIFunction> ConvertPluginsToAIFunctions(FrontendToolStateData state)
    {
        var allFunctions = new List<AIFunction>();

        foreach (var (pluginName, plugin) in state.RegisteredPlugins)
        {
            // Convert FrontendToolDefinitions to AIFunctions
            var toolAIFunctions = plugin.Tools
                .Where(t => !state.HiddenTools.Contains(t.Name))
                .Select(t => ConvertToolToAIFunction(t, pluginName))
                .ToList();

            // Convert skills to AIFunctions (if any)
            var skillAIFunctions = new List<AIFunction>();
            if (plugin.Skills != null)
            {
                foreach (var skill in plugin.Skills)
                {
                    var skillFunction = ConvertSkillToAIFunction(skill, pluginName);
                    skillAIFunctions.Add(skillFunction);
                }
            }

            // Determine if this plugin should be collapsed
            var shouldCollapse = plugin.StartCollapsed && !state.ExpandedPlugins.Contains(pluginName);

            if (shouldCollapse)
            {
                // Use ExternalToolScopingWrapper pattern - creates container + scoped tools
                var (container, scopedTools) = ExternalToolScopingWrapper.WrapFrontendPlugin(
                    pluginName,
                    plugin.Description!,  // Validated to exist for collapsed plugins
                    toolAIFunctions,
                    maxFunctionNamesInDescription: 10,
                    postExpansionInstructions: plugin.PostExpansionInstructions);

                allFunctions.Add(container);
                allFunctions.AddRange(scopedTools);

                // Skills are always visible (collapsed state) - they're entry points
                allFunctions.AddRange(skillAIFunctions);
            }
            else
            {
                // Not collapsed - add tools and skills directly (no container)
                allFunctions.AddRange(toolAIFunctions);
                allFunctions.AddRange(skillAIFunctions);
            }
        }

        return allFunctions;
    }

    /// <summary>
    /// Converts a FrontendToolDefinition to an AIFunction.
    /// The resulting function is intercepted by BeforeSequentialFunctionAsync.
    /// </summary>
    private static AIFunction ConvertToolToAIFunction(FrontendToolDefinition tool, string pluginName)
    {
        return HPDAIFunctionFactory.Create(
            async (args, ct) =>
            {
                // This should never be called - FrontendToolMiddleware intercepts
                throw new InvalidOperationException(
                    $"FrontendTool '{tool.Name}' should not be invoked directly. " +
                    "Ensure FrontendToolMiddleware is registered.");
            },
            new HPDAIFunctionFactoryOptions
            {
                Name = tool.Name,
                Description = tool.Description,
                RequiresPermission = tool.RequiresPermission,
                Validator = _ => new List<ValidationError>(),
                SchemaProvider = () => tool.ParametersSchema,
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsFrontendTool"] = true,
                    ["FrontendPluginName"] = pluginName,
                    ["SourceType"] = "FrontendPlugin"
                }
            });
    }

    /// <summary>
    /// Converts a FrontendSkillDefinition to an AIFunction.
    /// Sets metadata required by ToolVisibilityManager for read_skill_document visibility.
    /// </summary>
    private static AIFunction ConvertSkillToAIFunction(FrontendSkillDefinition skill, string pluginName)
    {
        // Build a return message with the skill instructions
        var returnMessage = $"Skill '{skill.Name}' activated.\n\n{skill.Instructions}";

        // Build document references for visibility manager (uses "frontend:" prefix)
        var documentReferences = Array.Empty<string>();
        if (skill.Documents != null && skill.Documents.Count > 0)
        {
            returnMessage += "\n\nAvailable documents:";
            documentReferences = new string[skill.Documents.Count];
            for (int i = 0; i < skill.Documents.Count; i++)
            {
                var doc = skill.Documents[i];
                // Use the frontend-prefixed document ID for the store
                documentReferences[i] = FrontendSkillDocumentRegistrar.GetStoreDocumentId(doc.DocumentId);
                returnMessage += $"\n- {doc.DocumentId}: {doc.Description}";
            }
        }

        // Build referenced function names for ToolVisibilityManager
        var referencedFunctions = Array.Empty<string>();
        var referencedPlugins = Array.Empty<string>();
        if (skill.References != null && skill.References.Count > 0)
        {
            var funcList = new List<string>();
            var pluginSet = new HashSet<string>();
            foreach (var reference in skill.References)
            {
                // Build qualified function name
                var qualifiedName = string.IsNullOrEmpty(reference.PluginName)
                    ? reference.ToolName  // Local reference
                    : $"{reference.PluginName}.{reference.ToolName}";  // Cross-plugin reference
                funcList.Add(qualifiedName);

                // Track referenced plugins for visibility
                if (!string.IsNullOrEmpty(reference.PluginName))
                {
                    pluginSet.Add(reference.PluginName);
                }
            }
            referencedFunctions = funcList.ToArray();
            referencedPlugins = pluginSet.ToArray();
        }

        return HPDAIFunctionFactory.Create(
            async (args, ct) =>
            {
                return returnMessage;
            },
            new HPDAIFunctionFactoryOptions
            {
                Name = skill.Name,
                Description = skill.Description,
                RequiresPermission = false, // Skills are entry points
                Validator = _ => new List<ValidationError>(),
                SchemaProvider = () => CreateEmptySchema(),
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["IsSkill"] = true,
                    ["IsContainer"] = true,  // Skills are containers (for ToolVisibilityManager)
                    ["IsFrontendSkill"] = true,
                    ["FrontendPluginName"] = pluginName,
                    ["SourceType"] = "FrontendPlugin",
                    ["Instructions"] = skill.Instructions,
                    // These are checked by ToolVisibilityManager.HasDocuments()
                    ["DocumentReferences"] = documentReferences,
                    // These are used by ToolVisibilityManager for visibility rules
                    ["ReferencedFunctions"] = referencedFunctions,
                    ["ReferencedPlugins"] = referencedPlugins
                }
            });
    }

    /// <summary>
    /// Creates an empty JSON schema for skills (no parameters).
    /// </summary>
    private static JsonElement CreateEmptySchema()
    {
        return Microsoft.Extensions.AI.AIJsonUtilities.CreateJsonSchema(
            null,
            serializerOptions: HPDJsonContext.Default.Options,
            inferenceOptions: new Microsoft.Extensions.AI.AIJsonSchemaCreateOptions
            {
                IncludeSchemaKeyword = false
            }
        );
    }

    // ============================================
    // FUNCTION LEVEL
    // ============================================

    /// <summary>
    /// Intercept frontend tool calls - emit request and wait for response.
    /// Detects frontend tools by checking IsFrontendTool in AdditionalProperties.
    /// </summary>
    public async Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CancellationToken ct)
    {
        // Check if this is a frontend tool
        if (context.Function?.AdditionalProperties?.TryGetValue("IsFrontendTool", out var isFrontendTool) != true
            || isFrontendTool is not true)
        {
            return; // Not a frontend tool, let normal execution proceed
        }

        var requestId = Guid.NewGuid().ToString();
        var toolName = context.Function.Name;

        // Emit invocation request
        context.Emit(new FrontendToolInvokeRequestEvent(
            RequestId: requestId,
            ToolName: toolName,
            CallId: context.FunctionCallId ?? string.Empty,
            Arguments: context.FunctionArguments?.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value) ?? new Dictionary<string, object?>(),
            Description: context.Function.Description));

        // Wait for response
        FrontendToolInvokeResponseEvent response;
        try
        {
            response = await context.WaitForResponseAsync<FrontendToolInvokeResponseEvent>(
                requestId,
                _config.InvokeTimeout);
        }
        catch (TimeoutException)
        {
            context.BlockFunctionExecution = true;
            context.FunctionResult = HandleTimeout(toolName);
            return;
        }
        catch (OperationCanceledException)
        {
            context.BlockFunctionExecution = true;
            context.FunctionResult = $"Frontend tool '{toolName}' was cancelled.";
            return;
        }

        // Block execution (we have the result from frontend)
        context.BlockFunctionExecution = true;

        if (response.Success)
        {
            // Convert Content to appropriate result format
            context.FunctionResult = ConvertContentToResult(response.Content);

            // Store augmentation for next iteration
            if (response.Augmentation != null)
            {
                var state = context.State.MiddlewareState.FrontendTool;
                if (state != null)
                {
                    var updatedState = state.WithPendingAugmentation(response.Augmentation);
                    context.UpdateState(s => s with
                    {
                        MiddlewareState = s.MiddlewareState.WithFrontendTool(updatedState)
                    });
                }
            }
        }
        else
        {
            context.FunctionResult = $"Error: {response.ErrorMessage ?? "Unknown error"}";
        }
    }

    /// <summary>
    /// Handles timeout based on configuration.
    /// </summary>
    private string HandleTimeout(string toolName)
    {
        return _config.DisconnectionStrategy switch
        {
            FrontendDisconnectionStrategy.FallbackMessage =>
                string.Format(_config.FallbackMessageTemplate, toolName),
            FrontendDisconnectionStrategy.FailFast =>
                throw new TimeoutException($"Frontend tool '{toolName}' timed out waiting for response."),
            _ => $"Frontend tool '{toolName}' timed out."
        };
    }

    /// <summary>
    /// Converts tool result content to a format suitable for FunctionResult.
    /// </summary>
    private static object? ConvertContentToResult(IReadOnlyList<IToolResultContent>? content)
    {
        if (content == null || content.Count == 0)
            return null;

        // Single text content - return as string
        if (content.Count == 1 && content[0] is TextContent text)
            return text.Text;

        // Single JSON content - return the value
        if (content.Count == 1 && content[0] is JsonContent json)
            return json.Value;

        // Multiple items or binary - return as structured list
        return content;
    }
}
