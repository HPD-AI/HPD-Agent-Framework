// Copyright (c) Einstein Essibu. All rights reserved.
// Concrete ingestion context with all lessons from Kernel Memory applied.

using HPDAgent.Memory.Abstractions.Models;
using HPDAgent.Memory.Abstractions.Pipeline;

namespace HPDAgent.Memory.Core.Contexts;

/// <summary>
/// Concrete implementation of ingestion context for document processing pipelines.
/// Incorporates all best practices learned from Kernel Memory:
/// - Handler idempotency tracking
/// - File lineage and artifact management
/// - Generated file tracking
/// - Per-file processing state
/// </summary>
public class DocumentIngestionContext : IIngestionContext
{
    // ========================================
    // IPipelineContext Implementation
    // ========================================

    public string PipelineId { get; init; } = Guid.NewGuid().ToString("N");
    public string ExecutionId { get; init; } = Guid.NewGuid().ToString("N");
    public required string Index { get; init; }
    public List<PipelineStep> Steps { get; init; } = new();
    public List<PipelineStep> CompletedSteps { get; } = new();
    public List<PipelineStep> RemainingSteps { get; set; } = new();
    public bool IsComplete => RemainingSteps.Count == 0;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object> Data { get; } = new();
    public required IServiceProvider Services { get; init; }
    public Dictionary<string, List<string>> Tags { get; init; } = new();
    public List<PipelineLogEntry> LogEntries { get; } = new();

    // Idempotency tracking (from Kernel Memory pattern)
    private readonly HashSet<string> _processedHandlers = new(StringComparer.OrdinalIgnoreCase);

    // Parallel step tracking
    private readonly HashSet<string> _completedHandlersInCurrentStep = new(StringComparer.OrdinalIgnoreCase);
    private bool _isIsolated;

    IReadOnlyList<PipelineStep> IPipelineContext.Steps => Steps.AsReadOnly();
    IReadOnlyList<PipelineStep> IPipelineContext.CompletedSteps => CompletedSteps.AsReadOnly();
    IReadOnlyList<PipelineStep> IPipelineContext.RemainingSteps => RemainingSteps.AsReadOnly();
    IDictionary<string, object> IPipelineContext.Data => Data;
    IDictionary<string, List<string>> IPipelineContext.Tags => Tags;
    IList<PipelineLogEntry> IPipelineContext.LogEntries => LogEntries;

    // Parallel execution properties
    public PipelineStep? CurrentStep => RemainingSteps.Count > 0 ? RemainingSteps[0] : null;
    public bool IsCurrentStepParallel => CurrentStep?.IsParallel ?? false;
    public IReadOnlyList<string> CurrentHandlerNames => CurrentStep?.GetHandlerNames() ?? Array.Empty<string>();
    public int CurrentStepIndex => CompletedSteps.Count;
    public int TotalSteps => Steps.Count;
    public float Progress => TotalSteps > 0 ? (float)CurrentStepIndex / TotalSteps : 0f;
    public bool IsIsolated => _isIsolated;

    // ========================================
    // Ingestion-Specific Properties
    // ========================================

    /// <summary>
    /// Document ID being ingested.
    /// Persists throughout pipeline and used for citations.
    /// </summary>
    public required string DocumentId { get; init; }

    /// <summary>
    /// Files being processed in this pipeline.
    /// Tracks source documents and all generated artifacts.
    /// </summary>
    public List<DocumentFile> Files { get; init; } = new();

    /// <summary>
    /// Files to upload before starting pipeline execution.
    /// </summary>
    public List<FileToUpload> FilesToUpload { get; init; } = new();

    /// <summary>
    /// Whether all files have been uploaded.
    /// </summary>
    public bool UploadComplete { get; set; }

    /// <summary>
    /// Previous pipeline executions to purge (for document updates/consolidation).
    /// Inspired by Kernel Memory's PreviousExecutionsToPurge pattern.
    /// </summary>
    public List<string> PreviousExecutionsToPurge { get; init; } = new();

    // ========================================
    // IPipelineContext Methods
    // ========================================

    public void Log(string source, string message, LogLevel level = LogLevel.Information)
    {
        LogEntries.Add(new PipelineLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Source = source,
            Message = message,
            Level = level
        });
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public T? GetData<T>(string key) where T : class
    {
        return Data.TryGetValue(key, out var value) ? value as T : null;
    }

    public void SetData<T>(string key, T value) where T : class
    {
        Data[key] = value;
    }

    public bool HasData(string key)
    {
        return Data.ContainsKey(key);
    }

    public bool AlreadyProcessedBy(string handlerName, string? subStep = null)
    {
        var key = string.IsNullOrWhiteSpace(subStep)
            ? handlerName
            : $"{handlerName}/{subStep}";
        return _processedHandlers.Contains(key);
    }

