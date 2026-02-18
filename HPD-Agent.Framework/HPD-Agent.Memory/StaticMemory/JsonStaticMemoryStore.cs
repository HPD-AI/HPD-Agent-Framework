using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HPD.Agent.TextExtraction;
using Microsoft.Extensions.Logging;

namespace HPD.Agent.Memory;

#pragma warning disable RS1035, IL2026, IL3050 // Allow file IO and dynamic JSON code

/// <summary>
/// JSON file-based implementation of StaticMemoryStore.
/// Stores each agent's knowledge documents in a separate JSON file.
/// Includes text extraction capabilities for loading documents from files/URLs.
/// Suitable for production use with persistent storage needs.
/// </summary>
public class JsonStaticMemoryStore : StaticMemoryStore
{
    private readonly string _storageDirectory;
    private readonly TextExtractionUtility _textExtractor;
    private readonly ILogger<JsonStaticMemoryStore>? _logger;
    private readonly List<Action> _invalidationCallbacks = new();
    private readonly object _fileLock = new();

    /// <summary>
    /// Creates a new JSON file-based static memory store.
    /// </summary>
    /// <param name="storageDirectory">Directory where JSON files will be stored</param>
    /// <param name="textExtractor">Text extraction utility for processing documents</param>
    /// <param name="logger">Optional logger for diagnostic messages</param>
    public JsonStaticMemoryStore(
        string storageDirectory,
        TextExtractionUtility textExtractor,
        ILogger<JsonStaticMemoryStore>? logger = null)
    {
        _storageDirectory = Path.GetFullPath(storageDirectory);
        _textExtractor = textExtractor ?? throw new ArgumentNullException(nameof(textExtractor));
        _logger = logger;
        EnsureDirectoryExists();
    }

    /// <summary>
    /// Helper method to add a document from a file path with text extraction.
    /// </summary>
    public async Task<string> AddDocumentFromFileAsync(
        string agentName,
        string filePath,
        string? description = null,
        List<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var extractionResult = await _textExtractor.ExtractTextAsync(filePath, cancellationToken);

        if (!extractionResult.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to extract text from {filePath}: {extractionResult.ErrorMessage}");
        }

        var metadata = new ContentMetadata
        {
            Name = extractionResult.FileName,
            OriginalSource = filePath,
            Description = description,
            Tags = tags?.ToDictionary(t => t, t => "true")
        };

        var data = Encoding.UTF8.GetBytes(extractionResult.ExtractedText ?? string.Empty);
        return await PutAsync(agentName, data, extractionResult.MimeType, metadata, cancellationToken);
    }

    /// <summary>
    /// Helper method to add a document from a URL with text extraction.
    /// </summary>
    public async Task<string> AddDocumentFromUrlAsync(
        string agentName,
        string url,
        string? description = null,
        List<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var extractionResult = await _textExtractor.ExtractTextAsync(url, cancellationToken);

        if (!extractionResult.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to extract text from {url}: {extractionResult.ErrorMessage}");
        }

        var metadata = new ContentMetadata
        {
            Name = extractionResult.FileName,
            OriginalSource = url,
            Description = description,
            Tags = tags?.ToDictionary(t => t, t => "true")
        };

        var data = Encoding.UTF8.GetBytes(extractionResult.ExtractedText ?? string.Empty);
        return await PutAsync(agentName, data, extractionResult.MimeType, metadata, cancellationToken);
    }

    public override async Task<string> GetCombinedKnowledgeTextAsync(string agentName, int maxTokens, CancellationToken cancellationToken = default)
    {
        var documents = await GetDocumentsForAgentAsync(agentName, cancellationToken);

        if (!documents.Any())
            return string.Empty;

        var combinedText = new StringBuilder();
        var currentTokens = 0;

        foreach (var doc in documents)
        {
            // Simple token estimation (characters / 4)
            var docTokens = doc.ExtractedText.Length / 4;

            if (currentTokens + docTokens > maxTokens)
            {
                break;
            }

            combinedText.AppendLine($"\n[KNOWLEDGE: {doc.FileName}]");
            combinedText.AppendLine(doc.ExtractedText);
            combinedText.AppendLine("[/KNOWLEDGE]\n");
            currentTokens += docTokens;

            // Update last accessed
            doc.LastAccessed = DateTime.UtcNow;
        }

        if (currentTokens > 0)
        {
            await SaveDocumentsAsync(agentName, documents, cancellationToken);
        }

        return combinedText.ToString();
    }

