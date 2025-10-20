// Copyright (c) Einstein Essibu. All rights reserved.

namespace HPDAgent.Memory.Abstractions.Client;

/// <summary>
/// Describes the capabilities of an IMemoryClient implementation.
/// Allows consumers to discover what features are supported at runtime.
/// </summary>
public interface IMemoryCapabilities
{
    /// <summary>
    /// Whether the implementation supports graph traversal (GraphRAG).
    /// </summary>
    bool SupportsGraphTraversal { get; }

    /// <summary>
    /// Whether the implementation supports streaming generation.
    /// </summary>
    bool SupportsStreaming { get; }

    /// <summary>
    /// Whether the implementation supports multi-modal content (images, audio, etc.).
    /// </summary>
    bool SupportsMultiModal { get; }

    /// <summary>
    /// Whether the implementation supports agentic/iterative retrieval.
    /// </summary>
    bool SupportsAgenticRetrieval { get; }

    /// <summary>
    /// Whether the implementation supports batch ingestion.
    /// </summary>
    bool SupportsBatchIngestion { get; }

    /// <summary>
    /// Whether the implementation supports metadata filtering in retrieval.
    /// </summary>
    bool SupportsMetadataFiltering { get; }

    /// <summary>
    /// Maximum number of retrieval items supported (null if unlimited).
    /// </summary>
    int? MaxRetrievalItems { get; }

    /// <summary>
    /// Maximum document size in bytes (null if unlimited).
    /// </summary>
    long? MaxDocumentSize { get; }

    /// <summary>
    /// Supported content types for ingestion (e.g., "application/pdf", "text/plain").
    /// Empty collection means all types are supported.
    /// </summary>
    IReadOnlyCollection<string> SupportedContentTypes => Array.Empty<string>();

    /// <summary>
    /// Custom capabilities dictionary for implementation-specific features.
    /// </summary>
    IReadOnlyDictionary<string, object> CustomCapabilities => new Dictionary<string, object>();
}
