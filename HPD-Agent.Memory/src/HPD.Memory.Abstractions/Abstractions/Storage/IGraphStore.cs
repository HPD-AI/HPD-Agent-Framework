// Copyright (c) Einstein Essibu. All rights reserved.
// Graph database abstraction for knowledge graphs in RAG.
// Microsoft doesn't provide graph database abstractions, so we create our own.

namespace HPDAgent.Memory.Abstractions.Storage;

/// <summary>
/// Abstraction for graph database operations in RAG pipelines.
/// Supports entity relationships, knowledge graphs, citations, and hierarchies.
/// Enables GraphRAG scenarios that Kernel Memory doesn't support.
/// </summary>
/// <remarks>
/// Graph databases are essential for modern RAG:
/// - Entity-centric retrieval (find documents about related entities)
/// - Citation networks (traverse document citations)
/// - Hierarchical knowledge (organizational structures)
/// - Multi-hop reasoning (find documents N relationships away)
///
/// Microsoft doesn't provide graph database abstractions in Extensions.VectorData,
/// so we create a clean, provider-agnostic interface.
///
/// Implementations: Neo4j, Azure Cosmos DB (Gremlin), in-memory (testing)
/// </remarks>
public interface IGraphStore
{
    // ========================================
    // Entity Operations
    // ========================================

    /// <summary>
    /// Get an entity by ID.
    /// </summary>
    /// <param name="id">Entity identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The entity if found, null otherwise</returns>
    Task<GraphEntity?> GetEntityAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get multiple entities by IDs.
    /// </summary>
    /// <param name="ids">Entity identifiers</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Found entities (may be fewer than requested if some don't exist)</returns>
    Task<IReadOnlyList<GraphEntity>> GetEntitiesAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Save or update an entity.
    /// </summary>
    /// <param name="entity">Entity to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveEntityAsync(GraphEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Save or update multiple entities (batch operation).
    /// </summary>
    /// <param name="entities">Entities to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveEntitiesAsync(
        IEnumerable<GraphEntity> entities,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete an entity and all its relationships.
    /// </summary>
    /// <param name="id">Entity identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteEntityAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for entities by type and properties.
    /// </summary>
    /// <param name="type">Entity type (e.g., "Document", "Person", "Company")</param>
    /// <param name="filters">Property filters (key-value pairs)</param>
    /// <param name="limit">Maximum number of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching entities</returns>
    Task<IReadOnlyList<GraphEntity>> SearchEntitiesAsync(
        string? type = null,
        Dictionary<string, object>? filters = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    // ========================================
    // Relationship Operations
    // ========================================

    /// <summary>
    /// Create or update a relationship between two entities.
    /// </summary>
    /// <param name="relationship">Relationship to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveRelationshipAsync(
        GraphRelationship relationship,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update multiple relationships (batch operation).
    /// </summary>
    /// <param name="relationships">Relationships to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveRelationshipsAsync(
        IEnumerable<GraphRelationship> relationships,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all relationships for an entity.
    /// </summary>
    /// <param name="entityId">Entity identifier</param>
    /// <param name="direction">Relationship direction (outgoing, incoming, both)</param>
    /// <param name="relationshipTypes">Filter by relationship types (null = all types)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching relationships</returns>
    Task<IReadOnlyList<GraphRelationship>> GetRelationshipsAsync(
        string entityId,
        RelationshipDirection direction = RelationshipDirection.Both,
        string[]? relationshipTypes = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a specific relationship.
    /// </summary>
    /// <param name="relationshipId">Relationship identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteRelationshipAsync(string relationshipId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all relationships between two entities.
    /// </summary>
    /// <param name="fromId">Source entity ID</param>
    /// <param name="toId">Target entity ID</param>
    /// <param name="relationshipType">Optional: only delete relationships of this type</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteRelationshipsAsync(
        string fromId,
        string toId,
        string? relationshipType = null,
        CancellationToken cancellationToken = default);

    // ========================================
    // Graph Traversal (The Power of Graphs!)
    // ========================================

    /// <summary>
    /// Traverse the graph from a starting entity.
    /// This is the key operation for GraphRAG - finding related entities through relationships.
    /// </summary>
    /// <param name="startEntityId">Starting entity ID</param>
    /// <param name="options">Traversal options (max hops, relationship types, filters)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Entities found during traversal, with their distances from start</returns>
    /// <remarks>
    /// Example: Find all documents citing document X, and documents citing those:
    /// - Start: document X
    /// - MaxHops: 2
    /// - RelationshipTypes: ["cites"]
    /// - Result: All documents in the citation network within 2 hops
    /// </remarks>
    Task<IReadOnlyList<GraphTraversalResult>> TraverseAsync(
        string startEntityId,
        GraphTraversalOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find the shortest path between two entities.
    /// Useful for understanding how entities are related.
    /// </summary>
    /// <param name="fromId">Source entity ID</param>
    /// <param name="toId">Target entity ID</param>
    /// <param name="maxHops">Maximum path length to search</param>
    /// <param name="relationshipTypes">Filter by relationship types (null = all types)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The shortest path, or null if no path exists within maxHops</returns>
    Task<GraphPath?> FindShortestPathAsync(
        string fromId,
        string toId,
        int maxHops = 5,
        string[]? relationshipTypes = null,
        CancellationToken cancellationToken = default);

    // ========================================
    // Utility Operations
    // ========================================

    /// <summary>
    /// Check if the graph store is healthy and connected.
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get statistics about the graph (entity counts, relationship counts, etc.).
    /// </summary>
    Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear all data from the graph store (use with caution!).
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Direction for relationship queries.
/// </summary>
public enum RelationshipDirection
{
    /// <summary>
    /// Relationships going out from the entity (entity is source).
    /// </summary>
    Outgoing,

    /// <summary>
    /// Relationships coming into the entity (entity is target).
    /// </summary>
    Incoming,

    /// <summary>
    /// All relationships regardless of direction.
    /// </summary>
    Both
}
