using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace HPD.Agent.Sandbox;

/// <summary>
/// Configuration for sandbox behavior.
/// </summary>
/// <remarks>
/// <para><b>Design Philosophy:</b></para>
/// <para>
/// Defaults are secure (deny by default). Users opt-in to permissions.
/// Configuration is validated at construction to fail fast.
/// </para>
/// </remarks>
public sealed partial class SandboxConfig
{
    /// <summary>
    /// Domains the sandbox is allowed to connect to.
    /// </summary>
    /// <remarks>
    /// <para>Supports wildcards: <c>*.github.com</c> matches any subdomain.</para>
    /// <para>Empty array = no network access. Null = no network filtering.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// AllowedDomains = ["github.com", "*.npmjs.org", "api.nuget.org"]
    /// </code>
    /// </example>
    public string[] AllowedDomains { get; init; } = [];

    /// <summary>
    /// Domains explicitly denied (takes precedence over AllowedDomains).
    /// </summary>
    public string[] DeniedDomains { get; init; } = [];

    /// <summary>
    /// Paths the sandbox can write to.
    /// </summary>
    /// <remarks>
    /// <para>Supports:</para>
    /// <list type="bullet">
    /// <item><c>.</c> - Current directory</item>
    /// <item><c>~</c> - Home directory</item>
    /// <item><c>~/path</c> - Relative to home</item>
    /// <item><c>/absolute/path</c> - Absolute path</item>
    /// <item><c>**/*.txt</c> - Glob patterns (macOS only)</item>
    /// </list>
    /// </remarks>
    public string[] AllowWrite { get; init; } = [".", "/tmp"];

    /// <summary>
    /// Paths explicitly denied for writing (takes precedence over AllowWrite).
    /// </summary>
    public string[] DenyWrite { get; init; } = [];

    /// <summary>
    /// Paths the sandbox cannot read from.
    /// </summary>
    public string[] DenyRead { get; init; } = ["~/.ssh", "~/.aws", "~/.gnupg"];

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
    /// <remarks>
    /// <para>Used for Docker socket, SSH agent, etc.</para>
    /// </remarks>
    public string[]? AllowUnixSockets { get; init; }

    /// <summary>
    /// Allow ALL Unix sockets (disables Unix socket filtering).
    /// </summary>
    /// <remarks>
    /// <para><b>Security Warning:</b> This disables an important security boundary.</para>
    /// <para>Only enable if your use case requires arbitrary Unix socket access.</para>
    /// </remarks>
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
    /// <remarks>
    /// <para>Enables git remote URL updates while keeping .git/hooks protected.</para>
    /// </remarks>
    public bool AllowGitConfig { get; init; }

    /// <summary>
    /// Maximum depth to search for dangerous files.
    /// </summary>
    /// <remarks>
    /// <para>Higher values = more protection, slower startup.</para>
    /// <para>Default: 3 (covers most nested git repos)</para>
    /// </remarks>
    [Range(1, 10)]
    public int MandatoryDenySearchDepth { get; init; } = 3;

    /// <summary>
    /// Enable weaker sandbox for nested container environments.
    /// </summary>
    /// <remarks>
    /// <para>When running inside Docker, some isolation features may not be available.</para>
    /// <para>This enables a degraded mode that still provides some protection.</para>
    /// </remarks>
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
    /// <remarks>
    /// <para>Supports wildcards: <c>Execute*</c>, <c>*Command</c></para>
    /// </remarks>
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
    /// Validates the configuration.
    /// </summary>
    /// <exception cref="ValidationException">If configuration is invalid.</exception>
    public void Validate()
    {
        var errors = new List<string>();

        // Validate domain patterns
        foreach (var domain in AllowedDomains)
        {
            if (!IsValidDomainPattern(domain))
                errors.Add($"Invalid domain pattern: '{domain}'");
        }

        foreach (var domain in DeniedDomains)
        {
            if (!IsValidDomainPattern(domain))
                errors.Add($"Invalid denied domain pattern: '{domain}'");
        }

        // Validate paths
        foreach (var path in AllowWrite.Concat(DenyWrite).Concat(DenyRead))
        {
            if (string.IsNullOrWhiteSpace(path))
                errors.Add("Empty path in configuration");
        }

        // Validate search depth
        if (MandatoryDenySearchDepth < 1 || MandatoryDenySearchDepth > 10)
            errors.Add($"MandatoryDenySearchDepth must be between 1 and 10, got {MandatoryDenySearchDepth}");

        // Validate proxy ports
        if (ExternalHttpProxyPort.HasValue && (ExternalHttpProxyPort < 1 || ExternalHttpProxyPort > 65535))
            errors.Add($"Invalid ExternalHttpProxyPort: {ExternalHttpProxyPort}");

        if (ExternalSocksProxyPort.HasValue && (ExternalSocksProxyPort < 1 || ExternalSocksProxyPort > 65535))
            errors.Add($"Invalid ExternalSocksProxyPort: {ExternalSocksProxyPort}");

        if (errors.Count > 0)
            throw new ValidationException($"Invalid SandboxConfig: {string.Join("; ", errors)}");
    }

    private static bool IsValidDomainPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        // Reject URLs
        if (pattern.Contains("://") || pattern.Contains('/') || pattern.Contains(':'))
            return false;

        // Allow localhost
        if (pattern == "localhost")
            return true;

        // Validate wildcard patterns
        if (pattern.StartsWith("*."))
        {
            var domain = pattern[2..];
            // Must have at least two parts after wildcard (e.g., *.example.com)
            var parts = domain.Split('.');
            return parts.Length >= 2 && parts.All(p => p.Length > 0);
        }

        // Reject other wildcards
        if (pattern.Contains('*'))
            return false;

        // Regular domain must have at least one dot
        return pattern.Contains('.') && !pattern.StartsWith('.') && !pattern.EndsWith('.');
    }

    /// <summary>
    /// Creates a restrictive configuration (secure defaults).
    /// </summary>
    public static SandboxConfig Restrictive => new()
    {
        AllowedDomains = [],
        AllowWrite = [".", "/tmp"],
        DenyRead = ["~/.ssh", "~/.aws", "~/.gnupg", "~/.config/gcloud", "~/.kube"],
        OnInitializationFailure = SandboxFailureBehavior.Block
    };

    /// <summary>
    /// Creates a permissive configuration for development.
    /// </summary>
    public static SandboxConfig Permissive => new()
    {
        AllowedDomains = null!, // No filtering
        AllowWrite = [".", "~", "/tmp"],
        DenyRead = ["~/.ssh", "~/.gnupg"],
        AllowAllUnixSockets = true,
        AllowLocalBinding = true,
        OnInitializationFailure = SandboxFailureBehavior.Warn
    };

    /// <summary>
    /// Creates a configuration for network-only restrictions.
    /// </summary>
    public static SandboxConfig NetworkOnly(params string[] allowedDomains) => new()
    {
        AllowedDomains = allowedDomains,
        AllowWrite = [".", "~", "/tmp"],
        DenyRead = [],
        OnInitializationFailure = SandboxFailureBehavior.Block
    };
}

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