    public override void RegisterInvalidationCallback(Action callback)
    {
        lock (_fileLock)
        {
            _invalidationCallbacks.Add(callback);
        }
    }

    public override StaticMemoryStoreSnapshot SerializeToSnapshot()
    {
        // Load all agent knowledge from disk
        var allDocuments = new Dictionary<string, List<StaticMemoryDocument>>();

        if (Directory.Exists(_storageDirectory))
        {
            foreach (var file in Directory.GetFiles(_storageDirectory, "*.json"))
            {
                try
                {
                    var agentName = GetAgentNameFromFile(file);
                    var json = File.ReadAllText(file);
                    var documents = JsonSerializer.Deserialize(
                        json,
                        MemoryJsonContext.Default.ListStaticMemoryDocument) ?? new List<StaticMemoryDocument>();
                    allDocuments[agentName] = documents;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to read knowledge from {File} during serialization", file);
                }
            }
        }

        return new StaticMemoryStoreSnapshot
        {
            StoreType = StaticMemoryStoreType.JsonFile,
            Documents = allDocuments,
            Configuration = new Dictionary<string, object>
            {
                { "StorageDirectory", _storageDirectory }
            }
        };
    }

    /// <summary>
    /// Deserialize a JSON store from a snapshot.
    /// </summary>
    internal new static JsonStaticMemoryStore Deserialize(StaticMemoryStoreSnapshot snapshot)
    {
        // Extract storage directory from configuration
        var storageDirectory = snapshot.Configuration?.GetValueOrDefault("StorageDirectory") as string
            ?? "./agent-static-memory";

        var textExtractor = new TextExtractionUtility();
        var store = new JsonStaticMemoryStore(storageDirectory, textExtractor);

        // Write all documents to disk
        foreach (var (agentName, documents) in snapshot.Documents)
        {
            store.SaveDocumentsAsync(agentName, documents, CancellationToken.None).GetAwaiter().GetResult();
        }

        return store;
    }

