// Copyright (c) Einstein Essibu. All rights reserved.
// Inspired by Microsoft Kernel Memory, enhanced with modern patterns.

using HPD.Pipeline;

namespace HPDAgent.Memory.Abstractions.Pipeline;

/// <summary>
/// Marker interface for ingestion pipeline contexts.
/// Ingestion pipelines process documents/data and store them in memory systems.
/// Use this interface for type constraints when creating ingestion-specific handlers.
/// </summary>
/// <remarks>
/// This interface extends IPipelineContext but doesn't add required members.
/// Concrete implementations will add ingestion-specific properties like:
/// - Documents to process
/// - Document ID being ingested
/// - File tracking
/// - Artifact management (extracted text, embeddings, etc.)
///
/// Example:
/// <code>
/// public class DocumentIngestionContext : IIngestionContext
/// {
///     // IPipelineContext implementation...
///
///     // Ingestion-specific
///     public List&lt;Document&gt; Documents { get; set; } = new();
///     public string DocumentId { get; set; } = "";
/// }
/// </code>
/// </remarks>
public interface IIngestionContext : IPipelineContext
{
    // Marker interface - no additional required members
    // Concrete implementations add ingestion-specific properties
}
