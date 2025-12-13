// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using HPD.Agent.Middleware;
using HPD.Agent.Collapsing;
using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Unified middleware for all container operations (plugins and skills).
/// Handles tool visibility, instruction injection, expansion detection, and cleanup.
/// </summary>
/// <remarks>
/// <para><b>Purpose:</b></para>
/// <para>
/// This middleware consolidates all container-related operations into a single, cohesive middleware.
/// It replaces the previous separate ToolCollapsingMiddleware and SkillInstructionMiddleware.
/// </para>
///
/// <para><b>Lifecycle Hooks Used:</b></para>
/// <list type="bullet">
/// <item><c>BeforeIterationAsync</c> - Filter tools + injectSystemPrompt</item>
/// <item><c>AfterIterationAsync</c> - Detect container expansions, update state</item>
/// <item><c>AfterMessageTurnAsync</c> - Filter ephemeral results + clear instructions (configurable)</item>
/// </list>
///
/// <para><b>Container Unification (V2):</b></para>
/// <para>
/// Treats plugins and skills uniformly as "containers" with dual-context support:
/// - <b>FunctionResult</b>: Ephemeral instructions returned in function result
/// - <b>SystemPrompt</b>: Persistent instructions injected into system prompt
/// </para>
///
/// <para><b>State Management:</b></para>
/// <para>
/// Uses <see cref="CollapsingStateData"/> stored in <c>context.State.MiddlewareState.Collapsing</c>.
/// State is immutable and flows through the context, preserving thread-safety.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Register via AgentBuilder
/// var agent = new AgentBuilder()
///     .WithToolCollapsing()  // Auto-registers ContainerMiddleware
///     .Build();
///
/// // Or with custom configuration
/// var agent = new AgentBuilder()
///     .WithToolCollapsing(config =>
///     {
///         config.Enabled = true;
///         config.PersistSystemPromptInjections = false;
///     })
///     .Build();
/// </code>
/// </example>
public class ContainerMiddleware : IAgentMiddleware
{
    //═════════════════════════════════════════════════════════════════════════════════════════════════
    // DEPENDENCIES
    //═════════════════════════════════════════════════════════════════════════════════════════════════

    private readonly ToolVisibilityManager _visibilityManager;
    private readonly CollapsingConfig _config;

    //═════════════════════════════════════════════════════════════════════════════════════════════════
    // CONSTRUCTOR
    //═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new ContainerMiddleware instance.
    /// </summary>
    /// <param name="initialTools">All available tools for the agent</param>
    /// <param name="explicitlyRegisteredPlugins">Plugins explicitly registered via WithPlugin (always visible)</param>
    /// <param name="config">Container configuration (optional, defaults to enabled)</param>
    public ContainerMiddleware(
        IList<AITool> initialTools,
        ImmutableHashSet<string> explicitlyRegisteredPlugins,
        CollapsingConfig? config = null)
    {
        if (initialTools == null)
            throw new ArgumentNullException(nameof(initialTools));
        if (explicitlyRegisteredPlugins == null)
            throw new ArgumentNullException(nameof(explicitlyRegisteredPlugins));

        // Extract AIFunctions from tools
        var aiFunctions = initialTools.OfType<AIFunction>().ToList();

        // Create ToolVisibilityManager for filtering
        _visibilityManager = new ToolVisibilityManager(aiFunctions, explicitlyRegisteredPlugins);

        _config = config ?? new CollapsingConfig { Enabled = true };
    }

    //═════════════════════════════════════════════════════════════════════════════════════════════════
    // BEFORE ITERATION: Filter tools + InjectSystemPrompt
    //═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called before each LLM call.
    /// 1. Filters tools based on expansion state (collapsing)
    /// 2. InjectsSystemPrompt for active containers into system prompt
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

        var collapsingState = context.State.MiddlewareState.Collapsing ?? new CollapsingStateData();

        //─────────────────────────────────────────────────────────────────────────────────────────────
        // STEP 1: Filter tool visibility
        //─────────────────────────────────────────────────────────────────────────────────────────────

        var expandedContainers = collapsingState.ExpandedContainers;

        // Extract AIFunctions from tools (single-pass for performance)
        var aiFunctions = new List<AIFunction>(context.Options.Tools.Count);
        for (int i = 0; i < context.Options.Tools.Count; i++)
        {
            if (context.Options.Tools[i] is AIFunction af)
            {
                aiFunctions.Add(af);
            }
        }

        // Apply visibility rules (unified container tracking)
        var visibleFunctions = _visibilityManager.GetToolsForAgentTurn(
            aiFunctions,
            expandedContainers);

        // Convert back to AITool list
        var visibleTools = new List<AITool>(visibleFunctions.Count);
        for (int i = 0; i < visibleFunctions.Count; i++)
        {
            visibleTools.Add(visibleFunctions[i]);
        }

        // Update options with filtered tool list using Clone() + mutation
        var clonedOptions = context.Options.Clone();
        clonedOptions.Tools = visibleTools;
        context.Options = clonedOptions;

