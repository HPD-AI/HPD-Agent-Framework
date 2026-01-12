using Xunit;
using HPD.Agent;

namespace HPD.Agent.Tests.Session;

/// <summary>
/// Tests for IAssetStore implementations.
/// </summary>
public class AssetStoreTests
{
    // ═════════════════════════════════════════════════════════════════════
    // INMEMORY ASSET STORE TESTS
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InMemoryAssetStore_Upload_Returns_AssetId()
    {
        // Arrange
        var store = new InMemoryAssetStore();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        var assetId = await store.UploadAssetAsync(data, "image/jpeg", CancellationToken.None);

        // Assert
        Assert.NotNull(assetId);
        Assert.NotEmpty(assetId);
    }

    [Fact]
    public async Task InMemoryAssetStore_Download_Returns_UploadedData()
    {
        // Arrange
        var store = new InMemoryAssetStore();
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var assetId = await store.UploadAssetAsync(data, "image/jpeg", CancellationToken.None);

        // Act
        var downloaded = await store.DownloadAssetAsync(assetId, CancellationToken.None);

        // Assert
        Assert.NotNull(downloaded);
        Assert.Equal(assetId, downloaded.AssetId);
        Assert.Equal(data, downloaded.Data);
        Assert.Equal("image/jpeg", downloaded.ContentType);
        Assert.True(downloaded.CreatedAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task InMemoryAssetStore_Download_NonExistent_Returns_Null()
    {
        // Arrange
        var store = new InMemoryAssetStore();

        // Act
        var downloaded = await store.DownloadAssetAsync("nonexistent", CancellationToken.None);

        // Assert
        Assert.Null(downloaded);
    }

    [Fact]
    public async Task InMemoryAssetStore_Delete_Removes_Asset()
    {
        // Arrange
        var store = new InMemoryAssetStore();
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var assetId = await store.UploadAssetAsync(data, "image/jpeg", CancellationToken.None);

        // Act
        await store.DeleteAssetAsync(assetId, CancellationToken.None);
        var downloaded = await store.DownloadAssetAsync(assetId, CancellationToken.None);

        // Assert
        Assert.Null(downloaded);
    }

    [Fact]
    public async Task InMemoryAssetStore_Delete_NonExistent_DoesNotThrow()
    {
        // Arrange
        var store = new InMemoryAssetStore();

        // Act & Assert (should not throw)
        await store.DeleteAssetAsync("nonexistent", CancellationToken.None);
    }

    [Fact]
    public async Task InMemoryAssetStore_Clear_Removes_AllAssets()
    {
        // Arrange
        var store = new InMemoryAssetStore();
        var assetId1 = await store.UploadAssetAsync(new byte[] { 1 }, "image/jpeg", CancellationToken.None);
        var assetId2 = await store.UploadAssetAsync(new byte[] { 2 }, "audio/mp3", CancellationToken.None);

        // Act
        store.Clear();

        // Assert
        Assert.Null(await store.DownloadAssetAsync(assetId1, CancellationToken.None));
        Assert.Null(await store.DownloadAssetAsync(assetId2, CancellationToken.None));
        Assert.Equal(0, store.Count);
    }

    // ═════════════════════════════════════════════════════════════════════
    // LOCALFILE ASSET STORE TESTS
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LocalFileAssetStore_Upload_Creates_File()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileAssetStore(tempDir);
            var data = new byte[] { 1, 2, 3, 4, 5 };

            // Act
            var assetId = await store.UploadAssetAsync(data, "image/jpeg", CancellationToken.None);

            // Assert
            Assert.NotNull(assetId);
            Assert.NotEmpty(assetId);

            // Verify file exists
            var expectedFile = Path.Combine(tempDir, "assets", $"{assetId}.jpg");
            Assert.True(File.Exists(expectedFile));
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalFileAssetStore_Download_Returns_UploadedData()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileAssetStore(tempDir);
            var data = new byte[] { 1, 2, 3, 4, 5 };
            var assetId = await store.UploadAssetAsync(data, "image/png", CancellationToken.None);

            // Act
            var downloaded = await store.DownloadAssetAsync(assetId, CancellationToken.None);

            // Assert
            Assert.NotNull(downloaded);
            Assert.Equal(assetId, downloaded.AssetId);
            Assert.Equal(data, downloaded.Data);
            Assert.Equal("image/png", downloaded.ContentType);
            Assert.True(downloaded.CreatedAt <= DateTime.UtcNow);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalFileAssetStore_Download_NonExistent_Returns_Null()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileAssetStore(tempDir);

            // Act
            var downloaded = await store.DownloadAssetAsync("nonexistent", CancellationToken.None);

            // Assert
            Assert.Null(downloaded);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalFileAssetStore_Delete_Removes_File()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileAssetStore(tempDir);
            var data = new byte[] { 1, 2, 3, 4, 5 };
            var assetId = await store.UploadAssetAsync(data, "image/jpeg", CancellationToken.None);

            // Act
            await store.DeleteAssetAsync(assetId, CancellationToken.None);
            var downloaded = await store.DownloadAssetAsync(assetId, CancellationToken.None);

            // Assert
            Assert.Null(downloaded);

            // Verify file is gone
            var expectedFile = Path.Combine(tempDir, "assets", $"{assetId}.jpg");
            Assert.False(File.Exists(expectedFile));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalFileAssetStore_Delete_NonExistent_DoesNotThrow()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileAssetStore(tempDir);

            // Act & Assert (should not throw)
            await store.DeleteAssetAsync("nonexistent", CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/png", ".png")]
    [InlineData("audio/mp3", ".mp3")]
    [InlineData("audio/mpeg", ".mp3")]
    [InlineData("video/mp4", ".mp4")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("application/octet-stream", ".bin")]
    [InlineData("unknown/type", ".bin")]
    public async Task LocalFileAssetStore_Uses_CorrectExtension(
        string contentType,
        string expectedExtension)
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileAssetStore(tempDir);
            var data = new byte[] { 1, 2, 3 };

            // Act
            var assetId = await store.UploadAssetAsync(data, contentType, CancellationToken.None);

            // Assert
            var expectedFile = Path.Combine(tempDir, "assets", $"{assetId}{expectedExtension}");
            Assert.True(File.Exists(expectedFile));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalFileAssetStore_Handles_LargeFiles()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileAssetStore(tempDir);
            var data = new byte[10 * 1024 * 1024]; // 10 MB
            new Random().NextBytes(data);

            // Act
            var assetId = await store.UploadAssetAsync(data, "application/octet-stream", CancellationToken.None);
            var downloaded = await store.DownloadAssetAsync(assetId, CancellationToken.None);

            // Assert
            Assert.NotNull(downloaded);
            Assert.Equal(data.Length, downloaded.Data.Length);
            Assert.Equal(data, downloaded.Data);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
