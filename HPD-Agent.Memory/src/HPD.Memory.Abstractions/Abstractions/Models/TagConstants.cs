// Copyright (c) Einstein Essibu. All rights reserved.
// Tag constants for document and file metadata.
// Inspired by Kernel Memory but organized for our generic architecture.

namespace HPDAgent.Memory.Abstractions.Models;

/// <summary>
/// Constants for tag keys and reserved prefixes.
/// Similar to Kernel Memory's Constants but designed for our generic pipeline system.
/// </summary>
public static class TagConstants
{
    /// <summary>
    /// Prefix for reserved (system) tags.
    /// User-defined tags should NOT start with this prefix.
    /// </summary>
    public const string ReservedPrefix = "__";

    // ========================================
    // Reserved System Tags
    // ========================================

    /// <summary>
    /// Tag for document ID. Value: document identifier.
    /// Used to filter search results to a specific document.
    /// </summary>
    public const string DocumentId = "__document_id";

    /// <summary>
    /// Tag for file ID within a document. Value: file identifier.
    /// </summary>
    public const string FileId = "__file_id";

    /// <summary>
    /// Tag indicating this is a partition/chunk. Value: partition identifier.
    /// </summary>
    public const string FilePartition = "__file_part";

    /// <summary>
    /// Tag for partition number (0-based). Value: number as string.
    /// </summary>
    public const string PartitionNumber = "__part_n";

    /// <summary>
    /// Tag for section number (page, scene, segment, etc.). Value: number as string.
    /// </summary>
    public const string SectionNumber = "__sect_n";

    /// <summary>
    /// Tag for file MIME type. Value: MIME type string (e.g., "application/pdf").
    /// </summary>
    public const string FileType = "__file_type";

    /// <summary>
    /// Tag for artifact type. Value: FileArtifactType enum name.
    /// </summary>
    public const string ArtifactType = "__artifact_type";

    /// <summary>
    /// Tag for synthetic data type. Value: type name (e.g., "summary", "qa_pair").
    /// </summary>
    public const string SyntheticType = "__synth";

    /// <summary>
    /// Tag for execution ID. Value: execution identifier.
    /// Used to track specific pipeline executions for consolidation.
    /// </summary>
    public const string ExecutionId = "__execution_id";

    /// <summary>
    /// Tag for pipeline ID. Value: pipeline identifier.
    /// </summary>
    public const string PipelineId = "__pipeline_id";

    /// <summary>
    /// Tag for index/collection name. Value: index name.
    /// </summary>
    public const string Index = "__index";

    // ========================================
    // Common User Tag Conventions
    // These are NOT reserved, just recommended naming conventions
    // ========================================

    /// <summary>
    /// Suggested tag for user ownership/access.
    /// Convention: tags["user"] = ["alice", "bob"]
    /// </summary>
    public const string UserTag = "user";

    /// <summary>
    /// Suggested tag for organization/tenant.
    /// Convention: tags["organization"] = ["acme-corp"]
    /// </summary>
    public const string OrganizationTag = "organization";

    /// <summary>
    /// Suggested tag for department.
    /// Convention: tags["department"] = ["engineering", "research"]
    /// </summary>
    public const string DepartmentTag = "department";

    /// <summary>
    /// Suggested tag for project.
    /// Convention: tags["project"] = ["project-alpha"]
    /// </summary>
    public const string ProjectTag = "project";

    /// <summary>
    /// Suggested tag for visibility/access level.
    /// Convention: tags["visibility"] = ["public"] or ["private", "team-only"]
    /// </summary>
    public const string VisibilityTag = "visibility";

    /// <summary>
    /// Suggested tag for content category.
    /// Convention: tags["category"] = ["research-paper", "blog-post"]
    /// </summary>
    public const string CategoryTag = "category";

    /// <summary>
    /// Suggested tag for version.
    /// Convention: tags["version"] = ["1.0", "2.0"]
    /// </summary>
    public const string VersionTag = "version";

    /// <summary>
    /// Suggested tag for language.
    /// Convention: tags["language"] = ["en", "es", "fr"]
    /// </summary>
    public const string LanguageTag = "language";

    // ========================================
    // Helper Methods
    // ========================================

    /// <summary>
    /// Check if a tag key is reserved (system tag).
    /// </summary>
    public static bool IsReserved(string tagKey)
    {
        return tagKey.StartsWith(ReservedPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Validate a tag key doesn't contain invalid characters.
    /// Throws ArgumentException if invalid.
    /// </summary>
    /// <exception cref="ArgumentException">Tag key contains invalid characters</exception>
    public static void ValidateTagKey(string tagKey)
    {
        if (string.IsNullOrWhiteSpace(tagKey))
        {
            throw new ArgumentException("Tag key cannot be null or whitespace", nameof(tagKey));
        }

        // '=' is reserved for query param encoding and backward compatibility
        if (tagKey.Contains('=', StringComparison.Ordinal))
        {
            throw new ArgumentException("Tag key cannot contain '=' character", nameof(tagKey));
        }

        // ':' is reserved for string representation formatting
        if (tagKey.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Tag key cannot contain ':' character", nameof(tagKey));
        }

        // ';' is reserved for string representation formatting
        if (tagKey.Contains(';', StringComparison.Ordinal))
        {
            throw new ArgumentException("Tag key cannot contain ';' character", nameof(tagKey));
        }
    }
}
