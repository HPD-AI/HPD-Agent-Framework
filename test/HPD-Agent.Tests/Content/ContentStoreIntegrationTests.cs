using Xunit;
using HPD.Agent;
using HPD.Agent.Memory;
using HPD.Agent.Skills.DocumentStore;
using HPD.Agent.TextExtraction;
using Microsoft.Extensions.Logging.Abstractions;

namespace HPD.Agent.Tests.Content;

/// <summary>
/// Integration tests demonstrating polymorphic usage of IContentStore across all four store types.
/// </summary>
public class ContentStoreIntegrationTests
{
    [Fact]
    public async Task PolymorphicBackup_WorksAcrossAllStores()
    {
        // Arrange - Create instances of all four store types
        var assetStore = new InMemoryAssetStore();
        var staticMemoryStore = new InMemoryStaticMemoryStore();
        var dynamicMemoryStore = new InMemoryDynamicMemoryStore();
        var instructionStore = new InMemoryInstructionStore(NullLogger.Instance);

        // Add some test content to each store using the V2 API
        await assetStore.PutAsync("session1", new byte[] { 1, 2, 3 }, "image/jpeg", cancellationToken: CancellationToken.None);
        await staticMemoryStore.PutAsync("test-agent", System.Text.Encoding.UTF8.GetBytes("Static memory content"), "text/plain",
            new ContentMetadata { Name = "test.txt" }, CancellationToken.None);
        await dynamicMemoryStore.PutAsync("test-agent", System.Text.Encoding.UTF8.GetBytes("Dynamic memory content"), "text/plain",
            new ContentMetadata { Name = "Test Memory" }, CancellationToken.None);
        await instructionStore.PutAsync(null, System.Text.Encoding.UTF8.GetBytes("Instruction content"), "text/plain",
            new ContentMetadata { Name = "Skill Documentation", Description = "Test skill doc" }, CancellationToken.None);

        // Act - Use polymorphic backup function that works with any IContentStore
        var stores = new (IContentStore Store, string? Scope)[]
        {
            (assetStore, "session1"),
            (staticMemoryStore, "test-agent"),
            (dynamicMemoryStore, "test-agent"),
            (instructionStore, null)
        };
        var backupResults = new List<(string StoreType, int ItemCount, long TotalBytes)>();

        foreach (var (store, scope) in stores)
        {
            var items = await store.QueryAsync(scope, cancellationToken: CancellationToken.None);
            long totalBytes = 0;
            foreach (var item in items)
            {
                totalBytes += item.SizeBytes;
            }
            backupResults.Add((store.GetType().Name, items.Count, totalBytes));
        }

        // Assert - Verify each store has content
        Assert.Equal(4, backupResults.Count);
        Assert.All(backupResults, result => Assert.True(result.ItemCount > 0, $"{result.StoreType} should have at least one item"));
        Assert.All(backupResults, result => Assert.True(result.TotalBytes > 0, $"{result.StoreType} should have content"));
    }

    [Fact]
    public async Task PolymorphicSearch_FindsRecentContentAcrossStores()
    {
        // Arrange
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        var assetStore = new InMemoryAssetStore();
        var dynamicMemoryStore = new InMemoryDynamicMemoryStore();

        // Add content after cutoff
        await Task.Delay(50);
        var assetId = await assetStore.PutAsync("session1", new byte[] { 1, 2, 3 }, "image/jpeg", cancellationToken: CancellationToken.None);
        var memoryId = await dynamicMemoryStore.PutAsync(
            "agent1",
            System.Text.Encoding.UTF8.GetBytes("Recent memory"),
            "text/plain",
            new ContentMetadata { Name = "Recent" },
            CancellationToken.None);

        // Act - Search for recent content across multiple stores
        var stores = new (IContentStore Store, string? Scope)[]
        {
            (assetStore, "session1"),
            (dynamicMemoryStore, "agent1")
        };
        var recentContent = new List<ContentInfo>();

        foreach (var (store, scope) in stores)
        {
            var items = await store.QueryAsync(scope, new ContentQuery
            {
                CreatedAfter = cutoff
            }, CancellationToken.None);
            recentContent.AddRange(items);
        }

        // Assert
        Assert.Equal(2, recentContent.Count);
        Assert.All(recentContent, item => Assert.True(item.CreatedAt >= cutoff));
    }

