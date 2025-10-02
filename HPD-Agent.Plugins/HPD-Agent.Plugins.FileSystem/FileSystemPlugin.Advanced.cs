using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace HPD.Agent.Plugins.FileSystem;

/// <summary>
/// Advanced file operations: glob search, grep, and smart editing
/// </summary>
public partial class FileSystemPlugin
{
    #region Advanced Search Operations

    [AIFunction<FileSystemContext>]
    [ConditionalFunction("EnableSearch")]
    [AIDescription("Find files matching a glob pattern (e.g., '**/*.cs', '*.txt', 'src/**/*.json'). Only available when search is enabled.")]
    public Task<string> FindFiles(
        [AIDescription("Glob pattern to match files (e.g., '**/*.cs', 'src/**/*.json')")] string pattern,
        [AIDescription("Optional: Directory to search in (defaults to workspace root)")] string? searchPath = null)
    {
        if (!_context.EnableSearch)
            return Task.FromResult("Error: Search operations are disabled in this context.");

        // Validate pattern
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult("Error: Pattern cannot be empty.");

        var basePath = searchPath ?? _context.WorkspaceRoot;

        // Validate path
        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(_context.WorkspaceRoot, basePath);

        if (!_context.IsPathWithinWorkspace(basePath))
            return Task.FromResult($"Error: Search path must be within workspace: {_context.WorkspaceRoot}");

        if (!Directory.Exists(basePath))
            return Task.FromResult($"Error: Directory not found: {basePath}");

        try
        {
            // Use Microsoft.Extensions.FileSystemGlobbing
            var matcher = new Matcher();
            matcher.AddInclude(pattern);

            var result = matcher.Execute(
                new DirectoryInfoWrapper(new DirectoryInfo(basePath)));

            var matches = result.Files
                .Select(f => new FileInfo(Path.Combine(basePath, f.Path)))
                .ToList();

            // Sort by most recently modified first
            var sortedMatches = matches
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();

            // Build result
            var sb = new StringBuilder();
            sb.AppendLine($"--- Found {sortedMatches.Count} files matching '{pattern}' ---");

            const int maxResults = 100;
            var displayCount = Math.Min(sortedMatches.Count, maxResults);

            for (int i = 0; i < displayCount; i++)
            {
                var file = sortedMatches[i];
                var relativePath = Path.GetRelativePath(_context.WorkspaceRoot, file.FullName);
                var size = FormatFileSize(file.Length);
                var age = FormatAge(DateTime.Now - file.LastWriteTime);

                sb.AppendLine($"{i + 1}. {relativePath} ({size}, modified {age})");
            }

            if (sortedMatches.Count > maxResults)
            {
                sb.AppendLine();
                sb.AppendLine($"... and {sortedMatches.Count - maxResults} more files (showing first {maxResults})");
            }

            return Task.FromResult(sb.ToString());
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error finding files: {ex.Message}");
        }
    }

