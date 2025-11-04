// Copyright (c) Einstein Essibu. All rights reserved.
// Concrete retrieval context for semantic search pipelines.

using HPDAgent.Memory.Abstractions.Models;
using HPDAgent.Memory.Abstractions.Pipeline;

namespace HPDAgent.Memory.Core.Contexts;

/// <summary>
/// Concrete implementation of retrieval context for semantic search pipelines.
/// Demonstrates that the same pipeline abstraction works for retrieval as well as ingestion.
/// This is what Kernel Memory couldn't do - they only supported ingestion pipelines.
/// </summary>
public class SemanticSearchContext : IRetrievalContext
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

    // Idempotency tracking
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
    // Retrieval-Specific Properties
    // ========================================

    /// <summary>
    /// Original user query.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Rewritten/expanded queries (from query rewriting handler).
    /// </summary>
    public List<string> RewrittenQueries { get; init; } = new();

    /// <summary>
    /// Search results accumulated through the pipeline.
    /// Different handlers (vector search, graph search, keyword search) add results here.
    /// </summary>
    public List<SearchResult> Results { get; init; } = new();

    /// <summary>
    /// Filters to apply during search (access control, metadata, etc.).
    /// Use MemoryFilters factory for fluent creation:
    /// filter = MemoryFilters.ByTag("user", "alice").ByDocument("doc-123")
    /// </summary>
    public MemoryFilter? Filter { get; init; }

    /// <summary>
    /// Minimum relevance score threshold (0.0 - 1.0).
    /// Results below this score will be filtered out.
    /// </summary>
    public double MinRelevance { get; init; } = 0.0;

    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int MaxResults { get; init; } = 10;

    /// <summary>
    /// Query embedding vector (computed by embedding handler).
    /// </summary>
    public float[]? QueryEmbedding { get; set; }

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
        var isolated = new SemanticSearchContext
        {
            PipelineId = PipelineId,
            ExecutionId = ExecutionId,
            Index = Index,
            Query = Query,
            Services = Services,
            Steps = new List<PipelineStep>(Steps),
            RemainingSteps = new List<PipelineStep>(RemainingSteps),
            Tags = new Dictionary<string, List<string>>(Tags.ToDictionary(
                kvp => kvp.Key,
                kvp => new List<string>(kvp.Value))),
            Filter = Filter,
            MinRelevance = MinRelevance,
            MaxResults = MaxResults,
            QueryEmbedding = QueryEmbedding,
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

        // Copy results
        isolated.Results.AddRange(Results);

        // Copy rewritten queries
        isolated.RewrittenQueries.AddRange(RewrittenQueries);

        return isolated;
    }

    public void MergeFrom(IPipelineContext isolatedContext)
    {
        if (isolatedContext is not SemanticSearchContext isolated)
        {
            throw new ArgumentException(
                $"Cannot merge from context of type {isolatedContext.GetType().Name}. " +
                $"Expected {nameof(SemanticSearchContext)}.");
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

        // Merge results (union by ID)
        foreach (var result in isolated.Results)
        {
            if (!Results.Any(r => r.Id == result.Id))
            {
                Results.Add(result);
            }
        }

        // Merge rewritten queries (union)
        foreach (var query in isolated.RewrittenQueries)
        {
            if (!RewrittenQueries.Contains(query))
            {
                RewrittenQueries.Add(query);
            }
        }

        // Update query embedding if set
        if (isolated.QueryEmbedding != null)
        {
            QueryEmbedding = isolated.QueryEmbedding;
        }

        // Merge processed handlers (union)
        foreach (var handler in isolated.GetProcessedHandlers())
        {
            _processedHandlers.Add(handler);
        }

        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    // ========================================
    // Retrieval-Specific Methods
    // ========================================

    /// <summary>
    /// Add search results from a handler.
    /// </summary>
    public void AddResults(IEnumerable<SearchResult> results)
    {
        Results.AddRange(results);
    }

    /// <summary>
    /// Get top N results by score.
    /// </summary>
    public IEnumerable<SearchResult> GetTopResults(int count)
    {
        return Results
            .OrderByDescending(r => r.Score)
            .Take(count);
    }

    /// <summary>
    /// Filter results by minimum score.
    /// </summary>
    public IEnumerable<SearchResult> GetResultsAboveScore(float minScore)
    {
        return Results.Where(r => r.Score >= minScore);
    }
}

/// <summary>
/// Represents a search result from retrieval pipeline.
/// </summary>
public record SearchResult
{
    /// <summary>
    /// Unique identifier for the result.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Document ID this result came from.
    /// </summary>
    public required string DocumentId { get; init; }

    /// <summary>
    /// Relevance score (0.0 - 1.0).
    /// </summary>
    public required float Score { get; init; }

    /// <summary>
    /// Text content of the result.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Metadata tags.
    /// </summary>
    public Dictionary<string, List<string>> Tags { get; init; } = new();

    /// <summary>
    /// Source of this result (e.g., "vector_search", "graph_search", "keyword_search").
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}
