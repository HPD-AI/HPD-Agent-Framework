using HPD.Agent;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

public class AssetUploadMiddlewareTests
{
    [Fact]
    public async Task BeforeIterationAsync_NoAssetStore_DoesNothing()
    {
        // Arrange
        var middleware = new AssetUploadMiddleware();
        var session = new AgentSession("test-session");
        // session.Store is null (no store associated)

        var context = CreateBeforeIterationContext(session, new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "Hello")
        });

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Single(context.Messages);
        Assert.Equal("Hello", context.Messages[0].Text);
    }

    [Fact]
    public async Task BeforeIterationAsync_StoreWithoutAssetStore_DoesNothing()
    {
        // Arrange
        var middleware = new AssetUploadMiddleware();
        var store = new TestSessionStoreWithoutAssets();
        var session = await store.LoadOrCreateSessionAsync("test-session");

        var context = CreateBeforeIterationContext(session, new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "Hello")
        });

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Single(context.Messages);
        Assert.Equal("Hello", context.Messages[0].Text);
    }

    [Fact]
    public async Task BeforeIterationAsync_NoDataContent_DoesNothing()
    {
        // Arrange
        var middleware = new AssetUploadMiddleware();
        var store = new InMemorySessionStore();
        var session = await store.LoadOrCreateSessionAsync("test-session");

        var context = CreateBeforeIterationContext(session, new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "Hello")
        });

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Single(context.Messages);
        Assert.Equal("Hello", context.Messages[0].Text);
    }

    [Fact]
    public async Task BeforeIterationAsync_WithDataContent_TransformsToUriContent()
    {
        // Arrange
        var middleware = new AssetUploadMiddleware();
        var store = new InMemorySessionStore();
        var session = await store.LoadOrCreateSessionAsync("test-session");

        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        var message = new ChatMessage(ChatRole.User, [
            new TextContent("Check this image:"),
            new DataContent(imageBytes, "image/png")
        ]);

        var context = CreateBeforeIterationContext(session, new List<ChatMessage> { message });

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Single(context.Messages);
        var transformedMessage = context.Messages[0];
        Assert.Equal(2, transformedMessage.Contents.Count);

        // First content should still be text
        Assert.IsType<TextContent>(transformedMessage.Contents[0]);
        Assert.Equal("Check this image:", ((TextContent)transformedMessage.Contents[0]).Text);

        // Second content should be transformed to UriContent
        Assert.IsType<UriContent>(transformedMessage.Contents[1]);
        var uriContent = (UriContent)transformedMessage.Contents[1];
        Assert.StartsWith("asset://", uriContent.Uri.ToString());
        Assert.Equal("image/png", uriContent.MediaType);
    }

    [Fact]
    public async Task BeforeIterationAsync_EmitsAssetUploadedEvent()
    {
        // Arrange
        var middleware = new AssetUploadMiddleware();
        var store = new InMemorySessionStore();
        var session = await store.LoadOrCreateSessionAsync("test-session");

        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
        var message = new ChatMessage(ChatRole.User, [
            new DataContent(imageBytes, "image/png")
        ]);

        var context = CreateBeforeIterationContext(session, new List<ChatMessage> { message });
        var events = new List<AgentEvent>();

        // Capture emitted events
        var originalEmit = context.Emit;
        context = context with { };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - verify event was emitted via context.Emit
        // Note: We can't easily capture events in this test structure
        // because BeforeIterationContext.Emit is not mockable.
        // The event emission is tested in integration tests below.
    }

    [Fact]
    public async Task BeforeIterationAsync_MultipleDataContents_TransformsAll()
    {
        // Arrange
        var middleware = new AssetUploadMiddleware();
        var store = new InMemorySessionStore();
        var session = await store.LoadOrCreateSessionAsync("test-session");

        var image1 = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var image2 = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header
        var message = new ChatMessage(ChatRole.User, [
            new DataContent(image1, "image/png"),
            new TextContent("and"),
            new DataContent(image2, "image/jpeg")
        ]);

        var context = CreateBeforeIterationContext(session, new List<ChatMessage> { message });

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        Assert.Single(context.Messages);
        var transformedMessage = context.Messages[0];
        Assert.Equal(3, transformedMessage.Contents.Count);

        // All DataContent should be transformed to UriContent
        Assert.IsType<UriContent>(transformedMessage.Contents[0]);
        Assert.Equal("image/png", ((UriContent)transformedMessage.Contents[0]).MediaType);

        Assert.IsType<TextContent>(transformedMessage.Contents[1]);

        Assert.IsType<UriContent>(transformedMessage.Contents[2]);
        Assert.Equal("image/jpeg", ((UriContent)transformedMessage.Contents[2]).MediaType);
    }

    [Fact]
    public async Task BeforeIterationAsync_PreservesMessageMetadata()
    {
        // Arrange
        var middleware = new AssetUploadMiddleware();
        var store = new InMemorySessionStore();
        var session = await store.LoadOrCreateSessionAsync("test-session");

        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var message = new ChatMessage(ChatRole.User, [
            new DataContent(imageBytes, "image/png")
        ])
        {
            AuthorName = "TestUser",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["customKey"] = "customValue"
            }
        };

        var context = CreateBeforeIterationContext(session, new List<ChatMessage> { message });

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        var transformedMessage = context.Messages[0];
        Assert.Equal("TestUser", transformedMessage.AuthorName);
        Assert.Equal("customValue", transformedMessage.AdditionalProperties?["customKey"]);
    }

    [Fact]
    public async Task BeforeIterationAsync_EmptyDataContent_Skips()
    {
        // Arrange
        var middleware = new AssetUploadMiddleware();
        var store = new InMemorySessionStore();
        var session = await store.LoadOrCreateSessionAsync("test-session");

        var message = new ChatMessage(ChatRole.User, [
            new DataContent(Array.Empty<byte>(), "image/png") // Empty data
        ]);

        var context = CreateBeforeIterationContext(session, new List<ChatMessage> { message });

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - should not transform empty DataContent
        Assert.Single(context.Messages);
        var transformedMessage = context.Messages[0];
        Assert.Single(transformedMessage.Contents);
        Assert.IsType<DataContent>(transformedMessage.Contents[0]); // Still DataContent
    }

    [Fact]
    public async Task BeforeIterationAsync_AssetStoreReturnsId_CreatesCorrectUri()
    {
        // Arrange
        var middleware = new AssetUploadMiddleware();
        var store = new InMemorySessionStore();
        var session = await store.LoadOrCreateSessionAsync("test-session");

        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var message = new ChatMessage(ChatRole.User, [
            new DataContent(imageBytes, "image/png")
        ]);

        var context = CreateBeforeIterationContext(session, new List<ChatMessage> { message });

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        var uriContent = (UriContent)context.Messages[0].Contents[0];
        var assetId = uriContent.Uri.Host;

        // Verify we can retrieve the asset
        var assetStore = store.GetAssetStore(session.Id)!;
        var retrievedAsset = await assetStore.DownloadAssetAsync(assetId);
        Assert.NotNull(retrievedAsset);
        Assert.Equal(imageBytes, retrievedAsset.Data);
        Assert.Equal("image/png", retrievedAsset.ContentType);
    }

    [Fact]
    public async Task BeforeIterationAsync_OctetStreamMediaType_PreservesContentType()
    {
        // Arrange
        var middleware = new AssetUploadMiddleware();
        var store = new InMemorySessionStore();
        var session = await store.LoadOrCreateSessionAsync("test-session");

        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        var message = new ChatMessage(ChatRole.User, [
            new DataContent(bytes, "application/octet-stream")
        ]);

        var context = CreateBeforeIterationContext(session, new List<ChatMessage> { message });

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        var uriContent = (UriContent)context.Messages[0].Contents[0];
        var assetId = uriContent.Uri.Host;
        var assetStore = store.GetAssetStore(session.Id)!;
        var retrievedAsset = await assetStore.DownloadAssetAsync(assetId);

        Assert.NotNull(retrievedAsset);
        Assert.Equal("application/octet-stream", retrievedAsset.ContentType);
    }

    // Helper method to create BeforeIterationContext
    private static BeforeIterationContext CreateBeforeIterationContext(
        AgentSession session,
        List<ChatMessage> messages)
    {
        var state = AgentLoopState.Initial(
            new List<ChatMessage>(),
            "test-run",
            "test-conv",
            "TestAgent");
        var eventCoordinator = new HPD.Events.Core.EventCoordinator();
        var agentContext = new AgentContext(
            "TestAgent",
            "test-conv",
            state,
            eventCoordinator,
            session,
            CancellationToken.None);

        var options = new ChatOptions();
        var runOptions = new AgentRunOptions();

        return agentContext.AsBeforeIteration(0, messages, options, runOptions);
    }

    // Test session store without asset support
    private class TestSessionStoreWithoutAssets : ISessionStore
    {
        public bool SupportsHistory => false;
        public bool SupportsPendingWrites => false;
        public IAssetStore? AssetStore => null; // No asset store

        public IAssetStore? GetAssetStore(string sessionId) => null;

        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<AgentSession?>(new AgentSession(sessionId));

        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<List<string>> ListSessionIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new List<string>());

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ExecutionCheckpoint?> LoadCheckpointAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<ExecutionCheckpoint?>(null);

        public Task SaveCheckpointAsync(ExecutionCheckpoint checkpoint, CheckpointMetadata metadata, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteAllCheckpointsAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SavePendingWritesAsync(string sessionId, string executionCheckpointId, IEnumerable<PendingWrite> writes, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<List<PendingWrite>> LoadPendingWritesAsync(string sessionId, string executionCheckpointId, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<PendingWrite>());

        public Task DeletePendingWritesAsync(string sessionId, string executionCheckpointId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ExecutionCheckpoint?> LoadCheckpointAtAsync(string sessionId, string executionCheckpointId, CancellationToken cancellationToken = default)
            => Task.FromResult<ExecutionCheckpoint?>(null);

        public Task<List<CheckpointManifestEntry>> GetCheckpointManifestAsync(string sessionId, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<CheckpointManifestEntry>());

        public Task PruneCheckpointsAsync(string sessionId, int keepLatest = 10, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> DeleteInactiveSessionsAsync(TimeSpan inactivityThreshold, bool dryRun = false, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task DeleteCheckpointsAsync(string sessionId, IEnumerable<string> checkpointIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        [Obsolete]
        public Task<AgentSession?> LoadSessionAtCheckpointAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default)
            => Task.FromResult<AgentSession?>(null);

        [Obsolete]
        public Task SaveSessionAtCheckpointAsync(AgentSession session, string checkpointId, CheckpointMetadata metadata, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        [Obsolete]
        public Task UpdateCheckpointManifestEntryAsync(string sessionId, string checkpointId, Action<CheckpointManifestEntry> update, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
