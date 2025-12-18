# SandboxConfig Merge Guide

## Overview

The enhancement package includes a new `SandboxConfig.cs` that needs to be merged with your existing configuration class. This guide shows how to merge the two versions.

## Comparison: Existing vs. New Properties

### Properties You Likely Already Have

```csharp
// These should already exist - keep them unchanged
public string[] AllowedDomains { get; init; }
public string[] DeniedDomains { get; init; }
public string[] AllowWrite { get; init; }
public string[] DenyWrite { get; init; }
public string[] DenyRead { get; init; }
```

### New Properties to Add

```csharp
// ADD these new properties to your existing SandboxConfig

/// <summary>
/// Environment variables to pass through to sandboxed processes.
/// </summary>
public string[] AllowedEnvironmentVariables { get; init; } = [
    "PATH", "HOME", "USER", "LANG", "TERM", "SHELL",
    "DOTNET_ROOT", "NODE_PATH", "GOPATH"
];

/// <summary>
/// Unix socket paths that are allowed (macOS only).
/// </summary>
public string[]? AllowUnixSockets { get; init; }

/// <summary>
/// Allow ALL Unix sockets (disables Unix socket filtering).
/// </summary>
public bool AllowAllUnixSockets { get; init; }

/// <summary>
/// Allow binding to local ports within the sandbox.
/// </summary>
public bool AllowLocalBinding { get; init; }

/// <summary>
/// Allow pseudo-terminal (pty) operations (macOS only).
/// </summary>
public bool AllowPty { get; init; }

/// <summary>
/// Allow writes to .git/config files.
/// </summary>
public bool AllowGitConfig { get; init; }

/// <summary>
/// Maximum depth to search for dangerous files.
/// </summary>
[Range(1, 10)]
public int MandatoryDenySearchDepth { get; init; } = 3;

/// <summary>
/// Enable weaker sandbox for nested container environments.
/// </summary>
public bool EnableWeakerNestedSandbox { get; init; }

/// <summary>
/// Enable violation monitoring (macOS only).
/// </summary>
public bool EnableViolationMonitoring { get; init; } = true;

/// <summary>
/// Violation message patterns to ignore.
/// </summary>
public string[]? IgnoreViolationPatterns { get; init; }

/// <summary>
/// Behavior when sandbox initialization fails.
/// </summary>
public SandboxFailureBehavior OnInitializationFailure { get; init; } = SandboxFailureBehavior.Block;

/// <summary>
/// Behavior when a sandbox violation is detected.
/// </summary>
public SandboxViolationBehavior OnViolation { get; init; } = SandboxViolationBehavior.EmitAndContinue;

/// <summary>
/// Function name patterns that should be sandboxed.
/// </summary>
public string[] SandboxableFunctions { get; init; } = [];

/// <summary>
/// Function name patterns to exclude from sandboxing.
/// </summary>
public string[] ExcludedFunctions { get; init; } = [];

/// <summary>
/// External HTTP proxy port (skip starting internal proxy).
/// </summary>
public int? ExternalHttpProxyPort { get; init; }

/// <summary>
/// External SOCKS5 proxy port (skip starting internal proxy).
/// </summary>
public int? ExternalSocksProxyPort { get; init; }

/// <summary>
/// Use enhanced sandbox implementation (side-by-side rollout).
/// </summary>
public bool UseEnhancedSandbox { get; init; } = false;  // Default to legacy for safety
```

### New Enums to Add

```csharp
/// <summary>
/// Behavior when sandbox initialization fails.
/// </summary>
public enum SandboxFailureBehavior
{
    /// <summary>Block execution entirely.</summary>
    Block,
    /// <summary>Log warning and continue unsandboxed.</summary>
    Warn,
    /// <summary>Silently continue unsandboxed.</summary>
    Ignore
}

/// <summary>
/// Behavior when a sandbox violation is detected.
/// </summary>
public enum SandboxViolationBehavior
{
    /// <summary>Emit event and continue execution.</summary>
    EmitAndContinue,
    /// <summary>Emit event and block future calls to this function.</summary>
    BlockAndEmit,
    /// <summary>Ignore violations silently.</summary>
    Ignore
}
```

## Merge Strategy

### Option 1: Extend Existing (Recommended)

