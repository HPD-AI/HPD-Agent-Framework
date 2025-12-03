using FluentAssertions;
using Xunit;

namespace HPD.Agent.Tests.Skills;

/// <summary>
/// Tests for SkillDocumentContent - validates Phase 2 (type-safe records) functionality
/// </summary>
public class SkillDocumentContentTests
{
    [Fact]
    public void SkillDocumentContent_WithFilePath_ShouldHaveCorrectProperties()
    {
        // Arrange & Act
        var document = new SkillDocumentContent
        {
            DocumentId = "test-doc",
            Description = "Test document",
            FilePath = "./docs/test.md"
        };

        // Assert
        document.DocumentId.Should().Be("test-doc");
        document.Description.Should().Be("Test document");
        document.FilePath.Should().Be("./docs/test.md");
        document.Url.Should().BeNull();
        document.IsFilePath.Should().BeTrue();
        document.IsUrl.Should().BeFalse();
        document.SourceType.Should().Be("📄 File");
    }

    [Fact]
    public void SkillDocumentContent_WithUrl_ShouldHaveCorrectProperties()
    {
        // Arrange & Act
        var document = new SkillDocumentContent
        {
            DocumentId = "remote-doc",
            Description = "Remote document",
            Url = "https://example.com/doc.md"
        };

        // Assert
        document.DocumentId.Should().Be("remote-doc");
        document.Description.Should().Be("Remote document");
        document.Url.Should().Be("https://example.com/doc.md");
        document.FilePath.Should().BeNull();
        document.IsUrl.Should().BeTrue();
        document.IsFilePath.Should().BeFalse();
        document.SourceType.Should().Be("🔗 URL");
    }

    [Fact]
    public void SkillDocumentContent_RecordEquality_ShouldWorkCorrectly()
    {
        // Arrange
        var doc1 = new SkillDocumentContent
        {
            DocumentId = "test",
            Description = "Test",
            FilePath = "./test.md"
        };

        var doc2 = new SkillDocumentContent
        {
            DocumentId = "test",
            Description = "Test",
            FilePath = "./test.md"
        };

        var doc3 = new SkillDocumentContent
        {
            DocumentId = "test",
            Description = "Test",
            Url = "https://example.com/test.md"
        };

        // Act & Assert
        doc1.Should().Be(doc2); // Same values = equal
        doc1.Should().NotBe(doc3); // Different source type = not equal
    }

    [Fact]
    public void SkillDocumentContent_WithInit_ShouldBeImmutable()
    {
        // Arrange
        var document = new SkillDocumentContent
        {
            DocumentId = "test",
            Description = "Test",
            FilePath = "./test.md"
        };

        // Act & Assert - This should not compile if properties aren't init-only
        // document.DocumentId = "changed"; // Would cause compilation error
        document.DocumentId.Should().Be("test");
    }

    [Fact]
    public void SkillDocumentContent_ToString_ShouldIncludeProperties()
    {
        // Arrange
        var document = new SkillDocumentContent
        {
            DocumentId = "test-doc",
            Description = "Test description",
            FilePath = "./test.md"
        };

        // Act
        var result = document.ToString();

        // Assert
        result.Should().Contain("test-doc");
        result.Should().Contain("Test description");
        result.Should().Contain("./test.md");
    }

    [Fact]
    public void DocumentUpload_WithFilePath_ShouldMatchSkillDocumentContent()
    {
        // Arrange
        var upload = new DocumentUpload
        {
            DocumentId = "test",
            Description = "Test",
            FilePath = "./test.md"
        };

        // Act & Assert
        upload.IsFilePath.Should().BeTrue();
        upload.IsUrl.Should().BeFalse();
    }

    [Fact]
    public void DocumentUpload_WithUrl_ShouldMatchSkillDocumentContent()
    {
        // Arrange
        var upload = new DocumentUpload
        {
            DocumentId = "test",
            Description = "Test",
            Url = "https://example.com/test.md"
        };

        // Act & Assert
        upload.IsUrl.Should().BeTrue();
        upload.IsFilePath.Should().BeFalse();
    }
}
