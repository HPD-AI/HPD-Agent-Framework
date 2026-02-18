using Microsoft.Extensions.Caching.Memory;

using Microsoft.Extensions.Logging;

using System.Security.Cryptography;

using System.Text;

namespace HPD.Agent.Skills.DocumentStore;



/// <summary>
/// Abstract base class for instruction document stores.
/// Implementers only need to override 5 core methods for storage backend.
/// Base class handles caching, hashing, metadata management, skill linking, version tracking, etc.
/// 
/// ARCHITECTURE NOTE - Separation of Concerns:
/// 
/// This store is AGNOSTIC about WHERE documents come from:
/// - File paths, URLs, or direct content are all handled through UploadFromContentAsync()
/// - The store only cares about WHAT (content) and WHERE (persistence backend)
/// 
/// File path resolution is handled by AgentBuilder because:
/// - AgentBuilder knows the deployment context (working directory, relative paths, etc.)
/// - Store should not depend on Directory.GetCurrentDirectory() or File.Exists()
/// - This keeps storage backends truly agnostic (FileSystem, S3, InMemory, etc.)
/// 
/// Flow:
/// 1. Source generator captures: FilePath, DocumentId, Description from AddDocumentFromFile()
/// 2. AgentBuilder.ProcessSkillDocumentsAsync():
///    - Resolves path using ResolveDocumentPath()
///    - Reads file content with error handling
///    - Calls store.UploadFromContentAsync() with the resolved content
/// 3. Store persists content (backend-specific implementation)
/// </summary>
public abstract class InstructionDocumentStoreBase : IInstructionDocumentStore
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _cacheTTL;
    protected readonly ILogger _logger;

    protected InstructionDocumentStoreBase(
        ILogger logger,
        TimeSpan? cacheTTL = null)
    {
        _logger = logger;
        _cacheTTL = cacheTTL ?? TimeSpan.FromMinutes(5);
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    // ===== ABSTRACT METHODS - Implementers override these =====

    /// <summary>
    /// Read raw content from storage backend
    /// </summary>
    protected abstract Task<string?> ReadContentAsync(
        string documentId,
        CancellationToken ct);

    /// <summary>
    /// Write raw content to storage backend
    /// </summary>
    protected abstract Task WriteContentAsync(
        string documentId,
        string content,
        CancellationToken ct);

    /// <summary>
    /// Check if document exists in storage backend
    /// </summary>
    protected abstract Task<bool> ContentExistsAsync(
        string documentId,
        CancellationToken ct);

    /// <summary>
    /// Write metadata to storage backend
    /// </summary>
    protected abstract Task WriteMetadataAsync(
        string documentId,
        GlobalDocumentInfo metadata,
        CancellationToken ct);

    /// <summary>
    /// Read metadata from storage backend
    /// </summary>
    protected abstract Task<GlobalDocumentInfo?> ReadMetadataAsync(
        string documentId,
        CancellationToken ct);

    // ===== HELPER METHODS =====

    /// <summary>
    /// Calculate SHA256 hash of content for idempotent uploads and version tracking
    /// </summary>
    protected string CalculateContentHash(string content)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    // ===== IINSTRUCTIONDOCUMENTSTORE IMPLEMENTATION =====

    public virtual async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            // Try to check if a test document exists (this should not throw)
            await ContentExistsAsync("__health_check__", ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ===== IDOCUMENTCONTENTSTORE IMPLEMENTATION =====

    public virtual Task UploadFromUrlAsync(
        string documentId,
        DocumentMetadata metadata,
        string url,
        CancellationToken ct = default)
    {
        // TODO: Implement URL download and extraction
        throw new NotImplementedException("URL upload not yet implemented");
    }

    public virtual async Task UploadFromContentAsync(
        string documentId,
        DocumentMetadata metadata,
        string content,
        CancellationToken ct = default)
    {
        var contentHash = CalculateContentHash(content);
        var existing = await ReadMetadataAsync(documentId, ct);

        if (existing != null && existing.ContentHash == contentHash)
        {
            // Same content - skip upload (idempotent)
            _logger.LogDebug(
                "Skipping upload of {DocumentId} - content unchanged (hash: {Hash})",
                documentId, contentHash);
            return;
        }

        if (existing != null && existing.ContentHash != contentHash)
        {
            // Content changed - increment version and warn
            _logger.LogWarning(
                "Updating document {DocumentId} - version {OldVersion} -> {NewVersion}. " +
                "Content hash changed: {OldHash} -> {NewHash}",
                documentId, existing.Version, existing.Version + 1,
                existing.ContentHash, contentHash);

            // Create updated metadata with new version
            var updatedInfo = new GlobalDocumentInfo
            {
                DocumentId = documentId,
                Name = metadata.Name,
                Description = metadata.Description,
                SizeBytes = Encoding.UTF8.GetByteCount(content),
                ContentHash = contentHash,
                Version = existing.Version + 1,  // ← Increment version
                CreatedAt = existing.CreatedAt,   // ← Preserve creation time
                LastModified = DateTime.UtcNow
            };

            await WriteMetadataAsync(documentId, updatedInfo, ct);
        }
        else
        {
            // New document - version 1
            _logger.LogInformation(
                "Uploading new document {DocumentId} (version 1, hash: {Hash})",
                documentId, contentHash);

            var newInfo = new GlobalDocumentInfo
            {
                DocumentId = documentId,
                Name = metadata.Name,
                Description = metadata.Description,
                SizeBytes = Encoding.UTF8.GetByteCount(content),
                ContentHash = contentHash,
                Version = 1,
                CreatedAt = DateTime.UtcNow,
                LastModified = DateTime.UtcNow
            };

            await WriteMetadataAsync(documentId, newInfo, ct);
        }

        // Write content
        await WriteContentAsync(documentId, content, ct);

        // Invalidate cache
        _cache.Remove($"doc:{documentId}");
    }

    public virtual async Task<bool> DocumentExistsAsync(
        string documentId,
        CancellationToken ct = default)
    {
        return await ContentExistsAsync(documentId, ct);
    }

    public virtual async Task DeleteDocumentAsync(
        string documentId,
        CancellationToken ct = default)
    {
        // TODO: Implement deletion (also remove skill links)
        throw new NotImplementedException("Delete not yet implemented");
    }

    public virtual async Task<string?> ReadDocumentAsync(
        string documentId,
        CancellationToken ct = default)
    {
        // Check cache first
        if (_cache.TryGetValue<string>($"doc:{documentId}", out var cached))
        {
            _logger.LogDebug("Cache hit for document {DocumentId}", documentId);
            return cached;
        }

        // Read from storage
        var content = await ReadContentAsync(documentId, ct);

        // Cache for TTL
        if (content != null)
        {
            _cache.Set($"doc:{documentId}", content, _cacheTTL);
            _logger.LogDebug("Cached document {DocumentId} for {TTL}", documentId, _cacheTTL);
        }

        return content;
    }

    // ===== IDOCUMENTMETADATASTORE IMPLEMENTATION =====

    public virtual async Task<GlobalDocumentInfo?> GetDocumentMetadataAsync(
        string documentId,
        CancellationToken ct = default)
    {
        return await ReadMetadataAsync(documentId, ct);
    }

    public virtual Task<List<GlobalDocumentInfo>> ListAllDocumentsAsync(
        CancellationToken ct = default)
    {
        // TODO: Implement listing (storage backend dependent)
        throw new NotImplementedException("ListAll not yet implemented");
    }

    // ===== ISKILLDOCUMENTLINKER IMPLEMENTATION =====

    public virtual Task LinkDocumentToSkillAsync(
        string skillNamespace,
        string documentId,
        SkillDocumentMetadata metadata,
        CancellationToken ct = default)
    {
        // TODO: Implement skill linking (Phase 5)
        throw new NotImplementedException("Skill linking not yet implemented (Phase 5)");
    }

    public virtual Task<List<SkillDocumentReference>> GetSkillDocumentsAsync(
        string skillNamespace,
        CancellationToken ct = default)
    {
        // TODO: Implement skill document retrieval (Phase 5)
        throw new NotImplementedException("Skill document retrieval not yet implemented (Phase 5)");
    }

    public virtual Task<SkillDocument?> ReadSkillDocumentAsync(
        string skillNamespace,
        string documentId,
        CancellationToken ct = default)
    {
        // TODO: Implement skill document reading (Phase 5)
        throw new NotImplementedException("Skill document reading not yet implemented (Phase 5)");
    }

    // ═══════════════════════════════════════════════════════════════════
    // IContentStore Implementation
    // ═══════════════════════════════════════════════════════════════════
    // Note: IInstructionDocumentStore is GLOBAL (not agent-scoped).
    // IContentStore methods operate on the same global document collection.

    /// <inheritdoc />
    public virtual async Task<string> PutAsync(
        string? scope,
        byte[] data,
        string contentType,
        ContentMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        // IInstructionDocumentStore is GLOBAL - scope is always null
        // We ignore the scope parameter for instruction documents

        // Generate document ID
        var documentId = metadata?.Name ?? Guid.NewGuid().ToString("N").Substring(0, 8);

        // Convert to string content
        var content = Encoding.UTF8.GetString(data);

        // Create document metadata
        var docMetadata = new DocumentMetadata
        {
            Name = metadata?.Name ?? documentId,
            Description = metadata?.Description ?? string.Empty
        };

        // Use existing UploadFromContentAsync method
        await UploadFromContentAsync(documentId, docMetadata, content, cancellationToken);

        return documentId;
    }

    /// <inheritdoc />
    public virtual async Task<ContentData?> GetAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default)
    {
        // IInstructionDocumentStore is GLOBAL - scope is always null
        // We ignore the scope parameter for instruction documents

        // Read content
        var content = await ReadContentAsync(contentId, cancellationToken);
        if (content == null)
        {
            return null;
        }

        // Read metadata
        var docMetadata = await ReadMetadataAsync(contentId, cancellationToken);
        if (docMetadata == null)
        {
            return null;
        }

        // Map GlobalDocumentInfo → ContentData
        var data = Encoding.UTF8.GetBytes(content);
        return new ContentData
        {
            Id = docMetadata.DocumentId,
            Data = data,
            ContentType = "text/plain",
            Info = MapToContentInfo(docMetadata)
        };
    }

    /// <inheritdoc />
    public virtual async Task DeleteAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default)
    {
        // IInstructionDocumentStore is GLOBAL - scope is always null
        // We ignore the scope parameter for instruction documents

        // Use existing DeleteDocumentAsync method
        await DeleteDocumentAsync(contentId, cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<ContentInfo>> QueryAsync(
        string? scope = null,
        ContentQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        // IInstructionDocumentStore is GLOBAL - scope is always null
        // We ignore the scope parameter for instruction documents

        // Get all documents
        var allDocuments = await ListAllDocumentsAsync(cancellationToken);

        // Apply filters (Phase 1: ContentType, CreatedAfter, Limit)
        IEnumerable<GlobalDocumentInfo> filtered = allDocuments;

        if (query?.ContentType != null)
        {
            filtered = filtered.Where(d =>
                "text/plain".Equals(query.ContentType, StringComparison.OrdinalIgnoreCase));
        }

        if (query?.CreatedAfter != null)
        {
            filtered = filtered.Where(d => d.CreatedAt >= query.CreatedAfter.Value);
        }

        // Map to ContentInfo
        var results = filtered.Select(MapToContentInfo);

        // Apply limit
        if (query?.Limit != null)
        {
            results = results.Take(query.Limit.Value);
        }

        return results.ToList();
    }

    /// <summary>
    /// Maps GlobalDocumentInfo to ContentInfo.
    /// </summary>
    private static ContentInfo MapToContentInfo(GlobalDocumentInfo document)
    {
        return new ContentInfo
        {
            Id = document.DocumentId,
            Name = document.Name,
            ContentType = "text/plain",
            SizeBytes = document.SizeBytes,
            CreatedAt = document.CreatedAt,
            LastModified = document.LastModified,
            LastAccessed = null, // Instruction documents don't track last accessed
            Origin = ContentSource.System, // Skill docs are system-managed
            Description = document.Description,
            ExtendedMetadata = new Dictionary<string, object>
            {
                ["version"] = document.Version,
                ["contentHash"] = document.ContentHash
            }
        };
    }
}
