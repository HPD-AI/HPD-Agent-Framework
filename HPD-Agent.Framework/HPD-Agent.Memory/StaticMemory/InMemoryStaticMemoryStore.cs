using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HPD.Agent.Memory;

/// <summary>
/// In-memory implementation of StaticMemoryStore.
/// Stores all knowledge documents in a dictionary for fast access.
/// Suitable for development, testing, or scenarios where persistence is not required.
/// </summary>
public class InMemoryStaticMemoryStore : StaticMemoryStore
{
    private readonly Dictionary<string, List<StaticMemoryDocument>> _documents = new();
    private readonly List<Action> _invalidationCallbacks = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new in-memory static memory store.
    /// </summary>
    public InMemoryStaticMemoryStore()
    {
    }

    /// <summary>
    /// Creates an in-memory store with pre-loaded documents (for deserialization).
    /// </summary>
    private InMemoryStaticMemoryStore(Dictionary<string, List<StaticMemoryDocument>> documents)
    {
        _documents = documents;
    }

    public override Task<string> GetCombinedKnowledgeTextAsync(string agentName, int maxTokens, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_documents.TryGetValue(agentName, out var documents) || !documents.Any())
            {
                return Task.FromResult(string.Empty);
            }

            var combinedText = new StringBuilder();
            var currentTokens = 0;

            foreach (var doc in documents.OrderByDescending(d => d.LastAccessed))
            {
                // Simple token estimation (characters / 4)
                var docTokens = doc.ExtractedText.Length / 4;

                if (currentTokens + docTokens > maxTokens)
                    break;

                combinedText.AppendLine($"\n[KNOWLEDGE: {doc.FileName}]");
                combinedText.AppendLine(doc.ExtractedText);
                combinedText.AppendLine("[/KNOWLEDGE]\n");
                currentTokens += docTokens;

                // Update last accessed
                doc.LastAccessed = DateTime.UtcNow;
            }

