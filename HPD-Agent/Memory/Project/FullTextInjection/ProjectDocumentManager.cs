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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProjectCagMemory;

/// <summary>
/// CRUD manager for project documents with text extraction integration
/// </summary>
public class ProjectDocumentManager
{
    private readonly string _storageDirectory;
    private readonly TextExtractionUtility _textExtractor;
    private readonly ILogger<ProjectDocumentManager>? _logger;
    private readonly List<Action> _invalidationCallbacks = new();
    private readonly object _fileLock = new();

    /// <summary>Current project context (project ID)</summary>
    public string? CurrentContext { get; private set; }

    public ProjectDocumentManager(
        string storageDirectory, 
        TextExtractionUtility textExtractor, 
        ILogger<ProjectDocumentManager>? logger = null)
    {
        _storageDirectory = Path.GetFullPath(storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        _textExtractor = textExtractor ?? throw new ArgumentNullException(nameof(textExtractor));
        _logger = logger;
    }

    public void SetContext(string? context)
    {
        CurrentContext = context;
    }

    public void ClearContext() => CurrentContext = null;

    public void RegisterCacheInvalidationCallback(Action invalidateCallback)
    {
        _invalidationCallbacks.Add(invalidateCallback);
    }

    public async Task<List<ProjectDocument>> GetDocumentsAsync(CancellationToken cancellationToken = default)
    {
        var file = GetFilePath();
        if (!File.Exists(file))
        {
            return new List<ProjectDocument>();
        }
        
        try
        {
            using var stream = File.OpenRead(file);
            var documents = await JsonSerializer.DeserializeAsync(
                stream,
                ProjectDocumentJsonContext.Default.ListProjectDocument,
                cancellationToken: cancellationToken)
                ?? new List<ProjectDocument>();
            return documents.OrderByDescending(d => d.LastAccessed).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read project documents from {File}", file);
            return new List<ProjectDocument>();
        }
    }

    public async Task<ProjectDocument?> GetDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var documents = await GetDocumentsAsync(cancellationToken);
        return documents.FirstOrDefault(d => d.Id == documentId);
    }

    public async Task<ProjectDocument> UploadDocumentAsync(
        string filePath, 
        string? description = null, 
        CancellationToken cancellationToken = default)
    {
        var extractionResult = await _textExtractor.ExtractTextAsync(filePath, cancellationToken);
        
        if (!extractionResult.Success)
        {
            throw new InvalidOperationException($"Failed to extract text from {filePath}: {extractionResult.ErrorMessage}");
        }

        var documents = await GetDocumentsAsync(cancellationToken);
        var now = DateTime.UtcNow;
        
        var document = new ProjectDocument
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
            FileName = extractionResult.FileName,
            OriginalPath = filePath,
            ExtractedText = extractionResult.ExtractedText ?? string.Empty,
            MimeType = extractionResult.MimeType,
            FileSize = extractionResult.FileSize,
            UploadedAt = now,
            LastAccessed = now,
            Description = description ?? string.Empty
        };
        
        documents.Add(document);
        SaveDocuments(documents);
        InvokeInvalidation();
        
        return document;
    }

    public async Task<ProjectDocument> UploadDocumentFromUrlAsync(
        string url, 
        string? description = null, 
        CancellationToken cancellationToken = default)
    {
        var extractionResult = await _textExtractor.ExtractTextAsync(url, cancellationToken);
        
        if (!extractionResult.Success)
        {
            throw new InvalidOperationException($"Failed to extract text from {url}: {extractionResult.ErrorMessage}");
        }

        var documents = await GetDocumentsAsync(cancellationToken);
        var now = DateTime.UtcNow;
        
        var document = new ProjectDocument
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
            FileName = extractionResult.FileName,
            OriginalPath = url,
            ExtractedText = extractionResult.ExtractedText ?? string.Empty,
            MimeType = extractionResult.MimeType,
            FileSize = extractionResult.FileSize,
            UploadedAt = now,
            LastAccessed = now,
            Description = description ?? string.Empty
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

    public async Task<string> GetCombinedDocumentTextAsync(int maxTokens, CancellationToken cancellationToken = default)
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
                
            combinedText += $"\n[DOCUMENT: {doc.FileName}]\n{doc.ExtractedText}\n[/DOCUMENT]\n";
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
        var fileName = "project-documents";
        if (!string.IsNullOrEmpty(CurrentContext))
        {
            fileName += "_" + CurrentContext;
        }
        return Path.Combine(_storageDirectory, fileName + ".json");
    }

    private void SaveDocuments(List<ProjectDocument> documents)
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
                    ProjectDocumentJsonContext.Default.ListProjectDocument);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to write project documents to {File}", file);
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