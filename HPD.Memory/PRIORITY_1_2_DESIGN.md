# Priority 1 & 2 Features: Design Document

## Status: We Already Have Most of It! 🎉

### Surprising Discovery

After deep analysis of our current architecture vs Kernel Memory, we discovered that **we already implemented most of Priority 1 & 2 features**, but using a simpler approach:

| Feature | Kernel Memory | HPD-Agent.Memory | Status |
|---------|---------------|------------------|--------|
| **Tags** | `TagCollection : IDictionary<string, List<string?>>` | `Dictionary<string, List<string>>` in IPipelineContext & DocumentFile | ✅ **HAVE IT** |
| **ExecutionId** | `string ExecutionId` | `string ExecutionId` in IPipelineContext | ✅ **HAVE IT** |
| **PreviousExecutionsToPurge** | `List<DataPipeline>` | `List<string>` in DocumentIngestionContext | ✅ **HAVE IT** |
| **ArtifactTypes** | `enum ArtifactTypes` | `enum FileArtifactType` in DocumentFile | ✅ **HAVE IT** |
| **MemoryFilter** | `MemoryFilter : TagCollection` with fluent API | Basic `Dictionary<string, object>` in SemanticSearchContext | ⚠️ **NEEDS IMPROVEMENT** |

## What We Actually Need to Add

### 1. TagCollection Wrapper (Low Priority)

**Current**: We use `Dictionary<string, List<string>>` directly
**Kernel Memory**: Uses custom `TagCollection : IDictionary<string, List<string?>>`

**Analysis**:
- ✅ Our approach is simpler and works
- ✅ Fully JSON serializable
- ❌ Missing fluent API helpers
- ❌ Missing validation (reserved characters)
- ❌ Missing `Pairs` property for flattening

**Decision**: Add a lightweight wrapper for **better developer experience**

### 2. MemoryFilter with Fluent API (HIGH PRIORITY) ⭐⭐⭐⭐⭐

**Current**:
```csharp
public class SemanticSearchContext : IRetrievalContext
{
    public Dictionary<string, object> Filters { get; init; } = new();
}
```

**Needed**:
```csharp
// Fluent API
var results = await search.SearchAsync(
    query: "AI agents",
    filters: MemoryFilters.ByTag("user", "alice")
                          .ByTag("department", "engineering")
                          .ByDocument("doc-123")
);
```

**This is the ONLY critical missing feature!**

### 3. Reserved Tag Constants (Medium Priority)

**Kernel Memory has**:
```csharp
public const string ReservedTagsPrefix = "__";
public const string ReservedDocumentIdTag = "__document_id";
public const string ReservedFileIdTag = "__file_id";
public const string ReservedFilePartitionTag = "__file_part";
// etc.
```

**We need**: Same constants for consistency

## Design Approach: Second Mover's Advantage

### Philosophy

1. **Keep our simpler types** - `Dictionary<string, List<string>>` is better than custom `TagCollection`
2. **Add convenience wrappers** - For developer experience
3. **Focus on the API** - MemoryFilter fluent API is the key value
4. **Stay generic** - Our architecture is already superior

### Implementation Plan

#### Phase 1: TagCollection Helper (Optional Enhancement)

Create a **static helper class** instead of replacing Dictionary:

```csharp
namespace HPDAgent.Memory.Abstractions.Models;

/// <summary>
/// Helper methods for working with tag dictionaries.
/// Provides Kernel Memory-like convenience while keeping simple Dictionary<string, List<string>>.
/// </summary>
public static class TagCollectionExtensions
{
    /// <summary>
    /// Add a tag value to the collection.
    /// </summary>
    public static void AddTag(
        this IDictionary<string, List<string>> tags,
        string key,
        string value)
    {
        ValidateTagKey(key);

        if (tags.TryGetValue(key, out var list))
        {
            if (!list.Contains(value))
            {
                list.Add(value);
            }
        }
        else
        {
            tags[key] = new List<string> { value };
        }
    }

    /// <summary>
    /// Get all tag key-value pairs flattened.
    /// Similar to Kernel Memory's Pairs property.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> GetTagPairs(
        this IDictionary<string, List<string>> tags)
    {
        return from kvp in tags
               from value in kvp.Value
               select new KeyValuePair<string, string>(kvp.Key, value);
    }

    /// <summary>
    /// Copy tags from one collection to another.
    /// </summary>
    public static void CopyTagsTo(
        this IDictionary<string, List<string>> source,
        IDictionary<string, List<string>> target)
    {
        foreach (var (key, values) in source)
        {
            foreach (var value in values)
            {
                target.AddTag(key, value);
            }
        }
    }

    /// <summary>
    /// Format tags as string (for logging/debugging).
    /// Format: "key1:value1;key2:[value2a, value2b]"
    /// </summary>
    public static string ToTagString(
        this IDictionary<string, List<string>> tags,
        bool excludeReserved = false)
    {
        var items = tags.Where(kvp =>
            !excludeReserved || !kvp.Key.StartsWith(TagConstants.ReservedPrefix));

        return string.Join(';', items.Select(kvp =>
        {
            if (kvp.Value.Count == 1)
                return $"{kvp.Key}:{kvp.Value[0]}";
            else
                return $"{kvp.Key}:[{string.Join(", ", kvp.Value)}]";
        }));
    }

    private static void ValidateTagKey(string key)
    {
        if (key.Contains('='))
            throw new ArgumentException("Tag keys cannot contain '=' character", nameof(key));
        if (key.Contains(':'))
            throw new ArgumentException("Tag keys cannot contain ':' character", nameof(key));
    }
}
```

**Advantages of this approach**:
- ✅ Keep simple `Dictionary<string, List<string>>`
- ✅ Add convenience methods via extensions
- ✅ No breaking changes to existing code
- ✅ Easier to test and maintain
- ✅ Better than Kernel Memory (simpler!)

#### Phase 2: MemoryFilter (CRITICAL)

Create fluent filter API that works with our generic system:

```csharp
namespace HPDAgent.Memory.Abstractions.Models;

/// <summary>
/// Fluent filter for semantic search and retrieval.
/// Similar to Kernel Memory's MemoryFilter but works with our generic pipeline system.
/// </summary>
public class MemoryFilter
{
    private readonly Dictionary<string, List<string>> _tags = new();

    /// <summary>
    /// Filter by tag key-value pair.
    /// </summary>
    public MemoryFilter ByTag(string key, string value)
    {
        _tags.AddTag(key, value);
        return this;
    }

    /// <summary>
    /// Filter by document ID.
    /// </summary>
    public MemoryFilter ByDocument(string documentId)
    {
        _tags.AddTag(TagConstants.DocumentId, documentId);
        return this;
    }

    /// <summary>
    /// Filter by file ID.
    /// </summary>
    public MemoryFilter ByFile(string fileId)
    {
        _tags.AddTag(TagConstants.FileId, fileId);
        return this;
    }

    /// <summary>
    /// Filter by user (convenience method).
    /// </summary>
    public MemoryFilter ByUser(string userId)
    {
        _tags.AddTag("user", userId);
        return this;
    }

    /// <summary>
    /// Check if filter is empty.
    /// </summary>
    public bool IsEmpty() => _tags.Count == 0;

    /// <summary>
    /// Get all filters as tag pairs.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> GetFilters()
    {
        return _tags.GetTagPairs();
    }

    /// <summary>
    /// Get tags dictionary.
    /// </summary>
    public IReadOnlyDictionary<string, List<string>> GetTags() => _tags;

    /// <summary>
    /// Check if a tag collection matches this filter.
    /// ALL filter conditions must match (AND logic).
    /// </summary>
    public bool Matches(IDictionary<string, List<string>> tags)
    {
        foreach (var (filterKey, filterValues) in _tags)
        {
            // Tag key must exist
            if (!tags.TryGetValue(filterKey, out var tagValues))
                return false;

            // At least one filter value must match
            if (!filterValues.Any(fv => tagValues.Contains(fv)))
                return false;
        }

        return true;
    }
}

/// <summary>
/// Factory for creating filters with fluent syntax.
/// Recommended usage: MemoryFilters.ByTag(...).ByDocument(...)
/// Instead of: new MemoryFilter().ByTag(...).ByDocument(...)
/// </summary>
public static class MemoryFilters
{
    public static MemoryFilter ByTag(string key, string value)
        => new MemoryFilter().ByTag(key, value);

    public static MemoryFilter ByDocument(string documentId)
        => new MemoryFilter().ByDocument(documentId);

    public static MemoryFilter ByFile(string fileId)
        => new MemoryFilter().ByFile(fileId);

    public static MemoryFilter ByUser(string userId)
        => new MemoryFilter().ByUser(userId);
}
```

