// Copyright (c) Einstein Essibu. All rights reserved.
// Fluent filter API for semantic search and retrieval.
// Inspired by Kernel Memory's MemoryFilter but lighter weight and more powerful.

namespace HPDAgent.Memory.Abstractions.Models;

/// <summary>
/// Fluent filter for semantic search and retrieval operations.
/// Similar to Kernel Memory's MemoryFilter but designed for our generic pipeline system.
///
/// Second Mover's Advantage:
/// - Kernel Memory: Inherits from heavy TagCollection class
/// - Us: Lightweight wrapper with added Matches() functionality
/// - Better performance, cleaner API, more flexible
///
/// Example usage:
/// <code>
/// var filter = MemoryFilters.ByTag("user", "alice")
///                           .ByTag("department", "engineering")
///                           .ByDocument("doc-123");
///
/// var results = await searchClient.SearchAsync(
///     query: "AI agents",
///     filter: filter
/// );
/// </code>
/// </summary>
public class MemoryFilter
{
    private readonly Dictionary<string, List<string>> _tags = new();

    /// <summary>
    /// Filter by any tag key-value pair.
    /// Multiple values for the same key are OR'd (any value matches).
    /// Different keys are AND'd (all keys must match).
    /// </summary>
    /// <param name="key">Tag key</param>
    /// <param name="value">Tag value</param>
    /// <returns>This filter for fluent chaining</returns>
    public MemoryFilter ByTag(string key, string value)
    {
        _tags.AddTag(key, value);
        return this;
    }

    /// <summary>
    /// Filter by multiple values for the same tag key.
    /// Any of the values can match (OR logic within the key).
    /// </summary>
    /// <param name="key">Tag key</param>
    /// <param name="values">Tag values (any can match)</param>
    /// <returns>This filter for fluent chaining</returns>
    public MemoryFilter ByTags(string key, params string[] values)
    {
        foreach (var value in values)
        {
            _tags.AddTag(key, value);
        }
        return this;
    }

    /// <summary>
    /// Filter by document ID.
    /// Convenience method for filtering to a specific document.
    /// </summary>
    /// <param name="documentId">Document identifier</param>
    /// <returns>This filter for fluent chaining</returns>
    public MemoryFilter ByDocument(string documentId)
    {
        _tags.AddTag(TagConstants.DocumentId, documentId);
        return this;
    }

    /// <summary>
    /// Filter by file ID.
    /// Useful for finding specific files within a document.
    /// </summary>
    /// <param name="fileId">File identifier</param>
    /// <returns>This filter for fluent chaining</returns>
    public MemoryFilter ByFile(string fileId)
    {
        _tags.AddTag(TagConstants.FileId, fileId);
        return this;
    }

    /// <summary>
    /// Filter by partition number.
    /// </summary>
    /// <param name="partitionNumber">Partition number (0-based)</param>
    /// <returns>This filter for fluent chaining</returns>
    public MemoryFilter ByPartition(int partitionNumber)
    {
        _tags.AddTag(TagConstants.PartitionNumber, partitionNumber.ToString());
        return this;
    }

    /// <summary>
    /// Filter by artifact type.
    /// </summary>
    /// <param name="artifactType">Type of artifact</param>
    /// <returns>This filter for fluent chaining</returns>
    public MemoryFilter ByArtifactType(FileArtifactType artifactType)
    {
        _tags.AddTag(TagConstants.ArtifactType, artifactType.ToString());
        return this;
    }

    /// <summary>
    /// Filter by execution ID.
    /// Useful for finding results from a specific pipeline execution.
    /// </summary>
    /// <param name="executionId">Execution identifier</param>
    /// <returns>This filter for fluent chaining</returns>
    public MemoryFilter ByExecution(string executionId)
    {
        _tags.AddTag(TagConstants.ExecutionId, executionId);
        return this;
    }

    /// <summary>
    /// Filter by user.
    /// Convenience method for the common "user" tag.
    /// </summary>
    /// <param name="userId">User identifier</param>
    /// <returns>This filter for fluent chaining</returns>
    public MemoryFilter ByUser(string userId)
    {
        _tags.AddTag(TagConstants.UserTag, userId);
        return this;
    }

    /// <summary>
    /// Filter by organization/tenant.
    /// Convenience method for the common "organization" tag.
    /// </summary>
    /// <param name="organizationId">Organization identifier</param>
    /// <returns>This filter for fluent chaining</returns>
    public MemoryFilter ByOrganization(string organizationId)
    {
        _tags.AddTag(TagConstants.OrganizationTag, organizationId);
        return this;
    }

    /// <summary>
    /// Filter by department.
    /// Convenience method for the common "department" tag.
    /// </summary>
    /// <param name="department">Department name</param>
    /// <returns>This filter for fluent chaining</returns>
    public MemoryFilter ByDepartment(string department)
    {
        _tags.AddTag(TagConstants.DepartmentTag, department);
        return this;
    }

    /// <summary>
    /// Filter by project.
    /// Convenience method for the common "project" tag.
    /// </summary>
    /// <param name="projectId">Project identifier</param>
    /// <returns>This filter for fluent chaining</returns>
    public MemoryFilter ByProject(string projectId)
    {
        _tags.AddTag(TagConstants.ProjectTag, projectId);
        return this;
    }

    /// <summary>
    /// Check if this filter is empty (no filter criteria).
    /// </summary>
    /// <returns>True if no filter criteria have been added</returns>
    public bool IsEmpty() => _tags.Count == 0;

