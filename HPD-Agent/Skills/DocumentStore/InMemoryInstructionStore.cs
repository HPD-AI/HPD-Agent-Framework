using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace HPD_Agent.Skills.DocumentStore;

/// <summary>
/// In-memory instruction document store for testing.
/// All data is stored in memory dictionaries with no persistence.
/// Fast for unit tests but data is lost on restart.
/// </summary>
public class InMemoryInstructionStore : InstructionDocumentStoreBase
{
    private readonly ConcurrentDictionary<string, string> _content = new();
    private readonly ConcurrentDictionary<string, GlobalDocumentInfo> _metadata = new();
    // Phase 5: Skill-document linking
    private readonly ConcurrentDictionary<string, List<(string DocumentId, SkillDocumentMetadata Metadata)>> _skillLinks = new();

    public InMemoryInstructionStore(
        ILogger logger,
        TimeSpan? cacheTTL = null)
        : base(logger, cacheTTL)
    {
        _logger.LogInformation("InMemoryInstructionStore initialized");
    }

    // ===== ABSTRACT METHOD IMPLEMENTATIONS =====

    protected override Task<string?> ReadContentAsync(
        string documentId,
        CancellationToken ct)
    {
        _content.TryGetValue(documentId, out var content);
        return Task.FromResult(content);
    }

    protected override Task WriteContentAsync(
        string documentId,
        string content,
        CancellationToken ct)
    {
        _content[documentId] = content;
        _logger.LogDebug("Wrote content for document {DocumentId} (in-memory)", documentId);
        return Task.CompletedTask;
    }

    protected override Task<bool> ContentExistsAsync(
        string documentId,
        CancellationToken ct)
    {
        return Task.FromResult(_content.ContainsKey(documentId));
    }

    protected override Task WriteMetadataAsync(
        string documentId,
        GlobalDocumentInfo metadata,
        CancellationToken ct)
    {
        _metadata[documentId] = metadata;
        _logger.LogDebug("Wrote metadata for document {DocumentId} (in-memory)", documentId);
        return Task.CompletedTask;
    }

    protected override Task<GlobalDocumentInfo?> ReadMetadataAsync(
        string documentId,
        CancellationToken ct)
    {
        _metadata.TryGetValue(documentId, out var metadata);
        return Task.FromResult(metadata);
    }

    // ===== PHASE 5: SKILL LINKING METHODS =====

    public override Task<List<GlobalDocumentInfo>> ListAllDocumentsAsync(
        CancellationToken ct = default)
    {
        var allDocs = _metadata.Values.ToList();
        return Task.FromResult(allDocs);
    }

    public override Task LinkDocumentToSkillAsync(
        string skillNamespace,
        string documentId,
        SkillDocumentMetadata metadata,
        CancellationToken ct = default)
    {
        if (!_content.ContainsKey(documentId))
        {
            throw new DocumentNotFoundException(
                $"Cannot link non-existent document '{documentId}' to skill '{skillNamespace}'",
                documentId);
        }

        var links = _skillLinks.GetOrAdd(skillNamespace, _ => new List<(string, SkillDocumentMetadata)>());

        // Remove existing link if any (update)
        lock (links)
        {
            links.RemoveAll(l => l.DocumentId == documentId);
            links.Add((documentId, metadata));
        }

        _logger.LogDebug("Linked document {DocumentId} to skill {SkillNamespace}", documentId, skillNamespace);
        return Task.CompletedTask;
    }

    public override Task<List<SkillDocumentReference>> GetSkillDocumentsAsync(
        string skillNamespace,
        CancellationToken ct = default)
    {
        if (!_skillLinks.TryGetValue(skillNamespace, out var links))
        {
            return Task.FromResult(new List<SkillDocumentReference>());
        }

        List<SkillDocumentReference> references;
        lock (links)
        {
            references = links.Select(link =>
            {
                // Get document name from metadata
                var docMeta = _metadata.GetValueOrDefault(link.DocumentId);
                return new SkillDocumentReference
                {
                    DocumentId = link.DocumentId,
                    Name = docMeta?.Name ?? link.DocumentId,
                    Description = link.Metadata.Description
                };
            }).ToList();
        }

        return Task.FromResult(references);
    }

    public override async Task<SkillDocument?> ReadSkillDocumentAsync(
        string skillNamespace,
        string documentId,
        CancellationToken ct = default)
    {
        // Check if skill has this document linked
        if (!_skillLinks.TryGetValue(skillNamespace, out var links))
        {
            return null;
        }

        (string DocumentId, SkillDocumentMetadata Metadata) linkInfo;
        lock (links)
        {
            var link = links.FirstOrDefault(l => l.DocumentId == documentId);
            if (link == default)
            {
                return null;
            }
            linkInfo = link;
        }

        // Read content and metadata
        var content = await ReadContentAsync(documentId, ct);
        var metadata = await ReadMetadataAsync(documentId, ct);

        if (content == null || metadata == null)
        {
            return null;
        }

        return new SkillDocument
        {
            DocumentId = documentId,
            Name = metadata.Name,
            Description = linkInfo.Metadata.Description,
            Content = content
        };
    }

    // ===== ADDITIONAL METHODS FOR TESTING =====

    /// <summary>
    /// Clear all data (useful for test cleanup)
    /// </summary>
    public void Clear()
    {
        _content.Clear();
        _metadata.Clear();
        _skillLinks.Clear();
        _logger.LogDebug("Cleared all in-memory data");
    }

    /// <summary>
    /// Get count of stored documents (useful for test assertions)
    /// </summary>
    public int DocumentCount => _content.Count;
}
