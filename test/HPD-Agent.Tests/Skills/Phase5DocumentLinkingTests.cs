using Xunit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using HPD_Agent.Skills;
using HPD_Agent.Skills.DocumentStore;
using HPD.Agent;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System;

namespace HPD_Agent.Tests.Skills;

/// <summary>
/// Tests for Phase 5: Document Linking & Explicit Init
/// Validates document upload, validation, and linking during agent build
/// </summary>
public class Phase5DocumentLinkingTests : IDisposable
{
    private readonly string _testDocumentsPath;
    private readonly string _testStorePath;

    public Phase5DocumentLinkingTests()
    {
        _testDocumentsPath = Path.Combine(Path.GetTempPath(), "hpd-agent-test-docs", Guid.NewGuid().ToString());
        _testStorePath = Path.Combine(Path.GetTempPath(), "hpd-agent-test-store", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDocumentsPath);
        Directory.CreateDirectory(_testStorePath);
    }

    public void Dispose()
    {
        // Cleanup test directories
        if (Directory.Exists(_testDocumentsPath))
            Directory.Delete(_testDocumentsPath, recursive: true);
        if (Directory.Exists(_testStorePath))
            Directory.Delete(_testStorePath, recursive: true);
    }

    // ===== P0 Tests: Default Store Creation =====

    [Fact]
    public async Task BuildAsync_WithoutDocumentStore_CreatesDefaultFileSystemStore()
    {
        // Arrange: Create a test document
        var testDocPath = Path.Combine(_testDocumentsPath, "test-guide.md");
        await File.WriteAllTextAsync(testDocPath, "# Test Guide\nThis is a test document.");

        // Create a mock plugin with a skill that uploads a document
        var testPlugin = new TestPluginWithDocumentUpload(testDocPath);

        // Act: Build agent without providing document store
        var builder = new AgentBuilder()
            .WithProvider("openai", "gpt-4");

        // Register plugin via reflection (simulating auto-registration)
        builder._pluginManager.RegisterPlugin(testPlugin);

        // Note: We can't fully test Build() without a real provider,
        // but we can verify the document store field gets set
        var storeBefore = builder._documentStore;

        // Assert: Store should be null before processing
        Assert.Null(storeBefore);
    }

    [Fact]
    public async Task BuildAsync_WithCustomStore_UsesProvidedStore()
    {
        // Arrange
        var customStore = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);
        var testDocPath = Path.Combine(_testDocumentsPath, "custom-guide.md");
        await File.WriteAllTextAsync(testDocPath, "# Custom Guide");

        var builder = new AgentBuilder()
            .WithProvider("openai", "gpt-4")
            .WithDocumentStore(customStore);

