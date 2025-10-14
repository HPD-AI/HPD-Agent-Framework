using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HPD_Agent.Memory.Agent.PlanMode;

/// <summary>
/// JSON file-based implementation of AgentPlanStore.
/// Persists plans to the file system for durability across application restarts.
/// Each conversation's plan is stored in a separate JSON file.
/// </summary>
public class JsonAgentPlanStore : AgentPlanStore
{
    private readonly string _storageDirectory;
    private readonly ILogger<JsonAgentPlanStore>? _logger;
    private readonly ConcurrentDictionary<string, AgentPlan> _cache = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public JsonAgentPlanStore(string storageDirectory, ILogger<JsonAgentPlanStore>? logger = null)
    {
        _storageDirectory = storageDirectory;
        _logger = logger;

        // Ensure directory exists
        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
            _logger?.LogInformation("Created plan storage directory: {Directory}", _storageDirectory);
        }

        // Load existing plans into cache
        LoadExistingPlans();
    }

    private void LoadExistingPlans()
    {
        try
        {
            var files = Directory.GetFiles(_storageDirectory, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var plan = JsonSerializer.Deserialize<AgentPlan>(json, JsonOptions);
                    if (plan != null)
                    {
                        var conversationId = Path.GetFileNameWithoutExtension(file);
                        _cache[conversationId] = plan;
                        _logger?.LogDebug("Loaded plan {PlanId} for conversation {ConversationId}", plan.Id, conversationId);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to load plan from file: {File}", file);
                }
            }
            _logger?.LogInformation("Loaded {Count} existing plans from storage", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load existing plans from directory: {Directory}", _storageDirectory);
        }
    }

    private string GetPlanFilePath(string conversationId)
    {
        // Sanitize conversation ID for file system
        var sanitized = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_storageDirectory, $"{sanitized}.json");
    }

    private async Task SavePlanAsync(string conversationId, AgentPlan plan, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var filePath = GetPlanFilePath(conversationId);
            var json = JsonSerializer.Serialize(plan, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
            _logger?.LogDebug("Saved plan {PlanId} for conversation {ConversationId} to {File}", plan.Id, conversationId, filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save plan for conversation {ConversationId}", conversationId);
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task DeletePlanFileAsync(string conversationId, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var filePath = GetPlanFilePath(conversationId);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger?.LogDebug("Deleted plan file for conversation {ConversationId}", conversationId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete plan file for conversation {ConversationId}", conversationId);
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public override async Task<AgentPlan> CreatePlanAsync(
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

        _cache[conversationId] = plan;
        await SavePlanAsync(conversationId, plan, cancellationToken);
        _logger?.LogInformation("Created plan {PlanId} for conversation {ConversationId} with goal: {Goal}", plan.Id, conversationId, goal);
        return plan;
    }

    public override Task<AgentPlan?> GetPlanAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        _cache.TryGetValue(conversationId, out var plan);
        return Task.FromResult(plan);
    }

    public override Task<bool> HasPlanAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_cache.ContainsKey(conversationId));
    }

    public override async Task<PlanStep?> UpdateStepAsync(
        string conversationId,
        string stepId,
        PlanStepStatus status,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (!_cache.TryGetValue(conversationId, out var plan))
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

        await SavePlanAsync(conversationId, plan, cancellationToken);
        _logger?.LogInformation("Updated step {StepId} to {Status} for conversation {ConversationId}", stepId, status, conversationId);
        return step;
    }

    public override async Task AddContextNoteAsync(
        string conversationId,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (!_cache.TryGetValue(conversationId, out var plan))
        {
            _logger?.LogWarning("Attempted to add context note but no plan exists for conversation {ConversationId}", conversationId);
            return;
        }

        plan.ContextNotes.Add($"[{DateTime.UtcNow:HH:mm:ss}] {note}");
        await SavePlanAsync(conversationId, plan, cancellationToken);
        _logger?.LogInformation("Added context note to plan {PlanId} for conversation {ConversationId}", plan.Id, conversationId);
    }

    public override async Task<PlanStep?> AddStepAsync(
        string conversationId,
        string description,
        string? afterStepId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_cache.TryGetValue(conversationId, out var plan))
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

        await SavePlanAsync(conversationId, plan, cancellationToken);
        _logger?.LogInformation("Added step {StepId} to plan {PlanId} for conversation {ConversationId}", newStepId, plan.Id, conversationId);
        return newStep;
    }

    public override async Task CompletePlanAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (!_cache.TryGetValue(conversationId, out var plan))
        {
            _logger?.LogWarning("Attempted to complete plan but no plan exists for conversation {ConversationId}", conversationId);
            return;
        }

        plan.IsComplete = true;
        plan.CompletedAt = DateTime.UtcNow;
        await SavePlanAsync(conversationId, plan, cancellationToken);
        _logger?.LogInformation("Completed plan {PlanId} for conversation {ConversationId}", plan.Id, conversationId);
    }

    public override async Task ClearPlanAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryRemove(conversationId, out var plan))
        {
            await DeletePlanFileAsync(conversationId, cancellationToken);
            _logger?.LogInformation("Cleared plan {PlanId} for conversation {ConversationId}", plan.Id, conversationId);
        }
    }

    public override Task<string> BuildPlanPromptAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (!_cache.TryGetValue(conversationId, out var plan))
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
            StoreType = AgentPlanStoreType.JsonFile,
            Plans = new Dictionary<string, AgentPlan>(_cache),
            Configuration = new Dictionary<string, object>
            {
                ["StorageDirectory"] = _storageDirectory
            }
        };
    }

    /// <summary>
    /// Creates a JsonAgentPlanStore from a snapshot.
    /// </summary>
    public static JsonAgentPlanStore FromSnapshot(AgentPlanStoreSnapshot snapshot)
    {
        var storageDirectory = snapshot.Configuration?["StorageDirectory"] as string ?? "./agent-plans";
        var store = new JsonAgentPlanStore(storageDirectory);

        // Override cache with snapshot data
        store._cache.Clear();
        foreach (var kvp in snapshot.Plans)
        {
            store._cache[kvp.Key] = kvp.Value;
        }

        return store;
    }
}
