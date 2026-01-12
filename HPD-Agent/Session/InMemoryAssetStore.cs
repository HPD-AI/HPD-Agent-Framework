using System.Collections.Concurrent;

namespace HPD.Agent;

/// <summary>
/// In-memory storage for binary assets (for testing and development).
/// </summary>
/// <remarks>
/// <para><b>Use Cases:</b></para>
/// <list type="bullet">
/// <item>Unit tests that need asset storage</item>
/// <item>Development/prototyping without file system dependencies</item>
/// <item>Ephemeral sessions that don't need persistence</item>
/// </list>
/// <para><b>Limitations:</b></para>
/// <list type="bullet">
/// <item>Assets lost on process restart (no persistence)</item>
/// <item>Memory usage grows unbounded (no automatic cleanup)</item>
/// <item>Not suitable for production (use LocalFileAssetStore or cloud storage)</item>
/// </list>
/// </remarks>
public class InMemoryAssetStore : IAssetStore
{
    private readonly ConcurrentDictionary<string, AssetData> _assets = new();

    /// <inheritdoc />
    public Task<string> UploadAssetAsync(
        byte[] data,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = "application/octet-stream";

        // Generate unique asset ID
        var assetId = Guid.NewGuid().ToString("N");

        // Store in memory
        var assetData = new AssetData(
            AssetId: assetId,
            Data: data,
            ContentType: contentType,
            CreatedAt: DateTime.UtcNow);

        _assets[assetId] = assetData;

        return Task.FromResult(assetId);
    }

    /// <inheritdoc />
    public Task<AssetData?> DownloadAssetAsync(
        string assetId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            return Task.FromResult<AssetData?>(null);

        _assets.TryGetValue(assetId, out var assetData);
        return Task.FromResult(assetData);
    }

    /// <inheritdoc />
    public Task DeleteAssetAsync(string assetId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(assetId))
        {
            _assets.TryRemove(assetId, out _);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clear all assets (for testing).
    /// </summary>
    public void Clear()
    {
        _assets.Clear();
    }

    /// <summary>
    /// Get count of stored assets (for testing).
    /// </summary>
    public int Count => _assets.Count;

    /// <summary>
    /// Check if asset exists (for testing).
    /// </summary>
    public bool Contains(string assetId) => _assets.ContainsKey(assetId);
}