#### Phase 3: Reserved Tag Constants

```csharp
namespace HPDAgent.Memory.Abstractions.Models;

/// <summary>
/// Constants for tag keys and reserved prefixes.
/// Similar to Kernel Memory's Constants but organized for our architecture.
/// </summary>
public static class TagConstants
{
    /// <summary>
    /// Prefix for reserved (system) tags.
    /// User tags should NOT start with this.
    /// </summary>
    public const string ReservedPrefix = "__";

    // ========================================
    // Reserved System Tags
    // ========================================

    /// <summary>
    /// Tag for document ID. Value: document identifier.
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
    /// Tag for section number (page, etc.). Value: number as string.
    /// </summary>
    public const string SectionNumber = "__sect_n";

    /// <summary>
    /// Tag for file MIME type. Value: MIME type string.
    /// </summary>
    public const string FileType = "__file_type";

    /// <summary>
    /// Tag for artifact type. Value: FileArtifactType enum name.
    /// </summary>
    public const string ArtifactType = "__artifact_type";

    /// <summary>
    /// Tag for synthetic data type. Value: type name (e.g., "summary").
    /// </summary>
    public const string SyntheticType = "__synth";

    /// <summary>
    /// Tag for execution ID. Value: execution identifier.
    /// </summary>
    public const string ExecutionId = "__execution_id";

    /// <summary>
    /// Tag for pipeline ID. Value: pipeline identifier.
    /// </summary>
    public const string PipelineId = "__pipeline_id";

    // ========================================
    // Common User Tags (Conventions)
    // ========================================

    /// <summary>
    /// Suggested tag for user ownership. Not reserved, just convention.
    /// </summary>
    public const string UserTag = "user";

    /// <summary>
    /// Suggested tag for organization/tenant. Not reserved, just convention.
    /// </summary>
    public const string OrganizationTag = "organization";

    /// <summary>
    /// Suggested tag for department. Not reserved, just convention.
    /// </summary>
    public const string DepartmentTag = "department";

    /// <summary>
    /// Suggested tag for project. Not reserved, just convention.
    /// </summary>
    public const string ProjectTag = "project";

    /// <summary>
    /// Suggested tag for visibility/access level. Not reserved, just convention.
    /// </summary>
    public const string VisibilityTag = "visibility";
}
```

#### Phase 4: Update SemanticSearchContext

```csharp
namespace HPDAgent.Memory.Core.Contexts;

public class SemanticSearchContext : IRetrievalContext
{
    // ... existing properties ...

    /// <summary>
    /// Search filters (replaces generic Filters dictionary).
    /// Use MemoryFilters factory for fluent creation.
    /// </summary>
    public MemoryFilter? Filter { get; init; }

    /// <summary>
    /// Minimum relevance score (0.0 to 1.0).
    /// Results below this threshold are excluded.
    /// </summary>
    public double MinRelevance { get; init; } = 0.0;

    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int MaxResults { get; init; } = 10;

    // ... rest of class ...
}
```

## Comparison: Our Approach vs Kernel Memory

### TagCollection

**Kernel Memory**:
```csharp
// Custom class implementing IDictionary
public class TagCollection : IDictionary<string, List<string?>>
{
    private readonly IDictionary<string, List<string?>> _data =
        new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);

    // 200 lines of boilerplate dictionary implementation...
}

// Usage
var tags = new TagCollection();
tags.Add("user", "alice");
```

