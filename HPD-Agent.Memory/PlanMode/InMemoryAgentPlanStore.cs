using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HPD.Agent.Memory;

/// <summary>
/// In-memory implementation of AgentPlanStore.
/// Plans are stored in memory and will be lost when the application restarts.
/// This is the default implementation and suitable for development/testing or ephemeral plan usage.
/// </summary>
public class InMemoryAgentPlanStore : AgentPlanStore
{
    private readonly ILogger<InMemoryAgentPlanStore>? _logger;
    private readonly ConcurrentDictionary<string, AgentPlan> _plans = new();

    public InMemoryAgentPlanStore(ILogger<InMemoryAgentPlanStore>? logger = null)
    {
        _logger = logger;
    }

    public override Task<AgentPlan> CreatePlanAsync(
        string conversationId,
        string goal,
        string[]? initialSteps = null,
        CancellationToken cancellationToken = default)
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
        return Task.FromResult(plan);
    }

    public override Task<AgentPlan?> GetPlanAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        _plans.TryGetValue(conversationId, out var plan);
        return Task.FromResult(plan);
    }

    public override Task<bool> HasPlanAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_plans.ContainsKey(conversationId));
    }

    public override Task<PlanStep?> UpdateStepAsync(
        string conversationId,
        string stepId,
        PlanStepStatus status,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (!_plans.TryGetValue(conversationId, out var plan))
        {
            _logger?.LogWarning("Attempted to update step {StepId} but no plan exists for conversation {ConversationId}", stepId, conversationId);
            return Task.FromResult<PlanStep?>(null);
        }

        var step = plan.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step == null)
        {
            _logger?.LogWarning("Step {StepId} not found in plan for conversation {ConversationId}", stepId, conversationId);
            return Task.FromResult<PlanStep?>(null);
        }

        step.Status = status;
        if (notes != null)
        {
            step.Notes = notes;
        }
        step.LastUpdated = DateTime.UtcNow;

        _logger?.LogInformation("Updated step {StepId} to {Status} for conversation {ConversationId}", stepId, status, conversationId);
        return Task.FromResult<PlanStep?>(step);
    }

    public override Task AddContextNoteAsync(
        string conversationId,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (!_plans.TryGetValue(conversationId, out var plan))
        {
            _logger?.LogWarning("Attempted to add context note but no plan exists for conversation {ConversationId}", conversationId);
            return Task.CompletedTask;
        }

        plan.ContextNotes.Add($"[{DateTime.UtcNow:HH:mm:ss}] {note}");
        _logger?.LogInformation("Added context note to plan {PlanId} for conversation {ConversationId}", plan.Id, conversationId);
        return Task.CompletedTask;
    }

    public override Task<PlanStep?> AddStepAsync(
        string conversationId,
        string description,
        string? afterStepId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_plans.TryGetValue(conversationId, out var plan))
        {
            _logger?.LogWarning("Attempted to add step but no plan exists for conversation {ConversationId}", conversationId);
            return Task.FromResult<PlanStep?>(null);
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
        return Task.FromResult<PlanStep?>(newStep);
    }

    public override Task CompletePlanAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (!_plans.TryGetValue(conversationId, out var plan))
        {
            _logger?.LogWarning("Attempted to complete plan but no plan exists for conversation {ConversationId}", conversationId);
            return Task.CompletedTask;
        }

        plan.IsComplete = true;
        plan.CompletedAt = DateTime.UtcNow;
        _logger?.LogInformation("Completed plan {PlanId} for conversation {ConversationId}", plan.Id, conversationId);
        return Task.CompletedTask;
    }

    public override Task ClearPlanAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (_plans.TryRemove(conversationId, out var plan))
        {
            _logger?.LogInformation("Cleared plan {PlanId} for conversation {ConversationId}", plan.Id, conversationId);
        }
        return Task.CompletedTask;
    }

    public override Task<string> BuildPlanPromptAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (!_plans.TryGetValue(conversationId, out var plan))
        {
            return Task.FromResult(string.Empty);
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
        return Task.FromResult(sb.ToString());
    }

    public override AgentPlanStoreSnapshot SerializeToSnapshot()
    {
        return new AgentPlanStoreSnapshot
        {
            StoreType = AgentPlanStoreType.InMemory,
            Plans = new Dictionary<string, AgentPlan>(_plans)
        };
    }

    /// <summary>
    /// Creates an InMemoryAgentPlanStore from a snapshot.
    /// </summary>
    public static InMemoryAgentPlanStore FromSnapshot(AgentPlanStoreSnapshot snapshot)
    {
        var store = new InMemoryAgentPlanStore();
        foreach (var kvp in snapshot.Plans)
        {
            store._plans[kvp.Key] = kvp.Value;
        }
        return store;
    }
}
