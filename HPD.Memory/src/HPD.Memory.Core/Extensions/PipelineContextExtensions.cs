// Copyright (c) Einstein Essibu. All rights reserved.
// Type-safe extension methods for accessing context data.
// Inspired by Kernel Memory's context argument extensions.

using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Abstractions.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace HPDAgent.Memory.Core.Extensions;

/// <summary>
/// Extension methods for type-safe access to common pipeline context data.
/// Inspired by Kernel Memory's IContext extension methods but more flexible.
/// </summary>
public static class PipelineContextExtensions
{
    // ========================================
    // Common Configuration
    // ========================================

    /// <summary>
    /// Get maximum tokens per chunk/partition, with fallback to default.
    /// </summary>
    public static int GetMaxTokensPerChunkOrDefault(this IPipelineContext context, int defaultValue = 1000)
    {
        if (context.Data.TryGetValue("max_tokens_per_chunk", out var value) && value is int intValue)
        {
            return intValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Set maximum tokens per chunk.
    /// </summary>
    public static void SetMaxTokensPerChunk(this IPipelineContext context, int maxTokens)
    {
        context.Data["max_tokens_per_chunk"] = maxTokens;
    }

    /// <summary>
    /// Get overlap tokens between chunks, with fallback to default.
    /// </summary>
    public static int GetOverlapTokensOrDefault(this IPipelineContext context, int defaultValue = 100)
    {
        if (context.Data.TryGetValue("overlap_tokens", out var value) && value is int intValue)
        {
            return intValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Set overlap tokens between chunks.
    /// </summary>
    public static void SetOverlapTokens(this IPipelineContext context, int overlapTokens)
    {
        context.Data["overlap_tokens"] = overlapTokens;
    }

    /// <summary>
    /// Get batch size for operations (embeddings, vector DB writes, etc.).
    /// </summary>
    public static int GetBatchSizeOrDefault(this IPipelineContext context, int defaultValue = 10)
    {
        if (context.Data.TryGetValue("batch_size", out var value) && value is int intValue)
        {
            return intValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Set batch size for operations.
    /// </summary>
    public static void SetBatchSize(this IPipelineContext context, int batchSize)
    {
        context.Data["batch_size"] = batchSize;
    }

    // ========================================
    // Chunking/Partitioning Configuration
    // ========================================

    /// <summary>
    /// Get custom chunk header (prepended to each chunk).
    /// </summary>
    public static string? GetChunkHeaderOrDefault(this IPipelineContext context, string? defaultValue = null)
    {
        if (context.Data.TryGetValue("chunk_header", out var value) && value is string strValue)
        {
            return strValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Set custom chunk header.
    /// </summary>
    public static void SetChunkHeader(this IPipelineContext context, string? header)
    {
        if (header != null)
        {
            context.Data["chunk_header"] = header;
        }
        else
        {
            context.Data.Remove("chunk_header");
        }
    }

    // ========================================
    // AI Provider Access (Microsoft.Extensions.AI)
    // ========================================

    /// <summary>
    /// Gets the registered IChatClient from the service provider.
    /// Provides clean, discoverable access without verbose GetRequiredService calls.
    /// </summary>
    /// <param name="context">The pipeline context</param>
    /// <returns>The registered IChatClient</returns>
    /// <exception cref="InvalidOperationException">If IChatClient is not registered</exception>
    public static IChatClient GetChatClient(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Services.GetRequiredService<IChatClient>();
    }

    /// <summary>
    /// Gets the registered IEmbeddingGenerator for string inputs and float embeddings.
    /// Provides clean, discoverable access without verbose GetRequiredService calls.
    /// </summary>
    /// <param name="context">The pipeline context</param>
    /// <returns>The registered embedding generator</returns>
    /// <exception cref="InvalidOperationException">If IEmbeddingGenerator is not registered</exception>
    public static IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
    }

    /// <summary>
    /// Tries to get the registered IChatClient from the service provider.
    /// Returns null if not registered.
    /// </summary>
    public static IChatClient? TryGetChatClient(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Services.GetService<IChatClient>();
    }

    /// <summary>
    /// Tries to get the registered IEmbeddingGenerator from the service provider.
    /// Returns null if not registered.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>>? TryGetEmbeddingGenerator(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Services.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
    }

    /// <summary>
    /// Checks if an IChatClient is registered in the service provider.
    /// </summary>
    public static bool HasChatClient(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryGetChatClient(context) is not null;
    }

    /// <summary>
    /// Checks if an IEmbeddingGenerator is registered in the service provider.
    /// </summary>
    public static bool HasEmbeddingGenerator(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryGetEmbeddingGenerator(context) is not null;
    }

    // ========================================
    // Storage Access
    // ========================================

    /// <summary>
    /// Gets the registered IDocumentStore from the service provider.
    /// Provides clean, discoverable access for file storage operations.
    /// </summary>
    /// <param name="context">The pipeline context</param>
    /// <returns>The registered document store</returns>
    /// <exception cref="InvalidOperationException">If IDocumentStore is not registered</exception>
    public static IDocumentStore GetDocumentStore(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Services.GetRequiredService<IDocumentStore>();
    }

    /// <summary>
    /// Gets the registered IGraphStore from the service provider.
    /// Provides clean, discoverable access for graph database operations.
    /// </summary>
    /// <param name="context">The pipeline context</param>
    /// <returns>The registered graph store</returns>
    /// <exception cref="InvalidOperationException">If IGraphStore is not registered</exception>
    public static IGraphStore GetGraphStore(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Services.GetRequiredService<IGraphStore>();
    }

    /// <summary>
    /// Tries to get the registered IDocumentStore from the service provider.
    /// Returns null if not registered.
    /// </summary>
    public static IDocumentStore? TryGetDocumentStore(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Services.GetService<IDocumentStore>();
    }

    /// <summary>
    /// Tries to get the registered IGraphStore from the service provider.
    /// Returns null if not registered.
    /// </summary>
    public static IGraphStore? TryGetGraphStore(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Services.GetService<IGraphStore>();
    }

    /// <summary>
    /// Checks if an IDocumentStore is registered in the service provider.
    /// </summary>
    public static bool HasDocumentStore(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryGetDocumentStore(context) is not null;
    }

    /// <summary>
    /// Checks if an IGraphStore is registered in the service provider.
    /// </summary>
    public static bool HasGraphStore(this IPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryGetGraphStore(context) is not null;
    }

    // ========================================
    // Embedding Configuration
    // ========================================

    /// <summary>
    /// Check if embedding generation should be enabled for this pipeline.
    /// </summary>
    public static bool GetEmbeddingEnabledOrDefault(this IPipelineContext context, bool defaultValue = true)
    {
        if (context.Data.TryGetValue("embedding_enabled", out var value) && value is bool boolValue)
        {
            return boolValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Set whether embedding generation is enabled.
    /// </summary>
    public static void SetEmbeddingEnabled(this IPipelineContext context, bool enabled)
    {
        context.Data["embedding_enabled"] = enabled;
    }

    /// <summary>
    /// Get embedding model name to use.
    /// </summary>
    public static string? GetEmbeddingModelOrDefault(this IPipelineContext context, string? defaultValue = null)
    {
        if (context.Data.TryGetValue("embedding_model", out var value) && value is string strValue)
        {
            return strValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Set embedding model name.
    /// </summary>
    public static void SetEmbeddingModel(this IPipelineContext context, string? model)
    {
        if (model != null)
        {
            context.Data["embedding_model"] = model;
        }
        else
        {
            context.Data.Remove("embedding_model");
        }
    }

    // ========================================
    // Retrieval Configuration
    // ========================================

    /// <summary>
    /// Get maximum number of results to return (retrieval pipelines).
    /// </summary>
    public static int GetMaxResultsOrDefault(this IPipelineContext context, int defaultValue = 10)
    {
        if (context.Data.TryGetValue("max_results", out var value) && value is int intValue)
        {
            return intValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Set maximum results.
    /// </summary>
    public static void SetMaxResults(this IPipelineContext context, int maxResults)
    {
        context.Data["max_results"] = maxResults;
    }

    /// <summary>
    /// Get minimum similarity score threshold (0.0 - 1.0).
    /// </summary>
    public static float GetMinScoreOrDefault(this IPipelineContext context, float defaultValue = 0.7f)
    {
        if (context.Data.TryGetValue("min_score", out var value) && value is float floatValue)
        {
            return floatValue;
        }
        if (value is double doubleValue)
        {
            return (float)doubleValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Set minimum similarity score.
    /// </summary>
    public static void SetMinScore(this IPipelineContext context, float minScore)
    {
        context.Data["min_score"] = minScore;
    }

    // ========================================
    // General Helpers
    // ========================================

    /// <summary>
    /// Get typed value with fallback.
    /// </summary>
    public static T GetValueOrDefault<T>(this IPipelineContext context, string key, T defaultValue)
    {
        if (context.Data.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Set typed value.
    /// </summary>
    public static void SetValue<T>(this IPipelineContext context, string key, T value)
    {
        if (value != null)
        {
            context.Data[key] = value;
        }
        else
        {
            context.Data.Remove(key);
        }
    }
}
