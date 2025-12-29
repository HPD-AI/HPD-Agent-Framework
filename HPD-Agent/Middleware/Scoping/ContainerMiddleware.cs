// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using HPD.Agent.Middleware;
using HPD.Agent.Collapsing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

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
    private readonly Microsoft.Extensions.Logging.ILogger<ContainerMiddleware>? _logger;
    private readonly IList<AITool> _initialTools; // V2: Store for container detection

    //═════════════════════════════════════════════════════════════════════════════════════════════════
    // CONSTRUCTOR
    //═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new ContainerMiddleware instance.
    /// </summary>
    /// <param name="initialTools">All available tools for the agent</param>
    /// <param name="explicitlyRegisteredToolGroups">Plugins explicitly registered via WithTools (always visible)</param>
    /// <param name="config">Container configuration (optional, defaults to enabled)</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    public ContainerMiddleware(
        IList<AITool> initialTools,
        ImmutableHashSet<string> explicitlyRegisteredToolGroups,
        CollapsingConfig? config = null,
        Microsoft.Extensions.Logging.ILogger<ContainerMiddleware>? logger = null)
    {
        if (initialTools == null)
            throw new ArgumentNullException(nameof(initialTools));
        if (explicitlyRegisteredToolGroups == null)
            throw new ArgumentNullException(nameof(explicitlyRegisteredToolGroups));

        // Extract AIFunctions from tools
        var aiFunctions = initialTools.OfType<AIFunction>().ToList();

        // Create ToolVisibilityManager for filtering
        _visibilityManager = new ToolVisibilityManager(aiFunctions, explicitlyRegisteredToolGroups);

        _config = config ?? new CollapsingConfig { Enabled = true };
        _logger = logger;
        _initialTools = initialTools; // V2: Store for container detection
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
        BeforeIterationContext context,
        CancellationToken cancellationToken)
    {
        // Skip if disabled or no tools
        if (!_config.Enabled ||
            context.Options?.Tools == null ||
            context.Options.Tools.Count == 0)
        {
            return Task.CompletedTask;
        }

        var collapsingState = context.GetMiddlewareState<CollapsingStateData>() ?? new CollapsingStateData();

        //─────────────────────────────────────────────────────────────────────────────────────────────
        // STEP 0: Filter [Collapse] container calls from messages (IMMEDIATE TRANSPARENCY)
        //─────────────────────────────────────────────────────────────────────────────────────────────

        // Remove [Collapse] container calls/results from the messages that will be sent to the LLM
        // This implements "immediate transparency" - containers disappear even within the same turn
        if (collapsingState.ContainersExpandedThisTurn.Count > 0)
        {
            FilterCollapseContainersFromMessages(context, collapsingState.ContainersExpandedThisTurn);
        }

        //─────────────────────────────────────────────────────────────────────────────────────────────
        // STEP 1: Filter tool visibility
        //─────────────────────────────────────────────────────────────────────────────────────────────

        var expandedContainers = collapsingState.ExpandedContainers;

        // 🔧 CRITICAL FIX: Always filter from _initialTools (complete list), not context.Options.Tools (already filtered)
        // This ensures that when containers expand, their nested functions become visible in subsequent iterations
        var aiFunctions = _initialTools.OfType<AIFunction>().ToList();

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

        // V2: Mutate Options.Tools directly (ChatOptions object is mutable)
        // The Options property itself is init-only, but the object it references is mutable
        context.Options.Tools = visibleTools;

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
    // BEFORE TOOL EXECUTION: Detect container expansions (V2)
    //═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called before tools execute.
    /// Detects container expansions from tool calls and updates state with instruction contexts.
    /// V2: Moved from AfterIteration to BeforeToolExecution to have access to tool calls.
    /// </summary>
    public Task BeforeToolExecutionAsync(
        BeforeToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Skip if disabled or no tool calls
        if (!_config.Enabled || context.ToolCalls.Count == 0)
            return Task.CompletedTask;

        // Detect container expansions (unified for plugins and skills)
        // Use the original DetectContainers method with tool calls from context
        var (containerExpansions, containerInstructions) =
            DetectContainers(context.ToolCalls, _initialTools);

        if (containerExpansions.Count == 0)
            return Task.CompletedTask;

        // Update state with expanded containers
        context.UpdateMiddlewareState<CollapsingStateData>(collapsingState =>
        {
            _logger?.LogInformation("BeforeToolExecutionAsync: Before expansion - ExpandedContainers count = {Count}",
                collapsingState.ExpandedContainers.Count);

            // Expand containers (unified - both plugins and skills)
            // WithExpandedContainer adds to both ExpandedContainers (session) and ContainersExpandedThisTurn (turn-level)
            foreach (var container in containerExpansions)
            {
                collapsingState = collapsingState.WithExpandedContainer(container);
                _logger?.LogInformation("BeforeToolExecutionAsync: Added container '{Container}' to ExpandedContainers and ContainersExpandedThisTurn",
                    container);
            }

            // Store container instructions (for SystemPrompt injection)
            foreach (var (containerName, instructions) in containerInstructions)
            {
                collapsingState = collapsingState.WithContainerInstructions(containerName, instructions);
            }

            _logger?.LogInformation("BeforeToolExecutionAsync: After expansion - ExpandedContainers count = {Count}, ContainersExpandedThisTurn count = {TurnCount}",
                collapsingState.ExpandedContainers.Count,
                collapsingState.ContainersExpandedThisTurn.Count);

            return collapsingState;
        });

        // NOTE: Tool visibility refresh happens automatically in the NEXT BeforeIterationAsync call
        // because BeforeIterationAsync now filters from _initialTools (complete list) instead of
        // context.Options.Tools (already filtered list). This ensures expanded functions become visible.

        return Task.CompletedTask;
    }

    //═════════════════════════════════════════════════════════════════════════════════════════════════
    // AFTER ITERATION: No-op (filtering moved to BeforeIterationAsync)
    //═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called after each iteration completes.
    /// NOTE: Container filtering happens in BeforeIterationAsync (before messages are sent to LLM).
    /// This hook is kept for potential future use.
    /// </summary>
    public Task AfterIterationAsync(
        AfterIterationContext context,
        CancellationToken cancellationToken)
    {
        // Container filtering moved to BeforeIterationAsync for immediate transparency
        return Task.CompletedTask;
    }

    //═════════════════════════════════════════════════════════════════════════════════════════════════
    // AFTER MESSAGE TURN: Final cleanup (all containers including skills)
    //═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// V2: Removes container function calls and results from the TurnHistory.
    /// Used in AfterMessageTurnAsync before session persistence.
    /// Works directly with mutable TurnHistory list from typed context.
    /// </summary>
    private void RemoveContainerCallsFromTurnHistory_V2(
        List<ChatMessage> turnHistory,
        HashSet<string> containerNames)
    {
        if (containerNames.Count == 0)
            return;

        _logger?.LogInformation("RemoveContainerCallsFromTurnHistory: Scanning for containers: {Containers}",
            string.Join(", ", containerNames));

        // Remove from TurnHistory (this is where messages get added for session persistence)
        if (turnHistory != null && turnHistory.Count > 0)
        {
            _logger?.LogInformation("RemoveContainerCallsFromTurnHistory: TurnHistory has {Count} messages",
                turnHistory.Count);

            // PASS 1: Collect all container CallIds by scanning Assistant messages
            var containerCallIds = new HashSet<string>();

            for (int i = 0; i < turnHistory.Count; i++)
            {
                var message = turnHistory[i];

                if (message.Role == ChatRole.Assistant)
                {
                    // Find container function calls by matching container names
                    foreach (var content in message.Contents)
                    {
                        if (content is FunctionCallContent fcc && containerNames.Contains(fcc.Name))
                        {
                            // This is a container call - track its CallId
                            _logger?.LogInformation("RemoveContainerCallsFromTurnHistory: Found container call '{Name}' with CallId '{CallId}'",
                                fcc.Name, fcc.CallId);
                            containerCallIds.Add(fcc.CallId);
                        }
                    }
                }
            }

            _logger?.LogInformation("RemoveContainerCallsFromTurnHistory: Collected {Count} container CallIds: {CallIds}",
                containerCallIds.Count, string.Join(", ", containerCallIds));

            // PASS 2: Remove container calls and results from messages (backwards to safely remove)
            for (int i = turnHistory.Count - 1; i >= 0; i--)
            {
                var message = turnHistory[i];

                if (message.Role == ChatRole.Assistant)
                {
                    // Remove container function calls
                    var nonContainerCalls = message.Contents
                        .Where(c => c is not FunctionCallContent fcc || !containerNames.Contains(fcc.Name))
                        .ToList();

                    if (nonContainerCalls.Count < message.Contents.Count)
                    {
                        _logger?.LogInformation("RemoveContainerCallsFromTurnHistory: Removed {Count} container calls from Assistant message",
                            message.Contents.Count - nonContainerCalls.Count);

                        if (nonContainerCalls.Count == 0)
                            turnHistory.RemoveAt(i);
                        else
                            turnHistory[i] = new ChatMessage(
                                ChatRole.Assistant, nonContainerCalls);
                    }
                }
                else if (message.Role == ChatRole.Tool)
                {
                    _logger?.LogInformation("RemoveContainerCallsFromTurnHistory: Found Tool message with {Count} contents",
                        message.Contents.Count);

                    // Log all CallIds in this Tool message
                    foreach (var content in message.Contents)
                    {
                        if (content is FunctionResultContent frc)
                        {
                            _logger?.LogInformation("RemoveContainerCallsFromTurnHistory: Tool message has result with CallId '{CallId}', container CallIds: {ContainerCallIds}",
                                frc.CallId, string.Join(", ", containerCallIds));
                        }
                    }

                    // Remove container function results using the CallIds we collected
                    var nonContainerResults = message.Contents
                        .Where(c => c is not FunctionResultContent frc
                            || !containerCallIds.Contains(frc.CallId))
                        .ToList();

                    if (nonContainerResults.Count < message.Contents.Count)
                    {
                        _logger?.LogInformation("RemoveContainerCallsFromTurnHistory: Removed {Count} container results from Tool message",
                            message.Contents.Count - nonContainerResults.Count);

                        if (nonContainerResults.Count == 0)
                            turnHistory.RemoveAt(i);
                        else
                            turnHistory[i] = new ChatMessage(
                                ChatRole.Tool, nonContainerResults);
                    }
                }
            }
        }
    }

    //═════════════════════════════════════════════════════════════════════════════════════════════════
    // AFTER MESSAGE TURN: Cleanup (ephemeral filtering + instruction clearing)
    //═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called after the message turn completes.
    /// 1. Remove any remaining container calls from TurnHistory (before session persistence)
    /// 2. ClearsSystemPrompt injections (if configured to not persist)
    /// </summary>
    public Task AfterMessageTurnAsync(
        AfterMessageTurnContext context,
        CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
            return Task.CompletedTask;

        //─────────────────────────────────────────────────────────────────────────────────────────────
        // STEP 1: Final cleanup - remove container calls from TurnHistory before session persistence
        //─────────────────────────────────────────────────────────────────────────────────────────────

        var collapsingState = context.GetMiddlewareState<CollapsingStateData>() ?? new CollapsingStateData();

        _logger?.LogInformation("AfterMessageTurnAsync: ExpandedContainers count = {Count}, ContainersExpandedThisTurn count = {TurnCount}, TurnHistory count = {HistoryCount}",
            collapsingState.ExpandedContainers.Count,
            collapsingState.ContainersExpandedThisTurn.Count,
            context.TurnHistory?.Count ?? 0);

        // Use state-based tracking (thread-safe, flows through context)
        if (collapsingState.ContainersExpandedThisTurn.Count > 0 && context.TurnHistory != null && context.TurnHistory.Count > 0)
        {
            _logger?.LogInformation("AfterMessageTurnAsync: Removing container calls for: {Containers}",
                string.Join(", ", collapsingState.ContainersExpandedThisTurn));

            // Convert ImmutableHashSet to HashSet for the removal method
            var containersToRemove = collapsingState.ContainersExpandedThisTurn.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // V2: Filter TurnHistory (mutable list in context)
            RemoveContainerCallsFromTurnHistory_V2(context.TurnHistory, containersToRemove);
        }
        else
        {
            _logger?.LogInformation("AfterMessageTurnAsync: No containers to remove (none were expanded this turn)");
        }

        //─────────────────────────────────────────────────────────────────────────────────────────────
        // STEP 2: ClearSystemPrompt injections (if not configured to persist)
        //─────────────────────────────────────────────────────────────────────────────────────────────

        // Always clear turn containers (turn-level state)
        var updatedCollapsing = collapsingState.ClearTurnContainers();

        // Optionally clear system prompt injections (if configured to not persist)
        if (!_config.PersistSystemPromptInjections && updatedCollapsing.ActiveContainerInstructions.Any())
        {
            updatedCollapsing = updatedCollapsing.ClearContainerInstructions();
        }

        // Update state if any changes were made
        if (collapsingState.ContainersExpandedThisTurn.Count > 0 ||
            (!_config.PersistSystemPromptInjections && collapsingState.ActiveContainerInstructions.Any()))
        {
            context.UpdateMiddlewareState<CollapsingStateData>(_ => updatedCollapsing);

            _logger?.LogInformation("AfterMessageTurnAsync: Cleared turn-level state - ContainersExpandedThisTurn reset to 0");
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
    /// Filters [Collapse] container calls/results from the messages list in BeforeIterationContext.
    /// This implements "immediate transparency" - containers disappear even within the same turn.
    /// Only filters [Collapse] containers, not [Skill] containers.
    /// </summary>
    private void FilterCollapseContainersFromMessages(
        BeforeIterationContext context,
        ImmutableHashSet<string> containersExpandedThisTurn)
    {
        // Identify which containers are [Collapse] vs [Skill]
        var collapseContainersToFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var containerName in containersExpandedThisTurn)
        {
            var containerFunction = FindContainerInInitialTools(containerName);
            if (containerFunction == null)
                continue;

            // Check if it's a [Collapse] container (IsCollapse=true OR just IsContainer=true without IsSkill)
            var isCollapse = containerFunction.AdditionalProperties?.TryGetValue("IsCollapse", out var collapseVal) == true
                && collapseVal is bool collapseFlag && collapseFlag;

            var isSkill = containerFunction.AdditionalProperties?.TryGetValue("IsSkill", out var skillVal) == true
                && skillVal is bool skillFlag && skillFlag;

            // Filter if it's a [Collapse] container OR a regular container (not a skill container)
            if (isCollapse || !isSkill)
            {
                collapseContainersToFilter.Add(containerName);
                _logger?.LogDebug("BeforeIterationAsync: Will filter [Collapse] container '{Container}' from messages (IsCollapse={IsCollapse}, IsSkill={IsSkill})",
                    containerName, isCollapse, isSkill);
            }
        }

        if (collapseContainersToFilter.Count == 0)
            return;

        _logger?.LogInformation("BeforeIterationAsync: Filtering {Count} [Collapse] containers from messages: {Containers}",
            collapseContainersToFilter.Count,
            string.Join(", ", collapseContainersToFilter));

        // First pass: Collect container CallIds
        var containerCallIds = new HashSet<string>();

        foreach (var message in context.Messages)
        {
            if (message.Role == ChatRole.Assistant)
            {
                foreach (var content in message.Contents)
                {
                    if (content is FunctionCallContent fcc && collapseContainersToFilter.Contains(fcc.Name))
                    {
                        containerCallIds.Add(fcc.CallId);
                        _logger?.LogDebug("BeforeIterationAsync: Found container call '{Name}' with CallId '{CallId}'",
                            fcc.Name, fcc.CallId);
                    }
                }
            }
        }

        if (containerCallIds.Count == 0)
            return;

        _logger?.LogInformation("BeforeIterationAsync: Collected {Count} container CallIds to filter", containerCallIds.Count);

        // Second pass: Remove container calls and results
        for (int i = context.Messages.Count - 1; i >= 0; i--)
        {
            var message = context.Messages[i];

            if (message.Role == ChatRole.Assistant)
            {
                // Filter out container function calls
                var nonContainerContents = message.Contents
                    .Where(c => c is not FunctionCallContent fcc || !collapseContainersToFilter.Contains(fcc.Name))
                    .ToList();

                if (nonContainerContents.Count < message.Contents.Count)
                {
                    _logger?.LogInformation("BeforeIterationAsync: Removed {Count} container calls from Assistant message",
                        message.Contents.Count - nonContainerContents.Count);

                    if (nonContainerContents.Count == 0)
                    {
                        context.Messages.RemoveAt(i);
                    }
                    else
                    {
                        context.Messages[i] = new ChatMessage(ChatRole.Assistant, nonContainerContents);
                    }
                }
            }
            else if (message.Role == ChatRole.Tool)
            {
                // Filter out container function results using collected CallIds
                var nonContainerResults = message.Contents
                    .Where(c => c is not FunctionResultContent frc || !containerCallIds.Contains(frc.CallId))
                    .ToList();

                if (nonContainerResults.Count < message.Contents.Count)
                {
                    _logger?.LogInformation("BeforeIterationAsync: Removed {Count} container results from Tool message",
                        message.Contents.Count - nonContainerResults.Count);

                    if (nonContainerResults.Count == 0)
                    {
                        context.Messages.RemoveAt(i);
                    }
                    else
                    {
                        context.Messages[i] = new ChatMessage(ChatRole.Tool, nonContainerResults);
                    }
                }
            }
        }

        _logger?.LogInformation("BeforeIterationAsync: Filtered {Count} [Collapse] containers from {MessageCount} messages",
            collapseContainersToFilter.Count, context.Messages.Count);
    }

    /// <summary>
    /// Finds a container function by name in the initial tools list.
    /// Checks both Name and PluginName metadata.
    /// </summary>
    private AIFunction? FindContainerInInitialTools(string containerName)
    {
        if (string.IsNullOrEmpty(containerName))
            return null;

        foreach (var tool in _initialTools)
        {
            if (tool is not AIFunction af)
                continue;

            // Check if it's a container
            var isContainer = af.AdditionalProperties?.TryGetValue("IsContainer", out var val) == true
                && val is bool b && b;

            if (!isContainer)
                continue;

            // Check if name matches (either function name or PluginName)
            if (af.Name == containerName)
                return af;

            var pluginName = af.AdditionalProperties?.TryGetValue("PluginName", out var pnVal) == true
                && pnVal is string pn ? pn : null;

            if (pluginName == containerName)
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
