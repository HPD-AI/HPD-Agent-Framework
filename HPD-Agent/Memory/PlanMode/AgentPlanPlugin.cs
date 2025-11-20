using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Linq;
using HPD_Agent.Memory.Agent.PlanMode;
using HPD.Agent;

/// <summary>
/// HPD-Agent AI plugin for Plan Mode management.
/// Provides functions for agents to create and manage execution plans.
/// Uses Agent.CurrentFunctionContext.RunContext.ConversationId to identify which conversation's plan to manipulate.
/// </summary>
public class AgentPlanPlugin
{
    private readonly AgentPlanStore _store;
    private readonly ILogger<AgentPlanPlugin>? _logger;

    public AgentPlanPlugin(AgentPlanStore store, ILogger<AgentPlanPlugin>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    [AIFunction]
    [Description("Create a new execution plan to track progress on multi-step tasks. Use when you need to plan and track complex work.")]
    public async Task<string> CreatePlanAsync(
        [Description("The goal or objective this plan aims to accomplish")] string goal,
        [Description("Array of step descriptions (e.g., ['Analyze code', 'Refactor auth', 'Run tests'])")] string[] steps)
    {
        var conversationId = AgentCore.CurrentFunctionContext?.State?.ConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return "Error: No conversation context available.";
        }

        if (string.IsNullOrEmpty(goal))
        {
            return "Error: Goal is required for creating a plan.";
        }

        if (steps == null || steps.Length == 0)
        {
            return "Error: At least one step is required for creating a plan.";
        }

        var plan = await _store.CreatePlanAsync(conversationId, goal, steps);

        var stepList = string.Join("\n", plan.Steps.Select(s => $"  {s.Id}. {s.Description}"));
        return $"Created plan {plan.Id} with {plan.Steps.Count} steps:\n{stepList}\n\nUse update_plan_step() to mark progress.";
    }

    [AIFunction]
    [Description("Update the status of a specific step in the current plan. Use this as you make progress.")]
    public async Task<string> UpdatePlanStepAsync(
        [Description("The step ID to update (e.g., '1', '2', '3')")] string stepId,
        [Description("The new status: 'pending', 'in_progress', 'completed', or 'blocked'")] string status,
        [Description("Optional notes about this step's progress, findings, or blockers")] string? notes = null)
    {
        var conversationId = AgentCore.CurrentFunctionContext?.State?.ConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return "Error: No conversation context available.";
        }

        if (string.IsNullOrEmpty(stepId))
        {
            return "Error: Step ID is required.";
        }

        if (!await _store.HasPlanAsync(conversationId))
        {
            return "Error: No active plan exists. Create a plan first using create_plan().";
        }

        var parsedStatus = status.ToLowerInvariant() switch
        {
            "pending" => PlanStepStatus.Pending,
            "in_progress" or "inprogress" => PlanStepStatus.InProgress,
            "completed" or "complete" or "done" => PlanStepStatus.Completed,
            "blocked" => PlanStepStatus.Blocked,
            _ => (PlanStepStatus?)null
        };

        if (parsedStatus == null)
        {
            return $"Error: Invalid status '{status}'. Use: pending, in_progress, completed, or blocked.";
        }

        var step = await _store.UpdateStepAsync(conversationId, stepId, parsedStatus.Value, notes);
        if (step == null)
        {
            return $"Error: Step '{stepId}' not found in current plan.";
        }

        var response = $"Updated step {stepId} to {parsedStatus}";
        if (notes != null)
        {
            response += $" with notes: {notes}";
        }
        return response;
    }

    [AIFunction]
    [Description("Add a new step to the current plan. Use this when you discover additional work is needed.")]
    public async Task<string> AddPlanStepAsync(
        [Description("Description of the new step to add")] string description,
        [Description("Optional: ID of step to insert after (e.g., '2'). If omitted, adds to end.")] string? afterStepId = null)
    {
        var conversationId = AgentCore.CurrentFunctionContext?.State?.ConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return "Error: No conversation context available.";
        }

        if (string.IsNullOrEmpty(description))
        {
            return "Error: Step description is required.";
        }

        if (!await _store.HasPlanAsync(conversationId))
        {
            return "Error: No active plan exists. Create a plan first using create_plan().";
        }

        var step = await _store.AddStepAsync(conversationId, description, afterStepId);
        if (step == null)
        {
            return "Error: Failed to add step to plan.";
        }

        return $"Added step {step.Id}: {description}";
    }

    [AIFunction]
    [Description("Add a context note to the current plan. Use this to record important discoveries, learnings, or context.")]
    public async Task<string> AddContextNoteAsync(
        [Description("The note to add (e.g., 'Discovered auth uses JWT not sessions')")] string note)
    {
        var conversationId = AgentCore.CurrentFunctionContext?.State?.ConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return "Error: No conversation context available.";
        }

        if (string.IsNullOrEmpty(note))
        {
            return "Error: Note content is required.";
        }

        if (!await _store.HasPlanAsync(conversationId))
        {
            return "Error: No active plan exists. Create a plan first using create_plan().";
        }

        await _store.AddContextNoteAsync(conversationId, note);
        return $"Added context note: {note}";
    }

    // Note: GetCurrentPlanAsync() removed - the plan is automatically injected into every request
    // via AgentPlanFilter, so the agent always has the current plan in context without needing
    // to call a function. This saves tokens and simplifies the API.

    [AIFunction]
    [Description("Mark the entire plan as complete. Use this when all steps are done and the goal is achieved.")]
    public async Task<string> CompletePlanAsync()
    {
        var conversationId = AgentCore.CurrentFunctionContext?.State?.ConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return "Error: No conversation context available.";
        }

        if (!await _store.HasPlanAsync(conversationId))
        {
            return "Error: No active plan exists.";
        }

        var plan = await _store.GetPlanAsync(conversationId);
        var incompleteSteps = plan?.Steps.Where(s => s.Status != PlanStepStatus.Completed).ToList();

        if (incompleteSteps?.Any() == true)
        {
            var incompleteList = string.Join(", ", incompleteSteps.Select(s => s.Id));
            return $"Warning: Plan has incomplete steps: {incompleteList}. Mark them as completed first or complete anyway?";
        }

        await _store.CompletePlanAsync(conversationId);
        return $"Plan {plan?.Id} marked as complete! Goal '{plan?.Goal}' achieved.";
    }
}
