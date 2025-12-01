// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Collections.Immutable;
using HPD.Agent.Middleware;
using HPD_Agent.Scoping;
using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Middleware for plugin collapsing and skills scoping architecture.
/// Manages tool visibility based on container expansion state.
/// </summary>
/// <remarks>
/// <para><b>Purpose:</b></para>
/// <para>
/// This middleware implements the tool scoping feature, which allows plugins and skills
/// to be "collapsed" (hidden) until explicitly expanded by the LLM. This reduces cognitive
/// load on the LLM by only showing relevant tools.
/// </para>
///
/// <para><b>Lifecycle Hooks Used:</b></para>
/// <list type="bullet">
/// <item><c>BeforeIterationAsync</c> - Apply tool visibility filtering before LLM call</item>
/// <item><c>AfterIterationAsync</c> - Detect container expansions, update state</item>
/// </list>
///
/// <para><b>State Management:</b></para>
/// <para>
/// Uses <see cref="ScopingStateData"/> stored in <c>context.State.MiddlewareState.Scoping</c>.
/// State is immutable and flows through the context, preserving thread-safety.
/// </para>
///
/// <para><b>Phase 1 Implementation:</b></para>
/// <para>
/// This is Phase 1 of the migration. The middleware reads from existing AgentLoopState fields
/// (<c>expandedScopedPluginContainers</c>, <c>ExpandedSkillContainers</c>) for backward
/// compatibility. Future phases will migrate fully to <c>MiddlewareState.Scoping</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Register via AgentBuilder
/// var agent = new AgentBuilder()
///     .WithToolScoping()
///     .Build();
///
/// // Or with configuration
/// var agent = new AgentBuilder()
///     .WithToolScoping(config => config.Enabled = true)
///     .Build();
/// </code>
/// </example>
public class ToolScopingMiddleware : IAgentMiddleware
{
    // ═══════════════════════════════════════════════════════════════
    // DEPENDENCIES (owned by middleware)
    // ═══════════════════════════════════════════════════════════════

    private readonly ToolVisibilityManager _visibilityManager;
    private readonly ScopingConfig _config;

