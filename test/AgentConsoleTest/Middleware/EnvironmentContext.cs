using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Captures the execution environment context for the agent.
/// Based on Codex CLI's EnvironmentContext - provides cwd, shell, platform, and sandbox info.
///
/// This context is serialized as XML and injected into the conversation at turn start,
/// giving the model awareness of where it's operating.
/// </summary>
public class EnvironmentContext
{
    /// <summary>
    /// Current working directory.
    /// </summary>
    public string Cwd { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Shell type (bash, zsh, pwsh, cmd).
    /// </summary>
    public string Shell { get; init; } = DetectShell();

    /// <summary>
    /// Platform (darwin, linux, windows).
    /// </summary>
    public string Platform { get; init; } = DetectPlatform();

    /// <summary>
    /// OS version string.
    /// </summary>
    public string OsVersion { get; init; } = Environment.OSVersion.ToString();

    /// <summary>
    /// Directories the agent is allowed to write to.
    /// If null, no restrictions are communicated.
    /// </summary>
    public IReadOnlyList<string>? WritableRoots { get; init; }

    /// <summary>
    /// Network access policy: "enabled" or "restricted".
    /// </summary>
    public string? NetworkAccess { get; init; }

    /// <summary>
    /// Whether the directory is a git repository.
    /// </summary>
    public bool IsGitRepo { get; init; } = DetectGitRepo();

    /// <summary>
    /// Today's date in ISO format.
    /// </summary>
    public string TodaysDate { get; init; } = DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>
    /// Creates a new EnvironmentContext with current environment values.
    /// </summary>
    public static EnvironmentContext CreateCurrent(IReadOnlyList<string>? writableRoots = null)
    {
        return new EnvironmentContext
        {
            Cwd = Directory.GetCurrentDirectory(),
            Shell = DetectShell(),
            Platform = DetectPlatform(),
            OsVersion = Environment.OSVersion.ToString(),
            WritableRoots = writableRoots,
            IsGitRepo = DetectGitRepo(),
            TodaysDate = DateTime.Now.ToString("yyyy-MM-dd")
        };
    }

    /// <summary>
    /// Serializes the environment context to XML format (matching Codex CLI).
    /// </summary>
    public string SerializeToXml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<environment_context>");
        sb.AppendLine($"  <cwd>{EscapeXml(Cwd)}</cwd>");
        sb.AppendLine($"  <shell>{EscapeXml(Shell)}</shell>");
        sb.AppendLine($"  <platform>{EscapeXml(Platform)}</platform>");
        sb.AppendLine($"  <os_version>{EscapeXml(OsVersion)}</os_version>");
        sb.AppendLine($"  <is_git_repo>{IsGitRepo.ToString().ToLowerInvariant()}</is_git_repo>");
        sb.AppendLine($"  <todays_date>{TodaysDate}</todays_date>");

        if (WritableRoots != null && WritableRoots.Count > 0)
        {
            sb.AppendLine("  <writable_roots>");
            foreach (var root in WritableRoots)
            {
                sb.AppendLine($"    <root>{EscapeXml(root)}</root>");
            }
            sb.AppendLine("  </writable_roots>");
        }

        if (!string.IsNullOrEmpty(NetworkAccess))
        {
            sb.AppendLine($"  <network_access>{EscapeXml(NetworkAccess)}</network_access>");
        }

        sb.AppendLine("</environment_context>");
        return sb.ToString();
    }

    /// <summary>
    /// Compares two contexts and returns a diff context with only changed values.
    /// Returns null if nothing changed.
    /// </summary>
    public static EnvironmentContext? Diff(EnvironmentContext before, EnvironmentContext after)
    {
        // Only track cwd changes for now (most common case)
        if (before.Cwd == after.Cwd)
            return null;

        return new EnvironmentContext
        {
            Cwd = after.Cwd,
            Shell = after.Shell,
            Platform = after.Platform,
            OsVersion = after.OsVersion,
            WritableRoots = after.WritableRoots,
            IsGitRepo = after.IsGitRepo,
            TodaysDate = after.TodaysDate
        };
    }

    private static string DetectShell()
    {
        // Check SHELL environment variable (Unix)
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(shell))
        {
            if (shell.Contains("zsh")) return "zsh";
            if (shell.Contains("bash")) return "bash";
            if (shell.Contains("fish")) return "fish";
            return Path.GetFileName(shell);
        }

        // Check ComSpec for Windows
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrEmpty(comSpec))
        {
            if (comSpec.Contains("powershell", StringComparison.OrdinalIgnoreCase)) return "pwsh";
            if (comSpec.Contains("cmd", StringComparison.OrdinalIgnoreCase)) return "cmd";
            return Path.GetFileName(comSpec);
        }

        // Check PSModulePath for PowerShell
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PSModulePath")))
            return "pwsh";

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : "bash";
    }

    private static string DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "darwin";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "windows";
        return "unknown";
    }

    private static bool DetectGitRepo()
    {
        try
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                    return true;
                dir = dir.Parent;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeXml(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
