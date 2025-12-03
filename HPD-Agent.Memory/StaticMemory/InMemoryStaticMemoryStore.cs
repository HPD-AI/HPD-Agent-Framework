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

    public override Task<List<StaticMemoryDocument>> GetDocumentsAsync(string agentName, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_documents.TryGetValue(agentName, out var documents))
            {
                return Task.FromResult(new List<StaticMemoryDocument>());
            }

            // Return copy, sorted by LastAccessed (most recent first)
            return Task.FromResult(documents.OrderByDescending(d => d.LastAccessed).ToList());
        }
    }

    public override Task<StaticMemoryDocument?> GetDocumentAsync(string agentName, string documentId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_documents.TryGetValue(agentName, out var documents))
            {
                return Task.FromResult<StaticMemoryDocument?>(null);
            }

            var document = documents.FirstOrDefault(d => d.Id == documentId);
            return Task.FromResult(document);
        }
    }

    public override Task<StaticMemoryDocument> AddDocumentAsync(string agentName, StaticMemoryDocument document, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_documents.TryGetValue(agentName, out var documents))
            {
                documents = new List<StaticMemoryDocument>();
                _documents[agentName] = documents;
            }

            documents.Add(document);
            InvokeInvalidationCallbacks();

            return Task.FromResult(document);
        }
    }

    public override Task DeleteDocumentAsync(string agentName, string documentId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_documents.TryGetValue(agentName, out var documents))
            {
                documents.RemoveAll(d => d.Id == documentId);
                InvokeInvalidationCallbacks();
            }

            return Task.CompletedTask;
        }
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
}
