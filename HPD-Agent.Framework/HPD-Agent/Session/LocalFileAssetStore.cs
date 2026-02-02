namespace HPD.Agent;

/// <summary>
/// Local file system storage for binary assets.
/// Stores assets as individual files in a directory.
/// </summary>
/// <remarks>
/// <para><b>Storage Layout:</b></para>
/// <code>
/// basePath/
///   assets/
///     {assetId}.jpg    (JPEG images)
///     {assetId}.png    (PNG images)
///     {assetId}.mp3    (Audio files)
///     {assetId}.pdf    (PDF documents)
///     {assetId}.bin    (Unknown types)
/// </code>
/// <para><b>Features:</b></para>
/// <list type="bullet">
/// <item>Automatic directory creation</item>
/// <item>Content-type based file extensions</item>
/// <item>Thread-safe (file system handles concurrency)</item>
/// <item>Simple cleanup (just delete files)</item>
/// </list>
/// <para><b>Limitations:</b></para>
/// <list type="bullet">
/// <item>No deduplication (same image uploaded twice = 2 files)</item>
/// <item>No automatic cleanup (manual deletion required)</item>
/// <item>Not suitable for distributed systems (local files only)</item>
/// </list>
/// </remarks>
public class LocalFileAssetStore : IAssetStore
{
    private readonly string _basePath;

    /// <summary>
    /// Create a new local file asset store.
    /// </summary>
    /// <param name="basePath">Base directory for asset storage</param>
    public LocalFileAssetStore(string basePath)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));

        // Ensure assets directory exists
        Directory.CreateDirectory(Path.Combine(basePath, "assets"));
    }

    /// <inheritdoc />
    public async Task<string> UploadAssetAsync(
        byte[] data,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = "application/octet-stream";

        // Generate unique asset ID
        var assetId = Guid.NewGuid().ToString("N");
        var extension = GetExtensionFromContentType(contentType);
        var filePath = Path.Combine(_basePath, "assets", $"{assetId}{extension}");

        // Write to disk
        await File.WriteAllBytesAsync(filePath, data, cancellationToken);

        return assetId;
    }

    /// <inheritdoc />
    public async Task<AssetData?> DownloadAssetAsync(
        string assetId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            return null;

        // Find file with this asset ID (extension may vary)
        var files = Directory.GetFiles(
            Path.Combine(_basePath, "assets"),
            $"{assetId}.*");

        if (files.Length == 0)
            return null;

        // Read first match (should only be one)
        var filePath = files[0];
        var data = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var contentType = GetContentTypeFromExtension(Path.GetExtension(filePath));
        var createdAt = File.GetCreationTimeUtc(filePath);

        return new AssetData(
            AssetId: assetId,
            Data: data,
            ContentType: contentType,
            CreatedAt: createdAt);
    }

    /// <inheritdoc />
    public Task DeleteAssetAsync(string assetId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            return Task.CompletedTask;

        // Find and delete all files with this asset ID
        var files = Directory.GetFiles(
            Path.Combine(_basePath, "assets"),
            $"{assetId}.*");

        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Ignore errors (file may not exist, permissions, etc.)
                // Delete is idempotent - no need to throw
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Map content type to file extension.
    /// </summary>
    private static string GetExtensionFromContentType(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            "audio/wav" => ".wav",
            "audio/mp3" => ".mp3",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "audio/flac" => ".flac",
            "video/mp4" => ".mp4",
            "video/mpeg" => ".mpeg",
            "video/webm" => ".webm",
            "application/pdf" => ".pdf",
            "application/json" => ".json",
            "application/xml" => ".xml",
            "text/plain" => ".txt",
            "text/csv" => ".csv",
            _ => ".bin"
        };
    }

    /// <summary>
    /// Map file extension to content type.
    /// </summary>
    private static string GetContentTypeFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tiff" => "image/tiff",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mp3",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            ".mp4" => "video/mp4",
            ".mpeg" => "video/mpeg",
            ".webm" => "video/webm",
            ".pdf" => "application/pdf",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            _ => "application/octet-stream"
        };
    }
}
