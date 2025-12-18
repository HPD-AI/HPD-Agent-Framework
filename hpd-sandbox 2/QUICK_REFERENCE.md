# Quick Reference Card

## TL;DR

**What:** Security enhancements to HPD-Agent sandbox  
**Why:** Achieve parity with Claude Code, close security gaps  
**Effort:** 2-3 days  
**Risk:** Low (backward compatible)

## Files to Copy

```
Security/
  SandboxDefaults.cs
  DangerousPathScanner.cs
  PathNormalizer.cs

Platforms/Linux/
  BubblewrapBuilder.cs
  UnixSocketBridge.cs
  LinuxSandboxEnhanced.cs
  Seccomp/
    SeccompNative.cs
    BpfProgramBuilder.cs
    SeccompFilter.cs
    SeccompChildProcess.cs
    SeccompCommandWrapper.cs

Platforms/MacOS/
  GlobToRegex.cs
  SeatbeltProfileBuilder.cs
  MacOSSandboxEnhanced.cs

Configuration/
  SandboxConfig.cs
```

## Quick Integration

```bash
# Copy folders to your project
cp -r Security/ /path/to/HPD.Sandbox.Local/
cp -r Platforms/Linux/Seccomp/ /path/to/HPD.Sandbox.Local/Platforms/Linux/
cp Platforms/Linux/*.cs /path/to/HPD.Sandbox.Local/Platforms/Linux/
cp Platforms/MacOS/*.cs /path/to/HPD.Sandbox.Local/Platforms/MacOS/
cp Configuration/SandboxConfig.cs /path/to/HPD.Sandbox.Local/
```

## Key Classes

| Class | Replaces | Purpose |
|-------|----------|---------|
| `LinuxSandboxEnhanced` | `LinuxSandbox` | Full Linux sandbox with seccomp |
| `MacOSSandboxEnhanced` | `MacOSSandbox` | Full macOS sandbox with move-blocking |
| `SandboxConfig` | `SandboxConfig` | Enhanced config (backward compatible) |

## New Dependencies (Linux only)

```bash
apt install bubblewrap socat gcc
```

## Test Commands

```bash
# Verify seccomp blocks Unix sockets
./apply-seccomp /bin/sh -c "python3 -c 'import socket; s=socket.socket(socket.AF_UNIX)'"
# Expected: Permission denied

# Verify dangerous file protection
bwrap ... -- touch .git/hooks/pre-commit
# Expected: Read-only file system
```

## Config Changes

```csharp
// Existing config works unchanged
var config = new SandboxConfig
{
    AllowedDomains = ["github.com"],
    AllowWrite = ["."],
};

// New options available (all optional)
config.MandatoryDenySearchDepth = 3;  // How deep to scan for dangerous files
config.AllowGitConfig = false;         // Block .git/config writes
config.AllowAllUnixSockets = false;    // Enable seccomp Unix socket blocking
```

## Rollback

```bash
# If issues arise
git checkout HEAD~1 -- src/HPD.Sandbox.Local/Platforms/
```

## Questions?

See INTEGRATION_PROPOSAL.md for full details.
