// Copyright (c) Einstein Essibu. All rights reserved.
// Example handler to test source generator functionality.

using HPDAgent.Memory.Abstractions.Attributes;
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Core.Contexts;
using Microsoft.Extensions.Logging;

namespace HPDAgent.Memory.Core.Handlers;

/// <summary>
/// Example text extraction handler for testing source generation.
/// This demonstrates the [PipelineHandler] attribute usage.
/// </summary>
[PipelineHandler(StepName = "extract-text")]
public class ExampleTextExtractionHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly ILogger<ExampleTextExtractionHandler> _logger;

    public ExampleTextExtractionHandler(ILogger<ExampleTextExtractionHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string StepName => "extract-text";

    public Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Extracting text from document {DocumentId} in pipeline {PipelineId}",
            context.DocumentId, context.PipelineId);

        // This is just an example - real implementation would extract text from files
        _logger.LogInformation("Text extraction completed (example handler)");

        return Task.FromResult(PipelineResult.Success());
    }
}

/// <summary>
/// Another example handler for the same context (tests multiple handlers per context).
/// </summary>
[PipelineHandler(StepName = "chunking")]
public class ExampleChunkingHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly ILogger<ExampleChunkingHandler> _logger;

    public ExampleChunkingHandler(ILogger<ExampleChunkingHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string StepName => "chunking";

    public Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Chunking document {DocumentId} in pipeline {PipelineId}",
            context.DocumentId, context.PipelineId);

        // This is just an example - real implementation would chunk the text
        _logger.LogInformation("Chunking completed (example handler)");

        return Task.FromResult(PipelineResult.Success());
    }
}

/// <summary>
/// Example handler for semantic search context (tests multiple context types).
/// </summary>
[PipelineHandler(StepName = "vector-search")]
public class ExampleVectorSearchHandler : IPipelineHandler<SemanticSearchContext>
{
    private readonly ILogger<ExampleVectorSearchHandler> _logger;

    public ExampleVectorSearchHandler(ILogger<ExampleVectorSearchHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string StepName => "vector-search";

    public Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Performing vector search with query: {Query}",
            context.Query);

        // This is just an example - real implementation would perform vector search
        _logger.LogInformation("Vector search completed (example handler)");

        return Task.FromResult(PipelineResult.Success());
    }
}
