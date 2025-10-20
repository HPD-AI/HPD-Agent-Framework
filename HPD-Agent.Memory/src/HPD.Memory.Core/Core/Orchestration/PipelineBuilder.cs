// Copyright (c) Einstein Essibu. All rights reserved.
// Fluent builder for creating pipelines with templates and defaults.
// Inspired by Kernel Memory's default steps pattern but more flexible.

using HPDAgent.Memory.Abstractions.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace HPDAgent.Memory.Core.Orchestration;

/// <summary>
/// Fluent builder for creating and configuring pipelines.
/// Provides template-based pipeline creation and configuration.
/// Inspired by Kernel Memory's default steps but more flexible.
/// </summary>
/// <typeparam name="TContext">Pipeline context type</typeparam>
public class PipelineBuilder<TContext> where TContext : IPipelineContext, new()
{
    private readonly List<PipelineStep> _steps = new();
    private readonly Dictionary<string, object> _configuration = new();
    private readonly Dictionary<string, List<string>> _tags = new();
    private string? _index;
    private IServiceProvider? _serviceProvider;

    /// <summary>
    /// Set the index/collection name for the pipeline.
    /// </summary>
    public PipelineBuilder<TContext> WithIndex(string index)
    {
        _index = index;
        return this;
    }

    /// <summary>
    /// Set the service provider for the pipeline context.
    /// </summary>
    public PipelineBuilder<TContext> WithServices(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        return this;
    }

    /// <summary>
    /// Add a sequential pipeline step.
    /// </summary>
    public PipelineBuilder<TContext> AddStep(string handlerName)
    {
        if (!string.IsNullOrWhiteSpace(handlerName))
        {
            _steps.Add(new SequentialStep { HandlerName = handlerName });
        }
        return this;
    }

    /// <summary>
    /// Add a pipeline step (sequential or parallel).
    /// </summary>
    public PipelineBuilder<TContext> AddStep(PipelineStep step)
    {
        _steps.Add(step);
        return this;
    }

    /// <summary>
    /// Add multiple sequential pipeline steps.
    /// </summary>
    public PipelineBuilder<TContext> AddSteps(params string[] handlerNames)
    {
        foreach (var handlerName in handlerNames)
        {
            AddStep(handlerName);
        }
        return this;
    }

    /// <summary>
    /// Add multiple pipeline steps (sequential or parallel).
    /// </summary>
    public PipelineBuilder<TContext> AddSteps(params PipelineStep[] steps)
    {
        foreach (var step in steps)
        {
            AddStep(step);
        }
        return this;
    }

    /// <summary>
    /// Add a parallel step with multiple handlers.
    /// </summary>
    public PipelineBuilder<TContext> AddParallelStep(params string[] handlerNames)
    {
        if (handlerNames.Length > 0)
        {
            _steps.Add(new ParallelStep { HandlerNames = handlerNames });
        }
        return this;
    }

    /// <summary>
    /// Add a tag to the pipeline.
    /// </summary>
    public PipelineBuilder<TContext> WithTag(string key, string value)
    {
        if (!_tags.ContainsKey(key))
        {
            _tags[key] = new List<string>();
        }
        _tags[key].Add(value);
        return this;
    }

    /// <summary>
    /// Add multiple tags to the pipeline.
    /// </summary>
    public PipelineBuilder<TContext> WithTags(Dictionary<string, List<string>> tags)
    {
        foreach (var kvp in tags)
        {
            foreach (var value in kvp.Value)
            {
                WithTag(kvp.Key, value);
            }
        }
        return this;
    }

    /// <summary>
    /// Set configuration value.
    /// </summary>
    public PipelineBuilder<TContext> WithConfiguration(string key, object value)
    {
        _configuration[key] = value;
        return this;
    }

    /// <summary>
    /// Set max tokens per chunk.
    /// </summary>
    public PipelineBuilder<TContext> WithMaxTokensPerChunk(int maxTokens)
    {
        _configuration["max_tokens_per_chunk"] = maxTokens;
        return this;
    }

