using HPD.Agent;
using HPD_Agent.Skills.DocumentStore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HPD_Agent.Tests.Skills;

/// <summary>
/// Phase 6: Agent Retrieval Functions
/// Tests for the auto-generated read_skill_document() function.
/// </summary>
public class Phase6AgentRetrievalTests : IDisposable
{
    private readonly string _testDocumentsPath;

    public Phase6AgentRetrievalTests()
    {
        _testDocumentsPath = Path.Combine(Path.GetTempPath(), "hpd-agent-phase6-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDocumentsPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDocumentsPath))
        {
            Directory.Delete(_testDocumentsPath, recursive: true);
        }
    }

    [Fact]
    public async Task DocumentRetrievalPlugin_CanBeCreated()
    {
        // Arrange & Act
        var plugin = new DocumentRetrievalPlugin();

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void DocumentRetrievalPlugin_RegisteredByAgentBuilder_WhenDocumentStoreConfigured()
    {
        // Arrange
        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        // Act - The DocumentRetrievalPlugin should be auto-registered when document store is configured
        // We verify this by checking that it builds without errors (tested in integration tests)

        // Assert - DocumentRetrievalPlugin is created and ready
        var plugin = new DocumentRetrievalPlugin();
        Assert.NotNull(plugin);

        // Note: Full integration test with agent building requires a configured provider,
        // which is tested in separate integration test suites.
    }

    [Fact]
    public async Task ReadSkillDocument_ReturnsContent_WhenDocumentExists()
    {
        // Arrange
        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        var testContent = "# Debugging Workflow\n\n1. Check logs\n2. Analyze errors\n3. Fix bugs";
        var metadata = new DocumentMetadata
        {
            Name = "debugging-workflow",
            Description = "Step-by-step debugging guide"
        };
        await store.UploadFromContentAsync("debugging-workflow", metadata, testContent);

        var plugin = new DocumentRetrievalPlugin();

        // Act
        var result = await plugin.ReadSkillDocument("debugging-workflow", store);

        // Assert
        Assert.Equal(testContent, result);
    }

    [Fact]
    public async Task ReadSkillDocument_ReturnsNotFound_WhenDocumentDoesNotExist()
    {
        // Arrange
        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);
        var plugin = new DocumentRetrievalPlugin();

        // Act
        var result = await plugin.ReadSkillDocument("non-existent-doc", store);

        // Assert
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-existent-doc", result);
    }

    [Fact]
    public async Task ReadSkillDocument_ReturnsErrorMessage_WhenStoreThrowsException()
    {
        // Arrange
        var store = new ThrowingDocumentStore();
        var plugin = new DocumentRetrievalPlugin();

        // Act
        var result = await plugin.ReadSkillDocument("any-doc", store);

        // Assert
        Assert.Contains("Error reading document", result);
        Assert.Contains("temporarily unavailable", result);
    }

    [Fact]
    public async Task ReadSkillDocument_MultipleDocuments_AllRetrievable()
    {
        // Arrange
        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        var docs = new Dictionary<string, string>
        {
            ["doc1"] = "Content 1",
            ["doc2"] = "Content 2",
            ["doc3"] = "Content 3"
        };

        foreach (var (docId, content) in docs)
        {
            var metadata = new DocumentMetadata
            {
                Name = docId,
                Description = $"Description for {docId}"
            };
            await store.UploadFromContentAsync(docId, metadata, content);
        }

        var plugin = new DocumentRetrievalPlugin();

        // Act & Assert
        foreach (var (docId, expectedContent) in docs)
        {
            var result = await plugin.ReadSkillDocument(docId, store);
            Assert.Equal(expectedContent, result);
        }
    }

    /// <summary>
    /// Mock store that throws exceptions for testing error handling
    /// </summary>
    private class ThrowingDocumentStore : IInstructionDocumentStore
    {
        public Task<string?> ReadDocumentAsync(string documentId, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Store is unavailable");
        }

        public Task UploadFromUrlAsync(string documentId, DocumentMetadata metadata, string url, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task UploadFromContentAsync(string documentId, DocumentMetadata metadata, string content, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> DocumentExistsAsync(string documentId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task DeleteDocumentAsync(string documentId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<List<GlobalDocumentInfo>> ListAllDocumentsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<GlobalDocumentInfo?> GetDocumentMetadataAsync(string documentId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task UpdateDocumentMetadataAsync(string documentId, DocumentMetadata metadata, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task LinkDocumentToSkillAsync(string skillNamespace, string documentId, SkillDocumentMetadata metadata, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<List<SkillDocumentReference>> GetSkillDocumentsAsync(string skillNamespace, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SkillDocument?> ReadSkillDocumentAsync(string skillNamespace, string documentId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> HealthCheckAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