    // ═══════════════════════════════════════════════════════════════
    // CONSTRUCTOR
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new ToolScopingMiddleware instance with its own ToolVisibilityManager.
    /// The middleware owns the visibility manager for complete encapsulation of scoping logic.
    /// </summary>
    /// <param name="initialTools">All available tools for the agent</param>
    /// <param name="explicitlyRegisteredPlugins">Plugins explicitly registered via WithPlugin (always visible)</param>
    /// <param name="config">Scoping configuration (optional, defaults to enabled)</param>
    public ToolScopingMiddleware(
        IList<AITool> initialTools,
        ImmutableHashSet<string> explicitlyRegisteredPlugins,
        ScopingConfig? config = null)
    {
        if (initialTools == null)
            throw new ArgumentNullException(nameof(initialTools));
        if (explicitlyRegisteredPlugins == null)
            throw new ArgumentNullException(nameof(explicitlyRegisteredPlugins));

        // Extract AIFunctions from tools
        var aiFunctions = initialTools.OfType<AIFunction>().ToList();
        
        // Create our own ToolVisibilityManager
        _visibilityManager = new ToolVisibilityManager(aiFunctions, explicitlyRegisteredPlugins);
        
        _config = config ?? new ScopingConfig();
        // Ensure Enabled is true when middleware is explicitly added
        if (config == null)
        {
            _config.Enabled = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // BEFORE ITERATION: Apply tool visibility scoping
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Called before each LLM call. Filters tools based on expansion state.
    /// </summary>
    public Task BeforeIterationAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        // Skip if disabled or no tools
        if (!_config.Enabled ||
            context.Options?.Tools == null ||
            context.Options.Tools.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Get expanded containers from scoping state
        var scopingState = context.State.MiddlewareState.Scoping ?? new ScopingStateData();
        var expandedPlugins = scopingState.ExpandedPlugins;
        var expandedSkills = scopingState.ExpandedSkills;

        // Extract AIFunctions from tools (single-pass for performance)
        var aiFunctions = new List<AIFunction>(context.Options.Tools.Count);
        for (int i = 0; i < context.Options.Tools.Count; i++)
        {
            if (context.Options.Tools[i] is AIFunction af)
            {
                aiFunctions.Add(af);
            }
        }

        // Apply visibility rules
        var visibleFunctions = _visibilityManager.GetToolsForAgentTurn(
            aiFunctions,
            expandedPlugins,
            expandedSkills);

        // Convert back to AITool list
        var visibleTools = new List<AITool>(visibleFunctions.Count);
        for (int i = 0; i < visibleFunctions.Count; i++)
        {
            visibleTools.Add(visibleFunctions[i]);
        }

        // Update options with scoped tool list using Clone() + mutation
        var clonedOptions = context.Options.Clone();
        clonedOptions.Tools = visibleTools;
        context.Options = clonedOptions;

        // Emit scoping state event for observability
        EmitScopingStateEvent(context, expandedPlugins, expandedSkills);

        // Emit visibility event for observability
        EmitVisibilityEvent(context, visibleFunctions, expandedPlugins, expandedSkills);

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // BEFORE TOOL EXECUTION: Enhanced error messages
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Called before tool execution. Provides enhanced error messages for scoped functions.
    /// </summary>
    public Task BeforeToolExecutionAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        // Skip if disabled or function found
        if (!_config.Enabled || context.Function != null)
        {
            return Task.CompletedTask;
        }

        // Function not found - check if it's a scoped function
        var functionName = context.FunctionCallId ?? "Unknown";
        var scopingState = context.State.MiddlewareState.Scoping ?? new ScopingStateData();
        
        // Try to find the function in all registered tools to provide helpful error
        var errorMessage = GenerateFunctionNotFoundMessage(
            functionName,
            scopingState.ExpandedPlugins,
            scopingState.ExpandedSkills,
            context.Options?.Tools);

        // Set the error message in context
        context.FunctionResult = errorMessage;

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // AFTER ITERATION: Detect container expansions
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Called after each iteration. Detects container expansions from tool calls
    /// and updates scoping state accordingly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method analyzes the tool calls from the LLM to identify:
    /// - Plugin containers: [Collapse] decorated plugins
    /// - Skill containers: [Skill] methods that return Skill instances
    /// </para>
    /// <para>
    /// When containers are detected, it:
    /// 1. Updates MiddlewareState.Scoping with expanded plugins/skills
    /// 2. Stores skill instructions for prompt injection
    /// 3. Emits ContainerExpandedEvent for observability
    /// </para>
    /// </remarks>
    public Task AfterIterationAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        // Skip if disabled or no tool calls
        if (!_config.Enabled || context.ToolCalls.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Detect containers from tool calls
        var (pluginExpansions, skillExpansions, skillInstructions) = DetectContainers(
            context.ToolCalls,
            context.Options?.Tools);

        // Skip if no containers detected
        if (pluginExpansions.Count == 0 && skillExpansions.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Update state with container expansions
        context.UpdateState(state =>
        {
            var scopingState = state.MiddlewareState.Scoping ?? new ScopingStateData();

            // Add plugin expansions
            foreach (var pluginName in pluginExpansions)
            {
                scopingState = scopingState.WithExpandedPlugin(pluginName);
            }

            // Add skill expansions
            foreach (var skillName in skillExpansions)
            {
                scopingState = scopingState.WithExpandedSkill(skillName);
            }

            // Add skill instructions
            foreach (var (skillName, instructions) in skillInstructions)
            {
                scopingState = scopingState.WithSkillInstructions(skillName, instructions);
            }

            // Also update the legacy AgentLoopState fields for backward compatibility
            return state with
            {
                MiddlewareState = state.MiddlewareState.WithScoping(scopingState)
            };
        });

        // Emit events for each container expansion
        EmitContainerExpandedEvents(context, pluginExpansions, skillExpansions);

        // Mark container results as ephemeral (should not be persisted)
        // Agent.cs will use this to filter results from turnHistory
        var ephemeralCallIds = new HashSet<string>();
        foreach (var toolCall in context.ToolCalls)
        {
            var function = FindFunctionInList(toolCall.Name, context.Options?.Tools);
            if (function?.AdditionalProperties?.TryGetValue("IsContainer", out var isContainerVal) == true
                && isContainerVal is bool isContainer && isContainer)
            {
                ephemeralCallIds.Add(toolCall.CallId);
            }
        }

        if (ephemeralCallIds.Count > 0)
        {
            context.Properties["EphemeralCallIds"] = ephemeralCallIds;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Detects plugin and skill containers from tool calls.
    /// </summary>
    private static (HashSet<string> plugins, HashSet<string> skills, Dictionary<string, string> instructions)
        DetectContainers(IReadOnlyList<FunctionCallContent> toolCalls, IList<AITool>? tools)
    {
        var pluginExpansions = new HashSet<string>();
        var skillExpansions = new HashSet<string>();
        var skillInstructions = new Dictionary<string, string>();

        foreach (var toolCall in toolCalls)
        {
            // Find the function in the tool list
            var function = FindFunctionInList(toolCall.Name, tools);
            if (function == null) continue;

            // Check if it's a container
            if (function.AdditionalProperties?.TryGetValue("IsContainer", out var isContainerVal) != true ||
                isContainerVal is not bool isContainer || !isContainer)
            {
                continue;
            }

            // Check if it's a skill container (has both IsContainer=true AND IsSkill=true)
            var isSkill = function.AdditionalProperties?.TryGetValue("IsSkill", out var isSkillVal) == true
                && isSkillVal is bool isS && isS;

            if (isSkill)
            {
                // Skill container
                var skillName = function.Name ?? toolCall.Name;
                skillExpansions.Add(skillName);

                // Extract instructions for prompt injection
                if (function.AdditionalProperties?.TryGetValue("Instructions", out var instructionsObj) == true
                    && instructionsObj is string instructions
                    && !string.IsNullOrWhiteSpace(instructions))
                {
                    skillInstructions[skillName] = instructions;
                }
            }
            else
            {
                // Plugin container
                var pluginName = function.AdditionalProperties
                    ?.TryGetValue("PluginName", out var pnVal) == true && pnVal is string pn
                    ? pn
                    : toolCall.Name;

                pluginExpansions.Add(pluginName);
            }
        }

        return (pluginExpansions, skillExpansions, skillInstructions);
    }

    /// <summary>
    /// Finds a function by name in the tool list (O(n) linear search).
    /// </summary>
    private static AIFunction? FindFunctionInList(string? name, IList<AITool>? tools)
    {
        if (string.IsNullOrEmpty(name) || tools is not { Count: > 0 })
            return null;

        for (int i = 0; i < tools.Count; i++)
        {
            if (tools[i] is AIFunction af && af.Name == name)
                return af;
        }

        return null;
    }

    /// <summary>
    /// Emits ContainerExpandedEvent for each plugin/skill expansion.
    /// </summary>
    private static void EmitContainerExpandedEvents(
        AgentMiddlewareContext context,
        HashSet<string> pluginExpansions,
        HashSet<string> skillExpansions)
    {
        try
        {
            // Note: UnlockedFunctions is empty here since we don't have access to
            // which functions were unlocked at this point. The visibility manager
            // handles the actual function unlocking in BeforeIterationAsync.
            var emptyFunctions = Array.Empty<string>();

            foreach (var pluginName in pluginExpansions)
            {
                context.Emit(new ContainerExpandedEvent(
                    ContainerName: pluginName,
                    Type: ContainerType.Plugin,
                    UnlockedFunctions: emptyFunctions,
                    Iteration: context.Iteration,
                    Timestamp: DateTimeOffset.UtcNow));
            }

            foreach (var skillName in skillExpansions)
            {
                context.Emit(new ContainerExpandedEvent(
                    ContainerName: skillName,
                    Type: ContainerType.Skill,
                    UnlockedFunctions: emptyFunctions,
                    Iteration: context.Iteration,
                    Timestamp: DateTimeOffset.UtcNow));
            }
        }
        catch (InvalidOperationException)
        {
            // EventCoordinator not configured - event emission is optional
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // AFTER MESSAGE TURN: Filter ephemeral results from turnHistory
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Filters ephemeral container results from turnHistory before persistence.
    /// Container expansion results are temporary (turn-scoped only) and should NOT be
    /// saved to persistent history or thread storage, but MUST be visible to the LLM
    /// within the current turn.
    /// </summary>
    public Task AfterMessageTurnAsync(AgentMiddlewareContext context, CancellationToken cancellationToken)
    {
        if (!_config.Enabled || context.TurnHistory == null || context.TurnHistory.Count == 0)
            return Task.CompletedTask;

        // Get ephemeral CallIds marked during AfterIterationAsync
        if (!context.Properties.TryGetValue("EphemeralCallIds", out var ephemeralObj) ||
            ephemeralObj is not HashSet<string> ephemeralCallIds ||
            ephemeralCallIds.Count == 0)
        {
            // No ephemeral results to filter
            return Task.CompletedTask;
        }

        // Filter turnHistory in-place to remove ephemeral results
        for (int i = context.TurnHistory.Count - 1; i >= 0; i--)
        {
            var message = context.TurnHistory[i];
            
            if (message.Role != ChatRole.Tool)
                continue;

            // Filter out ephemeral function results
            var persistentResults = message.Contents
                .Where(c => c is not FunctionResultContent frc || !ephemeralCallIds.Contains(frc.CallId))
                .ToList();

            if (persistentResults.Count == 0)
            {
                // Entire message was ephemeral - remove it
                context.TurnHistory.RemoveAt(i);
            }
            else if (persistentResults.Count < message.Contents.Count)
            {
                // Some results were ephemeral - replace with filtered message
                context.TurnHistory[i] = new ChatMessage(ChatRole.Tool, persistentResults);
            }
        }

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPER METHODS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Emits a ScopedToolsVisibleEvent for observability.
    /// </summary>
    private static void EmitVisibilityEvent(
        AgentMiddlewareContext context,
        List<AIFunction> visibleFunctions,
        ImmutableHashSet<string> expandedPlugins,
        ImmutableHashSet<string> expandedSkills)
    {
        var visibleToolNames = new List<string>(visibleFunctions.Count);
        for (int i = 0; i < visibleFunctions.Count; i++)
        {
            if (visibleFunctions[i].Name != null)
            {
                visibleToolNames.Add(visibleFunctions[i].Name);
            }
        }

        try
        {
            context.Emit(new ScopedToolsVisibleEvent(
                AgentName: context.AgentName,
                Iteration: context.Iteration,
                VisibleToolNames: visibleToolNames,
                ExpandedPlugins: expandedPlugins,
                ExpandedSkills: expandedSkills,
                TotalToolCount: visibleToolNames.Count,
                Timestamp: DateTimeOffset.UtcNow));
        }
        catch (InvalidOperationException)
        {
            // EventCoordinator not configured - event emission is optional
        }
    }

    /// <summary>
    /// Emits scoping state event for observability (expanded plugins/skills counts).
    /// </summary>
    private static void EmitScopingStateEvent(
        AgentMiddlewareContext context,
        ImmutableHashSet<string> expandedPlugins,
        ImmutableHashSet<string> expandedSkills)
    {
        try
        {
            context.Emit(new ScopingStateEvent(
                AgentName: context.AgentName,
                Iteration: context.Iteration,
                ExpandedPluginsCount: expandedPlugins.Count,
                ExpandedSkillsCount: expandedSkills.Count,
                Timestamp: DateTimeOffset.UtcNow));
        }
        catch (InvalidOperationException)
        {
            // EventCoordinator not configured - event emission is optional
        }
    }

    /// <summary>
    /// Generates a descriptive error message when a function is not found,
    /// with context about which containers need to be expanded.
    /// </summary>
    private static string GenerateFunctionNotFoundMessage(
        string functionName,
        ImmutableHashSet<string> expandedPlugins,
        ImmutableHashSet<string> expandedSkills,
        IList<AITool>? tools)
    {
        // Check if this function belongs to a scoped plugin by searching all registered tools
        if (tools != null)
        {
            foreach (var tool in tools)
            {
                if (tool is AIFunction func &&
                    string.Equals(func.Name, functionName, StringComparison.OrdinalIgnoreCase))
                {
                    // Found the function in registered tools
                    // A function can belong to BOTH a plugin container AND a skill container
                    var unexpandedContainers = new List<string>();

                    // Check if it belongs to a scoped plugin
                    if (func.AdditionalProperties?.TryGetValue("ParentPlugin", out var parentPluginObj) == true &&
                        parentPluginObj is string parentPlugin &&
                        !string.IsNullOrEmpty(parentPlugin))
                    {
                        // Check if this plugin has already been expanded
                        if (!expandedPlugins.Contains(parentPlugin))
                        {
                            unexpandedContainers.Add(parentPlugin);
                        }
                    }

                    // Check if it belongs to a skill container (ParentSkillContainer)
                    if (func.AdditionalProperties?.TryGetValue("ParentSkillContainer", out var skillContainerObj) == true &&
                        skillContainerObj is string skillContainer &&
                        !string.IsNullOrEmpty(skillContainer))
                    {
                        // Check if this skill container has already been expanded
                        if (!expandedSkills.Contains(skillContainer))
                        {
                            unexpandedContainers.Add(skillContainer);
                        }
                    }

                    // Generate appropriate error message based on what containers exist
                    if (unexpandedContainers.Count > 0)
                    {
                        if (unexpandedContainers.Count == 1)
                        {
                            return $"Function '{functionName}' is not currently available. It belongs to the '{unexpandedContainers[0]}' container. Call {unexpandedContainers[0]}() first to unlock this function.";
                        }
                        else
                        {
                            // Multiple containers - list them all
                            var containerList = string.Join(" or ", unexpandedContainers.Select(c => $"{c}()"));
                            return $"Function '{functionName}' is not currently available. It belongs to multiple containers. Call {containerList} first to unlock this function.";
                        }
                    }

                    break;
                }
            }
        }

        // Fallback if function not found in registered tools
        return $"Function '{functionName}' not found.";
    }
}

