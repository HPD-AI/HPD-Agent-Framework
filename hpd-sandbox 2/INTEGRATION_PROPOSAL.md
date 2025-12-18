# HPD-Agent Sandbox Enhancement Integration Proposal

## Executive Summary

This proposal outlines the integration of security enhancements to HPD-Agent's sandbox implementation, achieving feature parity with Claude Code's TypeScript sandbox while leveraging C#-specific advantages.

**Key Deliverables:**
- 15 new/enhanced C# files
- Full feature parity with Claude Code sandbox
- Zero external binary dependencies
- Backward-compatible API

**Estimated Integration Effort:** 2-3 days for an engineer familiar with the codebase

---

## Current State vs. Proposed State

### Feature Comparison

| Security Feature | Current HPD-Agent | After Integration | Claude Code |
|-----------------|-------------------|-------------------|-------------|
| Namespace isolation (Linux) | ✅ | ✅ | ✅ |
| sandbox-exec profiles (macOS) | ✅ | ✅ | ✅ |
| HTTP/SOCKS5 proxy filtering | ✅ | ✅ | ✅ |
| Dangerous file protection | ❌ | ✅ | ✅ |
| Unix socket bridges (Linux) | ❌ | ✅ | ✅ |
| Seccomp syscall filtering | ❌ | ✅ | ✅ |
| Move-blocking rules (macOS) | ❌ | ✅ | ✅ |
| Glob pattern support | ❌ | ✅ | ✅ |
| Case-insensitive path handling | ❌ | ✅ | ✅ |
| Symlink resolution | ❌ | ✅ | ✅ |

### Critical Security Gaps Being Addressed

1. **Dangerous File Protection** - Prevents writes to `.gitconfig`, `.bashrc`, `.git/hooks` which can execute arbitrary code
2. **Unix Socket Blocking** - Prevents sandbox escape via Unix domain sockets
3. **Network Bridge Architecture** - Fixes broken network filtering when using `--unshare-net`
4. **Move-Blocking Rules** - Prevents `mv ~/.ssh /tmp/exposed` attacks on macOS

---

## File Inventory

### New Files to Add

```
HPD.Sandbox.Local/
├── Security/                          [NEW FOLDER]
│   ├── SandboxDefaults.cs             (4.5 KB) - Security constants
│   ├── DangerousPathScanner.cs        (6.5 KB) - Scans for dangerous files
│   └── PathNormalizer.cs              (7.5 KB) - Path normalization
├── Platforms/
│   ├── Linux/
│   │   ├── BubblewrapBuilder.cs       (8.0 KB) - Fluent bwrap builder
│   │   ├── UnixSocketBridge.cs        (7.5 KB) - Network bridge
│   │   └── Seccomp/                   [NEW FOLDER]
│   │       ├── SeccompNative.cs       (6.5 KB) - P/Invoke declarations
│   │       ├── BpfProgramBuilder.cs   (7.0 KB) - BPF filter generation
│   │       ├── SeccompFilter.cs       (9.0 KB) - Filter application
│   │       ├── SeccompChildProcess.cs (10 KB)  - Helper binary management
│   │       └── SeccompCommandWrapper.cs (11 KB) - Command wrapping
│   └── MacOS/
│       ├── GlobToRegex.cs             (3.5 KB) - Pattern conversion
│       └── SeatbeltProfileBuilder.cs  (9.0 KB) - Profile generation
└── Configuration/
    └── SandboxConfig.cs               (10 KB)  - Enhanced config
```

### Files to Replace/Enhance

| Existing File | Action | Notes |
|---------------|--------|-------|
| `LinuxSandbox.cs` | Replace with `LinuxSandboxEnhanced.cs` | Or rename and keep old as fallback |
| `MacOSSandbox.cs` | Replace with `MacOSSandboxEnhanced.cs` | Or rename and keep old as fallback |
| `SandboxConfig.cs` | Merge or replace | New version has more options |

---

## Integration Steps

### Phase 1: Add New Files (Day 1)

```bash
# 1. Create new folders
mkdir -p src/HPD.Sandbox.Local/Security
mkdir -p src/HPD.Sandbox.Local/Platforms/Linux/Seccomp

# 2. Copy new files (from this package)
cp Security/*.cs src/HPD.Sandbox.Local/Security/
cp Platforms/Linux/Seccomp/*.cs src/HPD.Sandbox.Local/Platforms/Linux/Seccomp/
cp Platforms/Linux/BubblewrapBuilder.cs src/HPD.Sandbox.Local/Platforms/Linux/
cp Platforms/Linux/UnixSocketBridge.cs src/HPD.Sandbox.Local/Platforms/Linux/
cp Platforms/MacOS/GlobToRegex.cs src/HPD.Sandbox.Local/Platforms/MacOS/
cp Platforms/MacOS/SeatbeltProfileBuilder.cs src/HPD.Sandbox.Local/Platforms/MacOS/
```