    [AIFunction<FileSystemContext>]
    [ConditionalFunction("EnableSearch")]
    [AIDescription("Search for a regular expression pattern within file contents (like grep). Only available when search is enabled.")]
    public async Task<string> SearchContent(
        [AIDescription("Regular expression pattern to search for")] string pattern,
        [AIDescription("Optional: Directory to search in (defaults to workspace root)")] string? searchPath = null,
        [AIDescription("Optional: File glob pattern to filter files (e.g., '*.cs', '*.txt')")] string? filePattern = null,
        [AIDescription("Whether the search should be case-sensitive")] bool caseSensitive = false)
    {
        if (!_context.EnableSearch)
            return "Error: Search operations are disabled in this context.";

        // Validate pattern
        if (string.IsNullOrWhiteSpace(pattern))
            return "Error: Search pattern cannot be empty.";

        var basePath = searchPath ?? _context.WorkspaceRoot;

        // Validate path
        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(_context.WorkspaceRoot, basePath);

        if (!_context.IsPathWithinWorkspace(basePath))
            return $"Error: Search path must be within workspace: {_context.WorkspaceRoot}";

        if (!Directory.Exists(basePath))
            return $"Error: Directory not found: {basePath}";

        try
        {
            var regexOptions = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            var regex = new Regex(pattern, regexOptions);

            var matches = new List<(string file, int lineNumber, string line)>();
            const int maxTotalMatches = 500;

            // Get files (with optional glob filter)
            IEnumerable<string> filesToSearch;
            if (!string.IsNullOrWhiteSpace(filePattern))
            {
                var matcher = new Matcher();
                matcher.AddInclude(filePattern);
                var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(basePath)));
                filesToSearch = result.Files.Select(f => Path.Combine(basePath, f.Path));
            }
            else
            {
                filesToSearch = Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories);
            }

            foreach (var file in filesToSearch)
            {
                if (matches.Count >= maxTotalMatches)
                    break;

                // Skip binary files
                if (IsBinaryFile(file))
                    continue;

                // Search file content
                var lines = await File.ReadAllLinesAsync(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        var relativePath = Path.GetRelativePath(_context.WorkspaceRoot, file);
                        matches.Add((relativePath, i + 1, lines[i].Trim()));

                        if (matches.Count >= maxTotalMatches)
                            break;
                    }
                }
            }

            // Build result
            var sb = new StringBuilder();
            sb.AppendLine($"--- Found {matches.Count} matches for '{pattern}' ---");

            if (filePattern != null)
                sb.AppendLine($"File filter: {filePattern}");

            sb.AppendLine();

            // Group by file
            var groupedMatches = matches.GroupBy(m => m.file);

            foreach (var group in groupedMatches)
            {
                sb.AppendLine($"File: {group.Key}");
                foreach (var match in group)
                {
                    sb.AppendLine($"  Line {match.lineNumber}: {match.line}");
                }
                sb.AppendLine();
            }

            if (matches.Count == maxTotalMatches)
            {
                sb.AppendLine($"(Maximum of {maxTotalMatches} matches reached)");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error searching content: {ex.Message}";
        }
    }

    #endregion

    #region Advanced Edit Operations

    [AIFunction<FileSystemContext>]
    [AIDescription("Edit a file by replacing old_string with new_string. Shows a diff preview of changes.")]
    [RequiresPermission]
    public async Task<string> EditFile(
        [AIDescription("The absolute path to the file to edit")] string filePath,
        [AIDescription("The exact string to find and replace")] string oldString,
        [AIDescription("The new string to replace with")] string newString)
    {
        // Validate path
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: File path cannot be empty.";

        if (!Path.IsPathRooted(filePath))
            return $"Error: Path must be absolute, but was relative: {filePath}";

        if (!_context.IsPathWithinWorkspace(filePath))
            return $"Error: File path must be within workspace: {_context.WorkspaceRoot}";

        if (!File.Exists(filePath))
            return $"Error: File not found: {filePath}";

        if (string.IsNullOrEmpty(oldString))
            return "Error: old_string cannot be empty.";

        try
        {
            // Read current content
            var currentContent = await File.ReadAllTextAsync(filePath);

            // Check if old_string exists
            if (!currentContent.Contains(oldString))
            {
                return $"Error: Could not find the specified text in the file.{Environment.NewLine}Looking for: {oldString.Substring(0, Math.Min(100, oldString.Length))}...";
            }

            // Check if multiple occurrences
            var occurrences = Regex.Matches(currentContent, Regex.Escape(oldString)).Count;
            if (occurrences > 1)
            {
                return $"Error: Found {occurrences} occurrences of the text. The old_string must be unique in the file. Please provide more context to make it unique.";
            }

            // Perform replacement
            var newContent = currentContent.Replace(oldString, newString);

            // Generate diff
            var diffBuilder = new InlineDiffBuilder(new Differ());
            var diff = diffBuilder.BuildDiffModel(currentContent, newContent);

            // Generate diff display
            var diffDisplay = GenerateDiffDisplay(diff);

            // Write file
            await File.WriteAllTextAsync(filePath, newContent, Encoding.UTF8);

            // Get stats
            var linesChanged = diff.Lines.Count(l => l.Type != ChangeType.Unchanged);

            return $"✓ File edited successfully: {filePath}{Environment.NewLine}" +
                   $"Changed {linesChanged} lines{Environment.NewLine}{Environment.NewLine}" +
                   $"--- Diff ---{Environment.NewLine}{diffDisplay}";
        }
        catch (Exception ex)
        {
            return $"Error editing file: {ex.Message}";
        }
    }

    #endregion

    #region Helper Methods

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1)
            return "just now";
        if (age.TotalHours < 1)
            return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1)
            return $"{(int)age.TotalHours}h ago";
        if (age.TotalDays < 7)
            return $"{(int)age.TotalDays}d ago";
        if (age.TotalDays < 30)
            return $"{(int)(age.TotalDays / 7)}w ago";
        return $"{(int)(age.TotalDays / 30)}mo ago";
    }

    private static bool IsBinaryFile(string filePath)
    {
        try
        {
            // Check extension first
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var binaryExtensions = new[] { ".exe", ".dll", ".bin", ".dat", ".zip", ".tar", ".gz", ".jpg", ".png", ".gif", ".pdf", ".mp3", ".mp4" };
            if (binaryExtensions.Contains(ext))
                return true;

            // Read first 8KB and check for null bytes
            using var fs = File.OpenRead(filePath);
            var buffer = new byte[8192];
            var bytesRead = fs.Read(buffer, 0, buffer.Length);

            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0)
                    return true;
            }

            return false;
        }
        catch
        {
            return true; // Assume binary if can't read
        }
    }

    private static string GenerateDiffDisplay(DiffPaneModel diff)
    {
        var sb = new StringBuilder();
        const int contextLines = 3;
        const int maxDisplayLines = 50;

        var changedLineIndices = diff.Lines
            .Select((line, index) => (line, index))
            .Where(x => x.line.Type != ChangeType.Unchanged)
            .Select(x => x.index)
            .ToList();

        if (!changedLineIndices.Any())
            return "(no changes)";

        // Find ranges to display
        var ranges = new List<(int start, int end)>();
        foreach (var idx in changedLineIndices)
        {
            var start = Math.Max(0, idx - contextLines);
            var end = Math.Min(diff.Lines.Count - 1, idx + contextLines);

            if (ranges.Any() && start <= ranges.Last().end + 1)
            {
                ranges[^1] = (ranges.Last().start, end);
            }
            else
            {
                ranges.Add((start, end));
            }
        }

        var displayedLines = 0;
        foreach (var (start, end) in ranges)
        {
            if (displayedLines >= maxDisplayLines)
            {
                sb.AppendLine("... (more changes not shown)");
                break;
            }

            for (int i = start; i <= end && displayedLines < maxDisplayLines; i++)
            {
                var line = diff.Lines[i];
                var prefix = line.Type switch
                {
                    ChangeType.Inserted => "+ ",
                    ChangeType.Deleted => "- ",
                    ChangeType.Modified => "! ",
                    _ => "  "
                };

                sb.AppendLine($"{prefix}{line.Text}");
                displayedLines++;
            }

            if (ranges.Count > 1 && (start, end) != ranges.Last())
            {
                sb.AppendLine("...");
            }
        }

        return sb.ToString();
    }

    #endregion
}
