# Reference Handler Examples

**Complete, working handler implementations as learning references**

⚠️ **IMPORTANT**: These are **EXAMPLES**, not production code. They show patterns and approaches, but **you should implement handlers that match YOUR specific needs**.

---

## Table of Contents

1. [Text Extraction Handler](#1-text-extraction-handler)
2. [Text Partitioning Handler](#2-text-partitioning-handler)
3. [Embedding Generation Handler](#3-embedding-generation-handler)
4. [Vector Storage Handler](#4-vector-storage-handler)
5. [Query Rewriting Handler](#5-query-rewriting-handler)
6. [Vector Search Handler](#6-vector-search-handler)
7. [Graph Search Handler](#7-graph-search-handler)
8. [Reranking Handler](#8-reranking-handler)

---

## 1. Text Extraction Handler

Extracts text from various document formats.

```csharp
using HPDAgent.Memory.Abstractions.Models;
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Abstractions.Storage;
using HPDAgent.Memory.Core.Contexts;
using Microsoft.Extensions.Logging;

/// <summary>
/// Example handler for extracting text from documents.
/// This is a REFERENCE implementation - you should customize for your needs.
///
/// Possible approaches:
/// - Use Azure Document Intelligence for production
/// - Use Apache Tika for multi-format support
/// - Use PDFSharp for PDF-only
/// - Use specialized libraries per format (docx4j, etc.)
/// </summary>
public class TextExtractionHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly IDocumentStore _documentStore;
    private readonly ILogger<TextExtractionHandler> _logger;

    public string StepName => "extract_text";

    public TextExtractionHandler(
        IDocumentStore documentStore,
        ILogger<TextExtractionHandler> logger)
    {
        _documentStore = documentStore;
        _logger = logger;
    }

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var file in context.GetSourceDocuments())
            {
                if (file.AlreadyProcessedBy(StepName))
                {
                    _logger.LogDebug("File {FileId} already extracted, skipping", file.Id);
                    continue;
                }

                _logger.LogInformation(
                    "Extracting text from {FileName} ({MimeType})",
                    file.Name,
                    file.MimeType);

                // Read source file
                var fileData = await _documentStore.ReadFileAsync(
                    context.Index,
                    context.PipelineId,
                    file.Name,
                    cancellationToken);

                // Extract text based on MIME type
                string extractedText = file.MimeType switch
                {
                    "application/pdf" => await ExtractFromPdfAsync(fileData, cancellationToken),
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document" =>
                        await ExtractFromDocxAsync(fileData, cancellationToken),
                    "text/plain" => System.Text.Encoding.UTF8.GetString(fileData),
                    "text/markdown" => System.Text.Encoding.UTF8.GetString(fileData),
                    _ => throw new NotSupportedException($"MIME type {file.MimeType} not supported")
                };

                // Create extracted text file
                var extractedFile = new GeneratedFile
                {
                    Id = $"{file.Id}_extracted",
                    Name = $"{Path.GetFileNameWithoutExtension(file.Name)}.txt",
                    ParentId = file.Id,
                    Size = extractedText.Length,
                    MimeType = "text/plain",
                    ArtifactType = FileArtifactType.ExtractedText
                };

                // Store extracted text
                await _documentStore.WriteTextFileAsync(
                    context.Index,
                    context.PipelineId,
                    extractedFile.Name,
                    extractedText,
                    cancellationToken);

                // Track lineage
                file.GeneratedFiles[extractedFile.Id] = extractedFile;
                context.AddFile(extractedFile);

                // Log and mark as processed
                file.Log(StepName, $"Extracted {extractedText.Length} characters");
                file.MarkProcessedBy(StepName);

                _logger.LogInformation(
                    "Extracted {CharCount} characters from {FileName}",
                    extractedText.Length,
                    file.Name);
            }

            return PipelineResult.Success();
        }
        catch (NotSupportedException ex)
        {
            _logger.LogError(ex, "Unsupported file format");
            return PipelineResult.FatalFailure("Unsupported file format", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Text extraction failed");
            return PipelineResult.TransientFailure("Text extraction failed", ex);
        }
    }

    private async Task<string> ExtractFromPdfAsync(byte[] data, CancellationToken cancellationToken)
    {
        // TODO: Implement using your preferred PDF library
        // Examples:
        // - UglyToad.PdfPig (open source)
        // - Azure Document Intelligence (cloud)
        // - iTextSharp (commercial)

        throw new NotImplementedException("Implement PDF extraction with your preferred library");
    }

    private async Task<string> ExtractFromDocxAsync(byte[] data, CancellationToken cancellationToken)
    {
        // TODO: Implement using your preferred DOCX library
        // Examples:
        // - DocumentFormat.OpenXml (Microsoft)
        // - NPOI (open source)

        throw new NotImplementedException("Implement DOCX extraction with your preferred library");
    }
}
```

---

## 2. Text Partitioning Handler

Splits text into chunks for embedding.

```csharp
using HPDAgent.Memory.Abstractions.Models;
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Abstractions.Storage;
using HPDAgent.Memory.Core.Contexts;
using Microsoft.Extensions.Logging;

/// <summary>
/// Example handler for partitioning text into chunks.
///
/// Possible approaches:
/// - Fixed-size chunks (simple, fast)
/// - Semantic chunking (LLM-based, better quality)
/// - Recursive chunking (hierarchical)
/// - RAPTOR (recursive abstractive processing)
/// - Sliding window with overlap
///
/// This example shows fixed-size chunking. YOU should implement what works for YOUR domain.
/// </summary>
public class TextPartitioningHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly IDocumentStore _documentStore;
    private readonly ILogger<TextPartitioningHandler> _logger;

    public string StepName => "partition_text";

    public TextPartitioningHandler(
        IDocumentStore documentStore,
        ILogger<TextPartitioningHandler> logger)
    {
        _documentStore = documentStore;
        _logger = logger;
    }

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get configuration
            var maxTokensPerChunk = context.GetMaxTokensPerChunkOrDefault(defaultValue: 512);
            var overlapTokens = context.GetOverlapTokensOrDefault(defaultValue: 50);

            foreach (var file in context.GetFilesByType(FileArtifactType.ExtractedText))
            {
                if (file.AlreadyProcessedBy(StepName))
                {
                    _logger.LogDebug("File {FileId} already partitioned, skipping", file.Id);
                    continue;
                }

                _logger.LogInformation("Partitioning {FileName}", file.Name);

                // Read extracted text
                var text = await _documentStore.ReadTextFileAsync(
                    context.Index,
                    context.PipelineId,
                    file.Name,
                    cancellationToken);

                // Partition text
                var partitions = PartitionText(
                    text,
                    maxTokensPerChunk,
                    overlapTokens);

                // Create partition files
                for (int i = 0; i < partitions.Count; i++)
                {
                    var partition = new GeneratedFile
                    {
                        Id = $"{file.Id}_partition_{i}",
                        Name = $"{Path.GetFileNameWithoutExtension(file.Name)}.partition.{i}.txt",
                        ParentId = file.Id,
                        Size = partitions[i].Length,
                        MimeType = "text/plain",
                        ArtifactType = FileArtifactType.TextPartition,
                        PartitionNumber = i
                    };

                    // Store partition
                    await _documentStore.WriteTextFileAsync(
                        context.Index,
                        context.PipelineId,
                        partition.Name,
                        partitions[i],
                        cancellationToken);

                    // Copy tags from parent
                    file.Tags.CopyTagsTo(partition.Tags);
                    partition.Tags.AddTag(TagConstants.PartitionNumber, i.ToString());

                    // Track lineage
                    file.GeneratedFiles[partition.Id] = partition;
                    context.AddFile(partition);
                }

                file.Log(StepName, $"Created {partitions.Count} partitions");
                file.MarkProcessedBy(StepName);

                _logger.LogInformation(
                    "Created {Count} partitions from {FileName}",
                    partitions.Count,
                    file.Name);
            }

            return PipelineResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Partitioning failed");
            return PipelineResult.FatalFailure("Partitioning failed", ex);
        }
    }

    private List<string> PartitionText(
        string text,
        int maxTokensPerChunk,
        int overlapTokens)
    {
        // EXAMPLE: Simple sentence-based chunking
        // YOU should implement what works for YOUR domain:
        // - LangChain-style recursive text splitter
        // - Semantic chunking using embeddings
        // - LLM-based chunking
        // - RAPTOR hierarchical chunking

        var partitions = new List<string>();
        var sentences = text.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);

        var currentChunk = new StringBuilder();
        var currentTokens = 0;

        foreach (var sentence in sentences)
        {
            var sentenceTokens = EstimateTokens(sentence);

            if (currentTokens + sentenceTokens > maxTokensPerChunk && currentChunk.Length > 0)
            {
                // Chunk is full, save it
                partitions.Add(currentChunk.ToString().Trim());

                // Start new chunk with overlap
                currentChunk.Clear();
                currentTokens = 0;

                // Add overlap from previous chunk
                if (partitions.Count > 0 && overlapTokens > 0)
                {
                    var previousChunk = partitions[^1];
                    var overlapText = GetLastNTokens(previousChunk, overlapTokens);
                    currentChunk.Append(overlapText);
                    currentTokens = EstimateTokens(overlapText);
                }
            }

            currentChunk.Append(sentence).Append(". ");
            currentTokens += sentenceTokens;
        }

        // Add final chunk
        if (currentChunk.Length > 0)
        {
            partitions.Add(currentChunk.ToString().Trim());
        }

        return partitions;
    }

    private int EstimateTokens(string text)
    {
        // Rough estimate: 1 token ≈ 4 characters
        // For production, use actual tokenizer (tiktoken, etc.)
        return text.Length / 4;
    }

    private string GetLastNTokens(string text, int tokenCount)
    {
        var estimatedChars = tokenCount * 4;
        if (text.Length <= estimatedChars)
            return text;

        return text.Substring(text.Length - estimatedChars);
    }
}
```

---

## 3. Embedding Generation Handler

Generates embeddings using AI provider.

```csharp
using HPDAgent.Memory.Abstractions.Models;
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Abstractions.Storage;
using HPDAgent.Memory.Core.Contexts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// Example handler for generating embeddings.
/// Uses Microsoft.Extensions.AI abstractions.
/// </summary>
public class EmbeddingGenerationHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IDocumentStore _documentStore;
    private readonly ILogger<EmbeddingGenerationHandler> _logger;

    public string StepName => "generate_embeddings";

    public EmbeddingGenerationHandler(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IDocumentStore documentStore,
        ILogger<EmbeddingGenerationHandler> logger)
    {
        _embedder = embedder;
        _documentStore = documentStore;
        _logger = logger;
    }

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var partitions = context.GetFilesByType(FileArtifactType.TextPartition).ToList();

            // Get batch size from configuration
            var batchSize = context.GetBatchSizeOrDefault(defaultValue: 10);

            for (int i = 0; i < partitions.Count; i += batchSize)
            {
                var batch = partitions.Skip(i).Take(batchSize).ToList();
                var batchTexts = new List<string>();
                var batchFiles = new List<DocumentFile>();

                // Collect texts for batch
                foreach (var partition in batch)
                {
                    if (partition.AlreadyProcessedBy(StepName))
                        continue;

                    var text = await _documentStore.ReadTextFileAsync(
                        context.Index,
                        context.PipelineId,
                        partition.Name,
                        cancellationToken);

                    batchTexts.Add(text);
                    batchFiles.Add(partition);
                }

                if (batchTexts.Count == 0)
                    continue;

                _logger.LogInformation(
                    "Generating embeddings for batch of {Count} partitions",
                    batchTexts.Count);

                // Generate embeddings for batch
                var embeddings = await _embedder.GenerateAsync(
                    batchTexts,
                    cancellationToken: cancellationToken);

                // Store embeddings
                for (int j = 0; j < embeddings.Count; j++)
                {
                    var partition = batchFiles[j];
                    var embedding = embeddings[j];

                    var embeddingFile = new GeneratedFile
                    {
                        Id = $"{partition.Id}_embedding",
                        Name = $"{Path.GetFileNameWithoutExtension(partition.Name)}.embedding.json",
                        ParentId = partition.Id,
                        ArtifactType = FileArtifactType.EmbeddingVector,
                        Size = embedding.Vector.Length * sizeof(float)
                    };

                    // Store embedding data (you might store this in vector DB instead)
                    var embeddingData = new
                    {
                        vector = embedding.Vector.ToArray(),
                        model = embedding.ModelId,
                        created_at = DateTimeOffset.UtcNow
                    };

                    await _documentStore.WriteTextFileAsync(
                        context.Index,
                        context.PipelineId,
                        embeddingFile.Name,
                        System.Text.Json.JsonSerializer.Serialize(embeddingData),
                        cancellationToken);

                    partition.GeneratedFiles[embeddingFile.Id] = embeddingFile;
                    context.AddFile(embeddingFile);

                    partition.MarkProcessedBy(StepName);

                    _logger.LogDebug(
                        "Generated {Dimensions}-dimensional embedding for partition {PartitionId}",
                        embedding.Vector.Length,
                        partition.Id);
                }
            }

            return PipelineResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding generation failed");
            return PipelineResult.TransientFailure("Embedding generation failed", ex);
        }
    }
}
```

---

## 4. Vector Storage Handler

Stores embeddings in vector database.

```csharp
using HPDAgent.Memory.Abstractions.Models;
using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Abstractions.Storage;
using HPDAgent.Memory.Core.Contexts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;

/// <summary>
/// Example handler for storing vectors in a vector database.
/// Uses Microsoft.Extensions.VectorData abstractions.
/// </summary>
public class VectorStorageHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly IVectorStore _vectorStore;
    private readonly IDocumentStore _documentStore;
    private readonly ILogger<VectorStorageHandler> _logger;

    public string StepName => "store_vectors";

    public VectorStorageHandler(
        IVectorStore vectorStore,
        IDocumentStore documentStore,
        ILogger<VectorStorageHandler> logger)
    {
        _vectorStore = vectorStore;
        _documentStore = documentStore;
        _logger = logger;
    }

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var collection = _vectorStore.GetCollection<string, VectorRecord>(context.Index);

            // Ensure collection exists
            await collection.CreateCollectionIfNotExistsAsync(cancellationToken);

            var embeddingFiles = context.GetFilesByType(FileArtifactType.EmbeddingVector);

            foreach (var embeddingFile in embeddingFiles)
            {
                if (embeddingFile.AlreadyProcessedBy(StepName))
                {
                    _logger.LogDebug("Embedding {FileId} already stored, skipping", embeddingFile.Id);
                    continue;
                }

                // Read embedding data
                var embeddingJson = await _documentStore.ReadTextFileAsync(
                    context.Index,
                    context.PipelineId,
                    embeddingFile.Name,
                    cancellationToken);

                var embeddingData = System.Text.Json.JsonSerializer.Deserialize<EmbeddingData>(embeddingJson);

                // Create vector record
                var record = new VectorRecord
                {
                    Key = $"{context.DocumentId}/{embeddingFile.ParentId}",
                    Vector = new ReadOnlyMemory<float>(embeddingData.Vector),
                    Metadata = new Dictionary<string, object>
                    {
                        [TagConstants.DocumentId] = context.DocumentId,
                        [TagConstants.ExecutionId] = context.ExecutionId,
                        [TagConstants.FileId] = embeddingFile.ParentId,
                        ["partition_number"] = (embeddingFile as GeneratedFile)?.PartitionNumber ?? 0
                    }
                };

                // Copy tags to metadata
                foreach (var (key, values) in embeddingFile.Tags)
                {
                    record.Metadata[$"tag_{key}"] = string.Join(",", values);
                }

                // Upsert to vector store
                await collection.UpsertAsync(record, cancellationToken);

                embeddingFile.MarkProcessedBy(StepName);

                _logger.LogDebug(
                    "Stored vector for partition {FileId}",
                    embeddingFile.ParentId);
            }

            _logger.LogInformation(
                "Stored {Count} vectors in collection {Index}",
                embeddingFiles.Count(),
                context.Index);

            return PipelineResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vector storage failed");
            return PipelineResult.TransientFailure("Vector storage failed", ex);
        }
    }

    private class EmbeddingData
    {
        public float[] Vector { get; set; } = Array.Empty<float>();
        public string? Model { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}

// Vector record for storage
public class VectorRecord
{
    [VectorStoreRecordKey]
    public required string Key { get; init; }

    [VectorStoreRecordVector(Dimensions: 1536)]
    public required ReadOnlyMemory<float> Vector { get; init; }

    [VectorStoreRecordData]
    public Dictionary<string, object> Metadata { get; init; } = new();
}
```

---

## Retrieval Handlers

### 5. Query Rewriting Handler

```csharp
public class QueryRewritingHandler : IPipelineHandler<SemanticSearchContext>
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<QueryRewritingHandler> _logger;

    public string StepName => "query_rewrite";

    public QueryRewritingHandler(
        IChatClient chatClient,
        ILogger<QueryRewritingHandler> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.AlreadyProcessedBy(StepName))
            return PipelineResult.Success();

        try
        {
            // Generate query variations for better recall
            var prompt = $"""
                Generate 3 alternative phrasings of this search query.
                Keep the same intent but use different wording.

                Original query: {context.Query}

                Return only the 3 alternative queries, one per line.
                """;

            var response = await _chatClient.CompleteChatAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: cancellationToken);

            var rewrittenQueries = response.Message.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(q => q.Trim())
                .ToList();

            context.RewrittenQueries.AddRange(rewrittenQueries);

            context.Log(StepName, $"Generated {rewrittenQueries.Count} query variations");
            context.MarkProcessedBy(StepName);

            return PipelineResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Query rewriting failed");
            return PipelineResult.TransientFailure("Query rewriting failed", ex);
        }
    }
}
```

### 6. Vector Search Handler

```csharp
public class VectorSearchHandler : IPipelineHandler<SemanticSearchContext>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<VectorSearchHandler> _logger;

    public string StepName => "vector_search";

    // Constructor and HandleAsync implementation
    // See AI_PROVIDER_SETUP_GUIDE.md for complete example
}
```

### 7. Graph Search Handler

```csharp
public class GraphSearchHandler : IPipelineHandler<SemanticSearchContext>
{
    private readonly IGraphStore _graphStore;
    private readonly ILogger<GraphSearchHandler> _logger;

    public string StepName => "graph_search";

    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        // TODO: Implement graph traversal for GraphRAG
        // See your IGraphStore.TraverseAsync method
        throw new NotImplementedException("Implement based on your graph structure");
    }
}
```

### 8. Reranking Handler

```csharp
public class RerankingHandler : IPipelineHandler<SemanticSearchContext>
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<RerankingHandler> _logger;

    public string StepName => "rerank";

    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.AlreadyProcessedBy(StepName))
            return PipelineResult.Success();

        try
        {
            // Rerank using LLM or specialized reranking model
            // This is just one approach - you might use:
            // - Cohere Rerank API
            // - Cross-encoder models
            // - LLM-based reranking
            // - Custom ML model

            var results = context.Results.ToList();

            // TODO: Implement your reranking logic

            context.Results.Clear();
            context.Results.AddRange(results);

            context.MarkProcessedBy(StepName);

            return PipelineResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reranking failed");
            return PipelineResult.TransientFailure("Reranking failed", ex);
        }
    }
}
```

---

## Key Takeaways

1. **These are EXAMPLES** - Not production code, adapt to YOUR needs
2. **Every domain is different** - Legal ≠ Medical ≠ Code ≠ Research
3. **RAG evolves fast** - New techniques monthly, stay flexible
4. **Test thoroughly** - Unit tests for all handlers
5. **Use idempotency** - Always check `AlreadyProcessedBy()`
6. **Track lineage** - Use `GeneratedFile.ParentId`
7. **Add tags** - Enable filtering and organization
8. **Handle errors** - Return appropriate `PipelineResult`

---

## Next Steps

1. Read [HANDLER_DEVELOPMENT_GUIDE.md](HANDLER_DEVELOPMENT_GUIDE.md) for patterns
2. Read [RAG_TECHNIQUES_COOKBOOK.md](RAG_TECHNIQUES_COOKBOOK.md) for specific techniques
3. Implement handlers for YOUR domain
4. Test with real data
5. Iterate and improve

Remember: **We give you the plumbing, you build the house!**