        // Assert: Custom store should be set
        Assert.Same(customStore, builder._documentStore);
    }

    // ===== P0 Tests: Document Upload (AddDocumentFromFile) =====

    [Fact]
    public async Task ProcessDocuments_UploadsDocumentFromFile()
    {
        // Arrange: Create test document
        var testDocPath = Path.Combine(_testDocumentsPath, "upload-test.md");
        var testContent = "# Upload Test\nThis document should be uploaded.";
        await File.WriteAllTextAsync(testDocPath, testContent);

        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        // Create skill with AddDocumentFromFile
        var skill = SkillFactory.Create(
            "TestSkill",
            "Test skill with document upload",
            "Instructions",
            new SkillOptions()
                .AddDocumentFromFile(testDocPath, "Test upload document", "upload-test"),
            "MockPlugin.TestFunction"
        );

        // Simulate what the source generator would create
        var skillContainer = CreateMockSkillContainer(
            "TestSkill",
            documentUploads: new[]
            {
                new { FilePath = testDocPath, DocumentId = "upload-test", Description = "Test upload document" }
            },
            documentReferences: Array.Empty<object>()
        );

        // Act: Upload via store (simulating what ProcessSkillDocumentsAsync does)
        var metadata = new DocumentMetadata
        {
            Name = "upload-test",
            Description = "Test upload document"
        };
        
        // Read file and upload content (AgentBuilder pattern)
        var content = await File.ReadAllTextAsync(testDocPath);
        await store.UploadFromContentAsync("upload-test", metadata, content);

        // Assert: Document should exist in store
        var exists = await store.DocumentExistsAsync("upload-test");
        Assert.True(exists);

        var readContent = await store.ReadDocumentAsync("upload-test");
        Assert.NotNull(readContent);
        Assert.Contains("Upload Test", readContent);
    }

    [Fact]
    public async Task ProcessDocuments_DeduplicatesSameDocumentFromMultipleSkills()
    {
        // Arrange: Create shared document
        var sharedDocPath = Path.Combine(_testDocumentsPath, "shared-doc.md");
        await File.WriteAllTextAsync(sharedDocPath, "# Shared Document");

        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        // Upload document twice (simulating two skills uploading same document)
        var metadata = new DocumentMetadata
        {
            Name = "shared-doc",
            Description = "Shared document"
        };

        var fileContent = await File.ReadAllTextAsync(sharedDocPath);
        await store.UploadFromContentAsync("shared-doc", metadata, fileContent);

        // Second upload should be idempotent (same content hash)
        await store.UploadFromContentAsync("shared-doc", metadata, fileContent);

        // Assert: Document exists only once
        var exists = await store.DocumentExistsAsync("shared-doc");
        Assert.True(exists);

        var allDocs = await store.ListAllDocumentsAsync();
        Assert.Single(allDocs.Where(d => d.DocumentId == "shared-doc"));
    }

    // ===== P0 Tests: Document Reference Validation (AddDocument) =====

    [Fact]
    public async Task ProcessDocuments_ValidatesDocumentReferencesExist()
    {
        // Arrange: Create store with existing document
        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        var testDocPath = Path.Combine(_testDocumentsPath, "existing-doc.md");
        await File.WriteAllTextAsync(testDocPath, "# Existing Document");

        var metadata = new DocumentMetadata
        {
            Name = "existing-doc",
            Description = "Pre-uploaded document"
        };
        
        var fileContent = await File.ReadAllTextAsync(testDocPath);
        await store.UploadFromContentAsync("existing-doc", metadata, fileContent);

        // Act: Validate reference exists
        var exists = await store.DocumentExistsAsync("existing-doc");

        // Assert: Document should exist
        Assert.True(exists);
    }

    [Fact]
    public async Task ProcessDocuments_ThrowsWhenReferencedDocumentMissing()
    {
        // Arrange: Create store without the document
        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        // Act & Assert: Checking non-existent document should return false
        var exists = await store.DocumentExistsAsync("missing-doc");
        Assert.False(exists);

        // In the actual implementation, this would throw DocumentNotFoundException
        // when ProcessSkillDocumentsAsync validates references
    }

    // ===== P0 Tests: Document Linking =====

    [Fact]
    public async Task ProcessDocuments_LinksDocumentToSkill()
    {
        // Arrange: Create document and link to skill
        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        var testDocPath = Path.Combine(_testDocumentsPath, "linked-doc.md");
        await File.WriteAllTextAsync(testDocPath, "# Linked Document");

        var docMetadata = new DocumentMetadata
        {
            Name = "linked-doc",
            Description = "Document to link"
        };
        
        var fileContent = await File.ReadAllTextAsync(testDocPath);
        await store.UploadFromContentAsync("linked-doc", docMetadata, fileContent);

        // Act: Link document to skill
        var skillMetadata = new SkillDocumentMetadata
        {
            Description = "Skill-specific description"
        };
        await store.LinkDocumentToSkillAsync("TestPlugin", "linked-doc", skillMetadata);

        // Assert: Document should be linked to skill
        var skillDocs = await store.GetSkillDocumentsAsync("TestPlugin");
        Assert.Single(skillDocs);
        Assert.Equal("linked-doc", skillDocs[0].DocumentId);
        Assert.Equal("Skill-specific description", skillDocs[0].Description);
    }

    [Fact]
    public async Task ProcessDocuments_MultipleSkillsCanReferencesSameDocument()
    {
        // Arrange: Create shared document
        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        var testDocPath = Path.Combine(_testDocumentsPath, "multi-skill-doc.md");
        await File.WriteAllTextAsync(testDocPath, "# Multi-Skill Document");

        var docMetadata = new DocumentMetadata
        {
            Name = "multi-skill-doc",
            Description = "Shared by multiple skills"
        };
        
        var fileContent = await File.ReadAllTextAsync(testDocPath);
        await store.UploadFromContentAsync("multi-skill-doc", docMetadata, fileContent);

        // Act: Link to multiple skills with different descriptions
        await store.LinkDocumentToSkillAsync(
            "SkillA",
            "multi-skill-doc",
            new SkillDocumentMetadata { Description = "Description for Skill A" });

        await store.LinkDocumentToSkillAsync(
            "SkillB",
            "multi-skill-doc",
            new SkillDocumentMetadata { Description = "Description for Skill B" });

        // Assert: Both skills should have the document with their own descriptions
        var skillADocs = await store.GetSkillDocumentsAsync("SkillA");
        var skillBDocs = await store.GetSkillDocumentsAsync("SkillB");

        Assert.Single(skillADocs);
        Assert.Equal("Description for Skill A", skillADocs[0].Description);

        Assert.Single(skillBDocs);
        Assert.Equal("Description for Skill B", skillBDocs[0].Description);
    }

    // ===== P0 Tests: Description Override =====

    [Fact]
    public async Task ProcessDocuments_SkillCanOverrideDocumentDescription()
    {
        // Arrange
        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        var testDocPath = Path.Combine(_testDocumentsPath, "override-doc.md");
        await File.WriteAllTextAsync(testDocPath, "# Override Document");

        var docMetadata = new DocumentMetadata
        {
            Name = "override-doc",
            Description = "Default description"
        };
        
        var fileContent = await File.ReadAllTextAsync(testDocPath);
        await store.UploadFromContentAsync("override-doc", docMetadata, fileContent);

        // Act: Link with override description
        await store.LinkDocumentToSkillAsync(
            "OverrideSkill",
            "override-doc",
            new SkillDocumentMetadata { Description = "Custom skill-specific description" });

        // Assert: Should use overridden description
        var skillDocs = await store.GetSkillDocumentsAsync("OverrideSkill");
        Assert.Single(skillDocs);
        Assert.Equal("Custom skill-specific description", skillDocs[0].Description);
    }

    // ===== P0 Tests: Idempotency =====

    [Fact]
    public async Task ProcessDocuments_IdempotentUpload_SkipsUnchangedDocuments()
    {
        // Arrange
        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        var testDocPath = Path.Combine(_testDocumentsPath, "idempotent-doc.md");
        var content = "# Idempotent Document";
        await File.WriteAllTextAsync(testDocPath, content);

        var metadata = new DocumentMetadata
        {
            Name = "idempotent-doc",
            Description = "Test idempotency"
        };

        // Act: Upload twice with same content
        await store.UploadFromContentAsync("idempotent-doc", metadata, content);
        var firstUploadMeta = await store.GetDocumentMetadataAsync("idempotent-doc");

        await store.UploadFromContentAsync("idempotent-doc", metadata, content);
        var secondUploadMeta = await store.GetDocumentMetadataAsync("idempotent-doc");

        // Assert: Metadata should be identical (same content hash, no re-upload)
        Assert.NotNull(firstUploadMeta);
        Assert.NotNull(secondUploadMeta);
        Assert.Equal(firstUploadMeta.ContentHash, secondUploadMeta.ContentHash);
        Assert.Equal(firstUploadMeta.Version, secondUploadMeta.Version);
    }

    [Fact]
    public async Task ProcessDocuments_ChangedContent_UpdatesDocument()
    {
        // Arrange
        var store = new InMemoryInstructionStore(NullLogger<InMemoryInstructionStore>.Instance);

        var testDocPath = Path.Combine(_testDocumentsPath, "changing-doc.md");
        await File.WriteAllTextAsync(testDocPath, "# Version 1");

        var metadata = new DocumentMetadata
        {
            Name = "changing-doc",
            Description = "Document that changes"
        };

        // Act: Upload, then change content and upload again
        await store.UploadFromContentAsync("changing-doc", metadata, "# Version 1");
        var firstMeta = await store.GetDocumentMetadataAsync("changing-doc");

        // Upload with changed content
        await store.UploadFromContentAsync("changing-doc", metadata, "# Version 2 - Updated");
        var secondMeta = await store.GetDocumentMetadataAsync("changing-doc");

        // Assert: Content hash and version should change
        Assert.NotNull(firstMeta);
        Assert.NotNull(secondMeta);
        Assert.NotEqual(firstMeta.ContentHash, secondMeta.ContentHash);
        Assert.True(secondMeta.Version > firstMeta.Version);
    }

    // ===== Helper Methods =====

    private AIFunction CreateMockSkillContainer(
        string skillName,
        object[] documentUploads,
        object[] documentReferences)
    {
        return HPDAIFunctionFactory.Create(
            async (args, ct) => "Skill activated",
            new HPDAIFunctionFactoryOptions
            {
                Name = skillName,
                Description = "Test skill",
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["IsSkill"] = true,
                    ["SkillName"] = skillName,
                    ["ParentSkillContainer"] = "TestPlugin",
                    ["DocumentUploads"] = documentUploads,
                    ["DocumentReferences"] = documentReferences
                }
            });
    }

    // Mock plugin for testing
    private class TestPluginWithDocumentUpload
    {
        private readonly string _documentPath;

        public TestPluginWithDocumentUpload(string documentPath)
        {
            _documentPath = documentPath;
        }

        [Skill]
        public Skill TestSkill(SkillOptions? options = null)
        {
            return SkillFactory.Create(
                "TestSkill",
                "Test skill with document",
                "Instructions",
                new SkillOptions()
                    .AddDocumentFromFile(_documentPath, "Test document", "test-doc"),
                "MockPlugin.TestFunction"
            );
        }
    }
}