    private string GetFilePath(string agentName)
    {
        // Sanitize agent name for file system
        // Replace invalid chars AND spaces with underscores for consistency
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeAgentName = string.Join("_", agentName.Split(invalidChars.Append(' ').ToArray()));
        return Path.Combine(_storageDirectory, $"knowledge_{safeAgentName}.json");
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
        }
    }

    private Task SaveDocumentsAsync(string agentName, List<StaticMemoryDocument> documents, CancellationToken cancellationToken)
    {
        EnsureDirectoryExists();
        var file = GetFilePath(agentName);

        lock (_fileLock)
        {
            try
            {
                using var stream = File.Create(file);
                JsonSerializer.Serialize(
                    stream,
                    documents,
                    MemoryJsonContext.Default.ListStaticMemoryDocument);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to write knowledge documents to {File}", file);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    private async Task<List<StaticMemoryDocument>> GetDocumentsForAgentAsync(string agentName, CancellationToken cancellationToken = default)
    {
        var file = GetFilePath(agentName);

        if (!File.Exists(file))
        {
            return new List<StaticMemoryDocument>();
        }

        try
        {
            using var stream = File.OpenRead(file);
            var documents = await JsonSerializer.DeserializeAsync(
                stream,
                MemoryJsonContext.Default.ListStaticMemoryDocument,
                cancellationToken: cancellationToken)
                ?? new List<StaticMemoryDocument>();
            return documents.OrderByDescending(d => d.LastAccessed).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read knowledge documents from {File}", file);
            return new List<StaticMemoryDocument>();
        }
    }

    private void InvokeInvalidation()
    {
        foreach (var cb in _invalidationCallbacks)
        {
            try { cb(); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // IContentStore Implementation (V2)
    // ═══════════════════════════════════════════════════════════════════
    // scope = agentName for StaticMemoryStore
    // If scope is null in QueryAsync, query across ALL agents

    /// <inheritdoc />
    public override async Task<string> PutAsync(
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

        var documents = await GetDocumentsForAgentAsync(agentName, cancellationToken);
        documents.Add(document);
        await SaveDocumentsAsync(agentName, documents, cancellationToken);
        InvokeInvalidation();

        return document.Id;
    }

    /// <inheritdoc />
    public override async Task<ContentData?> GetAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default)
    {
        // If scope is provided, search only within that agent's documents
        if (scope != null)
        {
            var documents = await GetDocumentsForAgentAsync(scope, cancellationToken);
            var document = documents.FirstOrDefault(d => d.Id == contentId);
            if (document != null)
            {
                document.LastAccessed = DateTime.UtcNow;
                await SaveDocumentsAsync(scope, documents, cancellationToken);
                return MapToContentData(document);
            }
            return null;
        }

        // If scope is null, search across ALL agent files
        if (!Directory.Exists(_storageDirectory))
            return null;

        foreach (var file in Directory.GetFiles(_storageDirectory, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var documents = await JsonSerializer.DeserializeAsync(
                    stream,
                    MemoryJsonContext.Default.ListStaticMemoryDocument,
                    cancellationToken: cancellationToken)
                    ?? new List<StaticMemoryDocument>();

                var document = documents.FirstOrDefault(d => d.Id == contentId);
                if (document != null)
                {
                    // Update last accessed
                    document.LastAccessed = DateTime.UtcNow;
                    var agentName = GetAgentNameFromFile(file);
                    await SaveDocumentsAsync(agentName, documents, cancellationToken);
                    return MapToContentData(document);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read {File} during GetAsync", file);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public override async Task DeleteAsync(
        string? scope,
        string contentId,
        CancellationToken cancellationToken = default)
    {
        // If scope is provided, delete only within that agent's documents
        if (scope != null)
        {
            var documents = await GetDocumentsForAgentAsync(scope, cancellationToken);
            var removed = documents.RemoveAll(d => d.Id == contentId);
            if (removed > 0)
            {
                await SaveDocumentsAsync(scope, documents, cancellationToken);
                InvokeInvalidation();
            }
            return;
        }

        // If scope is null, search across ALL agent files and delete
        if (!Directory.Exists(_storageDirectory))
            return;

        foreach (var file in Directory.GetFiles(_storageDirectory, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var documents = await JsonSerializer.DeserializeAsync(
                    stream,
                    MemoryJsonContext.Default.ListStaticMemoryDocument,
                    cancellationToken: cancellationToken)
                    ?? new List<StaticMemoryDocument>();
                stream.Close();

                var removed = documents.RemoveAll(d => d.Id == contentId);
                if (removed > 0)
                {
                    var agentName = GetAgentNameFromFile(file);
                    await SaveDocumentsAsync(agentName, documents, cancellationToken);
                    InvokeInvalidation();
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to process {File} during DeleteAsync", file);
            }
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ContentInfo>> QueryAsync(
        string? scope = null,
        ContentQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var allDocuments = new List<StaticMemoryDocument>();

        // If scope is provided, query only within that agent's documents
        if (scope != null)
        {
            allDocuments = await GetDocumentsForAgentAsync(scope, cancellationToken);
        }
        else
        {
            // If scope is null, query across ALL agent files
            if (Directory.Exists(_storageDirectory))
            {
                foreach (var file in Directory.GetFiles(_storageDirectory, "*.json"))
                {
                    try
                    {
                        using var stream = File.OpenRead(file);
                        var documents = await JsonSerializer.DeserializeAsync(
                            stream,
                            MemoryJsonContext.Default.ListStaticMemoryDocument,
                            cancellationToken: cancellationToken)
                            ?? new List<StaticMemoryDocument>();
                        allDocuments.AddRange(documents);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to read {File} during QueryAsync", file);
                    }
                }
            }
        }

        // Apply filters
        var filtered = allDocuments.AsEnumerable();

        if (query?.ContentType != null)
        {
            filtered = filtered.Where(d =>
                d.MimeType.Equals(query.ContentType, StringComparison.OrdinalIgnoreCase));
        }

        if (query?.CreatedAfter != null)
        {
            filtered = filtered.Where(d => d.AddedAt >= query.CreatedAfter.Value);
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

    /// <summary>
    /// Extracts agent name from a file path like "knowledge_agentname.json".
    /// </summary>
    private static string GetAgentNameFromFile(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        // Remove "knowledge_" prefix
        return fileName.StartsWith("knowledge_")
            ? fileName.Substring("knowledge_".Length)
            : fileName;
    }
}

#pragma warning restore RS1035, IL2026, IL3050
