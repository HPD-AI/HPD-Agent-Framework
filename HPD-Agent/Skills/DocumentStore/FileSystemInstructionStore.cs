using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace HPD_Agent.Skills.DocumentStore;

/// <summary>
/// Filesystem-based instruction document store.
/// Stores content as text files and metadata as JSON files.
/// Structure: {baseDir}/content/{documentId}.txt and {baseDir}/metadata/{documentId}.json
/// </summary>
public class FileSystemInstructionStore : InstructionDocumentStoreBase
{
    private readonly string _baseDirectory;
    private readonly string _contentDirectory;
    private readonly string _metadataDirectory;

    public FileSystemInstructionStore(
        ILogger logger,
        string baseDirectory,
        TimeSpan? cacheTTL = null)
        : base(logger, cacheTTL)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _contentDirectory = Path.Combine(_baseDirectory, "content");
        _metadataDirectory = Path.Combine(_baseDirectory, "metadata");

        // Ensure directories exist
        Directory.CreateDirectory(_contentDirectory);
        Directory.CreateDirectory(_metadataDirectory);

        _logger.LogInformation(
            "FileSystemInstructionStore initialized at {BaseDirectory}",
            _baseDirectory);
    }

    // ===== ABSTRACT METHOD IMPLEMENTATIONS =====

    protected override async Task<string?> ReadContentAsync(
        string documentId,
        CancellationToken ct)
    {
        var filePath = GetContentPath(documentId);

        if (!File.Exists(filePath))
            return null;

        try
        {
            return await File.ReadAllTextAsync(filePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read content for document {DocumentId}", documentId);
            throw;
        }
    }

    protected override async Task WriteContentAsync(
        string documentId,
        string content,
        CancellationToken ct)
    {
        var filePath = GetContentPath(documentId);
        ValidatePath(filePath);

        try
        {
            await File.WriteAllTextAsync(filePath, content, ct);
            _logger.LogDebug("Wrote content for document {DocumentId}", documentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write content for document {DocumentId}", documentId);
            throw;
        }
    }

    protected override Task<bool> ContentExistsAsync(
        string documentId,
        CancellationToken ct)
    {
        var filePath = GetContentPath(documentId);
        return Task.FromResult(File.Exists(filePath));
    }

    protected override async Task WriteMetadataAsync(
        string documentId,
        GlobalDocumentInfo metadata,
        CancellationToken ct)
    {
        var filePath = GetMetadataPath(documentId);
        ValidatePath(filePath);

        try
        {
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(filePath, json, ct);
            _logger.LogDebug("Wrote metadata for document {DocumentId}", documentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write metadata for document {DocumentId}", documentId);
            throw;
        }
    }

    protected override async Task<GlobalDocumentInfo?> ReadMetadataAsync(
        string documentId,
        CancellationToken ct)
    {
        var filePath = GetMetadataPath(documentId);

        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<GlobalDocumentInfo>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read metadata for document {DocumentId}", documentId);
            throw;
        }
    }

    // ===== HELPER METHODS =====

    private string GetContentPath(string documentId)
    {
        return Path.Combine(_contentDirectory, $"{documentId}.txt");
    }

    private string GetMetadataPath(string documentId)
    {
        return Path.Combine(_metadataDirectory, $"{documentId}.json");
    }

    /// <summary>
    /// Validate path to prevent path traversal attacks
    /// </summary>
    private void ValidatePath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);

        if (!fullPath.StartsWith(_baseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path traversal detected: {filePath} is outside base directory {_baseDirectory}");
        }
    }
}
