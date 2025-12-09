using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HPD.Agent.Memory;

/// <summary>
/// Manages the lifecycle of agent plans across conversations.
/// Plans are conversation-Collapsed and stored in-memory (indexed by conversation ID).
/// </summary>
public class AgentPlanManager
{
    private readonly ILogger<AgentPlanManager>? _logger;
    private readonly ConcurrentDictionary<string, AgentPlan> _plans = new();

    public AgentPlanManager(ILogger<AgentPlanManager>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a new plan for a conversation. If a plan already exists, it will be replaced.
    /// </summary>
    public AgentPlan CreatePlan(string conversationId, string goal, string[]? initialSteps = null)
    {
        var plan = new AgentPlan
        {
            Goal = goal,
            CreatedAt = DateTime.UtcNow
        };

        if (initialSteps != null)
        {
            for (int i = 0; i < initialSteps.Length; i++)
            {
                plan.Steps.Add(new PlanStep
                {
                    Id = (i + 1).ToString(),
                    Description = initialSteps[i],
                    Status = PlanStepStatus.Pending
                });
            }
        }

        _plans[conversationId] = plan;
        _logger?.LogInformation("Created plan {PlanId} for conversation {ConversationId} with goal: {Goal}", plan.Id, conversationId, goal);
        return plan;
    }

    /// <summary>
    /// Gets the plan for a conversation, or null if no plan exists.
    /// </summary>
    public AgentPlan? GetPlan(string conversationId)
    {
        _plans.TryGetValue(conversationId, out var plan);
        return plan;
    }

    /// <summary>
    /// Checks if a plan exists for a conversation.
    /// </summary>
    public bool HasPlan(string conversationId)
    {
        return _plans.ContainsKey(conversationId);
    }

    /// <summary>
    /// Updates the status and notes of a specific step.
    /// </summary>
    public PlanStep? UpdateStep(string conversationId, string stepId, PlanStepStatus status, string? notes = null)
    {
        if (!_plans.TryGetValue(conversationId, out var plan))
        {
            _logger?.LogWarning("Attempted to update step {StepId} but no plan exists for conversation {ConversationId}", stepId, conversationId);
            return null;
        }

        var step = plan.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step == null)
        {
            _logger?.LogWarning("Step {StepId} not found in plan for conversation {ConversationId}", stepId, conversationId);
            return null;
        }

        step.Status = status;
        if (notes != null)
        {
            step.Notes = notes;
        }
        step.LastUpdated = DateTime.UtcNow;

        _logger?.LogInformation("Updated step {StepId} to {Status} for conversation {ConversationId}", stepId, status, conversationId);
        return step;
    }

    /// <summary>
    /// Adds a context note to a conversation's plan.
    /// </summary>
    public void AddContextNote(string conversationId, string note)
    {
        if (!_plans.TryGetValue(conversationId, out var plan))
        {
            _logger?.LogWarning("Attempted to add context note but no plan exists for conversation {ConversationId}", conversationId);
            return;
        }

        plan.ContextNotes.Add($"[{DateTime.UtcNow:HH:mm:ss}] {note}");
        _logger?.LogInformation("Added context note to plan {PlanId} for conversation {ConversationId}", plan.Id, conversationId);
    }

    /// <summary>
    /// Adds a new step to a conversation's plan.
    /// </summary>
    public PlanStep? AddStep(string conversationId, string description, string? afterStepId = null)
    {
        if (!_plans.TryGetValue(conversationId, out var plan))
        {
            _logger?.LogWarning("Attempted to add step but no plan exists for conversation {ConversationId}", conversationId);
            return null;
        }

        var newStepId = (plan.Steps.Count + 1).ToString();
        var newStep = new PlanStep
        {
            Id = newStepId,
            Description = description,
            Status = PlanStepStatus.Pending
        };

        if (afterStepId != null)
        {
            var afterIndex = plan.Steps.FindIndex(s => s.Id == afterStepId);
            if (afterIndex >= 0)
            {
                plan.Steps.Insert(afterIndex + 1, newStep);
            }
            else
            {
                plan.Steps.Add(newStep);
            }
        }
        else
        {
            plan.Steps.Add(newStep);
        }

        _logger?.LogInformation("Added step {StepId} to plan {PlanId} for conversation {ConversationId}", newStepId, plan.Id, conversationId);
        return newStep;
    }

    /// <summary>
    /// Marks a conversation's plan as complete.
    /// </summary>
    public void CompletePlan(string conversationId)
    {
        if (!_plans.TryGetValue(conversationId, out var plan))
        {
            _logger?.LogWarning("Attempted to complete plan but no plan exists for conversation {ConversationId}", conversationId);
            return;
        }

        plan.IsComplete = true;
        plan.CompletedAt = DateTime.UtcNow;
        _logger?.LogInformation("Completed plan {PlanId} for conversation {ConversationId}", plan.Id, conversationId);
    }

    /// <summary>
    /// Clears a conversation's plan.
    /// </summary>
    public void ClearPlan(string conversationId)
    {
        if (_plans.TryRemove(conversationId, out var plan))
        {
            _logger?.LogInformation("Cleared plan {PlanId} for conversation {ConversationId}", plan.Id, conversationId);
        }
    }

    /// <summary>
    /// Builds a formatted string representation of a conversation's plan for injection into prompts.
    /// </summary>
    public string BuildPlanPrompt(string conversationId)
    {
        if (!_plans.TryGetValue(conversationId, out var plan))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[CURRENT_PLAN]");
        sb.AppendLine($"Goal: {plan.Goal}");
        sb.AppendLine($"Plan ID: {plan.Id}");
        sb.AppendLine($"Created: {plan.CreatedAt:yyyy-MM-dd HH:mm:ss}");

        if (plan.IsComplete)
        {
            sb.AppendLine($"Status: ✓ COMPLETED at {plan.CompletedAt:HH:mm:ss}");
        }
        else
        {
            sb.AppendLine("Status: In Progress");
        }

        sb.AppendLine();
        sb.AppendLine("Steps:");
        foreach (var step in plan.Steps)
        {
            var statusIcon = step.Status switch
            {
                PlanStepStatus.Pending => "○",
                PlanStepStatus.InProgress => "◐",
                PlanStepStatus.Completed => "●",
                PlanStepStatus.Blocked => "✖",
                _ => "?"
            };

            sb.AppendLine($"  {statusIcon} [{step.Id}] {step.Description} ({step.Status})");
            if (!string.IsNullOrEmpty(step.Notes))
            {
                sb.AppendLine($"      Notes: {step.Notes}");
            }
        }

        if (plan.ContextNotes.Any())
        {
            sb.AppendLine();
            sb.AppendLine("Context Notes:");
            foreach (var note in plan.ContextNotes)
            {
                sb.AppendLine($"  • {note}");
            }
        }

        sb.AppendLine("[END_CURRENT_PLAN]");
        return sb.ToString();
    }
}
