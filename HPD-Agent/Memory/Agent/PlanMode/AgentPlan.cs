using System;
using System.Collections.Generic;

/// <summary>
/// Represents an agent's plan for completing a complex task.
/// Plans are conversation-scoped working memory that helps agents maintain context and progress.
/// </summary>
public class AgentPlan
{
    /// <summary>Unique identifier for this plan</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

    /// <summary>The goal or objective this plan aims to accomplish</summary>
    public string Goal { get; set; } = string.Empty;

    /// <summary>Ordered list of steps to complete the goal</summary>
    public List<PlanStep> Steps { get; set; } = new();

    /// <summary>When this plan was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Additional context notes discovered during execution</summary>
    public List<string> ContextNotes { get; set; } = new();

    /// <summary>Whether this plan has been marked as complete</summary>
    public bool IsComplete { get; set; } = false;

    /// <summary>When the plan was completed (if applicable)</summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Represents a single step in an agent's plan.
/// </summary>
public class PlanStep
{
    /// <summary>Unique identifier for this step (e.g., "1", "2a", "3")</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Description of what this step involves</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Current status of this step</summary>
    public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;

    /// <summary>Optional notes about this step (progress, blockers, findings)</summary>
    public string? Notes { get; set; }

    /// <summary>When this step was last updated</summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Status of a plan step.
/// </summary>
public enum PlanStepStatus
{
    /// <summary>Step has not been started yet</summary>
    Pending,

    /// <summary>Step is currently being worked on</summary>
    InProgress,

    /// <summary>Step has been successfully completed</summary>
    Completed,

    /// <summary>Step is blocked and cannot proceed</summary>
    Blocked
}