### Phase 2: Update Existing Files (Day 1-2)

#### Option A: Replace (Recommended for clean integration)

```csharp
// Rename existing files as backup
LinuxSandbox.cs → LinuxSandboxLegacy.cs
MacOSSandbox.cs → MacOSSandboxLegacy.cs

// Copy enhanced versions
LinuxSandboxEnhanced.cs → LinuxSandbox.cs
MacOSSandboxEnhanced.cs → MacOSSandbox.cs
```

#### Option B: Side-by-Side (Safer, allows gradual rollout)

```csharp
// Keep both, select via configuration
public static IPlatformSandbox Create(SandboxConfig config)
{
    if (config.UseEnhancedSandbox)
        return new LinuxSandboxEnhanced(config, ...);
    else
        return new LinuxSandbox(config, ...);
}
```

### Phase 3: Update Dependencies (Day 2)

#### Required Using Statements

```csharp
// In LinuxSandboxEnhanced.cs
using HPD.Sandbox.Local.Security;
using HPD.Sandbox.Local.Platforms.Linux.Seccomp;

// In MacOSSandboxEnhanced.cs
using HPD.Sandbox.Local.Security;
using HPD.Sandbox.Local.Platforms.MacOS;
```

#### Interface Compatibility

The enhanced classes implement the same `IPlatformSandbox` interface:

```csharp
public interface IPlatformSandbox : IAsyncDisposable
{
    ChannelReader<SandboxViolation>? Violations { get; }
    Task<bool> CheckDependenciesAsync(CancellationToken cancellationToken);
    Task<string> WrapCommandAsync(string command, CancellationToken cancellationToken);
}
```

### Phase 4: Update Tests (Day 2-3)

#### New Test Cases to Add

```csharp
// Security/DangerousPathScannerTests.cs
[Fact]
public async Task Scanner_FindsGitHooks()
{
    var scanner = new DangerousPathScanner(maxDepth: 3);
    var paths = await scanner.GetDangerousPathsAsync(testDir);
    Assert.Contains(paths, p => p.Contains(".git/hooks"));
}

// Security/PathNormalizerTests.cs
[Fact]
public void Normalizer_ResolvesSymlinks()
{
    var link = CreateSymlink("/tmp/link", "/etc/passwd");
    var normalized = PathNormalizer.Normalize(link);
    Assert.Equal("/etc/passwd", normalized);
}

// Platforms/Linux/Seccomp/SeccompFilterTests.cs
[Fact]
[Trait("Platform", "Linux")]
public void SeccompFilter_IsSupported_OnLinux()
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        return;
    
    Assert.True(SeccompFilter.IsSupported);
}

// Platforms/MacOS/GlobToRegexTests.cs
[Theory]
[InlineData("*.txt", "^[^/]*\\.txt$")]
[InlineData("**/*.cs", "^(.*/)?[^/]*\\.cs$")]
public void GlobToRegex_ConvertsCorrectly(string glob, string expectedRegex)
{
    Assert.Equal(expectedRegex, GlobToRegex.Convert(glob));
}
```

#### Update Existing Tests

```csharp
// Ensure existing sandbox tests still pass
[Fact]
public async Task LinuxSandbox_WrapsCommand_WithNamespaceIsolation()
{
    // This test should work unchanged with LinuxSandboxEnhanced
}
```

### Phase 5: Configuration Migration (Day 3)

#### SandboxConfig Changes

```csharp
// Old config
var config = new SandboxConfig
{
    AllowedDomains = ["github.com"],
    AllowWrite = ["."],
    DenyRead = ["~/.ssh"]
};

// New config (backward compatible, new options optional)
var config = new SandboxConfig
{
    // Existing options (unchanged)
    AllowedDomains = ["github.com"],
    AllowWrite = ["."],
    DenyRead = ["~/.ssh"],
    
    // New options (all have sensible defaults)
    MandatoryDenySearchDepth = 3,        // Default: 3
    AllowGitConfig = false,               // Default: false
    AllowAllUnixSockets = false,          // Default: false
    EnableWeakerNestedSandbox = false,    // Default: false
    EnableViolationMonitoring = true      // Default: true
};
```

---

## Breaking Changes

### None Expected

The integration is designed to be backward compatible:

1. `IPlatformSandbox` interface unchanged
2. `SandboxConfig` has same required properties
3. New properties have sensible defaults
4. Enhanced classes can be drop-in replacements

### Potential Issues