    [Fact]
    public async Task PolymorphicGetAndDelete_WorksAcrossStores()
    {
        // Arrange
        var assetStore = new InMemoryAssetStore();
        var dynamicStore = new InMemoryDynamicMemoryStore();

        var assetId = await assetStore.PutAsync("session1", new byte[] { 1, 2, 3 }, "image/jpeg", cancellationToken: CancellationToken.None);
        var memoryId = await dynamicStore.PutAsync(
            "agent1",
            System.Text.Encoding.UTF8.GetBytes("Memory content"),
            "text/plain",
            cancellationToken: CancellationToken.None);

        // Act - Get content polymorphically
        IContentStore store1 = assetStore;
        IContentStore store2 = dynamicStore;

        var content1 = await store1.GetAsync("session1", assetId, CancellationToken.None);
        var content2 = await store2.GetAsync("agent1", memoryId, CancellationToken.None);

        // Assert - Verify content retrieved
        Assert.NotNull(content1);
        Assert.Equal(assetId, content1.Id);
        Assert.Equal("image/jpeg", content1.ContentType);
        Assert.Equal(ContentSource.User, content1.Info.Origin);

        Assert.NotNull(content2);
        Assert.Equal(memoryId, content2.Id);
        Assert.Equal("text/plain", content2.ContentType);
        Assert.Equal(ContentSource.Agent, content2.Info.Origin);

        // Act - Delete polymorphically
        await store1.DeleteAsync("session1", assetId, CancellationToken.None);
        await store2.DeleteAsync("agent1", memoryId, CancellationToken.None);

        // Assert - Verify deletion
        Assert.Null(await store1.GetAsync("session1", assetId, CancellationToken.None));
        Assert.Null(await store2.GetAsync("agent1", memoryId, CancellationToken.None));
    }

    [Fact]
    public async Task QueryByContentType_FiltersCorrectly()
    {
        // Arrange
        var assetStore = new InMemoryAssetStore();
        await assetStore.PutAsync("session1", new byte[] { 1 }, "image/jpeg", cancellationToken: CancellationToken.None);
        await assetStore.PutAsync("session1", new byte[] { 2 }, "image/jpeg", cancellationToken: CancellationToken.None);
        await assetStore.PutAsync("session1", new byte[] { 3 }, "audio/mp3", cancellationToken: CancellationToken.None);

        // Act
        var jpegImages = await assetStore.QueryAsync("session1", new ContentQuery
        {
            ContentType = "image/jpeg"
        }, CancellationToken.None);

        // Assert
        Assert.Equal(2, jpegImages.Count);
        Assert.All(jpegImages, item => Assert.Equal("image/jpeg", item.ContentType));
    }

    [Fact]
    public async Task V2API_PutAndGetWorkWithScoping()
    {
        // Arrange
        var assetStore = new InMemoryAssetStore();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Act - Upload using V2 API to session1
        var assetId = await assetStore.PutAsync("session1", data, "image/png", cancellationToken: CancellationToken.None);

        // Act - Retrieve using IContentStore interface
        IContentStore contentStore = assetStore;
        var contentData = await contentStore.GetAsync("session1", assetId, CancellationToken.None);

        // Assert
        Assert.NotNull(contentData);
        Assert.Equal(data, contentData.Data);
        Assert.Equal("image/png", contentData.ContentType);
        Assert.Equal(assetId, contentData.Info.Id);

        // Act - Upload to different session using V2 API
        var contentId = await contentStore.PutAsync("session2", new byte[] { 6, 7, 8 }, "audio/mp3", cancellationToken: CancellationToken.None);

        // Act - Retrieve from correct session
        var assetData = await assetStore.GetAsync("session2", contentId, CancellationToken.None);

        // Assert
        Assert.NotNull(assetData);
        Assert.Equal(new byte[] { 6, 7, 8 }, assetData.Data);
        Assert.Equal("audio/mp3", assetData.ContentType);

        // Assert - Cannot access from wrong session
        var wrongSession = await assetStore.GetAsync("session1", contentId, CancellationToken.None);
        Assert.Null(wrongSession);
    }
}