```csharp
// Your existing SandboxConfig.cs

public sealed class SandboxConfig
{
    // ============ EXISTING PROPERTIES ============
    // Keep all your existing properties unchanged
    
    public string[] AllowedDomains { get; init; } = [];
    public string[] DeniedDomains { get; init; } = [];
    public string[] AllowWrite { get; init; } = [".", "/tmp"];
    public string[] DenyWrite { get; init; } = [];
    public string[] DenyRead { get; init; } = ["~/.ssh", "~/.aws", "~/.gnupg"];
    
    // ... your other existing properties ...
    
    // ============ NEW ENHANCED PROPERTIES ============
    // Add these for enhanced sandbox support
    
    #region Enhanced Sandbox Properties
    
    /// <summary>
    /// Use enhanced sandbox implementation. Default: false for backward compatibility.
    /// </summary>
    public bool UseEnhancedSandbox { get; init; } = false;
    
    /// <summary>
    /// Maximum depth to search for dangerous files (enhanced only).
    /// </summary>
    public int MandatoryDenySearchDepth { get; init; } = 3;
    
    /// <summary>
    /// Allow writes to .git/config files (enhanced only).
    /// </summary>
    public bool AllowGitConfig { get; init; }
    
    /// <summary>
    /// Allow ALL Unix sockets - disables seccomp filtering (enhanced only).
    /// </summary>
    public bool AllowAllUnixSockets { get; init; }
    
    /// <summary>
    /// Enable weaker sandbox for nested containers (enhanced only).
    /// </summary>
    public bool EnableWeakerNestedSandbox { get; init; }
    
    /// <summary>
    /// Behavior when sandbox initialization fails.
    /// </summary>
    public SandboxFailureBehavior OnInitializationFailure { get; init; } = SandboxFailureBehavior.Block;
    
    #endregion
    
    // ============ EXISTING VALIDATION ============
    // Extend your existing Validate() method
    
    public void Validate()
    {
        // Your existing validation code...
        
        // Add new validation
        if (MandatoryDenySearchDepth < 1 || MandatoryDenySearchDepth > 10)
            throw new ValidationException("MandatoryDenySearchDepth must be between 1 and 10");
    }
}
```

### Option 2: Replace Entirely

If your existing `SandboxConfig` is minimal, you can replace it entirely with the new version. Just ensure you update all call sites.

### Option 3: Inheritance

```csharp
// Keep existing config as base
public class SandboxConfig
{
    public string[] AllowedDomains { get; init; } = [];
    // ... existing properties
}

// New enhanced config extends base
public class EnhancedSandboxConfig : SandboxConfig
{
    public int MandatoryDenySearchDepth { get; init; } = 3;
    public bool AllowGitConfig { get; init; }
    public bool AllowAllUnixSockets { get; init; }
    // ... new properties
}
```

## Factory Pattern for Side-by-Side

```csharp
// In your SandboxMiddleware or factory class

public static IPlatformSandbox CreatePlatformSandbox(
    SandboxConfig config,
    IHttpProxyServer? httpProxy,
    ISocks5ProxyServer? socksProxy,
    ILogger? logger)
{
    var platform = PlatformDetector.Current;
    
    return (platform, config.UseEnhancedSandbox) switch
    {
        (PlatformType.Linux, true) => new LinuxSandboxEnhanced(config, httpProxy, socksProxy, logger),
        (PlatformType.Linux, false) => new LinuxSandbox(config, httpProxy, logger),
        (PlatformType.MacOS, true) => new MacOSSandboxEnhanced(config, httpProxy, socksProxy, logger),
        (PlatformType.MacOS, false) => new MacOSSandbox(config, httpProxy, logger),
        (PlatformType.Windows, _) => new WindowsSandbox(config, logger),
        _ => throw new PlatformNotSupportedException()
    };
}
```

## Migration Checklist

- [ ] Add new properties to `SandboxConfig`
- [ ] Add new enums (`SandboxFailureBehavior`, `SandboxViolationBehavior`)
- [ ] Update `Validate()` method with new validations
- [ ] Add `UseEnhancedSandbox` property (default: false)
- [ ] Update factory/middleware to check `UseEnhancedSandbox`
- [ ] Add preset profiles if desired (`Restrictive`, `Permissive`, `NetworkOnly`)
- [ ] Update tests for new properties
- [ ] Update documentation

## Testing After Merge

```csharp
[Fact]
public void MergedConfig_BackwardCompatible()
{
    // Old-style config should still work
    var config = new SandboxConfig
    {
        AllowedDomains = ["github.com"],
        AllowWrite = ["."]
    };
    
    config.Validate(); // Should not throw
    Assert.False(config.UseEnhancedSandbox); // Default to legacy
}

[Fact]
public void MergedConfig_SupportsEnhanced()
{
    var config = new SandboxConfig
    {
        AllowedDomains = ["github.com"],
        UseEnhancedSandbox = true,
        MandatoryDenySearchDepth = 5
    };
    
    config.Validate();
    Assert.True(config.UseEnhancedSandbox);
    Assert.Equal(5, config.MandatoryDenySearchDepth);
}
```
