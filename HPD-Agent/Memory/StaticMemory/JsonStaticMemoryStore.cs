using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HPD_Agent.TextExtraction;
using Microsoft.Extensions.Logging;

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

    public override async Task<List<StaticMemoryDocument>> GetDocumentsAsync(string agentName, CancellationToken cancellationToken = default)
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
                StaticMemoryJsonContext.Default.ListStaticMemoryDocument,
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

    public override async Task<StaticMemoryDocument?> GetDocumentAsync(string agentName, string documentId, CancellationToken cancellationToken = default)
    {
        var documents = await GetDocumentsAsync(agentName, cancellationToken);
        return documents.FirstOrDefault(d => d.Id == documentId);
    }

    public override async Task<StaticMemoryDocument> AddDocumentAsync(string agentName, StaticMemoryDocument document, CancellationToken cancellationToken = default)
    {
        var documents = await GetDocumentsAsync(agentName, cancellationToken);
        documents.Add(document);
        await SaveDocumentsAsync(agentName, documents, cancellationToken);
        InvokeInvalidation();
        return document;
    }

    /// <summary>
    /// Helper method to add a document from a file path with text extraction.
    /// </summary>
    public async Task<StaticMemoryDocument> AddDocumentFromFileAsync(
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

        var now = DateTime.UtcNow;
        var document = new StaticMemoryDocument
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
            FileName = extractionResult.FileName,
            OriginalPath = filePath,
            ExtractedText = extractionResult.ExtractedText ?? string.Empty,
            MimeType = extractionResult.MimeType,
            FileSize = extractionResult.FileSizeBytes,
            AddedAt = now,
            LastAccessed = now,
            Description = description ?? string.Empty,
            Tags = tags ?? new List<string>()
        };

        return await AddDocumentAsync(agentName, document, cancellationToken);
    }

    /// <summary>
    /// Helper method to add a document from a URL with text extraction.
    /// </summary>
    public async Task<StaticMemoryDocument> AddDocumentFromUrlAsync(
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

        var now = DateTime.UtcNow;
        var document = new StaticMemoryDocument
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
            FileName = extractionResult.FileName,
            OriginalPath = url,
            ExtractedText = extractionResult.ExtractedText ?? string.Empty,
            MimeType = extractionResult.MimeType,
            FileSize = extractionResult.FileSizeBytes,
            AddedAt = now,
            LastAccessed = now,
            Description = description ?? string.Empty,
            Tags = tags ?? new List<string>()
        };

        return await AddDocumentAsync(agentName, document, cancellationToken);
    }

    public override async Task DeleteDocumentAsync(string agentName, string documentId, CancellationToken cancellationToken = default)
    {
        var documents = await GetDocumentsAsync(agentName, cancellationToken);
        documents.RemoveAll(d => d.Id == documentId);
        await SaveDocumentsAsync(agentName, documents, cancellationToken);
        InvokeInvalidation();
    }

    public override async Task<string> GetCombinedKnowledgeTextAsync(string agentName, int maxTokens, CancellationToken cancellationToken = default)
    {
        var documents = await GetDocumentsAsync(agentName, cancellationToken);

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
                    var agentName = Path.GetFileNameWithoutExtension(file);
                    var json = File.ReadAllText(file);
                    var documents = JsonSerializer.Deserialize(
                        json,
                        StaticMemoryJsonContext.Default.ListStaticMemoryDocument) ?? new List<StaticMemoryDocument>();
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
    internal static JsonStaticMemoryStore Deserialize(StaticMemoryStoreSnapshot snapshot)
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
                    StaticMemoryJsonContext.Default.ListStaticMemoryDocument);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to write knowledge documents to {File}", file);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    private void InvokeInvalidation()
    {
        foreach (var cb in _invalidationCallbacks)
        {
            try { cb(); } catch { }
        }
    }
}

#pragma warning restore RS1035, IL2026, IL3050
