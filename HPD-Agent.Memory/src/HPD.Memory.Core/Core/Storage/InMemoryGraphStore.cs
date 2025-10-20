// Copyright (c) Einstein Essibu. All rights reserved.
// In-memory graph store implementation for testing and development.

using HPDAgent.Memory.Abstractions.Storage;
using Microsoft.Extensions.Logging;

namespace HPDAgent.Memory.Core.Storage;

/// <summary>
/// In-memory graph store implementation.
/// Good for testing, development, and small deployments.
/// For production, use Neo4j, Azure Cosmos DB (Gremlin), or other graph databases.
/// </summary>
/// <remarks>
/// This implementation uses simple dictionaries for storage.
/// It's not optimized for large graphs or complex traversals.
/// Traversal uses breadth-first search (BFS) which is good enough for testing.
/// </remarks>
public class InMemoryGraphStore : IGraphStore
{
    private readonly Dictionary<string, GraphEntity> _entities = new();
    private readonly Dictionary<string, GraphRelationship> _relationships = new();
    private readonly object _lock = new();
    private readonly ILogger<InMemoryGraphStore> _logger;

    public InMemoryGraphStore(ILogger<InMemoryGraphStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("InMemoryGraphStore initialized");
    }

    // ========================================
    // Entity Operations
    // ========================================

