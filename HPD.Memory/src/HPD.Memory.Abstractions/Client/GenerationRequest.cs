// Copyright (c) Einstein Essibu. All rights reserved.
// Request contracts for IMemoryClient generation operations

namespace HPDAgent.Memory.Abstractions.Client;

/// <summary>
/// Request to generate an answer using RAG (retrieval + generation).
/// </summary>
public class GenerationRequest
{
    /// <summary>
    /// The question to answer.
    /// Examples: "What is RAG?", "How do I configure authentication?", "Summarize the Q4 sales report"
    /// </summary>
    public required string Question { get; init; }

    /// <summary>
    /// Index/collection to retrieve knowledge from.
    /// If null, uses the default index.
    /// </summary>
    public string? Index { get; init; }

    /// <summary>
    /// Maximum number of documents/chunks to retrieve for context.
    /// Default: 5
    /// </summary>
    public int? MaxResults { get; init; } = 5;

    /// <summary>
    /// Minimum relevance score for retrieved items (0.0 to 1.0).
    /// Default: null (no filtering)
    /// </summary>
    public double? MinRelevanceScore { get; init; }

    /// <summary>
    /// Filter documents by metadata/tags (same as RetrievalRequest).
    /// </summary>
    public MemoryFilter? Filter { get; init; }

    /// <summary>
    /// System prompt to use for generation.
    /// If null, implementation uses default RAG prompt.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Conversation history (for multi-turn conversations).
    /// Each message should have Role (User/Assistant) and Content.
    /// </summary>
    public List<ConversationMessage>? ConversationHistory { get; init; }

    /// <summary>
    /// Implementation-specific options.
    /// Common options (conventions, not required):
    ///
    /// LLM parameters:
    /// - "temperature": double (0.0 to 2.0, default: 0.7)
    /// - "top_p": double (0.0 to 1.0, default: 0.9)
    /// - "max_tokens": int (max tokens to generate)
    /// - "model": string (LLM model to use, e.g., "gpt-4")
    ///
    /// Retrieval options (passed to RetrieveAsync):
    /// - "query_rewrite": bool
    /// - "multi_query": bool
    /// - "hyde": bool
    /// - "max_hops": int (for GraphRAG)
    /// - "rerank": bool
    ///
    /// Generation options:
    /// - "stream": bool (enable streaming)
    /// - "include_citations": bool (include source citations in answer)
    /// - "citation_format": string ("inline", "footnote", "endnote")
    /// - "answer_format": string ("concise", "detailed", "bullet_points")
    ///
    /// Agentic options:
    /// - "enable_self_refinement": bool (check and refine answer quality)
    /// - "max_iterations": int (for iterative retrieval/generation)
    /// </summary>
    public Dictionary<string, object> Options { get; init; } = new();
}

/// <summary>
/// A message in a conversation (for multi-turn RAG).
/// </summary>
public record ConversationMessage
{
    /// <summary>
    /// Role of the message sender.
    /// </summary>
    public required MessageRole Role { get; init; }

    /// <summary>
    /// Content of the message.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Timestamp of the message (optional).
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>
/// Role of a conversation message.
/// </summary>
public enum MessageRole
{
    /// <summary>
    /// System prompt/instructions.
    /// </summary>
    System,

    /// <summary>
    /// User message (question/request).
    /// </summary>
    User,

    /// <summary>
    /// Assistant message (response).
    /// </summary>
    Assistant
}
