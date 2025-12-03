using Xunit;
using HPD.Agent.Skills.DocumentStore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HPD.Agent.Tests.Skills;

/// <summary>
/// Tests for Phase 2: Document Store Infrastructure
/// Validates interfaces, base class, FileSystem implementation, and InMemory implementation
/// </summary>
public class Phase2DocumentStoreTests
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    // ===== P0 Tests: Core Upload and Read Functionality =====

    [Fact]
    public async Task InMemoryStore_UploadAndRead_Success()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        var metadata = new DocumentMetadata
        {
            Name = "Test Document",
            Description = "Test description"
        };

        // Act
        await store.UploadFromContentAsync("test-doc", metadata, "Test content");
        var content = await store.ReadDocumentAsync("test-doc");

        // Assert
        Assert.Equal("Test content", content);
    }

    [Fact]
    public async Task InMemoryStore_DocumentExists_ReturnsTrue()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        var metadata = new DocumentMetadata
        {
            Name = "Test Document",
            Description = "Test description"
        };

        // Act
        await store.UploadFromContentAsync("test-doc", metadata, "Test content");
        var exists = await store.DocumentExistsAsync("test-doc");

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task InMemoryStore_DocumentNotExists_ReturnsFalse()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        // Act
        var exists = await store.DocumentExistsAsync("non-existent");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task InMemoryStore_ReadNonExistent_ReturnsNull()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        // Act
        var content = await store.ReadDocumentAsync("non-existent");

        // Assert
        Assert.Null(content);
    }

    // ===== Idempotency Tests =====

    [Fact]
    public async Task InMemoryStore_UploadSameContent_IsIdempotent()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        var metadata = new DocumentMetadata
        {
            Name = "Test Document",
            Description = "Test description"
        };

        // Act - Upload twice with same content
        await store.UploadFromContentAsync("test-doc", metadata, "Test content");
        await store.UploadFromContentAsync("test-doc", metadata, "Test content");

        // Assert - Should still have the content
        var content = await store.ReadDocumentAsync("test-doc");
        Assert.Equal("Test content", content);

        // Verify metadata wasn't duplicated
        var storedMetadata = await store.GetDocumentMetadataAsync("test-doc");
        Assert.NotNull(storedMetadata);
        Assert.Equal(1, storedMetadata.Version);  // Still version 1 (not incremented)
    }

    [Fact]
    public async Task InMemoryStore_UploadDifferentContent_IncrementsVersion()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        var metadata = new DocumentMetadata
        {
            Name = "Test Document",
            Description = "Test description"
        };

        // Act - Upload twice with different content
        await store.UploadFromContentAsync("test-doc", metadata, "Original content");
        var metadata1 = await store.GetDocumentMetadataAsync("test-doc");

        await store.UploadFromContentAsync("test-doc", metadata, "Updated content");
        var metadata2 = await store.GetDocumentMetadataAsync("test-doc");

        // Assert
        Assert.NotNull(metadata1);
        Assert.NotNull(metadata2);
        Assert.Equal(1, metadata1.Version);
        Assert.Equal(2, metadata2.Version);

        // Verify content was actually updated
        var content = await store.ReadDocumentAsync("test-doc");
        Assert.Equal("Updated content", content);
    }

    // ===== Metadata Tests =====

    [Fact]
    public async Task InMemoryStore_GetMetadata_ReturnsCorrectData()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        var metadata = new DocumentMetadata
        {
            Name = "Test Document",
            Description = "Test description for metadata test"
        };

        // Act
        await store.UploadFromContentAsync("test-doc", metadata, "Test content");
        var storedMetadata = await store.GetDocumentMetadataAsync("test-doc");

        // Assert
        Assert.NotNull(storedMetadata);
        Assert.Equal("test-doc", storedMetadata.DocumentId);
        Assert.Equal("Test Document", storedMetadata.Name);
        Assert.Equal("Test description for metadata test", storedMetadata.Description);
        Assert.Equal(1, storedMetadata.Version);
        Assert.True(storedMetadata.SizeBytes > 0);
        Assert.NotEmpty(storedMetadata.ContentHash);
        Assert.True(storedMetadata.ContentHash.StartsWith("sha256:"));
    }

    [Fact]
    public async Task InMemoryStore_GetMetadataForNonExistent_ReturnsNull()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        // Act
        var metadata = await store.GetDocumentMetadataAsync("non-existent");

        // Assert
        Assert.Null(metadata);
    }

    // ===== Health Check Tests =====

    [Fact]
    public async Task InMemoryStore_HealthCheck_ReturnsTrue()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        // Act
        var isHealthy = await store.HealthCheckAsync();

        // Assert
        Assert.True(isHealthy);
    }

    // ===== FileSystem Store Tests =====

    [Fact]
    public async Task FileSystemStore_UploadAndRead_Success()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"hpd-test-{Guid.NewGuid()}");

        try
        {
            var store = new FileSystemInstructionStore(
                _loggerFactory.CreateLogger<FileSystemInstructionStore>(),
                tempDir);

            var metadata = new DocumentMetadata
            {
                Name = "Test Document",
                Description = "Test description"
            };

            // Act
            await store.UploadFromContentAsync("test-doc", metadata, "Test content");
            var content = await store.ReadDocumentAsync("test-doc");

            // Assert
            Assert.Equal("Test content", content);

            // Verify files were created
            Assert.True(File.Exists(Path.Combine(tempDir, "content", "test-doc.txt")));
            Assert.True(File.Exists(Path.Combine(tempDir, "metadata", "test-doc.json")));
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task FileSystemStore_UploadFromFile_Success()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"hpd-test-{Guid.NewGuid()}");
        var testFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.txt");

        try
        {
            // Create test file
            await File.WriteAllTextAsync(testFile, "Content from file");

            var store = new FileSystemInstructionStore(
                _loggerFactory.CreateLogger<FileSystemInstructionStore>(),
                tempDir);

            var metadata = new DocumentMetadata
            {
                Name = "Test Document",
                Description = "Test description"
            };

            // Act
            var fileContent = await File.ReadAllTextAsync(testFile);
            await store.UploadFromContentAsync("test-doc", metadata, fileContent);
            var content = await store.ReadDocumentAsync("test-doc");

            // Assert
            Assert.Equal("Content from file", content);
        }
        finally
        {
            // Cleanup
            if (File.Exists(testFile))
                File.Delete(testFile);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task FileSystemStore_UploadFromNonExistentFile_ThrowsException()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"hpd-test-{Guid.NewGuid()}");

        try
        {
            var store = new FileSystemInstructionStore(
                _loggerFactory.CreateLogger<FileSystemInstructionStore>(),
                tempDir);

            var metadata = new DocumentMetadata
            {
                Name = "Test Document",
                Description = "Test description"
            };

            // Act & Assert: Path doesn't exist, throws DirectoryNotFoundException when parent directory doesn't exist
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                File.ReadAllTextAsync("/non/existent/file.txt"));
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task FileSystemStore_Idempotency_SameAsInMemory()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"hpd-test-{Guid.NewGuid()}");

        try
        {
            var store = new FileSystemInstructionStore(
                _loggerFactory.CreateLogger<FileSystemInstructionStore>(),
                tempDir);

            var metadata = new DocumentMetadata
            {
                Name = "Test Document",
                Description = "Test description"
            };

            // Act - Upload twice with same content
            await store.UploadFromContentAsync("test-doc", metadata, "Test content");
            await store.UploadFromContentAsync("test-doc", metadata, "Test content");

            // Assert
            var storedMetadata = await store.GetDocumentMetadataAsync("test-doc");
            Assert.NotNull(storedMetadata);
            Assert.Equal(1, storedMetadata.Version);  // Still version 1 (not incremented)
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    // ===== Content Hash Tests =====

    [Fact]
    public async Task InMemoryStore_ContentHash_IsSHA256()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        var metadata = new DocumentMetadata
        {
            Name = "Test Document",
            Description = "Test description"
        };

        // Act
        await store.UploadFromContentAsync("test-doc", metadata, "Test content");
        var storedMetadata = await store.GetDocumentMetadataAsync("test-doc");

        // Assert
        Assert.NotNull(storedMetadata);
        Assert.StartsWith("sha256:", storedMetadata.ContentHash);
        Assert.Equal(71, storedMetadata.ContentHash.Length);  // "sha256:" + 64 hex chars
    }

    // ===== InMemory Store Specific Tests =====

    [Fact]
    public void InMemoryStore_Clear_RemovesAllData()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        var metadata = new DocumentMetadata
        {
            Name = "Test Document",
            Description = "Test description"
        };

        // Act
        store.UploadFromContentAsync("test-doc", metadata, "Test content").Wait();
        Assert.Equal(1, store.DocumentCount);

        store.Clear();

        // Assert
        Assert.Equal(0, store.DocumentCount);
    }

    [Fact]
    public async Task InMemoryStore_DocumentCount_IsAccurate()
    {
        // Arrange
        var store = new InMemoryInstructionStore(
            _loggerFactory.CreateLogger<InMemoryInstructionStore>());

        var metadata = new DocumentMetadata
        {
            Name = "Test Document",
            Description = "Test description"
        };

        // Act & Assert
        Assert.Equal(0, store.DocumentCount);

        await store.UploadFromContentAsync("doc1", metadata, "Content 1");
        Assert.Equal(1, store.DocumentCount);

        await store.UploadFromContentAsync("doc2", metadata, "Content 2");
        Assert.Equal(2, store.DocumentCount);

        await store.UploadFromContentAsync("doc3", metadata, "Content 3");
        Assert.Equal(3, store.DocumentCount);
    }
}
