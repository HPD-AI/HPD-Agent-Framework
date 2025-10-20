# Bugfix: Added Missing Index Property to IIngestionResult

## Issue

The `IIngestionResult` interface was missing the `Index` property, even though the V2 design uses a scoped client pattern where each client is bound to a specific index.

## Why This Matters

When using the scoped client pattern:

```csharp
var client = factory.CreateClient("documents");
await client.IngestAsync(request);
```

The result should confirm which index the document was ingested into. This is important for:

1. **Verification** - Confirming the operation went to the correct index
2. **Logging** - Including the index in log messages
3. **Multi-tenant scenarios** - Tracking which tenant's index was used
4. **Debugging** - Troubleshooting ingestion issues

## What Was Fixed

### Interface Update

```csharp
// BEFORE (Missing Index):
public interface IIngestionResult
{
    string DocumentId { get; }
    bool Success { get; }
    string? ErrorMessage { get; }
    IReadOnlyDictionary<string, int> ArtifactCounts { get; }
    IReadOnlyDictionary<string, object> Metadata { get; }
}

// AFTER (Index Added):
public interface IIngestionResult
{
    string DocumentId { get; }
    string Index { get; }  // ✅ ADDED
    bool Success { get; }
    string? ErrorMessage { get; }
    IReadOnlyDictionary<string, int> ArtifactCounts { get; }
    IReadOnlyDictionary<string, object> Metadata { get; }
}
```

### Implementation Update

```csharp
// BEFORE:
public record IngestionResult : IIngestionResult
{
    public required string DocumentId { get; init; }
    public required bool Success { get; init; }
    // ... missing Index
}

// AFTER:
public record IngestionResult : IIngestionResult
{
    public required string DocumentId { get; init; }
    public required string Index { get; init; }  // ✅ ADDED
    public required bool Success { get; init; }
    // ...
}
```

### Factory Method Updates

```csharp
// BEFORE:
public static IngestionResult CreateSuccess(
    string documentId,
    IReadOnlyDictionary<string, int>? artifactCounts = null,
    IReadOnlyDictionary<string, object>? metadata = null)
{
    return new IngestionResult
    {
        DocumentId = documentId,
        Success = true,
        // ... missing Index
    };
}

// AFTER:
public static IngestionResult CreateSuccess(
    string documentId,
    string index,  // ✅ ADDED PARAMETER
    IReadOnlyDictionary<string, int>? artifactCounts = null,
    IReadOnlyDictionary<string, object>? metadata = null)
{
    return new IngestionResult
    {
        DocumentId = documentId,
        Index = index,  // ✅ ADDED
        Success = true,
        ArtifactCounts = artifactCounts ?? new Dictionary<string, int>(),
        Metadata = metadata ?? new Dictionary<string, object>()
    };
}
```

Same pattern applied to `CreateFailure()`.

## Impact

### Breaking Change ⚠️

This is a **minor breaking change** for anyone already implementing `IIngestionResult` or using the factory methods:

**Implementers must add:**
```csharp
public string Index { get; init; }
```

**Factory method callers must update:**
```csharp
// OLD:
var result = IngestionResult.CreateSuccess(documentId, artifactCounts);

// NEW:
var result = IngestionResult.CreateSuccess(documentId, index, artifactCounts);
```

### Migration

For BasicMemoryClient implementation:

```csharp
// OLD:
return IngestionResult.CreateSuccess(
    documentId: completedContext.DocumentId,
    artifactCounts: counts);

// NEW:
return IngestionResult.CreateSuccess(
    documentId: completedContext.DocumentId,
    index: this.Index,  // ✅ Use client's scoped index
    artifactCounts: counts);
```

## Usage Example

```csharp
var factory = serviceProvider.GetRequiredService<IMemoryClientFactory>();
var client = factory.CreateClient("documents");

using var request = await IngestionRequest.FromFileAsync("doc.pdf");
var result = await client.IngestAsync(request);

// Now you can verify the index:
Console.WriteLine($"Ingested into index: {result.Index}");
Debug.Assert(result.Index == client.Index);  // ✅ Should match

// Useful for logging:
_logger.LogInformation(
    "Document {DocumentId} ingested into {Index} with {ChunkCount} chunks",
    result.DocumentId,
    result.Index,  // ✅ Now available
    result.ArtifactCounts.GetValueOrDefault(StandardArtifacts.Chunks, 0));
```

## Checklist

- [x] `IIngestionResult.Index` property added
- [x] `IngestionResult.Index` property implemented
- [x] `CreateSuccess()` factory method updated with `index` parameter
- [x] `CreateFailure()` factory method updated with `index` parameter
- [x] XML documentation updated
- [x] Update all example code in documentation:
  - [x] IMPLEMENTATION_SUMMARY.md - Updated BasicMemoryClient example with artifact counting
  - [x] IMEMORYCLIENT_V2_CHANGES.md - Added `index` parameter to all factory method examples
  - [x] DECISION_V1_VS_V2.md - Added `index` parameter to all factory method examples
  - [x] README.md - Added `index` parameter and artifact counts to implementation example
- [ ] Update BasicMemoryClient implementation (next step)
- [ ] Update GraphMemoryClient implementation (next step)

## Files Modified

- `src/HPD.Memory.Abstractions/Client/Results.cs`
  - Lines 21-24: Added `Index` property to interface
  - Line 106: Added `Index` property to implementation
  - Line 119: Added `index` parameter to `CreateSuccess()`
  - Line 113: Set `Index` in result
  - Line 138: Added `index` parameter to `CreateFailure()`
  - Line 145: Set `Index` in result

## Status

✅ **FIXED** - All interface and implementation updates complete.

⏳ **PENDING** - Implementation updates needed in BasicMemoryClient, GraphMemoryClient, etc.

