// Copyright (c) Einstein Essibu. All rights reserved.
// Result contracts for IMemoryClient generation operations

namespace HPDAgent.Memory.Abstractions.Client;

/// <summary>
/// Result of a RAG generation operation (question → answer + citations).
/// </summary>
public interface IGenerationResult
{
    /// <summary>
    /// Original question that was asked.
    /// </summary>
    string Question { get; }

    /// <summary>
    /// Generated answer.
    /// </summary>
    string Answer { get; }

    /// <summary>
    /// Citations/sources used to generate the answer.
    /// Allows user to verify answer accuracy and explore source documents.
    /// </summary>
    IReadOnlyList<ICitation> Citations { get; }

    /// <summary>
    /// Implementation-specific metadata about the generation.
    /// Examples:
    /// - "retrieval_count": int (number of items retrieved)
    /// - "model": string (LLM model used, e.g., "gpt-4")
    /// - "total_tokens": int
    /// - "prompt_tokens": int
    /// - "completion_tokens": int
    /// - "generation_duration_ms": int
    /// - "query_rewritten_to": string (if query was rewritten)
    /// - "iterations": int (for agentic RAG)
    /// - "confidence": double (0.0 to 1.0, answer confidence score)
    /// </summary>
    IReadOnlyDictionary<string, object> Metadata { get; }
}

/// <summary>
/// A citation/source for a generated answer.
/// </summary>
public interface ICitation
{
    /// <summary>
    /// Content excerpt from the source.
    /// The specific text that supports the answer.
    /// </summary>
    string Content { get; }

    /// <summary>
    /// Source name (file name, URL, document title, etc.).
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Document ID in the memory system.
    /// </summary>
    string? DocumentId { get; }

    /// <summary>
    /// Relevance score (0.0 to 1.0).
    /// How relevant is this citation to the answer?
    /// </summary>
    double Relevance { get; }

    /// <summary>
    /// Additional metadata about the citation.
    /// Examples:
    /// - "page_number": int
    /// - "section": string
    /// - "url": string
    /// - "created_at": DateTimeOffset
    /// - "author": string
    /// </summary>
    IReadOnlyDictionary<string, object>? Metadata { get; }
}

/// <summary>
/// A chunk of streaming generation output.
/// </summary>
public interface IGenerationChunk
{
    /// <summary>
    /// Type of chunk.
    /// </summary>
    GenerationChunkType ChunkType { get; }

    /// <summary>
    /// Text content (for Text chunks).
    /// </summary>
    string? Text { get; }

    /// <summary>
    /// Citation (for Citation chunks).
    /// </summary>
    ICitation? Citation { get; }

    /// <summary>
    /// Metadata (for Metadata chunks or additional info).
    /// </summary>
    IReadOnlyDictionary<string, object>? Metadata { get; }
}

/// <summary>
/// Type of generation chunk in a stream.
/// </summary>
public enum GenerationChunkType
{
    /// <summary>
    /// Text chunk (partial answer).
    /// </summary>
    Text,

    /// <summary>
    /// Citation/source reference.
    /// </summary>
    Citation,

    /// <summary>
    /// Metadata about the generation.
    /// </summary>
    Metadata,

    /// <summary>
    /// End of stream marker.
    /// </summary>
    End
}

/// <summary>
/// Default implementation of IGenerationResult.
/// </summary>
public record GenerationResult : IGenerationResult
{
    public required string Question { get; init; }
    public required string Answer { get; init; }
    public IReadOnlyList<ICitation> Citations { get; init; } = Array.Empty<ICitation>();
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();

    /// <summary>
    /// Create a successful generation result.
    /// </summary>
    public static GenerationResult Create(
        string question,
        string answer,
        IReadOnlyList<ICitation>? citations = null,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        return new GenerationResult
        {
            Question = question,
            Answer = answer,
            Citations = citations ?? Array.Empty<ICitation>(),
            Metadata = metadata ?? new Dictionary<string, object>()
        };
    }
}

/// <summary>
/// Default implementation of ICitation.
/// </summary>
public record Citation : ICitation
{
    public required string Content { get; init; }
    public required string SourceName { get; init; }
    public string? DocumentId { get; init; }
    public required double Relevance { get; init; }
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Create a citation from a retrieved item.
    /// </summary>
    public static Citation FromRetrievedItem(IRetrievedItem item)
    {
        var sourceName = item.Metadata.TryGetValue("document_name", out var name)
            ? name.ToString() ?? "unknown"
            : item.Metadata.TryGetValue("document_id", out var id)
                ? id.ToString() ?? "unknown"
                : "unknown";

        var documentId = item.Metadata.TryGetValue("document_id", out var docId)
            ? docId.ToString()
            : null;

        return new Citation
        {
            Content = item.Content,
            SourceName = sourceName,
            DocumentId = documentId,
            Relevance = item.Score,
            Metadata = item.Metadata
        };
    }
}

/// <summary>
/// Default implementation of IGenerationChunk.
/// </summary>
public record GenerationChunk : IGenerationChunk
{
    public required GenerationChunkType ChunkType { get; init; }
    public string? Text { get; init; }
    public ICitation? Citation { get; init; }
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Create a text chunk.
    /// </summary>
    public static GenerationChunk CreateText(string text)
    {
        return new GenerationChunk
        {
            ChunkType = GenerationChunkType.Text,
            Text = text
        };
    }

    /// <summary>
    /// Create a citation chunk.
    /// </summary>
    public static GenerationChunk CreateCitation(ICitation citation)
    {
        return new GenerationChunk
        {
            ChunkType = GenerationChunkType.Citation,
            Citation = citation
        };
    }

    /// <summary>
    /// Create an end-of-stream chunk.
    /// </summary>
    public static GenerationChunk CreateEnd(IReadOnlyDictionary<string, object>? metadata = null)
    {
        return new GenerationChunk
        {
            ChunkType = GenerationChunkType.End,
            Metadata = metadata
        };
    }
}