    /// <summary>
    /// Set overlap tokens between chunks.
    /// </summary>
    public PipelineBuilder<TContext> WithOverlapTokens(int overlapTokens)
    {
        _configuration["overlap_tokens"] = overlapTokens;
        return this;
    }

    /// <summary>
    /// Set batch size for operations.
    /// </summary>
    public PipelineBuilder<TContext> WithBatchSize(int batchSize)
    {
        _configuration["batch_size"] = batchSize;
        return this;
    }

    /// <summary>
    /// Set maximum results for retrieval pipelines.
    /// </summary>
    public PipelineBuilder<TContext> WithMaxResults(int maxResults)
    {
        _configuration["max_results"] = maxResults;
        return this;
    }

    /// <summary>
    /// Set minimum similarity score for retrieval pipelines.
    /// </summary>
    public PipelineBuilder<TContext> WithMinScore(float minScore)
    {
        _configuration["min_score"] = minScore;
        return this;
    }

    /// <summary>
    /// Build the context (without creating orchestrator).
    /// Useful for testing or custom orchestration.
    /// Note: TContext must have a parameterless constructor and settable properties.
    /// For complex initialization, create contexts directly instead of using builder.
    /// </summary>
    public TContext BuildContext()
    {
        throw new NotSupportedException(
            "BuildContext() with generic TContext is not supported due to init-only properties. " +
            "Instead, create your context directly with object initializer syntax. " +
            "See PipelineTemplates for examples.");
    }
}

/// <summary>
/// Pipeline templates for common scenarios.
/// Inspired by Kernel Memory's default steps but more flexible and extensible.
/// </summary>
public static class PipelineTemplates
{
    /// <summary>
    /// Get standard document ingestion steps.
    /// Steps: extract → partition → generate_embeddings → save_records
    /// </summary>
    public static PipelineStep[] DocumentIngestionSteps => new PipelineStep[]
    {
        new SequentialStep { HandlerName = "extract_text" },
        new SequentialStep { HandlerName = "partition_text" },
        new SequentialStep { HandlerName = "generate_embeddings" },
        new SequentialStep { HandlerName = "save_records" }
    };

    /// <summary>
    /// Document ingestion with entity extraction and graph building steps.
    /// </summary>
    public static PipelineStep[] DocumentIngestionWithGraphSteps => new PipelineStep[]
    {
        new SequentialStep { HandlerName = "extract_text" },
        new SequentialStep { HandlerName = "partition_text" },
        new SequentialStep { HandlerName = "extract_entities" },
        new SequentialStep { HandlerName = "generate_embeddings" },
        new SequentialStep { HandlerName = "save_records" },
        new SequentialStep { HandlerName = "build_graph" }
    };

    /// <summary>
    /// Basic semantic search steps.
    /// </summary>
    public static PipelineStep[] SemanticSearchSteps => new PipelineStep[]
    {
        new SequentialStep { HandlerName = "generate_query_embedding" },
        new SequentialStep { HandlerName = "vector_search" },
        new SequentialStep { HandlerName = "rerank" }
    };

    /// <summary>
    /// Advanced hybrid search steps with parallel search execution.
    /// Vector and graph search run in parallel for better performance.
    /// </summary>
    public static PipelineStep[] HybridSearchSteps => new PipelineStep[]
    {
        new SequentialStep { HandlerName = "query_rewrite" },
        new SequentialStep { HandlerName = "generate_query_embedding" },
        new ParallelStep { HandlerNames = new[] { "vector_search", "graph_search" } },
        new SequentialStep { HandlerName = "hybrid_merge" },
        new SequentialStep { HandlerName = "rerank" },
        new SequentialStep { HandlerName = "filter_access" }
    };

    /// <summary>
    /// GraphRAG-style retrieval steps with parallel search execution.
    /// Graph traversal and vector search run in parallel.
    /// </summary>
    public static PipelineStep[] GraphRAGSteps => new PipelineStep[]
    {
        new SequentialStep { HandlerName = "extract_entities_from_query" },
        new ParallelStep { HandlerNames = new[] { "graph_traverse", "vector_search" } },
        new SequentialStep { HandlerName = "hybrid_merge" },
        new SequentialStep { HandlerName = "rerank" }
    };
}
