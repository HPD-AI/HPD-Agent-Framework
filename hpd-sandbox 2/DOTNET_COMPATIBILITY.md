# .NET Compatibility Guide

## Target Framework Requirements

The enhanced sandbox implementation requires **.NET 7.0 or later** due to the use of:

- `LibraryImport` attribute (replaces `DllImport` for better AOT support)
- `partial` methods with `LibraryImport`
- Collection expressions (`[]` syntax)
- File-scoped namespaces

## Framework Compatibility Matrix

| Feature | .NET 6 | .NET 7 | .NET 8 | .NET 9 |
|---------|--------|--------|--------|--------|
| `LibraryImport` | ❌ | ✅ | ✅ | ✅ |
| Collection expressions | ❌ | ❌ | ✅ | ✅ |
| Native AOT | ⚠️ | ✅ | ✅ | ✅ |
| Enhanced sandbox | ❌ | ✅* | ✅ | ✅ |

*Requires minor syntax adjustments

## If You're on .NET 6

You have two options:

### Option A: Upgrade to .NET 7+ (Recommended)

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
</PropertyGroup>
```

### Option B: Modify Code for .NET 6 Compatibility

Replace `LibraryImport` with `DllImport`:

```csharp
// .NET 7+ (current code)
[LibraryImport("libc", SetLastError = true)]
public static partial int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

// .NET 6 compatible
[DllImport("libc", SetLastError = true)]
public static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);
```

Replace collection expressions:

```csharp
// .NET 8+ (current code)
public string[] AllowedDomains { get; init; } = [];
public static readonly string[] DangerousFiles = [".gitconfig", ".bashrc"];

// .NET 6/7 compatible
public string[] AllowedDomains { get; init; } = Array.Empty<string>();
public static readonly string[] DangerousFiles = new[] { ".gitconfig", ".bashrc" };
```

## .NET 6 Compatibility Script

Run this script to automatically convert the code:

```bash
#!/bin/bash
# Convert .NET 8 syntax to .NET 6 compatible

find . -name "*.cs" -exec sed -i \
  -e 's/\[LibraryImport/[DllImport/g' \
  -e 's/public static partial/public static extern/g' \
  -e 's/ = \[\];/ = Array.Empty<string>();/g' \
  {} \;

echo "Conversion complete. Manual review recommended."
```

## Verifying Your Target Framework

Check your `.csproj` files:

```xml
<!-- Look for this -->
<TargetFramework>net8.0</TargetFramework>
<!-- or -->
<TargetFramework>net7.0</TargetFramework>
```

If multi-targeting:

```xml
<TargetFrameworks>net6.0;net7.0;net8.0</TargetFrameworks>
```

## Conditional Compilation

If you need to support multiple frameworks:

```csharp
#if NET7_0_OR_GREATER
    [LibraryImport("libc", SetLastError = true)]
    public static partial int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);
#else
    [DllImport("libc", SetLastError = true)]
    public static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);
#endif
```

## Build Verification

After integration, verify the build:

```bash
# Build for your target framework
dotnet build -c Release

# Run tests
dotnet test

# Check for warnings
dotnet build -c Release -warnaserror
```

## Native AOT Considerations

If using Native AOT publishing:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

The `LibraryImport` attribute is preferred over `DllImport` for AOT scenarios because it generates source at compile time rather than using runtime reflection.

## Summary

| Your Framework | Action Needed |
|----------------|---------------|
| .NET 8+ | None - use code as-is |
| .NET 7 | Minor: replace `[]` with `Array.Empty<>()` |
| .NET 6 | Replace `LibraryImport` and collection expressions |
| .NET 5 or earlier | Not supported - upgrade required |
