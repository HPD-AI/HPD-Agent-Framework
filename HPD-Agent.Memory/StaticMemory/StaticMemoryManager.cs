using System;
// Allow file IO in runtime code (not analyzer context)
#pragma warning disable RS1035 // File IO allowed
// AOT and trimming compatible via source-gen for JSON
#pragma warning disable IL2026, IL3050
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using HPD.Agent.TextExtraction;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HPD.Agent.Memory;

/// <summary>
/// CRUD manager for agent knowledge documents with text extraction integration.
/// Manages an agent's static, read-only knowledge base (e.g., Python docs, design patterns).
/// Adapted from ProjectDocumentManager for agent-level use.
/// </summary>
public class StaticMemoryManager
{
    private readonly string _storageDirectory;
    private readonly TextExtractionUtility _textExtractor;
    private readonly ILogger<StaticMemoryManager>? _logger;
    private readonly List<Action> _invalidationCallbacks = new();
    private readonly object _fileLock = new();

    /// <summary>Current agent context (agent name) for scoping knowledge storage</summary>
    public string? CurrentAgentName { get; private set; }

    public StaticMemoryManager(
        string storageDirectory,
        TextExtractionUtility textExtractor,
        ILogger<StaticMemoryManager>? logger = null)
    {
        _storageDirectory = Path.GetFullPath(storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        _textExtractor = textExtractor ?? throw new ArgumentNullException(nameof(textExtractor));
        _logger = logger;
    }

    public void SetAgentContext(string? agentName)
    {
        CurrentAgentName = agentName;
    }

    public void ClearAgentContext() => CurrentAgentName = null;

    public void RegisterCacheInvalidationCallback(Action invalidateCallback)
    {
        _invalidationCallbacks.Add(invalidateCallback);
    }

    public async Task<List<StaticMemoryDocument>> GetDocumentsAsync(CancellationToken cancellationToken = default)
    {
        var file = GetFilePath();
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
            _logger?.LogWarning(ex, "Failed to read agent knowledge documents from {File}", file);
            return new List<StaticMemoryDocument>();
        }
    }

    public async Task<StaticMemoryDocument?> GetDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var documents = await GetDocumentsAsync(cancellationToken);
        return documents.FirstOrDefault(d => d.Id == documentId);
    }

    public async Task<StaticMemoryDocument> AddDocumentAsync(
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

        var documents = await GetDocumentsAsync(cancellationToken);
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

        documents.Add(document);
        SaveDocuments(documents);
        InvokeInvalidation();

        return document;
    }

    public async Task<StaticMemoryDocument> AddDocumentFromUrlAsync(
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

        var documents = await GetDocumentsAsync(cancellationToken);
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

        documents.Add(document);
        SaveDocuments(documents);
        InvokeInvalidation();

        return document;
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var documents = await GetDocumentsAsync(cancellationToken);
        documents.RemoveAll(d => d.Id == documentId);
        SaveDocuments(documents);
        InvokeInvalidation();
    }

    /// <summary>
    /// Gets combined text from all knowledge documents up to a token limit.
    /// Used for FullTextInjection strategy.
    /// </summary>
    public async Task<string> GetCombinedKnowledgeTextAsync(int maxTokens, CancellationToken cancellationToken = default)
    {
        var documents = await GetDocumentsAsync(cancellationToken);

        if (!documents.Any())
            return string.Empty;

        var combinedText = string.Empty;
        var currentTokens = 0;

        foreach (var doc in documents)
        {
            // Simple token estimation (characters / 4)
            var docTokens = doc.ExtractedText.Length / 4;

            if (currentTokens + docTokens > maxTokens)
                break;

            combinedText += $"\n[KNOWLEDGE: {doc.FileName}]\n{doc.ExtractedText}\n[/KNOWLEDGE]\n";
            currentTokens += docTokens;

            // Update last accessed
            doc.LastAccessed = DateTime.UtcNow;
        }

        if (currentTokens > 0)
        {
            SaveDocuments(documents);
        }

        return combinedText;
    }

    private string GetFilePath()
    {
        var fileName = "agent-knowledge";
        if (!string.IsNullOrEmpty(CurrentAgentName))
        {
            // Sanitize agent name for file system
            var safeAgentName = string.Join("_", CurrentAgentName.Split(Path.GetInvalidFileNameChars()));
            fileName += "_" + safeAgentName;
        }
        return Path.Combine(_storageDirectory, fileName + ".json");
    }

    private void SaveDocuments(List<StaticMemoryDocument> documents)
    {
        var file = GetFilePath();
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
                _logger?.LogError(ex, "Failed to write agent knowledge documents to {File}", file);
            }
        }
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