| Issue | Mitigation |
|-------|------------|
| gcc not installed (Linux seccomp) | Graceful fallback, logs warning |
| socat not installed (Linux network) | `CheckDependenciesAsync` returns false |
| Old .NET version | Requires .NET 7+ for `LibraryImport` |

---

## Runtime Dependencies

### Linux

| Dependency | Required For | Install Command |
|------------|--------------|-----------------|
| `bubblewrap` | Namespace isolation | `apt install bubblewrap` |
| `socat` | Network bridging | `apt install socat` |
| `gcc` | Seccomp helper compilation | `apt install gcc` |

### macOS

| Dependency | Required For | Notes |
|------------|--------------|-------|
| `sandbox-exec` | Seatbelt profiles | Built into macOS |

### Windows

Not supported. Enhanced sandbox provides clear error messages directing users to Docker Desktop or WSL2.

---

## Performance Impact

| Operation | Impact | Notes |
|-----------|--------|-------|
| Startup (first run) | +2-3 seconds | Compiling seccomp helper (cached) |
| Startup (subsequent) | +50-100ms | Dangerous path scanning |
| Runtime | Negligible | BPF filter is kernel-level |
| Memory | +1-2 MB | Socat bridge processes |

---

## Rollback Plan

If issues arise after integration:

```csharp
// 1. Keep legacy classes available
public class LinuxSandboxLegacy : IPlatformSandbox { ... }

// 2. Add feature flag
if (Environment.GetEnvironmentVariable("HPD_USE_LEGACY_SANDBOX") == "1")
    return new LinuxSandboxLegacy(...);

// 3. Or revert the file changes
git checkout HEAD~1 -- src/HPD.Sandbox.Local/Platforms/Linux/LinuxSandbox.cs
```

---

## Validation Checklist

### Before Merging

- [ ] All existing sandbox tests pass
- [ ] New unit tests added and passing
- [ ] Manual test: Linux sandbox with network filtering
- [ ] Manual test: Linux sandbox with seccomp (verify Unix socket blocked)
- [ ] Manual test: macOS sandbox with dangerous file protection
- [ ] Manual test: Nested Docker environment (weaker sandbox mode)
- [ ] Documentation updated

### Integration Tests

```bash
# Linux seccomp verification
./apply-seccomp /bin/sh -c "python3 -c 'import socket; socket.socket(socket.AF_UNIX)'"
# Expected: "Permission denied" or "Operation not permitted"

# Network bridge verification  
curl -x http://localhost:3128 https://github.com
# Expected: Success (if github.com in AllowedDomains)

# Dangerous file protection
touch .git/hooks/pre-commit
# Expected: "Permission denied" inside sandbox
```

---

## Questions for Discussion

1. **Migration Strategy**: Replace existing classes or run side-by-side?
2. **Feature Flags**: Should enhanced features be opt-in initially?
3. **Test Coverage**: What's the minimum test coverage requirement?
4. **CI/CD**: Do we need Linux and macOS runners for full test coverage?

---

## Appendix: File Descriptions

### Security/SandboxDefaults.cs
Constants for dangerous files (`.gitconfig`, `.bashrc`, etc.), safe environment variables, and default paths. Used by both Linux and macOS sandboxes.

### Security/DangerousPathScanner.cs
Parallel directory scanner that finds dangerous files in the working directory tree. Caches results for performance. Configurable search depth.

### Security/PathNormalizer.cs
Handles path normalization including:
- Tilde expansion (`~/` → `/home/user/`)
- Symlink resolution (prevents escape via symlinks)
- Case normalization (for macOS/Windows)

### Platforms/Linux/BubblewrapBuilder.cs
Fluent API for building `bwrap` command arguments. Cleaner than string concatenation, handles escaping.

### Platforms/Linux/UnixSocketBridge.cs
Manages socat processes that bridge Unix sockets across network namespaces. Critical for making HTTP_PROXY work inside `--unshare-net`.

### Platforms/Linux/Seccomp/*
P/Invoke-based seccomp implementation. Generates and compiles a minimal C helper at runtime that applies BPF filter blocking `socket(AF_UNIX, ...)`.

### Platforms/MacOS/GlobToRegex.cs
Converts glob patterns (`**/*.config`) to regex for macOS sandbox profiles.

### Platforms/MacOS/SeatbeltProfileBuilder.cs
Fluent API for generating sandbox-exec profiles. Includes move-blocking rules that prevent `mv` bypass attacks.

### Configuration/SandboxConfig.cs
Enhanced configuration with validation, preset profiles (`Restrictive`, `Permissive`), and new security options.

---

## Contact

For questions about this proposal, contact the HPD-Agent development team.

**Document Version:** 1.0  
**Last Updated:** December 2024
