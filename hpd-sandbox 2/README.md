# HPD.Sandbox.Local - Enhanced Implementation

## Feature Parity with Claude Code

| Feature | Claude Code | HPD-Agent | Status |
|---------|-------------|-----------|--------|
| Namespace isolation (Linux) | ✅ | ✅ | Done |
| sandbox-exec profiles (macOS) | ✅ | ✅ | Done |
| HTTP proxy filtering | ✅ | ✅ | Done |
| SOCKS5 proxy filtering | ✅ | ✅ | Done |
| Dangerous file protection | ✅ | ✅ | Done |
| Unix socket bridges (Linux) | ✅ | ✅ | Done |
| Move-blocking rules (macOS) | ✅ | ✅ | Done |
| Glob patterns (macOS) | ✅ | ✅ | Done |
| Case normalization | ✅ | ✅ | Done |
| Symlink resolution | ✅ | ✅ | Done |
| Violation monitoring | ✅ | ✅ | Done |
| Seccomp Unix socket blocking | ✅ | ✅ | **Done (P/Invoke)** |
| Middleware integration | ❌ | ✅ | C# advantage |
| Declarative attributes | ❌ | ✅ | C# advantage |

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         SandboxMiddleware                           │
│  • Integrates with HPD-Agent pipeline                               │
│  • Auto-detects sandboxable functions                               │
│  • [Sandboxable] attribute support                                  │
└─────────────────────────────────────────────────────────────────────┘
                                   │
                                   ▼
┌─────────────────────────────────────────────────────────────────────┐
│                          SandboxManager                             │
│  • Platform detection                                               │
│  • Proxy lifecycle management                                       │
│  • Configuration validation                                         │
└─────────────────────────────────────────────────────────────────────┘
                                   │
              ┌────────────────────┼────────────────────┐
              ▼                    ▼                    ▼
┌─────────────────────┐ ┌─────────────────┐ ┌─────────────────────┐
│   LinuxSandbox      │ │  MacOSSandbox   │ │   WindowsSandbox    │
│   Enhanced          │ │  Enhanced       │ │   (Unsupported)     │
├─────────────────────┤ ├─────────────────┤ ├─────────────────────┤
│ • BubblewrapBuilder │ │ • SeatbeltBuilder│ │ • Graceful fallback │
│ • UnixSocketBridge  │ │ • GlobToRegex   │ │ • Clear error msgs  │
│ • DangerousScanner  │ │ • MoveBlocking  │ │                     │
│ • SeccompFilter     │ │                 │ │                     │
└─────────────────────┘ └─────────────────┘ └─────────────────────┘
              │                    │
              ▼                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                        Network Proxies                              │
│  ┌──────────────────┐        ┌──────────────────┐                  │
│  │  HttpProxyServer │        │ Socks5ProxyServer│                  │
│  │  (Titanium.Web)  │        │   (RFC 1928)     │                  │
│  └──────────────────┘        └──────────────────┘                  │
└─────────────────────────────────────────────────────────────────────┘
```

## File Structure

```
HPD.Sandbox.Local/
├── Configuration/
│   └── SandboxConfig.cs           # Enhanced config with validation
├── Security/
│   ├── SandboxDefaults.cs         # Dangerous files, safe env vars
│   ├── DangerousPathScanner.cs    # Parallel directory scanning
│   └── PathNormalizer.cs          # Case/symlink normalization
├── Network/
│   ├── HttpProxyServer.cs         # Titanium.Web.Proxy wrapper
│   ├── IHttpProxyServer.cs
│   ├── Socks5ProxyServer.cs       # Custom RFC 1928 implementation
│   └── ISocks5ProxyServer.cs
├── Platforms/
│   ├── Linux/
│   │   ├── LinuxSandboxEnhanced.cs
│   │   ├── BubblewrapBuilder.cs   # Fluent bwrap argument builder
│   │   ├── UnixSocketBridge.cs    # socat bridge management
│   │   └── Seccomp/
│   │       ├── SeccompNative.cs       # P/Invoke declarations
│   │       ├── BpfFilterBuilder.cs    # BPF program generation
│   │       ├── SeccompFilter.cs       # Filter application
│   │       └── SeccompChildProcess.cs # Child process wrapper
│   ├── MacOS/
│   │   ├── MacOSSandboxEnhanced.cs
│   │   ├── SeatbeltProfileBuilder.cs
│   │   └── GlobToRegex.cs
│   ├── Windows/
│   │   └── WindowsSandbox.cs      # Graceful unsupported handling
│   ├── IPlatformSandbox.cs
│   └── PlatformDetector.cs
├── Middleware/
│   ├── SandboxMiddleware.cs       # HPD-Agent integration
│   └── SandboxableAttribute.cs    # Declarative sandboxing
├── Events/
│   ├── SandboxEvents.cs           # Event types
│   └── SandboxEventTypes.cs       # Event constants
└── SandboxViolation.cs
```

## Usage Examples

### Basic Usage

```csharp
// Configure sandbox
var config = new SandboxConfig
{
    AllowedDomains = ["github.com", "*.npmjs.org"],
    AllowWrite = [".", "/tmp"],
    DenyRead = ["~/.ssh", "~/.aws"]
};