    public Task<GraphEntity?> GetEntityAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _entities.TryGetValue(id, out var entity);
            return Task.FromResult(entity);
        }
    }

    public Task<IReadOnlyList<GraphEntity>> GetEntitiesAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var entities = ids
                .Where(id => _entities.ContainsKey(id))
                .Select(id => _entities[id])
                .ToList();

            return Task.FromResult<IReadOnlyList<GraphEntity>>(entities);
        }
    }

    public Task SaveEntityAsync(GraphEntity entity, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            entity.UpdatedAt = DateTimeOffset.UtcNow;

            if (_entities.ContainsKey(entity.Id))
            {
                _logger.LogDebug("Updating entity: {Id} ({Type})", entity.Id, entity.Type);
            }
            else
            {
                _logger.LogDebug("Creating entity: {Id} ({Type})", entity.Id, entity.Type);
            }

            _entities[entity.Id] = entity;
        }

        return Task.CompletedTask;
    }

    public Task SaveEntitiesAsync(
        IEnumerable<GraphEntity> entities,
        CancellationToken cancellationToken = default)
    {
        foreach (var entity in entities)
        {
            SaveEntityAsync(entity, cancellationToken).Wait();
        }

        return Task.CompletedTask;
    }

    public Task DeleteEntityAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // Delete entity
            if (_entities.Remove(id))
            {
                _logger.LogDebug("Deleted entity: {Id}", id);
            }

            // Delete all relationships involving this entity
            var toDelete = _relationships.Values
                .Where(r => r.FromId == id || r.ToId == id)
                .Select(r => r.Id)
                .Where(relId => relId != null)
                .Cast<string>()
                .ToList();

            foreach (var relId in toDelete)
            {
                _relationships.Remove(relId);
            }

            if (toDelete.Count > 0)
            {
                _logger.LogDebug("Deleted {Count} relationships for entity {Id}", toDelete.Count, id);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GraphEntity>> SearchEntitiesAsync(
        string? type = null,
        Dictionary<string, object>? filters = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var query = _entities.Values.AsEnumerable();

            // Filter by type
            if (!string.IsNullOrEmpty(type))
            {
                query = query.Where(e => e.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
            }

            // Filter by properties
            if (filters != null && filters.Count > 0)
            {
                query = query.Where(e =>
                    filters.All(f =>
                        e.Properties.TryGetValue(f.Key, out var value) &&
                        value?.Equals(f.Value) == true));
            }

            var results = query.Take(limit).ToList();
            _logger.LogDebug("Search found {Count} entities (type={Type}, filters={FilterCount})",
                results.Count, type ?? "any", filters?.Count ?? 0);

            return Task.FromResult<IReadOnlyList<GraphEntity>>(results);
        }
    }

    // ========================================
    // Relationship Operations
    // ========================================

    public Task SaveRelationshipAsync(
        GraphRelationship relationship,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // Verify entities exist
            if (!_entities.ContainsKey(relationship.FromId))
            {
                throw new InvalidOperationException($"Source entity not found: {relationship.FromId}");
            }

            if (!_entities.ContainsKey(relationship.ToId))
            {
                throw new InvalidOperationException($"Target entity not found: {relationship.ToId}");
            }

            // Auto-generate ID if not provided
            if (string.IsNullOrEmpty(relationship.Id))
            {
                relationship.Id = $"rel_{Guid.NewGuid():N}";
            }

            relationship.CreatedAt = DateTimeOffset.UtcNow;
            _relationships[relationship.Id] = relationship;

            _logger.LogDebug("Saved relationship: {FromId} --[{Type}]--> {ToId}",
                relationship.FromId, relationship.Type, relationship.ToId);
        }

        return Task.CompletedTask;
    }

    public Task SaveRelationshipsAsync(
        IEnumerable<GraphRelationship> relationships,
        CancellationToken cancellationToken = default)
    {
        foreach (var relationship in relationships)
        {
            SaveRelationshipAsync(relationship, cancellationToken).Wait();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GraphRelationship>> GetRelationshipsAsync(
        string entityId,
        RelationshipDirection direction = RelationshipDirection.Both,
        string[]? relationshipTypes = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var query = _relationships.Values.AsEnumerable();

            // Filter by direction
            query = direction switch
            {
                RelationshipDirection.Outgoing => query.Where(r => r.FromId == entityId),
                RelationshipDirection.Incoming => query.Where(r => r.ToId == entityId),
                RelationshipDirection.Both => query.Where(r => r.FromId == entityId || r.ToId == entityId),
                _ => query
            };

            // Filter by type
            if (relationshipTypes != null && relationshipTypes.Length > 0)
            {
                var types = new HashSet<string>(relationshipTypes, StringComparer.OrdinalIgnoreCase);
                query = query.Where(r => types.Contains(r.Type));
            }

            var results = query.ToList();
            return Task.FromResult<IReadOnlyList<GraphRelationship>>(results);
        }
    }

    public Task DeleteRelationshipAsync(string relationshipId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_relationships.Remove(relationshipId))
            {
                _logger.LogDebug("Deleted relationship: {Id}", relationshipId);
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteRelationshipsAsync(
        string fromId,
        string toId,
        string? relationshipType = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var toDelete = _relationships.Values
                .Where(r => r.FromId == fromId && r.ToId == toId)
                .Where(r => relationshipType == null || r.Type.Equals(relationshipType, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Id)
                .Where(id => id != null)
                .Cast<string>()
                .ToList();

            foreach (var id in toDelete)
            {
                _relationships.Remove(id);
            }

            _logger.LogDebug("Deleted {Count} relationships from {FromId} to {ToId}",
                toDelete.Count, fromId, toId);
        }

        return Task.CompletedTask;
    }

    // ========================================
    // Graph Traversal (BFS)
    // ========================================

    public Task<IReadOnlyList<GraphTraversalResult>> TraverseAsync(
        string startEntityId,
        GraphTraversalOptions options,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_entities.ContainsKey(startEntityId))
            {
                throw new InvalidOperationException($"Start entity not found: {startEntityId}");
            }

            var results = new List<GraphTraversalResult>();
            var visited = new HashSet<string>();
            var queue = new Queue<(string EntityId, int Distance, List<GraphRelationship> Path)>();

            // Add start entity if requested
            if (options.IncludeStartEntity)
            {
                results.Add(new GraphTraversalResult
                {
                    Entity = _entities[startEntityId],
                    Distance = 0,
                    Path = new List<GraphRelationship>()
                });
            }

            queue.Enqueue((startEntityId, 0, new List<GraphRelationship>()));
            visited.Add(startEntityId);

            // BFS traversal
            while (queue.Count > 0 && results.Count < options.Limit)
            {
                var (currentId, distance, path) = queue.Dequeue();

                if (distance >= options.MaxHops)
                {
                    continue;
                }

                // Get relationships based on direction
                var relationships = options.Direction switch
                {
                    RelationshipDirection.Outgoing => _relationships.Values.Where(r => r.FromId == currentId),
                    RelationshipDirection.Incoming => _relationships.Values.Where(r => r.ToId == currentId),
                    RelationshipDirection.Both => _relationships.Values.Where(r => r.FromId == currentId || r.ToId == currentId),
                    _ => Enumerable.Empty<GraphRelationship>()
                };

                // Filter by relationship types
                if (options.RelationshipTypes != null && options.RelationshipTypes.Length > 0)
                {
                    var types = new HashSet<string>(options.RelationshipTypes, StringComparer.OrdinalIgnoreCase);
                    relationships = relationships.Where(r => types.Contains(r.Type));
                }

                foreach (var rel in relationships)
                {
                    var nextId = rel.FromId == currentId ? rel.ToId : rel.FromId;

                    if (visited.Contains(nextId))
                    {
                        continue;
                    }

                    if (!_entities.TryGetValue(nextId, out var nextEntity))
                    {
                        continue;
                    }

                    // Filter by entity type
                    if (options.EntityTypes != null && options.EntityTypes.Length > 0 &&
                        !options.EntityTypes.Contains(nextEntity.Type, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Filter by properties
                    if (options.PropertyFilters != null && options.PropertyFilters.Count > 0)
                    {
                        var matches = options.PropertyFilters.All(f =>
                            nextEntity.Properties.TryGetValue(f.Key, out var value) &&
                            value?.Equals(f.Value) == true);

                        if (!matches)
                        {
                            continue;
                        }
                    }

                    visited.Add(nextId);

                    var newPath = new List<GraphRelationship>(path) { rel };
                    results.Add(new GraphTraversalResult
                    {
                        Entity = nextEntity,
                        Distance = distance + 1,
                        Path = newPath
                    });

                    queue.Enqueue((nextId, distance + 1, newPath));
                }
            }

            _logger.LogDebug("Traversal from {StartId} found {Count} entities in {MaxHops} hops",
                startEntityId, results.Count, options.MaxHops);

            return Task.FromResult<IReadOnlyList<GraphTraversalResult>>(results);
        }
    }

    public Task<GraphPath?> FindShortestPathAsync(
        string fromId,
        string toId,
        int maxHops = 5,
        string[]? relationshipTypes = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_entities.ContainsKey(fromId) || !_entities.ContainsKey(toId))
            {
                return Task.FromResult<GraphPath?>(null);
            }

            // BFS to find shortest path
            var queue = new Queue<(string CurrentId, List<string> EntityPath, List<GraphRelationship> RelPath)>();
            var visited = new HashSet<string>();

            queue.Enqueue((fromId, new List<string> { fromId }, new List<GraphRelationship>()));
            visited.Add(fromId);

            while (queue.Count > 0)
            {
                var (currentId, entityPath, relPath) = queue.Dequeue();

                if (relPath.Count >= maxHops)
                {
                    continue;
                }

                var relationships = _relationships.Values
                    .Where(r => r.FromId == currentId || r.ToId == currentId);

                if (relationshipTypes != null && relationshipTypes.Length > 0)
                {
                    var types = new HashSet<string>(relationshipTypes, StringComparer.OrdinalIgnoreCase);
                    relationships = relationships.Where(r => types.Contains(r.Type));
                }

                foreach (var rel in relationships)
                {
                    var nextId = rel.FromId == currentId ? rel.ToId : rel.FromId;

                    if (visited.Contains(nextId))
                    {
                        continue;
                    }

                    visited.Add(nextId);

                    var newEntityPath = new List<string>(entityPath) { nextId };
                    var newRelPath = new List<GraphRelationship>(relPath) { rel };

                    // Found target!
                    if (nextId == toId)
                    {
                        return Task.FromResult<GraphPath?>(new GraphPath
                        {
                            FromId = fromId,
                            ToId = toId,
                            Entities = newEntityPath.Select(id => _entities[id]).ToList(),
                            Relationships = newRelPath
                        });
                    }

                    queue.Enqueue((nextId, newEntityPath, newRelPath));
                }
            }

            // No path found
            return Task.FromResult<GraphPath?>(null);
        }
    }

    // ========================================
    // Utility Operations
    // ========================================

    public Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var stats = new GraphStatistics
            {
                EntityCount = _entities.Count,
                RelationshipCount = _relationships.Count
            };

            // Count by entity type
            foreach (var entity in _entities.Values)
            {
                if (!stats.EntityCountByType.ContainsKey(entity.Type))
                {
                    stats.EntityCountByType[entity.Type] = 0;
                }

                stats.EntityCountByType[entity.Type]++;
            }

            // Count by relationship type
            foreach (var rel in _relationships.Values)
            {
                if (!stats.RelationshipCountByType.ContainsKey(rel.Type))
                {
                    stats.RelationshipCountByType[rel.Type] = 0;
                }

                stats.RelationshipCountByType[rel.Type]++;
            }

            return Task.FromResult(stats);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var entityCount = _entities.Count;
            var relationshipCount = _relationships.Count;

            _entities.Clear();
            _relationships.Clear();

            _logger.LogWarning("Cleared graph store: {EntityCount} entities, {RelationshipCount} relationships",
                entityCount, relationshipCount);
        }

        return Task.CompletedTask;
    }
}