            return Task.FromResult(combinedText.ToString());
        }
    }

    public override void RegisterInvalidationCallback(Action callback)
    {
        lock (_lock)
        {
            _invalidationCallbacks.Add(callback);
        }
    }

    public override StaticMemoryStoreSnapshot SerializeToSnapshot()
    {
        lock (_lock)
        {
            // Deep copy to avoid modification during serialization
            var documentsCopy = _documents.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToList()
            );

            return new StaticMemoryStoreSnapshot
            {
                StoreType = StaticMemoryStoreType.InMemory,
                Documents = documentsCopy
            };
        }
    }

    /// <summary>
    /// Deserialize an in-memory store from a snapshot.
    /// </summary>
    internal new static InMemoryStaticMemoryStore Deserialize(StaticMemoryStoreSnapshot snapshot)
    {
        // Deep copy from snapshot
        var documents = snapshot.Documents.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToList()
        );

        return new InMemoryStaticMemoryStore(documents);
    }

    private void InvokeInvalidationCallbacks()
    {
        foreach (var callback in _invalidationCallbacks)
        {
            try
            {
                callback();
            }
            catch
            {
                // Ignore callback errors
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // IContentStore Implementation (V2)
    // ═══════════════════════════════════════════════════════════════════
    // scope = agentName for StaticMemoryStore
    // If scope is null in QueryAsync, query across ALL agents

    /// <inheritdoc />
    public override Task<string> PutAsync(
        string? scope,
        byte[] data,
        string contentType,
        ContentMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var agentName = scope ?? throw new ArgumentNullException(nameof(scope), "Scope (agentName) is required for StaticMemoryStore.PutAsync");

        var document = new StaticMemoryDocument
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
            FileName = metadata?.Name ?? $"content-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            OriginalPath = metadata?.OriginalSource ?? string.Empty,
            ExtractedText = Encoding.UTF8.GetString(data),
            MimeType = contentType,
            FileSize = data.Length,
            AddedAt = DateTime.UtcNow,
            LastAccessed = DateTime.UtcNow,
            Description = metadata?.Description ?? string.Empty,
            Tags = metadata?.Tags?.Keys.ToList() ?? new List<string>()
        };

        lock (_lock)
        {
            if (!_documents.TryGetValue(agentName, out var documents))
            {
                documents = new List<StaticMemoryDocument>();
                _documents[agentName] = documents;
            }

            documents.Add(document);
            InvokeInvalidationCallbacks();

            return Task.FromResult(document.Id);
        }
    }

    /// <inheritdoc />
    public override Task<ContentData?> GetAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // If scope is provided, search only within that agent's documents
            if (scope != null)
            {
                if (_documents.TryGetValue(scope, out var documents))
                {
                    var document = documents.FirstOrDefault(d => d.Id == contentId);
                    if (document != null)
                    {
                        document.LastAccessed = DateTime.UtcNow;
                        return Task.FromResult<ContentData?>(MapToContentData(document));
                    }
                }
                return Task.FromResult<ContentData?>(null);
            }

            // If scope is null, search across ALL agents
            foreach (var (agentName, documents) in _documents)
            {
                var document = documents.FirstOrDefault(d => d.Id == contentId);
                if (document != null)
                {
                    document.LastAccessed = DateTime.UtcNow;
                    return Task.FromResult<ContentData?>(MapToContentData(document));
                }
            }

            return Task.FromResult<ContentData?>(null);
        }
    }

    /// <inheritdoc />
    public override Task DeleteAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // If scope is provided, delete only within that agent's documents
            if (scope != null)
            {
                if (_documents.TryGetValue(scope, out var documents))
                {
                    var removed = documents.RemoveAll(d => d.Id == contentId);
                    if (removed > 0)
                    {
                        InvokeInvalidationCallbacks();
                    }
                }
                return Task.CompletedTask;
            }

            // If scope is null, search across ALL agents and delete
            foreach (var (agentName, documents) in _documents)
            {
                var removed = documents.RemoveAll(d => d.Id == contentId);
                if (removed > 0)
                {
                    InvokeInvalidationCallbacks();
                    break;
                }
            }

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<ContentInfo>> QueryAsync(
        string? scope = null,
        ContentQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // If scope is provided, query only within that agent's documents
            IEnumerable<StaticMemoryDocument> allDocuments;

            if (scope != null)
            {
                allDocuments = _documents.TryGetValue(scope, out var documents)
                    ? documents
                    : Enumerable.Empty<StaticMemoryDocument>();
            }
            else
            {
                // If scope is null, query across ALL agents
                allDocuments = _documents.Values.SelectMany(docs => docs);
            }

            // Apply filters
            if (query?.ContentType != null)
            {
                allDocuments = allDocuments.Where(d =>
                    d.MimeType.Equals(query.ContentType, StringComparison.OrdinalIgnoreCase));
            }

            if (query?.CreatedAfter != null)
            {
                allDocuments = allDocuments.Where(d => d.AddedAt >= query.CreatedAfter.Value);
            }

            // Map to ContentInfo
            var results = allDocuments.Select(MapToContentInfo);

            // Apply limit
            if (query?.Limit != null)
            {
                results = results.Take(query.Limit.Value);
            }

            return Task.FromResult<IReadOnlyList<ContentInfo>>(results.ToList());
        }
    }

    /// <summary>
    /// Maps StaticMemoryDocument to ContentData.
    /// </summary>
    private static ContentData MapToContentData(StaticMemoryDocument document)
    {
        var data = Encoding.UTF8.GetBytes(document.ExtractedText);
        return new ContentData
        {
            Id = document.Id,
            Data = data,
            ContentType = document.MimeType,
            Info = MapToContentInfo(document)
        };
    }

    /// <summary>
    /// Maps StaticMemoryDocument to ContentInfo.
    /// </summary>
    private static ContentInfo MapToContentInfo(StaticMemoryDocument document)
    {
        return new ContentInfo
        {
            Id = document.Id,
            Name = document.FileName,
            ContentType = document.MimeType,
            SizeBytes = document.FileSize,
            CreatedAt = document.AddedAt,
            LastModified = null, // StaticMemory doesn't track modifications
            LastAccessed = document.LastAccessed,
            Origin = ContentSource.User, // Implicit: all static memory is user-uploaded
            Description = document.Description,
            Tags = document.Tags.Any()
                ? document.Tags.ToDictionary(t => t, t => "true")
                : null,
            OriginalSource = document.OriginalPath,
            ExtendedMetadata = new Dictionary<string, object>
            {
                ["extractedTextLength"] = document.ExtractedTextLength
            }
        };
    }
}
