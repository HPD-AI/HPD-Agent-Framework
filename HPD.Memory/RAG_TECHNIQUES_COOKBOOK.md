# RAG Techniques Cookbook

**How to implement various RAG techniques as HPD-Agent.Memory handlers**

⚠️ **IMPORTANT**: RAG techniques evolve constantly. This guide shows **HOW to implement** popular approaches, not prescriptive solutions. You should implement what works for YOUR domain.

---

## Table of Contents

1. [Basic RAG](#1-basic-rag)
2. [HyDE (Hypothetical Document Embeddings)](#2-hyde)
3. [RAG-Fusion](#3-rag-fusion)
4. [Self-RAG](#4-self-rag)
5. [RAPTOR (Recursive Abstractive Processing)](#5-raptor)
6. [GraphRAG](#6-graphrag)
7. [ColBERT (Late Interaction)](#7-colbert)
8. [Reranking Strategies](#8-reranking-strategies)
9. [Hybrid Search (Vector + BM25)](#9-hybrid-search)
10. [Multi-Query RAG](#10-multi-query-rag)

---

## 1. Basic RAG

**Pipeline**: `extract → partition → embed → store → search → generate`

```csharp
// Configure basic RAG pipeline
var context = new DocumentIngestionContext
{
    Index = "docs",
    DocumentId = "doc-123",
    Services = serviceProvider,
    Steps = new[]
    {
        "extract_text",
        "partition_text",
        "generate_embeddings",
        "store_vectors"
    }.ToList()
};

// Add your handlers
await orchestrator.AddHandlerAsync(new TextExtractionHandler(...));
await orchestrator.AddHandlerAsync(new TextPartitioningHandler(...));
await orchestrator.AddHandlerAsync(new EmbeddingGenerationHandler(...));
await orchestrator.AddHandlerAsync(new VectorStorageHandler(...));

// Execute
await orchestrator.ExecuteAsync(context);
```

**Retrieval**:
```csharp
var searchContext = new SemanticSearchContext
{
    Index = "docs",
    Query = "What is GraphRAG?",
    Services = serviceProvider,
    Steps = new[] { "vector_search" }.ToList()
};

await searchOrchestrator.AddHandlerAsync(new VectorSearchHandler(...));
await searchOrchestrator.ExecuteAsync(searchContext);
```

---

## 2. HyDE (Hypothetical Document Embeddings)

**Technique**: Generate a hypothetical answer, embed it, search with that embedding.

**Reference**: "Precise Zero-Shot Dense Retrieval without Relevance Labels" (2022)

```csharp
public class HyDEHandler : IPipelineHandler<SemanticSearchContext>
{
    private readonly IChatClient _chatClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;

    public string StepName => "hyde_generation";

    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.AlreadyProcessedBy(StepName))
            return PipelineResult.Success();

        try
        {
            // Step 1: Generate hypothetical document
            var prompt = $"""
                Write a detailed paragraph that would answer this question:
                {context.Query}

                Write as if you are an expert answering the question with confidence.
                Do not acknowledge uncertainty or ask for more information.
                """;

            var response = await _chatClient.CompleteChatAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: cancellationToken);

            var hypotheticalDoc = response.Message.Text;

            context.Log(StepName, "Generated hypothetical document");

            // Step 2: Embed the hypothetical document (instead of query)
            var embedding = await _embedder.GenerateEmbeddingVectorAsync(
                hypotheticalDoc,
                cancellationToken: cancellationToken);

            context.QueryEmbedding = embedding.ToArray();

            context.MarkProcessedBy(StepName);

            return PipelineResult.Success();
        }
        catch (Exception ex)
        {
            return PipelineResult.TransientFailure("HyDE generation failed", ex);
        }
    }
}

// Pipeline configuration
var steps = new[]
{
    "hyde_generation",  // ← Generate hypothetical doc & embed it
    "vector_search",     // Search with hypothetical embedding
    "generate_answer"
};
```

**When to use**: When query is vague or user doesn't know exact terminology.

---

## 3. RAG-Fusion

**Technique**: Generate multiple query variations, search with each, fuse results using reciprocal rank fusion.

**Reference**: "RAG-Fusion: a New Take on Retrieval-Augmented Generation" (2023)

```csharp
public class RAGFusionHandler : IPipelineHandler<SemanticSearchContext>
{
    private readonly IChatClient _chatClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IVectorStore _vectorStore;

    public string StepName => "rag_fusion";

    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.AlreadyProcessedBy(StepName))
            return PipelineResult.Success();

        try
        {
            // Step 1: Generate query variations
            var prompt = $"""
                Generate 4 different search queries that capture various aspects of this question:
                {context.Query}

                Return only the queries, one per line.
                """;

            var response = await _chatClient.CompleteChatAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: cancellationToken);

            var queries = response.Message.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(q => q.Trim())
                .Prepend(context.Query) // Include original
                .ToList();

            context.Log(StepName, $"Generated {queries.Count} query variations");

            // Step 2: Search with each query
            var allResults = new Dictionary<string, List<(SearchResult result, int rank)>>();

            for (int i = 0; i < queries.Count; i++)
            {
                var embedding = await _embedder.GenerateEmbeddingVectorAsync(
                    queries[i],
                    cancellationToken: cancellationToken);

                var results = await SearchWithEmbeddingAsync(
                    context,
                    embedding,
                    cancellationToken);

                // Track rank for each result
                for (int rank = 0; rank < results.Count; rank++)
                {
                    var docId = results[rank].Id;

                    if (!allResults.ContainsKey(docId))
                        allResults[docId] = new List<(SearchResult, int)>();

                    allResults[docId].Add((results[rank], rank));
                }
            }

            // Step 3: Reciprocal Rank Fusion
            var k = 60.0; // RRF constant
            var fusedResults = allResults
                .Select(kvp =>
                {
                    var docId = kvp.Key;
                    var occurrences = kvp.Value;

                    // RRF score = sum(1 / (k + rank)) for each query
                    var rrfScore = occurrences.Sum(occ => 1.0 / (k + occ.rank));

                    return (result: occurrences.First().result, score: rrfScore);
                })
                .OrderByDescending(r => r.score)
                .Take(context.MaxResults)
                .Select(r => new SearchResult
                {
                    Id = r.result.Id,
                    DocumentId = r.result.DocumentId,
                    Content = r.result.Content,
                    Score = (float)r.score,
                    Source = StepName,
                    Tags = r.result.Tags
                })
                .ToList();

            context.Results.Clear();
            context.Results.AddRange(fusedResults);

            context.MarkProcessedBy(StepName);

            return PipelineResult.Success();
        }
        catch (Exception ex)
        {
            return PipelineResult.TransientFailure("RAG-Fusion failed", ex);
        }
    }

    private async Task<List<SearchResult>> SearchWithEmbeddingAsync(
        SemanticSearchContext context,
        ReadOnlyMemory<float> embedding,
        CancellationToken cancellationToken)
    {
        // Implement vector search
        // Return top N results
        throw new NotImplementedException();
    }
}
```

**When to use**: When precision matters more than speed, willing to make multiple embedding calls.

---

## 4. Self-RAG

**Technique**: LLM decides when to retrieve and evaluates retrieved documents.

**Reference**: "Self-RAG: Learning to Retrieve, Generate, and Critique through Self-Reflection" (2023)

```csharp
public class SelfRAGHandler : IPipelineHandler<SemanticSearchContext>
{
    public string StepName => "self_rag";

    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Should we retrieve?
        var shouldRetrieve = await DetermineIfRetrievalNeeded(context.Query);

        if (!shouldRetrieve)
        {
            context.Log(StepName, "LLM determined retrieval not needed");
            return PipelineResult.Success();
        }

        // Step 2: Retrieve documents
        await PerformRetrievalAsync(context, cancellationToken);

        // Step 3: Evaluate relevance of each document
        var relevantDocs = new List<SearchResult>();

        foreach (var result in context.Results)
        {
            var isRelevant = await EvaluateRelevance(context.Query, result.Content);

            if (isRelevant)
            {
                relevantDocs.Add(result);
            }
            else
            {
                context.Log(StepName, $"Document {result.Id} deemed not relevant");
            }
        }

        context.Results.Clear();
        context.Results.AddRange(relevantDocs);

        // Step 4: Generate with self-critique
        // (Implement in separate generation handler)

        context.MarkProcessedBy(StepName);

        return PipelineResult.Success();
    }

    private async Task<bool> DetermineIfRetrievalNeeded(string query)
    {
        // Ask LLM: "Can you answer this without external knowledge?"
        throw new NotImplementedException();
    }

    private async Task<bool> EvaluateRelevance(string query, string document)
    {
        // Ask LLM: "Is this document relevant to the query?"
        throw new NotImplementedException();
    }
}
```

**When to use**: When you want to minimize unnecessary retrievals and filter low-quality results.

---

## 5. RAPTOR (Recursive Abstractive Processing)

**Technique**: Build hierarchical summaries, embed at multiple levels.

**Reference**: "RAPTOR: Recursive Abstractive Processing for Tree-Organized Retrieval" (2024)

```csharp
public class RAPTORPartitioningHandler : IPipelineHandler<DocumentIngestionContext>
{
    public string StepName => "raptor_partitioning";

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var file in context.GetFilesByType(FileArtifactType.ExtractedText))
        {
            if (file.AlreadyProcessedBy(StepName))
                continue;

            var text = await LoadTextAsync(file, context);

            // Level 0: Original chunks
            var level0Chunks = PartitionIntoChunks(text, chunkSize: 512);

            // Level 1: Summarize groups of 5 chunks
            var level1Summaries = await SummarizeChunksAsync(level0Chunks, groupSize: 5);

            // Level 2: Summarize groups of 5 level-1 summaries
            var level2Summaries = await SummarizeChunksAsync(level1Summaries, groupSize: 5);

            // Store all levels
            await StoreHierarchicalChunksAsync(
                context,
                file,
                new[] { level0Chunks, level1Summaries, level2Summaries });

            file.MarkProcessedBy(StepName);
        }

        return PipelineResult.Success();
    }

    private async Task<List<string>> SummarizeChunksAsync(
        List<string> chunks,
        int groupSize)
    {
        var summaries = new List<string>();

        for (int i = 0; i < chunks.Count; i += groupSize)
        {
            var group = chunks.Skip(i).Take(groupSize);
            var combined = string.Join("\n\n", group);

            // Summarize group
            var summary = await _chatClient.CompleteChatAsync(...)
                .Message.Text;

            summaries.Add(summary);
        }

        return summaries;
    }
}
```

**When to use**: For long documents where you need both high-level and detailed retrieval.

---

## 6. GraphRAG

**Technique**: Build knowledge graph, traverse for retrieval.

**Reference**: "From Local to Global: A Graph RAG Approach" (Microsoft, 2024)

```csharp
public class GraphRAGHandler : IPipelineHandler<DocumentIngestionContext>
{
    private readonly IChatClient _chatClient;
    private readonly IGraphStore _graphStore;

    public string StepName => "build_knowledge_graph";

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var partition in context.GetFilesByType(FileArtifactType.TextPartition))
        {
            if (partition.AlreadyProcessedBy(StepName))
                continue;

            var text = await LoadTextAsync(partition, context);

            // Extract entities and relationships
            var prompt = $"""
                Extract entities and relationships from this text.
                Return as JSON:
                {{
                  "entities": [
                    {{"id": "unique_id", "type": "Person|Organization|Concept", "name": "..."}}
                  ],
                  "relationships": [
                    {{"from": "entity_id", "to": "entity_id", "type": "relationship_type"}}
                  ]
                }}

                Text: {text}
                """;

            var response = await _chatClient.CompleteChatAsync(...);
            var graphData = JsonSerializer.Deserialize<GraphData>(response.Message.Text);

            // Store in graph
            foreach (var entity in graphData.Entities)
            {
                await _graphStore.SaveEntityAsync(new GraphEntity
                {
                    Id = entity.Id,
                    Type = entity.Type,
                    Properties = new Dictionary<string, object>
                    {
                        ["name"] = entity.Name,
                        ["source_document"] = context.DocumentId,
                        ["partition"] = partition.Id
                    }
                });
            }

            foreach (var rel in graphData.Relationships)
            {
                await _graphStore.SaveRelationshipAsync(new GraphRelationship
                {
                    FromId = rel.From,
                    ToId = rel.To,
                    Type = rel.Type
                });
            }

            partition.MarkProcessedBy(StepName);
        }

        return PipelineResult.Success();
    }
}

// Retrieval
public class GraphTraversalHandler : IPipelineHandler<SemanticSearchContext>
{
    private readonly IGraphStore _graphStore;

    public string StepName => "graph_traversal";

    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        // Extract entities from query
        var queryEntities = await ExtractEntitiesFromQueryAsync(context.Query);

        // Traverse graph from each entity
        foreach (var entityId in queryEntities)
        {
            var traversalResults = await _graphStore.TraverseAsync(
                entityId,
                new GraphTraversalOptions
                {
                    MaxHops = 2,
                    Direction = RelationshipDirection.Both
                });

            // Convert graph results to search results
            foreach (var graphResult in traversalResults)
            {
                context.Results.Add(new SearchResult
                {
                    Id = graphResult.Entity.Id,
                    DocumentId = graphResult.Entity.Properties.GetValueOrDefault("source_document")?.ToString() ?? "",
                    Content = await LoadEntityContentAsync(graphResult.Entity),
                    Score = 1.0f / (graphResult.Distance + 1), // Closer = higher score
                    Source = StepName
                });
            }
        }

        context.MarkProcessedBy(StepName);

        return PipelineResult.Success();
    }
}
```

**When to use**: When relationships between entities matter (e.g., legal citations, scientific papers, org charts).

---

## 7. ColBERT (Late Interaction)

**Technique**: Multi-vector embeddings with late interaction scoring.

**Reference**: "ColBERT: Efficient and Effective Passage Search via Contextualized Late Interaction" (2020)

```csharp
// Note: Requires specialized embedding model (ColBERTv2, etc.)
// This is pseudocode showing the concept

public class ColBERTEmbeddingHandler : IPipelineHandler<DocumentIngestionContext>
{
    public string StepName => "colbert_embeddings";

    public async Task<PipelineResult> HandleAsync(
        DocumentIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var partition in context.GetFilesByType(FileArtifactType.TextPartition))
        {
            var text = await LoadTextAsync(partition, context);

            // ColBERT: Embed each token separately
            var tokenEmbeddings = await GenerateTokenEmbeddingsAsync(text);

            // Store multi-vector representation
            await StoreMultiVectorAsync(partition.Id, tokenEmbeddings);

            partition.MarkProcessedBy(StepName);
        }

        return PipelineResult.Success();
    }
}

// Retrieval with MaxSim
public class ColBERTSearchHandler : IPipelineHandler<SemanticSearchContext>
{
    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        // Embed query tokens
        var queryTokenEmbeddings = await GenerateTokenEmbeddingsAsync(context.Query);

        // For each document, compute MaxSim score
        var scores = await ComputeMaxSimScoresAsync(queryTokenEmbeddings);

        // MaxSim = sum over query tokens of max similarity to any doc token

        context.MarkProcessedBy(StepName);

        return PipelineResult.Success();
    }
}
```

**When to use**: When you need fine-grained semantic matching (e.g., question answering, fact verification).

---

## 8. Reranking Strategies

### Cross-Encoder Reranking

```csharp
public class CrossEncoderRerankHandler : IPipelineHandler<SemanticSearchContext>
{
    public string StepName => "cross_encoder_rerank";

    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        // Use cross-encoder model to score query-document pairs
        var rerankedResults = new List<(SearchResult result, float score)>();

        foreach (var result in context.Results.Take(100)) // Rerank top 100
        {
            var score = await ComputeCrossEncoderScore(context.Query, result.Content);
            rerankedResults.Add((result, score));
        }

        // Sort by cross-encoder score
        var sorted = rerankedResults
            .OrderByDescending(r => r.score)
            .Take(context.MaxResults)
            .Select(r => new SearchResult
            {
                Id = r.result.Id,
                DocumentId = r.result.DocumentId,
                Content = r.result.Content,
                Score = r.score,
                Source = StepName,
                Tags = r.result.Tags
            });

        context.Results.Clear();
        context.Results.AddRange(sorted);

        context.MarkProcessedBy(StepName);

        return PipelineResult.Success();
    }
}
```

### LLM-Based Reranking

```csharp
public class LLMRerankHandler : IPipelineHandler<SemanticSearchContext>
{
    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        // Ask LLM to rank results by relevance
        var prompt = $"""
            Query: {context.Query}

            Rank these passages from most to least relevant (return only the passage numbers):

            {string.Join("\n\n", context.Results.Select((r, i) => $"Passage {i}: {r.Content}"))}
            """;

        var response = await _chatClient.CompleteChatAsync(...);

        // Parse ranking from response
        var rankedIndices = ParseRanking(response.Message.Text);

        // Reorder results
        var reranked = rankedIndices
            .Select((index, rank) => new SearchResult
            {
                Id = context.Results[index].Id,
                DocumentId = context.Results[index].DocumentId,
                Content = context.Results[index].Content,
                Score = 1.0f / (rank + 1), // Higher rank = higher score
                Source = StepName,
                Tags = context.Results[index].Tags
            });

        context.Results.Clear();
        context.Results.AddRange(reranked);

        context.MarkProcessedBy(StepName);

        return PipelineResult.Success();
    }
}
```

---

## 9. Hybrid Search (Vector + BM25)

```csharp
public class HybridSearchHandler : IPipelineHandler<SemanticSearchContext>
{
    private readonly IVectorStore _vectorStore;
    private readonly IBM25Index _bm25Index; // Your keyword search implementation

    public string StepName => "hybrid_search";

    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        // Vector search
        var vectorResults = await PerformVectorSearchAsync(context, cancellationToken);

        // BM25 keyword search
        var bm25Results = await PerformBM25SearchAsync(context, cancellationToken);

        // Combine with weighted fusion
        var combined = FuseResults(
            vectorResults,
            bm25Results,
            vectorWeight: 0.7,
            bm25Weight: 0.3);

        context.Results.Clear();
        context.Results.AddRange(combined);

        context.MarkProcessedBy(StepName);

        return PipelineResult.Success();
    }

    private List<SearchResult> FuseResults(
        List<SearchResult> vectorResults,
        List<SearchResult> bm25Results,
        float vectorWeight,
        float bm25Weight)
    {
        var scoreMap = new Dictionary<string, float>();

        // Add normalized vector scores
        var maxVectorScore = vectorResults.Max(r => r.Score);
        foreach (var result in vectorResults)
        {
            scoreMap[result.Id] = (result.Score / maxVectorScore) * vectorWeight;
        }

        // Add normalized BM25 scores
        var maxBM25Score = bm25Results.Max(r => r.Score);
        foreach (var result in bm25Results)
        {
            var normalized = (result.Score / maxBM25Score) * bm25Weight;

            if (scoreMap.ContainsKey(result.Id))
                scoreMap[result.Id] += normalized;
            else
                scoreMap[result.Id] = normalized;
        }

        // Combine and sort
        var allResults = vectorResults.Concat(bm25Results)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .Select(r => new SearchResult
            {
                Id = r.Id,
                DocumentId = r.DocumentId,
                Content = r.Content,
                Score = scoreMap[r.Id],
                Source = StepName,
                Tags = r.Tags
            })
            .OrderByDescending(r => r.Score)
            .Take(50)
            .ToList();

        return allResults;
    }
}
```

---

## 10. Multi-Query RAG

```csharp
// Similar to RAG-Fusion but simpler - just merge results from multiple queries

public class MultiQueryHandler : IPipelineHandler<SemanticSearchContext>
{
    public async Task<PipelineResult> HandleAsync(
        SemanticSearchContext context,
        CancellationToken cancellationToken = default)
    {
        // Generate 3 related queries
        var queries = await GenerateRelatedQueriesAsync(context.Query);

        var allResults = new List<SearchResult>();

        // Search with each query
        foreach (var query in queries)
        {
            var results = await SearchAsync(query, context, cancellationToken);
            allResults.AddRange(results);
        }

        // Deduplicate and sort by score
        var deduplicated = allResults
            .GroupBy(r => r.Id)
            .Select(g => new SearchResult
            {
                Id = g.Key,
                DocumentId = g.First().DocumentId,
                Content = g.First().Content,
                Score = g.Max(r => r.Score), // Take best score
                Source = StepName,
                Tags = g.First().Tags
            })
            .OrderByDescending(r => r.Score)
            .Take(context.MaxResults)
            .ToList();

        context.Results.Clear();
        context.Results.AddRange(deduplicated);

        context.MarkProcessedBy(StepName);

        return PipelineResult.Success();
    }
}
```

---

## Key Principles

1. **Stay Flexible** - New techniques emerge monthly
2. **Test Everything** - What works in papers may not work for your data
3. **Start Simple** - Basic RAG often works surprisingly well
4. **Iterate** - Try techniques, measure, improve
5. **Know Your Domain** - Legal ≠ Medical ≠ Code ≠ Research
6. **Combine Techniques** - Hybrid approaches often win
7. **Monitor Performance** - Track latency, quality, cost

---

## Measuring RAG Quality

```csharp
public class RAGEvaluationMetrics
{
    // Answer Relevance: Does the answer address the question?
    public float AnswerRelevance { get; set; }

    // Context Relevance: Are retrieved docs relevant?
    public float ContextRelevance { get; set; }

    // Groundedness: Is answer supported by retrieved docs?
    public float Groundedness { get; set; }

    // Latency: How fast?
    public TimeSpan Latency { get; set; }

    // Cost: How expensive?
    public decimal Cost { get; set; }
}
```

Tools for evaluation:
- **RAGAS** - RAG Assessment framework
- **TruLens** - Evaluation and monitoring
- **LangSmith** - Tracing and debugging
- **Custom evals** - Domain-specific metrics

---

## Resources

### Papers
- "Retrieval-Augmented Generation for Knowledge-Intensive NLP Tasks" (2020) - Original RAG
- "HyDE: Precise Zero-Shot Dense Retrieval" (2022)
- "Self-RAG: Learning to Retrieve, Generate, and Critique" (2023)
- "RAPTOR: Recursive Abstractive Processing" (2024)
- "From Local to Global: A Graph RAG Approach" (Microsoft, 2024)

### Tools
- **LangChain** - RAG orchestration framework
- **LlamaIndex** - Data framework for LLMs
- **DSPy** - Programming framework for LLM pipelines
- **Haystack** - End-to-end NLP framework

---

## Remember

**HPD-Agent.Memory gives you the infrastructure. You build the intelligence.**

These techniques are starting points. Your domain knowledge + experimentation = optimal RAG for YOUR use case.