    /// <summary>
    /// Get all filter criteria as flattened key-value pairs.
    /// Compatible with Kernel Memory's GetFilters() method.
    /// </summary>
    /// <returns>Enumerable of key-value pairs</returns>
    public IEnumerable<KeyValuePair<string, string>> GetFilters()
    {
        return _tags.GetTagPairs();
    }

    /// <summary>
    /// Get the underlying tags dictionary.
    /// </summary>
    /// <returns>Read-only view of filter tags</returns>
    public IReadOnlyDictionary<string, List<string>> GetTags() => _tags;

    /// <summary>
    /// Check if a tag collection matches this filter.
    /// ALL filter keys must match (AND logic between keys).
    /// At least ONE value per key must match (OR logic within each key).
    ///
    /// This is an improvement over Kernel Memory - they don't provide this method!
    /// </summary>
    /// <param name="tags">Tags to check against this filter</param>
    /// <returns>True if tags match all filter criteria</returns>
    /// <example>
    /// Filter: user=alice, department=[engineering, research]
    /// Tags: user=alice, department=engineering, project=alpha
    /// Result: TRUE (user matches, department has matching value)
    ///
    /// Tags: user=bob, department=engineering
    /// Result: FALSE (user doesn't match)
    ///
    /// Tags: user=alice, department=marketing
    /// Result: FALSE (department doesn't have matching value)
    /// </example>
    public bool Matches(IDictionary<string, List<string>> tags)
    {
        // Empty filter matches everything
        if (IsEmpty())
        {
            return true;
        }

        // Check each filter key
        foreach (var (filterKey, filterValues) in _tags)
        {
            // Tag key must exist in the tags being checked
            if (!tags.TryGetValue(filterKey, out var tagValues) || tagValues.Count == 0)
            {
                return false;
            }

            // At least one filter value must match (OR logic within key)
            bool hasMatch = filterValues.Any(filterValue =>
                tagValues.Contains(filterValue, StringComparer.Ordinal));

            if (!hasMatch)
            {
                return false;
            }
        }

        // All filter keys matched
        return true;
    }

    /// <summary>
    /// Get a string representation of this filter (for debugging).
    /// Format: "user:alice;department:[engineering, research]"
    /// </summary>
    /// <returns>Formatted filter string</returns>
    public override string ToString()
    {
        return _tags.ToTagString();
    }

    /// <summary>
    /// Get number of filter criteria (unique tag keys).
    /// </summary>
    /// <returns>Number of tag keys in filter</returns>
    public int Count => _tags.Count;

    /// <summary>
    /// Clear all filter criteria.
    /// </summary>
    public void Clear()
    {
        _tags.Clear();
    }

    /// <summary>
    /// Clone this filter.
    /// </summary>
    /// <returns>New MemoryFilter with same criteria</returns>
    public MemoryFilter Clone()
    {
        var clone = new MemoryFilter();
        foreach (var (key, values) in _tags)
        {
            foreach (var value in values)
            {
                clone._tags.AddTag(key, value);
            }
        }
        return clone;
    }
}

/// <summary>
/// Factory for creating MemoryFilter instances with fluent syntax.
/// Recommended usage: MemoryFilters.ByTag(...).ByDocument(...)
/// Instead of: new MemoryFilter().ByTag(...).ByDocument(...)
///
/// This provides a cleaner, more discoverable API.
/// </summary>
public static class MemoryFilters
{
    /// <summary>
    /// Create a filter by tag key-value.
    /// </summary>
    public static MemoryFilter ByTag(string key, string value)
        => new MemoryFilter().ByTag(key, value);

    /// <summary>
    /// Create a filter by multiple tag values for one key.
    /// </summary>
    public static MemoryFilter ByTags(string key, params string[] values)
        => new MemoryFilter().ByTags(key, values);

    /// <summary>
    /// Create a filter by document ID.
    /// </summary>
    public static MemoryFilter ByDocument(string documentId)
        => new MemoryFilter().ByDocument(documentId);

    /// <summary>
    /// Create a filter by file ID.
    /// </summary>
    public static MemoryFilter ByFile(string fileId)
        => new MemoryFilter().ByFile(fileId);

    /// <summary>
    /// Create a filter by partition number.
    /// </summary>
    public static MemoryFilter ByPartition(int partitionNumber)
        => new MemoryFilter().ByPartition(partitionNumber);

    /// <summary>
    /// Create a filter by artifact type.
    /// </summary>
    public static MemoryFilter ByArtifactType(FileArtifactType artifactType)
        => new MemoryFilter().ByArtifactType(artifactType);

    /// <summary>
    /// Create a filter by execution ID.
    /// </summary>
    public static MemoryFilter ByExecution(string executionId)
        => new MemoryFilter().ByExecution(executionId);

    /// <summary>
    /// Create a filter by user.
    /// </summary>
    public static MemoryFilter ByUser(string userId)
        => new MemoryFilter().ByUser(userId);

    /// <summary>
    /// Create a filter by organization.
    /// </summary>
    public static MemoryFilter ByOrganization(string organizationId)
        => new MemoryFilter().ByOrganization(organizationId);

    /// <summary>
    /// Create a filter by department.
    /// </summary>
    public static MemoryFilter ByDepartment(string department)
        => new MemoryFilter().ByDepartment(department);

    /// <summary>
    /// Create a filter by project.
    /// </summary>
    public static MemoryFilter ByProject(string projectId)
        => new MemoryFilter().ByProject(projectId);
}
