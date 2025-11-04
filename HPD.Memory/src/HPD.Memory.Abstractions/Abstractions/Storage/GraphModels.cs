// Copyright (c) Einstein Essibu. All rights reserved.
// Graph data models for knowledge graphs in RAG.

namespace HPDAgent.Memory.Abstractions.Storage;

/// <summary>
/// Represents an entity (node) in the knowledge graph.
/// </summary>
/// <remarks>
/// Examples of entities:
/// - Documents (type="Document", id="doc_123")
/// - People (type="Person", id="person_456")
/// - Companies (type="Company", id="company_789")
/// - Concepts (type="Concept", id="concept_rag")
/// - Topics (type="Topic", id="topic_ai")
/// </remarks>
public class GraphEntity
{
    /// <summary>
    /// Unique identifier for the entity.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Entity type (e.g., "Document", "Person", "Company", "Concept").
    /// Used for filtering and type-specific operations.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Human-readable name or label for the entity.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Optional description of the entity.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Arbitrary properties for the entity.
    /// Store domain-specific attributes here.
    /// </summary>
    /// <remarks>
    /// Examples:
    /// - Document: { "title": "RAG paper", "author": "...", "date": "2024-01-01" }
    /// - Person: { "email": "...", "role": "Engineer" }
    /// - Company: { "industry": "Technology", "founded": "2020" }
    /// </remarks>
    public Dictionary<string, object> Properties { get; init; } = new();

    /// <summary>
    /// When the entity was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the entity was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Represents a relationship (edge) between two entities in the knowledge graph.
/// </summary>
/// <remarks>
/// Examples of relationships:
/// - Document cites Document: type="cites"
/// - Document authored_by Person: type="authored_by"
/// - Document about Topic: type="about"
/// - Company owns Company: type="owns" (subsidiaries)
/// - Person works_for Company: type="works_for"
/// </remarks>
public class GraphRelationship
{
    /// <summary>
    /// Unique identifier for the relationship (optional - some graph DBs auto-generate).
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Source entity ID (the relationship goes FROM this entity).
    /// </summary>
    public required string FromId { get; init; }

    /// <summary>
    /// Target entity ID (the relationship goes TO this entity).
    /// </summary>
    public required string ToId { get; init; }

    /// <summary>
    /// Relationship type/label (e.g., "cites", "authored_by", "mentions").
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Optional properties for the relationship.
    /// Can store relationship-specific data like weight, confidence, date, etc.
    /// </summary>
    /// <remarks>
    /// Examples:
    /// - Citation: { "page": 5, "section": "introduction" }
    /// - Mention: { "confidence": 0.95, "context": "..." }
    /// - Temporal: { "start_date": "2020-01-01", "end_date": "2023-12-31" }
    /// </remarks>
    public Dictionary<string, object> Properties { get; init; } = new();

    /// <summary>
    /// When the relationship was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Options for graph traversal operations.
/// </summary>
public class GraphTraversalOptions
{
    /// <summary>
    /// Maximum number of hops/steps from the starting entity.
    /// Default: 2 (immediate neighbors + their neighbors)
    /// </summary>
    public int MaxHops { get; set; } = 2;

    /// <summary>
    /// Filter by relationship types. Null means all types.
    /// Example: ["cites", "mentions"] to only follow citation and mention relationships.
    /// </summary>
    public string[]? RelationshipTypes { get; set; }

    /// <summary>
    /// Filter by entity types. Null means all types.
    /// Example: ["Document"] to only return document entities.
    /// </summary>
    public string[]? EntityTypes { get; set; }

    /// <summary>
    /// Direction to traverse. Default is Outgoing.
    /// </summary>
    public RelationshipDirection Direction { get; set; } = RelationshipDirection.Outgoing;

    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int Limit { get; set; } = 100;

    /// <summary>
    /// Additional property filters for entities encountered during traversal.
    /// Example: { "language": "english", "year": 2024 }
    /// </summary>
    public Dictionary<string, object>? PropertyFilters { get; set; }

    /// <summary>
    /// Whether to include the starting entity in results.
    /// Default: false (only return discovered entities)
    /// </summary>
    public bool IncludeStartEntity { get; set; }
}

/// <summary>
/// Result of a graph traversal operation.
/// </summary>
public class GraphTraversalResult
{
    /// <summary>
    /// The discovered entity.
    /// </summary>
    public required GraphEntity Entity { get; init; }

    /// <summary>
    /// Distance (number of hops) from the starting entity.
    /// 0 = starting entity, 1 = direct neighbor, 2 = neighbor of neighbor, etc.
    /// </summary>
    public required int Distance { get; init; }

    /// <summary>
    /// The path of relationships taken to reach this entity.
    /// Useful for understanding HOW entities are connected.
    /// </summary>
    public List<GraphRelationship> Path { get; init; } = new();

    /// <summary>
    /// Optional score/relevance for ranking results.
    /// Can be computed based on relationship strength, frequency, etc.
    /// </summary>
    public float? Score { get; set; }
}

/// <summary>
/// Represents a path between two entities in the graph.
/// </summary>
public class GraphPath
{
    /// <summary>
    /// Starting entity ID.
    /// </summary>
    public required string FromId { get; init; }

    /// <summary>
    /// Ending entity ID.
    /// </summary>
    public required string ToId { get; init; }

    /// <summary>
    /// Entities along the path (includes start and end).
    /// </summary>
    public required List<GraphEntity> Entities { get; init; }

    /// <summary>
    /// Relationships connecting the entities.
    /// Length is Entities.Count - 1.
    /// </summary>
    public required List<GraphRelationship> Relationships { get; init; }

    /// <summary>
    /// Path length (number of hops/relationships).
    /// </summary>
    public int Length => Relationships.Count;
}

/// <summary>
/// Statistics about the graph store.
/// </summary>
public class GraphStatistics
{
    /// <summary>
    /// Total number of entities in the graph.
    /// </summary>
    public long EntityCount { get; set; }

    /// <summary>
    /// Total number of relationships in the graph.
    /// </summary>
    public long RelationshipCount { get; set; }

    /// <summary>
    /// Count of entities by type.
    /// Example: { "Document": 1000, "Person": 50, "Company": 25 }
    /// </summary>
    public Dictionary<string, long> EntityCountByType { get; init; } = new();

    /// <summary>
    /// Count of relationships by type.
    /// Example: { "cites": 500, "authored_by": 200, "mentions": 300 }
    /// </summary>
    public Dictionary<string, long> RelationshipCountByType { get; init; } = new();

    /// <summary>
    /// When these statistics were computed.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
