using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HPD.Agent.Memory;

/// <summary>
/// Injects plan mode instructions and active execution plans into the conversation.
/// </summary>
/// <remarks>
/// <para><b>UNIFIED MIDDLEWARE:</b></para>
/// <para>
/// Implements <see cref="IAgentMiddleware"/> with <see cref="IAgentMiddleware.BeforeMessageTurnAsync"/>
/// to inject plan mode instructions and active plans at the start of each message turn.
/// </para>
///
/// <para><b>Two-Phase Injection:</b></para>
/// <list type="number">
/// <item><b>Instructions (Options.Instructions):</b> General plan mode guidance explaining how to use plan tools.
/// Injected when plan mode is enabled, regardless of whether an active plan exists.</item>
/// <item><b>Active Plan (System Message):</b> Current plan state (goals, steps, progress).
/// Only injected when a plan exists for the conversation.</item>
/// </list>
///
/// <para><b>Plan Lifecycle:</b></para>
/// <para>
/// Plans are created per-conversation and persist across message turns.
/// The LLM uses the plan to track progress and coordinate complex multi-step tasks.
/// </para>
/// </remarks>
public class AgentPlanAgentMiddleware : IAgentMiddleware
{
    private readonly AgentPlanStore _store;
    private readonly PlanModeConfig? _config;
    private readonly ILogger<AgentPlanAgentMiddleware>? _logger;

    public AgentPlanAgentMiddleware(
        AgentPlanStore store,
        PlanModeConfig? config = null,
        ILogger<AgentPlanAgentMiddleware>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Injects plan mode instructions and active plan (if exists) into the conversation.
    /// </summary>
    /// <remarks>
    /// <para><b>Phase 1: Instruction Injection</b></para>
    /// <para>
    /// If plan mode is enabled, appends plan mode instructions to Options.Instructions.
    /// This happens regardless of whether an active plan exists, so the LLM knows how to use plan tools.
    /// </para>
    ///
    /// <para><b>Phase 2: Active Plan Injection</b></para>
    /// <para>
    /// If an active plan exists for this conversation, injects it as a system message.
    /// This provides the current plan state (goals, steps, progress).
    /// </para>
    /// </remarks>
    public async Task BeforeMessageTurnAsync(
        BeforeMessageTurnContext context,
        CancellationToken cancellationToken)
    {
        // V2: This middleware hooks into BeforeMessageTurn but needs access to Options which isn't available
        // NOTE: Plan mode instructions would normally be set at agent configuration time, not dynamically
        // For now, log a warning if plan mode is enabled but we can't inject instructions
        if (_config?.Enabled == true)
        {
            _logger?.LogWarning(
                "Plan mode instructions cannot be injected in V2 BeforeMessageTurn hook. " +
                "Set instructions via AgentConfig.ChatOptions.Instructions instead.");
        }

        // PHASE 2: Inject active plan (if exists)
        var conversationId = context.ConversationId;

        if (string.IsNullOrEmpty(conversationId))
        {
            // No conversation ID available, skip active plan injection
            return;
        }

        // Only inject plan if one exists for this conversation
        if (!await _store.HasPlanAsync(conversationId))
        {
            return;
        }

        var planPrompt = await _store.BuildPlanPromptAsync(conversationId);
        if (string.IsNullOrEmpty(planPrompt))
        {
            return;
        }

        // Inject plan as a system message at the beginning
        var messagesWithPlan = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, planPrompt)
        };
        messagesWithPlan.AddRange(context.ConversationHistory);

        // V2: ConversationHistory is mutable - replace content
        context.ConversationHistory.Clear();
        foreach (var msg in messagesWithPlan)
        {
            context.ConversationHistory.Add(msg);
        }

        _logger?.LogDebug(
            "Injected active plan into prompt for agent {AgentName}, conversation {ConversationId}",
            context.AgentName,
            conversationId);
    }

    /// <summary>
    /// Gets default plan mode instructions explaining how to use plan tools.
    /// Moved from Agent to make plan mode fully middleware-based.
    /// </summary>
    private static string GetDefaultPlanModeInstructions()
    {
        return @"[PLAN MODE ENABLED]
You have access to plan management tools for complex multi-step tasks.

Available functions:
- create_plan(goal, steps[]): Create a new plan with a goal and initial steps
- update_plan_step(stepId, status, notes): Update step status (pending/in_progress/completed/blocked) and add notes
- add_plan_step(description, afterStepId): Add a new step when you discover additional work needed
- add_context_note(note): Record important discoveries, learnings, or context during execution
- complete_plan(): Mark the entire plan as complete when goal is achieved

Best practices:
- Create plans for tasks requiring 3+ steps, affecting multiple files, or with uncertain Collapse
- Update step status as you progress to maintain context across conversation turns
- Add context notes when discovering important information (e.g., ""Found auth uses JWT, not sessions"")
- Plans are conversation-Collapsed working memory - they help you maintain progress and avoid repeating failed approaches
- When a step is blocked, mark it as 'blocked' with notes explaining why, then continue with other steps if possible";
    }
}
