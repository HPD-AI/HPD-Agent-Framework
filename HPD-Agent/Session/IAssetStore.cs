namespace HPD.Agent;

/// <summary>
/// Storage interface for binary assets (images, audio, videos, PDFs).
/// </summary>
/// <remarks>
/// <para>
/// IAssetStore provides a simple abstraction for storing large binary objects
/// that don't fit well in conversation history (images, audio files, documents).
/// </para>
/// <para><b>Key Concepts:</b></para>
/// <list type="bullet">
/// <item>Assets are stored separately from conversation messages</item>
/// <item>Messages reference assets via URI (asset://assetId)</item>
/// <item>Enables efficient storage (e.g., S3, blob storage, local files)</item>
/// <item>Supports multi-tenant scenarios (per-session isolation)</item>
/// </list>
/// <para><b>Usage:</b></para>
/// <para>
/// This interface is typically implemented by session stores that support
/// binary asset storage. The AssetUploadMiddleware automatically uploads
/// DataContent bytes to the asset store before LLM processing.
/// </para>
/// </remarks>
public interface IAssetStore
{
    /// <summary>
    /// Upload binary asset and return unique identifier.
    /// </summary>
    /// <param name="data">Binary data to upload</param>
    /// <param name="contentType">MIME type (e.g., "image/jpeg", "audio/mp3")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Unique asset identifier for later retrieval</returns>
    /// <remarks>
    /// The returned asset ID should be stable and usable in URIs (e.g., "asset://assetId").
    /// Implementations should handle duplicate uploads efficiently (e.g., content-based deduplication).
    /// </remarks>
    Task<string> UploadAssetAsync(
        byte[] data,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Download asset by identifier.
    /// Returns null if not found.
    /// </summary>
    /// <param name="assetId">Asset identifier returned by UploadAssetAsync</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asset data, or null if not found</returns>
    /// <remarks>
    /// Implementations should handle missing assets gracefully (return null, not throw).
    /// </remarks>
    Task<AssetData?> DownloadAssetAsync(
        string assetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete asset by identifier.
    /// No-op if asset doesn't exist.
    /// </summary>
    /// <param name="assetId">Asset identifier to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// This method should be idempotent - calling it multiple times or on
    /// non-existent assets should not throw exceptions.
    /// </remarks>
    Task DeleteAssetAsync(
        string assetId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents downloaded asset data.
/// </summary>
/// <param name="AssetId">Unique identifier for this asset</param>
/// <param name="Data">Binary data</param>
/// <param name="ContentType">MIME type (e.g., "image/jpeg", "audio/mp3")</param>
/// <param name="CreatedAt">When this asset was created (UTC)</param>
public record AssetData(
    string AssetId,
    byte[] Data,
    string ContentType,
    DateTime CreatedAt);