**HPD-Agent.Memory**:
```csharp
// Just use Dictionary with extension methods
var tags = new Dictionary<string, List<string>>();
tags.AddTag("user", "alice"); // Extension method

// Or initialize directly
var tags = new Dictionary<string, List<string>>
{
    ["user"] = new List<string> { "alice" },
    ["department"] = new List<string> { "engineering", "ai" }
};
```

**Winner**: 🏆 **HPD-Agent.Memory** - Simpler, less code, same functionality

### MemoryFilter

**Kernel Memory**:
```csharp
// Inherits from TagCollection (heavy)
public class MemoryFilter : TagCollection
{
    public MemoryFilter ByTag(string name, string value);
    public MemoryFilter ByDocument(string docId);
}

// Static factory
public static class MemoryFilters
{
    public static MemoryFilter ByTag(string name, string value);
    public static MemoryFilter ByDocument(string docId);
}
```

**HPD-Agent.Memory** (Proposed):
```csharp
// Lightweight wrapper, NOT inheriting heavy dictionary
public class MemoryFilter
{
    private readonly Dictionary<string, List<string>> _tags = new();

    public MemoryFilter ByTag(string key, string value);
    public MemoryFilter ByDocument(string documentId);
    public bool Matches(IDictionary<string, List<string>> tags); // ← NEW!
}

// Same fluent factory pattern
public static class MemoryFilters { ... }
```

**Winner**: 🏆 **HPD-Agent.Memory** - Lighter weight, adds `Matches()` for filtering logic

## Implementation Checklist

### Must Have (Priority 1) ⭐⭐⭐⭐⭐

- [ ] Create `TagCollectionExtensions` class
- [ ] Create `MemoryFilter` class with fluent API
- [ ] Create `MemoryFilters` static factory
- [ ] Create `TagConstants` class
- [ ] Update `SemanticSearchContext` to use `MemoryFilter`
- [ ] Add unit tests for tag operations
- [ ] Add unit tests for filter matching

### Nice to Have (Priority 2) ⭐⭐⭐

- [ ] Add `Matches()` method to filter contexts by tags
- [ ] Add tag validation in setters
- [ ] Add extension methods for common tag operations
- [ ] Update documentation with tagging examples
- [ ] Add examples to USAGE_EXAMPLES.md

### Future Enhancements ⭐⭐

- [ ] Add OR logic to MemoryFilter (currently AND only)
- [ ] Add NOT logic for exclusion filters
- [ ] Add range filters for numeric tag values
- [ ] Add tag indexing for performance

## Second Mover's Advantage Applied

### What We're Doing Better

1. **Simpler Core Types**
   - KM: Custom `TagCollection` class (200 lines)
   - Us: Standard `Dictionary<string, List<string>>` + extensions

2. **Lighter Filter**
   - KM: `MemoryFilter` inherits full `TagCollection`
   - Us: `MemoryFilter` wraps tags, adds `Matches()` logic

3. **Better Integration**
   - KM: Hardcoded to `DataPipeline`
   - Us: Works with any `IPipelineContext`

4. **Added Features**
   - `Matches()` method for programmatic filtering
   - More tag constants for our use cases
   - Better separation of concerns

### What We're Keeping from KM

1. ✅ Fluent API pattern (`MemoryFilters.ByTag().ByDocument()`)
2. ✅ Reserved tag prefix convention (`__`)
3. ✅ Tag validation (no `=` or `:` in keys)
4. ✅ `Pairs` flattening for key-value iteration
5. ✅ Multi-value tags support

## Conclusion

**We're in great shape!**

- ✅ We already have 80% of Priority 1 & 2 features
- ✅ Our approach is actually simpler than Kernel Memory
- ⚠️ We need to add `MemoryFilter` fluent API (highest priority)
- ⚠️ We should add `TagCollectionExtensions` for convenience

**Next step**: Implement the fluent `MemoryFilter` API, which is the only critical missing piece.