    public void MarkProcessedBy(string handlerName, string? subStep = null)
    {
        var key = string.IsNullOrWhiteSpace(subStep)
            ? handlerName
            : $"{handlerName}/{subStep}";
        _processedHandlers.Add(key);
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<string> GetProcessedHandlers()
    {
        return _processedHandlers.ToList();
    }

    public void MoveToNextStep()
    {
        if (RemainingSteps.Count > 0)
        {
            var currentStep = RemainingSteps[0];
            CompletedSteps.Add(currentStep);
            RemainingSteps.RemoveAt(0);
            _completedHandlersInCurrentStep.Clear(); // Reset for next step
            LastUpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void MarkHandlerComplete(string handlerName)
    {
        _completedHandlersInCurrentStep.Add(handlerName);
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool IsHandlerComplete(string handlerName)
    {
        return _completedHandlersInCurrentStep.Contains(handlerName);
    }

    public IReadOnlyList<string> GetCompletedHandlersInCurrentStep()
    {
        return _completedHandlersInCurrentStep.ToList();
    }

    public IPipelineContext CreateIsolatedCopy()
    {
        var isolated = new DocumentIngestionContext
        {
            PipelineId = PipelineId,
            ExecutionId = ExecutionId,
            Index = Index,
            DocumentId = DocumentId,
            Services = Services,
            Steps = new List<PipelineStep>(Steps),
            RemainingSteps = new List<PipelineStep>(RemainingSteps),
            Tags = new Dictionary<string, List<string>>(Tags.ToDictionary(
                kvp => kvp.Key,
                kvp => new List<string>(kvp.Value))),
            Files = new List<DocumentFile>(Files),
            FilesToUpload = new List<FileToUpload>(FilesToUpload),
            PreviousExecutionsToPurge = new List<string>(PreviousExecutionsToPurge),
            UploadComplete = UploadComplete,
            _isIsolated = true
        };

        // Copy completed steps
        foreach (var step in CompletedSteps)
        {
            isolated.CompletedSteps.Add(step);
        }

        // Copy data dictionary (shallow copy of values)
        foreach (var kvp in Data)
        {
            isolated.Data[kvp.Key] = kvp.Value;
        }

        // Copy log entries
        foreach (var entry in LogEntries)
        {
            isolated.LogEntries.Add(entry);
        }

        return isolated;
    }

    public void MergeFrom(IPipelineContext isolatedContext)
    {
        if (isolatedContext is not DocumentIngestionContext isolated)
        {
            throw new ArgumentException(
                $"Cannot merge from context of type {isolatedContext.GetType().Name}. " +
                $"Expected {nameof(DocumentIngestionContext)}.");
        }

        if (!isolated.IsIsolated)
        {
            throw new ArgumentException("Can only merge from isolated contexts");
        }

        // Merge data dictionary (union merge - isolated wins on conflicts)
        foreach (var kvp in isolated.Data)
        {
            Data[kvp.Key] = kvp.Value;
        }

        // Merge tags (union merge)
        foreach (var kvp in isolated.Tags)
        {
            if (!Tags.ContainsKey(kvp.Key))
            {
                Tags[kvp.Key] = new List<string>();
            }
            foreach (var value in kvp.Value)
            {
                if (!Tags[kvp.Key].Contains(value))
                {
                    Tags[kvp.Key].Add(value);
                }
            }
        }

        // Merge log entries (append)
        foreach (var entry in isolated.LogEntries)
        {
            if (!LogEntries.Contains(entry))
            {
                LogEntries.Add(entry);
            }
        }

        // Merge files (union by ID)
        foreach (var file in isolated.Files)
        {
            if (!Files.Any(f => f.Id == file.Id))
            {
                Files.Add(file);
            }
        }

        // Merge processed handlers (union)
        foreach (var handler in isolated.GetProcessedHandlers())
        {
            _processedHandlers.Add(handler);
        }

        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    // ========================================
    // Ingestion-Specific Methods
    // ========================================

    /// <summary>
    /// Get a file by ID.
    /// </summary>
    public DocumentFile? GetFile(string fileId)
    {
        return Files.FirstOrDefault(f => f.Id == fileId);
    }

    /// <summary>
    /// Add a file to the pipeline.
    /// </summary>
    public void AddFile(DocumentFile file)
    {
        Files.Add(file);
    }

    /// <summary>
    /// Get all files of a specific artifact type.
    /// </summary>
    public IEnumerable<DocumentFile> GetFilesByType(FileArtifactType artifactType)
    {
        return Files.Where(f => f.ArtifactType == artifactType);
    }

    /// <summary>
    /// Get all source documents (not generated files).
    /// </summary>
    public IEnumerable<DocumentFile> GetSourceDocuments()
    {
        return Files.Where(f => f.ArtifactType == FileArtifactType.SourceDocument);
    }
}

/// <summary>
/// Represents a file to be uploaded before pipeline execution.
/// </summary>
public record FileToUpload
{
    public required string FileName { get; init; }
    public required Stream Content { get; init; }
    public string? MimeType { get; init; }
}
