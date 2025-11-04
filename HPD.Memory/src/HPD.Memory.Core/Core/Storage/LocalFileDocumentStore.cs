// Copyright (c) Einstein Essibu. All rights reserved.
// Simple local file system implementation of IDocumentStore.

using HPDAgent.Memory.Abstractions.Storage;
using Microsoft.Extensions.Logging;

namespace HPDAgent.Memory.Core.Storage;

/// <summary>
/// Simple local file system implementation of IDocumentStore.
/// Stores files in a directory structure: {basePath}/{index}/{pipelineId}/{fileName}
/// Good for development, testing, and small deployments.
/// For production, use Azure Blob Storage or S3 implementations.
/// </summary>
public class LocalFileDocumentStore : IDocumentStore
{
    private readonly string _basePath;
    private readonly ILogger<LocalFileDocumentStore> _logger;

    /// <summary>
    /// Create a new local file document store.
    /// </summary>
    /// <param name="basePath">Base directory path for file storage</param>
    /// <param name="logger">Logger</param>
    public LocalFileDocumentStore(string basePath, ILogger<LocalFileDocumentStore> logger)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Ensure base directory exists
        Directory.CreateDirectory(_basePath);

        _logger.LogInformation("LocalFileDocumentStore initialized at: {BasePath}", _basePath);
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadFileAsync(
        string index,
        string pipelineId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(index, pipelineId, fileName);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {fileName}", filePath);
        }

        _logger.LogDebug("Reading file: {FilePath}", filePath);
        return await File.ReadAllBytesAsync(filePath, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> ReadTextFileAsync(
        string index,
        string pipelineId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(index, pipelineId, fileName);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {fileName}", filePath);
        }

        _logger.LogDebug("Reading text file: {FilePath}", filePath);
        return await File.ReadAllTextAsync(filePath, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Stream> ReadFileStreamAsync(
        string index,
        string pipelineId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(index, pipelineId, fileName);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {fileName}", filePath);
        }

        _logger.LogDebug("Opening file stream: {FilePath}", filePath);
        return Task.FromResult<Stream>(File.OpenRead(filePath));
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(
        string index,
        string pipelineId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(index, pipelineId, fileName);
        EnsureDirectoryExists(filePath);

        _logger.LogDebug("Writing file ({Size} bytes): {FilePath}", content.Length, filePath);
        await File.WriteAllBytesAsync(filePath, content, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteTextFileAsync(
        string index,
        string pipelineId,
        string fileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(index, pipelineId, fileName);
        EnsureDirectoryExists(filePath);

        _logger.LogDebug("Writing text file ({Length} chars): {FilePath}", content.Length, filePath);
        await File.WriteAllTextAsync(filePath, content, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteFileStreamAsync(
        string index,
        string pipelineId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(index, pipelineId, fileName);
        EnsureDirectoryExists(filePath);

        _logger.LogDebug("Writing file stream: {FilePath}", filePath);

        using var fileStream = File.Create(filePath);
        await content.CopyToAsync(fileStream, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> FileExistsAsync(
        string index,
        string pipelineId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(index, pipelineId, fileName);
        var exists = File.Exists(filePath);

        _logger.LogDebug("File exists check for {FilePath}: {Exists}", filePath, exists);
        return Task.FromResult(exists);
    }

    /// <inheritdoc />
    public Task DeleteFileAsync(
        string index,
        string pipelineId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(index, pipelineId, fileName);

        if (File.Exists(filePath))
        {
            _logger.LogDebug("Deleting file: {FilePath}", filePath);
            File.Delete(filePath);
        }
        else
        {
            _logger.LogWarning("Attempted to delete non-existent file: {FilePath}", filePath);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListFilesAsync(
        string index,
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        var directoryPath = GetDirectoryPath(index, pipelineId);

        if (!Directory.Exists(directoryPath))
        {
            _logger.LogDebug("Directory does not exist: {DirectoryPath}", directoryPath);
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var files = Directory.GetFiles(directoryPath)
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Cast<string>()
            .ToList();

        _logger.LogDebug("Listed {Count} files in {DirectoryPath}", files.Count, directoryPath);
        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    /// <inheritdoc />
    public Task DeleteAllFilesAsync(
        string index,
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        var directoryPath = GetDirectoryPath(index, pipelineId);

        if (Directory.Exists(directoryPath))
        {
            _logger.LogInformation("Deleting all files in: {DirectoryPath}", directoryPath);
            Directory.Delete(directoryPath, recursive: true);
        }
        else
        {
            _logger.LogDebug("Directory does not exist, nothing to delete: {DirectoryPath}", directoryPath);
        }

        return Task.CompletedTask;
    }

    // ========================================
    // Helper Methods
    // ========================================

    private string GetDirectoryPath(string index, string pipelineId)
    {
        // Sanitize inputs to prevent path traversal attacks
        var safeIndex = SanitizePathSegment(index);
        var safePipelineId = SanitizePathSegment(pipelineId);

        return Path.Combine(_basePath, safeIndex, safePipelineId);
    }

    private string GetFilePath(string index, string pipelineId, string fileName)
    {
        var safeFileName = SanitizePathSegment(fileName);
        return Path.Combine(GetDirectoryPath(index, pipelineId), safeFileName);
    }

    private static void EnsureDirectoryExists(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string SanitizePathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            throw new ArgumentException("Path segment cannot be null or whitespace", nameof(segment));
        }

        // Remove any path traversal attempts and invalid characters
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(segment.Where(c => !invalid.Contains(c) && c != '.').ToArray());

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new ArgumentException($"Path segment '{segment}' contains only invalid characters", nameof(segment));
        }

        return sanitized;
    }
}