        //─────────────────────────────────────────────────────────────────────────────────────────────
        // STEP 2: InjectSystemPrompt for active containers
        //─────────────────────────────────────────────────────────────────────────────────────────────

        // Use ActiveContainerInstructions (supports both plugins and skills)
        var activeContainers = collapsingState.ActiveContainerInstructions;

        if (activeContainers.Any() && context.Options != null)
        {
            // Build rich container protocols section
            var protocolsSection = BuildContainerProtocolsSection(activeContainers, context.Options);

            // Inject with proper formatting - append AFTER original instructions
            var currentInstructions = context.Options.Instructions ?? string.Empty;

            // Avoid duplicate injection
            if (!currentInstructions.Contains("🔧 ACTIVE") && !string.IsNullOrEmpty(protocolsSection))
            {
                context.Options.Instructions = string.IsNullOrEmpty(currentInstructions)
                    ? protocolsSection
                    : $"{currentInstructions}\n\n{protocolsSection}";
            }
        }

        return Task.CompletedTask;
    }

    //═════════════════════════════════════════════════════════════════════════════════════════════════
    // AFTER ITERATION: Detect container expansions
    //═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called after tool execution completes.
    /// Detects container expansions and updates state with instruction contexts.
    /// </summary>
    public Task AfterIterationAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        // Skip if disabled or no tool calls
        if (!_config.Enabled || context.ToolCalls.Count == 0)
            return Task.CompletedTask;

        // Detect container expansions (unified for plugins and skills)
        var (containerExpansions, containerInstructions) =
            DetectContainers(context.ToolCalls, context.Options?.Tools);

        if (containerExpansions.Count == 0)
            return Task.CompletedTask;

        // Update state with expanded containers
        context.UpdateState(state =>
        {
            var collapsingState = state.MiddlewareState.Collapsing ?? new CollapsingStateData();

            // Expand containers (unified - both plugins and skills)
            foreach (var container in containerExpansions)
            {
                collapsingState = collapsingState.WithExpandedContainer(container);
            }

            // Store container instructions (for SystemPrompt injection)
            foreach (var (containerName, instructions) in containerInstructions)
            {
                collapsingState = collapsingState.WithContainerInstructions(containerName, instructions);
            }

            return state with
            {
                MiddlewareState = state.MiddlewareState.WithCollapsing(collapsingState)
            };
        });

        // Mark container results as ephemeral (should not be persisted in history)
        // AfterMessageTurnAsync will use this to filter results from turnHistory
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

    //═════════════════════════════════════════════════════════════════════════════════════════════════
    // AFTER MESSAGE TURN: Cleanup (ephemeral filtering + instruction clearing)
    //═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called after the message turn completes.
    /// 1. Filters ephemeral container results from turn history
    /// 2. ClearsSystemPrompt injections (if configured to not persist)
    /// </summary>
    public Task AfterMessageTurnAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
            return Task.CompletedTask;

        //─────────────────────────────────────────────────────────────────────────────────────────────
        // STEP 1: Filter ephemeral container results from turn history
        //─────────────────────────────────────────────────────────────────────────────────────────────

        if (context.TurnHistory != null && context.TurnHistory.Count > 0)
        {
            // Get ephemeral CallIds marked during AfterIterationAsync
            if (context.Properties.TryGetValue("EphemeralCallIds", out var ephemeralObj) &&
                ephemeralObj is HashSet<string> ephemeralCallIds &&
                ephemeralCallIds.Count > 0)
            {
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
                        // Some results were ephemeral - update message
                        context.TurnHistory[i] = new ChatMessage(
                            ChatRole.Tool,
                            persistentResults);
                    }
                }
            }
        }

        //─────────────────────────────────────────────────────────────────────────────────────────────
        // STEP 2: ClearSystemPrompt injections (if not configured to persist)
        //─────────────────────────────────────────────────────────────────────────────────────────────

        var collapsingState = context.State.MiddlewareState.Collapsing ?? new CollapsingStateData();

        if (!_config.PersistSystemPromptInjections && collapsingState.ActiveContainerInstructions.Any())
        {
            // Clear instructions at end of message turn (NOT during iteration)
            // This prevents instructions from leaking across message turns
            var updatedCollapsing = collapsingState.ClearContainerInstructions();
            context.UpdateState(s => s with
            {
                MiddlewareState = s.MiddlewareState.WithCollapsing(updatedCollapsing)
            });
        }

        return Task.CompletedTask;
    }

    //═════════════════════════════════════════════════════════════════════════════════════════════════
    // HELPER METHODS
    //═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Detects containers from tool calls (unified for plugins and skills).
    /// Extracts both FunctionResult and SystemPrompt.
    /// </summary>
    private static (
        HashSet<string> containerExpansions,
        Dictionary<string, ContainerInstructionSet> containerInstructions)
        DetectContainers(IReadOnlyList<FunctionCallContent> toolCalls, IList<AITool>? tools)
    {
        var containerExpansions = new HashSet<string>();
        var containerInstructions = new Dictionary<string, ContainerInstructionSet>();

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

            // Determine container name
            string containerName;
            if (isSkill)
            {
                // Skill container - use function name
                containerName = function.Name ?? toolCall.Name;
            }
            else
            {
                // Plugin container - use PluginName metadata or function name
                containerName = function.AdditionalProperties
                    ?.TryGetValue("PluginName", out var pnVal) == true && pnVal is string pn
                    ? pn
                    : toolCall.Name;
            }

            containerExpansions.Add(containerName);

            // Extract both contexts from metadata
            var funcResultCtx = ExtractStringMetadata(function, "FunctionResult");
            var sysPromptCtx = ExtractStringMetadata(function, "SystemPrompt");

            // Fallback to legacy "Instructions" for skills if SystemPrompt not present
            if (string.IsNullOrEmpty(sysPromptCtx) && isSkill)
            {
                sysPromptCtx = ExtractStringMetadata(function, "Instructions");
            }

            // Store instruction contexts
            if (funcResultCtx != null || sysPromptCtx != null)
            {
                containerInstructions[containerName] = new ContainerInstructionSet(funcResultCtx, sysPromptCtx);
            }
        }

        return (containerExpansions, containerInstructions);
    }

    /// <summary>
    /// Builds a rich, formatted container protocols section with metadata.
    /// Injects SystemPrompt for all containers (plugins + skills).
    /// </summary>
    private static string BuildContainerProtocolsSection(
        ImmutableDictionary<string, ContainerInstructionSet> activeContainers,
        ChatOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("═════════════════════════════════════════════════════════════════════════════════════════════════");
        sb.AppendLine("🔧 ACTIVE CONTAINER PROTOCOLS (Execute ALL steps completely)");
        sb.AppendLine("═════════════════════════════════════════════════════════════════════════════════════════════════");
        sb.AppendLine();

        // Order alphabetically for consistency
        foreach (var (containerName, instructionSet) in activeContainers.OrderBy(kvp => kvp.Key))
        {
            // Only injectSystemPrompt (not FunctionResult)
            if (string.IsNullOrEmpty(instructionSet.SystemPrompt))
                continue;

            sb.AppendLine($"## {containerName}:");
            sb.AppendLine();

            // Find the container's AIFunction to extract metadata
            var containerFunction = options.Tools?.OfType<AIFunction>()
                .FirstOrDefault(f => f.Name == containerName);

            if (containerFunction != null)
            {
                // Add function list from metadata
                if (containerFunction.AdditionalProperties?.TryGetValue("ReferencedFunctions", out var functionsObj) == true
                    && functionsObj is string[] functions && functions.Length > 0)
                {
                    sb.AppendLine($"**Available functions:** {string.Join(", ", functions)}");
                    sb.AppendLine();
                }
                else if (containerFunction.AdditionalProperties?.TryGetValue("FunctionNames", out var funcNamesObj) == true
                    && funcNamesObj is string[] funcNames && funcNames.Length > 0)
                {
                    sb.AppendLine($"**Available functions:** {string.Join(", ", funcNames)}");
                    sb.AppendLine();
                }

                // Add document information from metadata (for skills)
                var hasDocuments = BuildDocumentSection(containerFunction, sb);
                if (hasDocuments)
                {
                    sb.AppendLine();
                }
            }

            // Add theSystemPrompt instructions
            sb.AppendLine(instructionSet.SystemPrompt);
            sb.AppendLine();
        }

        return sb.ToString();
    }


    /// <summary>
    /// Builds the document section for a skill, showing available documents.
    /// </summary>
    private static bool BuildDocumentSection(AIFunction skillFunction, StringBuilder sb)
    {
        // Use type-safe SkillDocuments property
        if (skillFunction is HPDAIFunctionFactory.HPDAIFunction hpdFunction &&
            hpdFunction.SkillDocuments?.Any() == true)
        {
            sb.AppendLine("📚 **Available Documents:**");
            foreach (var doc in hpdFunction.SkillDocuments)
            {
                sb.AppendLine($"- {doc.DocumentId}: {doc.Description} ({doc.SourceType})");
            }
            sb.AppendLine();
            sb.AppendLine("Use `read_skill_document(documentId)` to retrieve document content.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts string metadata from AdditionalProperties.
    /// </summary>
    private static string? ExtractStringMetadata(AIFunction function, string key)
    {
        if (function.AdditionalProperties?.TryGetValue(key, out var value) == true &&
            value is string str &&
            !string.IsNullOrWhiteSpace(str))
        {
            return str;
        }
        return null;
    }

    /// <summary>
    /// Finds an AIFunction by name in the tools list.
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
    /// Exposed for testing: Builds the document section for a skill function.
    /// </summary>
    public static string BuildDocumentSectionForTesting(AIFunction skillFunction)
    {
        var sb = new StringBuilder();
        BuildDocumentSection(skillFunction, sb);
        return sb.ToString();
    }
}
