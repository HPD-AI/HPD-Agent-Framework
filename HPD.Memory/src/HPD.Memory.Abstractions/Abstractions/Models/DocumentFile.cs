// Copyright (c) Einstein Essibu. All rights reserved.
// Inspired by Microsoft Kernel Memory's FileDetails pattern.

using HPDAgent.Memory.Abstractions.Pipeline;

namespace HPDAgent.Memory.Abstractions.Models;

/// <summary>
/// Represents a file in the ingestion pipeline.
/// Tracks file metadata, processing state, and generated artifacts.
/// Inspired by Kernel Memory's FileDetails but simplified and more flexible.
/// </summary>
public class DocumentFile
{
    /// <summary>
    /// Unique identifier for this file.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// File name (not full path).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// MIME type (e.g., "application/pdf", "text/plain").
    /// </summary>
    public string MimeType { get; set; } = string.Empty;

    /// <summary>
    /// Type of artifact this file represents.
    /// </summary>
    public FileArtifactType ArtifactType { get; set; } = FileArtifactType.SourceDocument;

    /// <summary>
    /// If this is a partition/chunk, which number in the sequence (0-based).
    /// </summary>
    public int PartitionNumber { get; set; }

    /// <summary>
    /// If this is a partition, which section of the source document (page number, etc.).
    /// </summary>
    public int SectionNumber { get; set; }

    /// <summary>
    /// Metadata tags for this file.
    /// </summary>
    public Dictionary<string, List<string>> Tags { get; set; } = new();

    /// <summary>
    /// List of handler names that have processed this file.
    /// Used for idempotency tracking.
    /// Format: "handler_name" or "handler_name/substep"
    /// </summary>
    public List<string> ProcessedBy { get; set; } = new();

    /// <summary>
    /// Log entries specific to this file's processing.
    /// </summary>
    public List<FileLogEntry> LogEntries { get; set; } = new();

    /// <summary>
    /// Files generated from this file (e.g., extracted text, partitions, embeddings).
    /// Key is the generated file name, value is the file details.
    /// </summary>
    public Dictionary<string, GeneratedFile> GeneratedFiles { get; set; } = new();

    /// <summary>
    /// Check if this file has been processed by a specific handler.
    /// </summary>
    public bool AlreadyProcessedBy(string handlerName, string? subStep = null)
    {
        var key = string.IsNullOrWhiteSpace(subStep)
            ? handlerName
            : $"{handlerName}/{subStep}";
        return ProcessedBy.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mark this file as processed by a handler.
    /// </summary>
    public void MarkProcessedBy(string handlerName, string? subStep = null)
    {
        var key = string.IsNullOrWhiteSpace(subStep)
            ? handlerName
            : $"{handlerName}/{subStep}";

        if (!ProcessedBy.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            ProcessedBy.Add(key);
        }
    }

    /// <summary>
    /// Add a log entry for this file.
    /// </summary>
    public void Log(string source, string message, LogLevel level = LogLevel.Information)
    {
        LogEntries.Add(new FileLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Source = source,
            Message = message,
            Level = level
        });
    }

    /// <summary>
    /// Get standard partition file name.
    /// </summary>
    public string GetPartitionFileName(int partitionNumber)
        => $"{Name}.partition.{partitionNumber}.txt";

    /// <summary>
    /// Get standard handler output file name.
    /// </summary>
    public string GetHandlerOutputFileName(string handlerName, int index = 0)
        => $"{Name}.{handlerName}.{index}.txt";
}

/// <summary>
/// Represents a file generated during pipeline processing.
/// Links back to the parent file that generated it.
/// </summary>
public class GeneratedFile : DocumentFile
{
    /// <summary>
    /// ID of the parent file that generated this file.
    /// </summary>
    public required string ParentId { get; init; }

    /// <summary>
    /// If generated from a partition, the partition's ID.
    /// </summary>
    public string? SourcePartitionId { get; set; }

    /// <summary>
    /// SHA256 hash of content for deduplication.
    /// </summary>
    public string? ContentSHA256 { get; set; }
}

/// <summary>
/// Type of file artifact in the pipeline.
/// Inspired by Kernel Memory's ArtifactTypes.
/// </summary>
public enum FileArtifactType
{
    /// <summary>
    /// Original uploaded document.
    /// </summary>
    SourceDocument,

    /// <summary>
    /// Extracted plain text from source.
    /// </summary>
    ExtractedText,

    /// <summary>
    /// Structured content (pages, sections).
    /// </summary>
    ExtractedContent,

    /// <summary>
    /// Text partition/chunk.
    /// </summary>
    TextPartition,

    /// <summary>
    /// Embedding vector.
    /// </summary>
    EmbeddingVector,

    /// <summary>
    /// Synthetic data (summaries, etc.).
    /// </summary>
    SyntheticData,

    /// <summary>
    /// Metadata or index.
    /// </summary>
    Metadata
}

/// <summary>
/// Log entry for file processing.
/// </summary>
public record FileLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }
    public LogLevel Level { get; init; } = LogLevel.Information;
}
