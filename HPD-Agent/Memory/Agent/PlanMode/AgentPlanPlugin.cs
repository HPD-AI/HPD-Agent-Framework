using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Linq;

/// <summary>
/// HPD-Agent AI plugin for Plan Mode management.
/// Provides functions for agents to create and manage execution plans.
/// Uses ConversationContext.CurrentConversationId to identify which conversation's plan to manipulate.
/// </summary>
public class AgentPlanPlugin
{
    private readonly AgentPlanManager _manager;
    private readonly ILogger<AgentPlanPlugin>? _logger;

    public AgentPlanPlugin(AgentPlanManager manager, ILogger<AgentPlanPlugin>? logger = null)
    {
        _manager = manager;
        _logger = logger;
    }

    [AIFunction]
    [Description("Create a new execution plan with a goal and initial steps. Use this for complex multi-step tasks.")]
    public Task<string> CreatePlanAsync(
        [Description("The goal or objective this plan aims to accomplish")] string goal,
        [Description("Array of step descriptions (e.g., ['Analyze code', 'Refactor auth', 'Run tests'])")] string[] steps)
    {
        var conversationId = ConversationContext.CurrentConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return Task.FromResult("Error: No conversation context available.");
        }

        if (string.IsNullOrEmpty(goal))
        {
            return Task.FromResult("Error: Goal is required for creating a plan.");
        }

        if (steps == null || steps.Length == 0)
        {
            return Task.FromResult("Error: At least one step is required for creating a plan.");
        }

        var plan = _manager.CreatePlan(conversationId, goal, steps);

        var stepList = string.Join("\n", plan.Steps.Select(s => $"  {s.Id}. {s.Description}"));
        return Task.FromResult($"Created plan {plan.Id} with {plan.Steps.Count} steps:\n{stepList}\n\nUse update_plan_step() to mark progress.");
    }

    [AIFunction]
    [Description("Update the status of a specific step in the current plan. Use this as you make progress.")]
    public Task<string> UpdatePlanStepAsync(
        [Description("The step ID to update (e.g., '1', '2', '3')")] string stepId,
        [Description("The new status: 'pending', 'in_progress', 'completed', or 'blocked'")] string status,
        [Description("Optional notes about this step's progress, findings, or blockers")] string? notes = null)
    {
        var conversationId = ConversationContext.CurrentConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return Task.FromResult("Error: No conversation context available.");
        }

        if (string.IsNullOrEmpty(stepId))
        {
            return Task.FromResult("Error: Step ID is required.");
        }

        if (!_manager.HasPlan(conversationId))
        {
            return Task.FromResult("Error: No active plan exists. Create a plan first using create_plan().");
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
            return Task.FromResult($"Error: Invalid status '{status}'. Use: pending, in_progress, completed, or blocked.");
        }

        var step = _manager.UpdateStep(conversationId, stepId, parsedStatus.Value, notes);
        if (step == null)
        {
            return Task.FromResult($"Error: Step '{stepId}' not found in current plan.");
        }

        var response = $"Updated step {stepId} to {parsedStatus}";
        if (notes != null)
        {
            response += $" with notes: {notes}";
        }
        return Task.FromResult(response);
    }

    [AIFunction]
    [Description("Add a new step to the current plan. Use this when you discover additional work is needed.")]
    public Task<string> AddPlanStepAsync(
        [Description("Description of the new step to add")] string description,
        [Description("Optional: ID of step to insert after (e.g., '2'). If omitted, adds to end.")] string? afterStepId = null)
    {
        var conversationId = ConversationContext.CurrentConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return Task.FromResult("Error: No conversation context available.");
        }

        if (string.IsNullOrEmpty(description))
        {
            return Task.FromResult("Error: Step description is required.");
        }

        if (!_manager.HasPlan(conversationId))
        {
            return Task.FromResult("Error: No active plan exists. Create a plan first using create_plan().");
        }

        var step = _manager.AddStep(conversationId, description, afterStepId);
        if (step == null)
        {
            return Task.FromResult("Error: Failed to add step to plan.");
        }

        return Task.FromResult($"Added step {step.Id}: {description}");
    }

    [AIFunction]
    [Description("Add a context note to the current plan. Use this to record important discoveries, learnings, or context.")]
    public Task<string> AddContextNoteAsync(
        [Description("The note to add (e.g., 'Discovered auth uses JWT not sessions')")] string note)
    {
        var conversationId = ConversationContext.CurrentConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return Task.FromResult("Error: No conversation context available.");
        }

        if (string.IsNullOrEmpty(note))
        {
            return Task.FromResult("Error: Note content is required.");
        }

        if (!_manager.HasPlan(conversationId))
        {
            return Task.FromResult("Error: No active plan exists. Create a plan first using create_plan().");
        }

        _manager.AddContextNote(conversationId, note);
        return Task.FromResult($"Added context note: {note}");
    }

    [AIFunction]
    [Description("Get the current plan details including all steps and their status.")]
    public Task<string> GetCurrentPlanAsync()
    {
        var conversationId = ConversationContext.CurrentConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return Task.FromResult("Error: No conversation context available.");
        }

        var plan = _manager.GetPlan(conversationId);
        if (plan == null)
        {
            return Task.FromResult("No active plan exists. Create one using create_plan().");
        }

        var stepList = string.Join("\n", plan.Steps.Select(s =>
            $"  {s.Id}. [{s.Status}] {s.Description}" +
            (string.IsNullOrEmpty(s.Notes) ? "" : $"\n     Notes: {s.Notes}")));

        var response = $"Plan {plan.Id}: {plan.Goal}\n" +
                      $"Status: {(plan.IsComplete ? "COMPLETED" : "In Progress")}\n" +
                      $"Steps:\n{stepList}";

        if (plan.ContextNotes.Any())
        {
            response += $"\n\nContext Notes:\n" + string.Join("\n", plan.ContextNotes.Select(n => $"  • {n}"));
        }

        return Task.FromResult(response);
    }

    [AIFunction]
    [Description("Mark the entire plan as complete. Use this when all steps are done and the goal is achieved.")]
    public Task<string> CompletePlanAsync()
    {
        var conversationId = ConversationContext.CurrentConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return Task.FromResult("Error: No conversation context available.");
        }

        if (!_manager.HasPlan(conversationId))
        {
            return Task.FromResult("Error: No active plan exists.");
        }

        var plan = _manager.GetPlan(conversationId);
        var incompleteSteps = plan?.Steps.Where(s => s.Status != PlanStepStatus.Completed).ToList();

        if (incompleteSteps?.Any() == true)
        {
            var incompleteList = string.Join(", ", incompleteSteps.Select(s => s.Id));
            return Task.FromResult($"Warning: Plan has incomplete steps: {incompleteList}. Mark them as completed first or complete anyway?");
        }

        _manager.CompletePlan(conversationId);
        return Task.FromResult($"Plan {plan?.Id} marked as complete! Goal '{plan?.Goal}' achieved.");
    }
}
