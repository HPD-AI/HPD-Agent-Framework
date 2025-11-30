using FluentAssertions;
using HPD.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace HPD_Agent.Tests.Skills;

/// <summary>
/// Tests for SkillInstructionMiddleware - validates that it prefers new type-safe format
/// </summary>
public class SkillInstructionMiddlewareTests
{
    [Fact]
    public void BuildDocumentSection_WithTypeSafeSkillDocuments_ShouldUseNewFormat()
    {
        // Arrange
        var skillFunction = HPDAIFunctionFactory.Create(
            async (args, ct) => "test",
            new HPDAIFunctionFactoryOptions
            {
                Name = "TestSkill",
                Description = "Test skill",
                SkillDocuments = new[]
                {
                    new SkillDocumentContent
                    {
                        DocumentId = "doc1",
                        Description = "First document",
                        FilePath = "./docs/file1.md"
                    },
                    new SkillDocumentContent
                    {
                        DocumentId = "doc2",
                        Description = "Second document",
                        Url = "https://example.com/doc2.md"
                    }
                }
            });

        // Act
        var result = SkillInstructionMiddleware.BuildDocumentSectionForTesting(skillFunction);

        // Assert
        result.Should().Contain("📚 **Available Documents:**");
        result.Should().Contain("doc1: First document (📄 File)");
        result.Should().Contain("doc2: Second document (🔗 URL)");
        result.Should().Contain("Use `read_skill_document(documentId)` to retrieve document content.");
    }


    [Fact]
    public void BuildDocumentSection_WithNoDocuments_ShouldNotShowSection()
    {
        // Arrange
        var skillFunction = HPDAIFunctionFactory.Create(
            async (args, ct) => "test",
            new HPDAIFunctionFactoryOptions
            {
                Name = "TestSkill",
                Description = "Test skill"
            });

        // Act
        var result = SkillInstructionMiddleware.BuildDocumentSectionForTesting(skillFunction);

        // Assert
        result.Should().NotContain("📚 **Available Documents:**");
        result.Should().NotContain("read_skill_document");
    }

    [Fact]
    public void BuildDocumentSection_WithUrlDocument_ShouldShowUrlIndicator()
    {
        // Arrange
        var skillFunction = HPDAIFunctionFactory.Create(
            async (args, ct) => "test",
            new HPDAIFunctionFactoryOptions
            {
                Name = "TestSkill",
                Description = "Test skill",
                SkillDocuments = new[]
                {
                    new SkillDocumentContent
                    {
                        DocumentId = "url-doc",
                        Description = "Remote documentation",
                        Url = "https://docs.example.com/api.md"
                    }
                }
            });

        // Act
        var result = SkillInstructionMiddleware.BuildDocumentSectionForTesting(skillFunction);

        // Assert
        result.Should().Contain("🔗 URL");
        result.Should().Contain("url-doc: Remote documentation");
    }
}