// Use with middleware
var agent = new AgentBuilder()
    .WithMiddleware(new SandboxMiddleware(config))
    .Build();
```

### Declarative with Attributes

```csharp
[AIFunction]
[Sandboxable(
    AllowedDomains = "api.github.com",
    AllowWrite = "./workspace",
    DenyRead = "~/.ssh,~/.aws")]
public async Task<string> ExecuteCommand(string command)
{
    // Automatically sandboxed
}
```

### Preset Profiles

```csharp
// Maximum security
var restrictive = SandboxConfig.Restrictive;

// Development convenience
var permissive = SandboxConfig.Permissive;

// Network filtering only
var networkOnly = SandboxConfig.NetworkOnly("api.github.com", "*.npmjs.org");
```

## C# Advantages Over TypeScript

1. **Strong Typing**: Compile-time validation of configuration
2. **Native AOT**: Single binary deployment, faster startup
3. **Source Generators**: Could generate sandbox wrappers at compile time
4. **P/Invoke**: Could implement seccomp directly without external binaries
5. **Channels**: Better async streaming than Node.js EventEmitter
6. **Records**: Immutable configuration with `with` expressions

## TODO: Remaining Work

### High Priority

~~1. **Seccomp Integration (Linux)**~~ ✅ **DONE**
   - Implemented via P/Invoke + runtime-compiled C helper
   - No external binary dependencies (gcc builds helper on first run)
   - Blocks `socket(AF_UNIX, ...)` and `socketpair(AF_UNIX, ...)`

### Medium Priority

3. **Source Generator for [Sandboxable]**
   - Generate wrapper code at compile time
   - Zero runtime reflection overhead

4. **Container Sandbox Tier**
   - Docker-based isolation for maximum security
   - HPD.Sandbox.Container package

### Low Priority

5. **Windows Support**
   - Windows Sandbox (Hyper-V based)
   - WSL2 fallback
   - AppContainer for UWP-style isolation

## Testing

```csharp
[Fact]
public async Task DangerousFiles_AreProtected()
{
    var scanner = new DangerousPathScanner(maxDepth: 3);
    var paths = await scanner.GetDangerousPathsAsync(Environment.CurrentDirectory);
    
    Assert.Contains(paths, p => p.EndsWith(".gitconfig"));
    Assert.Contains(paths, p => p.Contains(".git/hooks"));
}

[Fact]
public void GlobToRegex_ConvertsCorrectly()
{
    Assert.Equal("^.*$", GlobToRegex.Convert("**"));
    Assert.Equal("^[^/]*\\.txt$", GlobToRegex.Convert("*.txt"));
    Assert.Equal("^src/(.*/)?[^/]*\\.cs$", GlobToRegex.Convert("src/**/*.cs"));
}

[Fact]
public async Task LinuxSandbox_NetworkBridge_Works()
{
    var bridge = new UnixSocketBridge();
    await bridge.InitializeAsync(httpPort: 8080, socksPort: 1080);
    
    Assert.True(File.Exists(bridge.HttpSocketPath));
    Assert.True(File.Exists(bridge.SocksSocketPath));
}
```

## Security Considerations

1. **Always resolve symlinks** - Prevents escape via symlink to protected path
2. **Case-insensitive comparison** - Prevents bypass on macOS/Windows
3. **Move-blocking rules** - Prevents `mv ~/.ssh /tmp/exposed`
4. **Ancestor protection** - Can't move `/home` to expose `/home/user/.ssh`
5. **Dangerous file scanning** - Protects nested git repos, IDE configs
