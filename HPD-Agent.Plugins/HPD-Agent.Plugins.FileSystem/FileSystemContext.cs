using System.Collections.Generic;

namespace HPD.Agent.Plugins.FileSystem;

/// <summary>
/// Context for FileSystem plugin operations with workspace validation.
/// </summary>
public class FileSystemContext : IPluginMetadataContext
{
    private readonly Dictionary<string, object> _properties = new();

    /// <summary>
    /// Root directory of the workspace
    /// </summary>
    public string WorkspaceRoot { get; }

    /// <summary>
    /// Whether to allow operations outside the workspace (dangerous!)
    /// </summary>
    public bool AllowOutsideWorkspace { get; }

    /// <summary>
    /// Whether to respect .gitignore patterns
    /// </summary>
    public bool RespectGitIgnore { get; }

    /// <summary>
    /// Whether to respect .geminiignore patterns
    /// </summary>
    public bool RespectGeminiIgnore { get; }

    /// <summary>
    /// Maximum file size to read (in bytes)
    /// </summary>
    public long MaxFileSize { get; }

    /// <summary>
    /// Whether glob/search operations are enabled
    /// </summary>
    public bool EnableSearch { get; }

    public FileSystemContext(
        string workspaceRoot,
        bool allowOutsideWorkspace = false,
        bool respectGitIgnore = true,
        bool respectGeminiIgnore = true,
        long maxFileSize = 10_000_000, // 10 MB default
        bool enableSearch = true)
    {
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        AllowOutsideWorkspace = allowOutsideWorkspace;
        RespectGitIgnore = respectGitIgnore;
        RespectGeminiIgnore = respectGeminiIgnore;
        MaxFileSize = maxFileSize;
        EnableSearch = enableSearch;

        // Populate properties dictionary
        _properties[nameof(WorkspaceRoot)] = WorkspaceRoot;
        _properties[nameof(AllowOutsideWorkspace)] = AllowOutsideWorkspace;
        _properties[nameof(RespectGitIgnore)] = RespectGitIgnore;
        _properties[nameof(RespectGeminiIgnore)] = RespectGeminiIgnore;
        _properties[nameof(MaxFileSize)] = MaxFileSize;
        _properties[nameof(EnableSearch)] = EnableSearch;
    }

    /// <summary>
    /// Validates that a path is within the workspace
    /// </summary>
    public bool IsPathWithinWorkspace(string path)
    {
        if (AllowOutsideWorkspace)
            return true;

        var fullPath = Path.GetFullPath(path);
        var workspaceFullPath = Path.GetFullPath(WorkspaceRoot);

        return fullPath.StartsWith(workspaceFullPath, StringComparison.OrdinalIgnoreCase);
    }

    #region IPluginMetadataContext Implementation

    public T? GetProperty<T>(string propertyName, T? defaultValue = default)
    {
        if (_properties.TryGetValue(propertyName, out var value))
        {
            if (value is T typedValue)
                return typedValue;
            if (typeof(T) == typeof(string))
                return (T)(object)value.ToString()!;
        }
        return defaultValue;
    }

    public bool HasProperty(string propertyName) => _properties.ContainsKey(propertyName);

    public IEnumerable<string> GetPropertyNames() => _properties.Keys;

    #endregion
}
