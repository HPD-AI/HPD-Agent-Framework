using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using HPD.Agent;


/// <summary>
/// CodingToolkit - Comprehensive coding assistant with file operations, search, execution, and analysis.
/// Features: Line-based reading, diff generation, glob patterns, .gitignore support, grep search, shell execution.
/// </summary>
[Toolkit(
    "Contains tools Coding operations: file operations, code search, shell execution, and code analysis.",
    SystemPrompt: CodingToolkitPrompts.SystemPrompt)]
public class CodingToolkit
{

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".bin", ".obj", ".o",
        ".zip", ".tar", ".gz", ".rar", ".7z",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".woff", ".woff2", ".ttf", ".eot",
        ".pyc", ".class", ".pdb"
    };

    private static readonly HashSet<string> DefaultIgnoreDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".vs", ".idea",
        "__pycache__", "venv", ".venv", "dist", "build", "target",
        ".next", ".nuxt", "coverage", ".cache"
    };

    // ═══════════════════════════════════════════════════════════════════
    // READ FILE - With offset/limit like Gemini
    // ═══════════════════════════════════════════════════════════════════

    [AIFunction]
    [AIDescription("Read file contents with optional line offset and limit. Returns file content with line numbers.")]
    public string ReadFile(
        [AIDescription("Absolute path to the file to read.")] string filePath,
        [AIDescription("Line number to start reading from (1-based). Default: 1")] int offset = 1,
        [AIDescription("Maximum number of lines to read. Default: 500 (0 = all lines)")] int limit = 500)
    {
        if (!File.Exists(filePath))
            return $"Error: File not found: {filePath}";

        // Check if binary
        var ext = Path.GetExtension(filePath);
        if (BinaryExtensions.Contains(ext))
            return $"Error: Cannot read binary file ({ext}). Use appropriate tool for binary files.";

        try
        {
            var lines = File.ReadAllLines(filePath);
            var totalLines = lines.Length;
            
            // Validate offset
            if (offset < 1) offset = 1;
            if (offset > totalLines)
                return $"Error: Offset {offset} exceeds file length ({totalLines} lines).";

            // Calculate range
            var startIndex = offset - 1;
            var endIndex = limit > 0 
                ? Math.Min(startIndex + limit, totalLines) 
                : totalLines;
            var linesRead = endIndex - startIndex;

            // Build output with line numbers
            var sb = new StringBuilder();
            var mimeType = GetMimeType(ext);
            
            sb.AppendLine($"File: {Path.GetFileName(filePath)}");
            sb.AppendLine($"Type: {mimeType}");
            sb.AppendLine($"Lines: {offset}-{offset + linesRead - 1} of {totalLines}");
            sb.AppendLine("---");

            for (var i = startIndex; i < endIndex; i++)
            {
                sb.AppendLine($"{i + 1,4}│ {lines[i]}");
            }

            // Add truncation notice if needed
            if (endIndex < totalLines)
            {
                sb.AppendLine("---");
                sb.AppendLine($"TRUNCATED: Showing {linesRead} of {totalLines} lines.");
                sb.AppendLine($"To read more, use offset: {endIndex + 1}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // EDIT FILE - Targeted string replacement (PREFERRED for modifications)
    // ═══════════════════════════════════════════════════════════════════

    [AIFunction]
    [AIDescription("Edit a file by replacing exact string matches. PREFERRED over WriteFile for targeted changes. Safer and more precise.")]
    public string EditFile(
        [AIDescription("Absolute path to the file to edit.")] string filePath,
        [AIDescription("Exact string to find and replace (must match exactly).")] string oldString,
        [AIDescription("New string to replace with.")] string newString,
        [AIDescription("Replace all occurrences (true) or just first (false). Default: false")] bool replaceAll = false)
    {
        if (!File.Exists(filePath))
            return $"Error: File not found: {filePath}";

        if (string.IsNullOrEmpty(oldString))
            return "Error: oldString cannot be empty";

        if (oldString == newString)
            return "Error: oldString and newString are identical - no changes needed";

        try
        {
            var content = File.ReadAllText(filePath);

            // Check if old string exists
            if (!content.Contains(oldString))
            {
                return $"Error: String not found in file. Make sure the string matches exactly.\n" +
                       $"Looking for: {oldString.Substring(0, Math.Min(100, oldString.Length))}...";
            }

            // Count occurrences
            var occurrences = CountOccurrences(content, oldString);

            // Perform replacement
            var newContent = replaceAll
                ? content.Replace(oldString, newString)
                : ReplaceFirst(content, oldString, newString);

            // Generate diff for preview
            var sb = new StringBuilder();
            var fileName = Path.GetFileName(filePath);
            var diffBuilder = new InlineDiffBuilder(new Differ());
            var diff = diffBuilder.BuildDiffModel(content, newContent);

            var additions = diff.Lines.Count(l => l.Type == ChangeType.Inserted);
            var deletions = diff.Lines.Count(l => l.Type == ChangeType.Deleted);

            sb.AppendLine($"Editing: {filePath}");
            sb.AppendLine($"Replacements: {(replaceAll ? occurrences : 1)} occurrence(s)");
            sb.AppendLine($"Changes: +{additions} -{deletions} lines");
            sb.AppendLine("---");

            // Show diff (condensed)
            var diffLineCount = 0;
            foreach (var line in diff.Lines)
            {
                if (diffLineCount >= 30 && line.Type == ChangeType.Unchanged)
                    continue; // Skip unchanged lines after showing enough context

                if (diffLineCount >= 50)
                {
                    sb.AppendLine("... (diff truncated)");
                    break;
                }

                var prefix = line.Type switch
                {
                    ChangeType.Inserted => "+ ",
                    ChangeType.Deleted => "- ",
                    ChangeType.Modified => "~ ",
                    _ => "  "
                };

                if (line.Type != ChangeType.Unchanged || diffLineCount < 30)
                {
                    sb.AppendLine($"{prefix}{line.Text}");
                    diffLineCount++;
                }
            }

            // Write the file
            File.WriteAllText(filePath, newContent);

            sb.AppendLine("---");
            sb.AppendLine($"✓ Successfully edited {filePath}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error editing file: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // WRITE FILE - With diff preview like Gemini
    // ═══════════════════════════════════════════════════════════════════

    [AIFunction]
    [AIDescription("Write content to a file. Shows diff if file exists. Creates directories if needed.")]
    public string WriteFile(
        [AIDescription("Absolute path to the file to write.")] string filePath,
        [AIDescription("Content to write to the file.")] string content)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var sb = new StringBuilder();
            var fileExists = File.Exists(filePath);
            string? originalContent = null;

            if (fileExists)
            {
                originalContent = File.ReadAllText(filePath);
                
                // Generate unified diff format for display
                var fileName = Path.GetFileName(filePath);
                var unifiedDiff = GenerateUnifiedDiff(originalContent, content, fileName);
                
                sb.AppendLine(unifiedDiff);
                sb.AppendLine();
                
                // Also generate inline diff for summary
                var diffBuilder = new InlineDiffBuilder(new Differ());
                var diff = diffBuilder.BuildDiffModel(originalContent, content);
                
                var additions = diff.Lines.Count(l => l.Type == ChangeType.Inserted);
                var deletions = diff.Lines.Count(l => l.Type == ChangeType.Deleted);
                
                sb.AppendLine($"Modifying: {filePath}");
                sb.AppendLine($"Changes: +{additions} -{deletions} lines");
                sb.AppendLine("---");
                
                // Show condensed diff (max 50 lines)
                var diffLineCount = 0;
                foreach (var line in diff.Lines)
                {
                    if (diffLineCount >= 50)
                    {
                        sb.AppendLine("... (diff truncated)");
                        break;
                    }
                    
                    var prefix = line.Type switch
                    {
                        ChangeType.Inserted => "+ ",
                        ChangeType.Deleted => "- ",
                        ChangeType.Modified => "~ ",
                        _ => "  "
                    };
                    
                    // Only show changed lines and minimal context
                    if (line.Type != ChangeType.Unchanged || diffLineCount < 3)
                    {
                        sb.AppendLine($"{prefix}{line.Text}");
                        diffLineCount++;
                    }
                }
            }
            else
            {
                sb.AppendLine($"Creating: {filePath}");
                sb.AppendLine($"Size: {content.Length} characters, {content.Split('\n').Length} lines");
            }

            // Write the file
            File.WriteAllText(filePath, content);
            
            sb.AppendLine("---");
            sb.AppendLine($"✓ Successfully wrote to {filePath}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // LIST DIRECTORY - With .gitignore support like Gemini
    // ═══════════════════════════════════════════════════════════════════

    [AIFunction]
    [AIDescription("List directory contents with file metadata. Respects .gitignore patterns.")]
    public string ListDirectory(
        [AIDescription("Absolute path to the directory to list. If empty, uses current working directory.")] string? directoryPath = null,
        [AIDescription("Include hidden files/directories. Default: false")] bool showHidden = false,
        [AIDescription("Respect .gitignore patterns. Default: true")] bool respectGitIgnore = true)
    {
        // Default to current directory if not provided
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            directoryPath = Directory.GetCurrentDirectory();
        }

        if (!Directory.Exists(directoryPath))
            return $"Error: Directory not found: {directoryPath}";

        try
        {
            var sb = new StringBuilder();
            var ignorePatterns = new List<string>();

            // Load .gitignore if present
            if (respectGitIgnore)
            {
                var gitignorePath = Path.Combine(directoryPath, ".gitignore");
                if (File.Exists(gitignorePath))
                {
                    ignorePatterns.AddRange(
                        File.ReadAllLines(gitignorePath)
                            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
                    );
                }
            }

            sb.AppendLine($"Directory: {directoryPath}");
            sb.AppendLine("---");

            // List directories first
            var dirs = Directory.GetDirectories(directoryPath)
                .Select(d => new DirectoryInfo(d))
                .Where(d => showHidden || !d.Name.StartsWith('.'))
                .Where(d => !DefaultIgnoreDirs.Contains(d.Name))
                .OrderBy(d => d.Name);

            foreach (var dir in dirs)
            {
                sb.AppendLine($"📁 {dir.Name}/");
            }

            // List files
            var files = Directory.GetFiles(directoryPath)
                .Select(f => new FileInfo(f))
                .Where(f => showHidden || !f.Name.StartsWith('.'))
                .Where(f => !ShouldIgnore(f.Name, ignorePatterns))
                .OrderBy(f => f.Name);

            foreach (var file in files)
            {
                var size = FormatFileSize(file.Length);
                var modified = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                sb.AppendLine($"📄 {file.Name,-40} {size,10} {modified}");
            }

            var dirCount = dirs.Count();
            var fileCount = files.Count();
            
            sb.AppendLine("---");
            sb.AppendLine($"Total: {dirCount} directories, {fileCount} files");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error listing directory: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // GLOB SEARCH - Using Microsoft.Extensions.FileSystemGlobbing
    // ═══════════════════════════════════════════════════════════════════

    [AIFunction]
    [AIDescription("Search for files using glob patterns (e.g., '**/*.cs', 'src/**/*.ts').")]
    public string GlobSearch(
        [AIDescription("Root directory to search from.")] string rootPath,
        [AIDescription("Glob pattern (e.g., '**/*.cs', 'src/**/*.json').")] string pattern,
        [AIDescription("Maximum results to return. Default: 100")] int maxResults = 100)
    {
        if (!Directory.Exists(rootPath))
            return $"Error: Directory not found: {rootPath}";

        try
        {
            var matcher = new Matcher();
            matcher.AddInclude(pattern);
            
            // Exclude common directories
            foreach (var dir in DefaultIgnoreDirs)
            {
                matcher.AddExclude($"**/{dir}/**");
            }

            var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(rootPath)));
            
            if (!result.HasMatches)
                return $"No files found matching '{pattern}'";

            var sb = new StringBuilder();
            var matches = result.Files.Take(maxResults).ToList();
            
            sb.AppendLine($"Found {result.Files.Count()} file(s) matching '{pattern}':");
            sb.AppendLine("---");

            foreach (var file in matches)
            {
                sb.AppendLine(file.Path);
            }

            if (result.Files.Count() > maxResults)
            {
                sb.AppendLine($"... and {result.Files.Count() - maxResults} more files");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error searching files: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // GREP - Content search with regex support like Gemini
    // ═══════════════════════════════════════════════════════════════════

    [AIFunction]
    [AIDescription("Search file contents using regex pattern. Returns matching lines with context.")]
    public string Grep(
        [AIDescription("Root directory to search in.")] string rootPath,
        [AIDescription("Regex pattern to search for.")] string pattern,
        [AIDescription("File glob pattern to filter (e.g., '*.cs'). Default: all files")] string? includeFiles = null,
        [AIDescription("Case-insensitive search. Default: true")] bool ignoreCase = true,
        [AIDescription("Maximum results. Default: 50")] int maxResults = 50)
    {
        if (!Directory.Exists(rootPath))
            return $"Error: Directory not found: {rootPath}";

        try
        {
            var regexOptions = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
            var regex = new Regex(pattern, regexOptions | RegexOptions.Compiled);

            // Get files to search
            var matcher = new Matcher();
            matcher.AddInclude(includeFiles ?? "**/*");
            foreach (var dir in DefaultIgnoreDirs)
                matcher.AddExclude($"**/{dir}/**");
            // Exclude binary files
            foreach (var ext in BinaryExtensions)
                matcher.AddExclude($"**/*{ext}");

            var filesToSearch = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(rootPath)));

            var results = new List<(string File, int Line, string Content)>();

            foreach (var fileMatch in filesToSearch.Files)
            {
                if (results.Count >= maxResults) break;

                var fullPath = Path.Combine(rootPath, fileMatch.Path);
                try
                {
                    var lines = File.ReadAllLines(fullPath);
                    for (var i = 0; i < lines.Length && results.Count < maxResults; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            results.Add((fileMatch.Path, i + 1, lines[i].Trim()));
                        }
                    }
                }
                catch
                {
                    // Skip unreadable files
                }
            }

            if (results.Count == 0)
                return $"No matches found for pattern '{pattern}'";

            var sb = new StringBuilder();
            sb.AppendLine($"Found {results.Count} match(es) for '{pattern}':");
            sb.AppendLine("---");

            // Group by file
            var byFile = results.GroupBy(r => r.File);
            foreach (var group in byFile)
            {
                sb.AppendLine($"File: {group.Key}");
                foreach (var match in group)
                {
                    var preview = match.Content.Length > 100 
                        ? match.Content[..100] + "..." 
                        : match.Content;
                    sb.AppendLine($"  L{match.Line}: {preview}");
                }
            }

            if (results.Count >= maxResults)
            {
                sb.AppendLine($"--- (limited to {maxResults} results)");
            }

            return sb.ToString();
        }
        catch (RegexParseException ex)
        {
            return $"Error: Invalid regex pattern - {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error searching: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // DIFF FILES - Compare two files
    // ═══════════════════════════════════════════════════════════════════

    [AIFunction]
    [AIDescription("Compare two files and show differences.")]
    public string DiffFiles(
        [AIDescription("Path to the original file.")] string originalPath,
        [AIDescription("Path to the modified file.")] string modifiedPath)
    {
        if (!File.Exists(originalPath))
            return $"Error: Original file not found: {originalPath}";
        if (!File.Exists(modifiedPath))
            return $"Error: Modified file not found: {modifiedPath}";

        try
        {
            var original = File.ReadAllText(originalPath);
            var modified = File.ReadAllText(modifiedPath);

            var diffBuilder = new SideBySideDiffBuilder(new Differ());
            var diff = diffBuilder.BuildDiffModel(original, modified);

            var sb = new StringBuilder();
            sb.AppendLine($"Diff: {Path.GetFileName(originalPath)} ↔ {Path.GetFileName(modifiedPath)}");
            sb.AppendLine("---");

            var lineNum = 0;
            foreach (var (oldLine, newLine) in diff.OldText.Lines.Zip(diff.NewText.Lines))
            {
                lineNum++;
                if (oldLine.Type == ChangeType.Unchanged && newLine.Type == ChangeType.Unchanged)
                    continue;

                if (oldLine.Type == ChangeType.Deleted)
                    sb.AppendLine($"-{lineNum,4}│ {oldLine.Text}");
                if (newLine.Type == ChangeType.Inserted)
                    sb.AppendLine($"+{lineNum,4}│ {newLine.Text}");
                if (oldLine.Type == ChangeType.Modified)
                {
                    sb.AppendLine($"-{lineNum,4}│ {oldLine.Text}");
                    sb.AppendLine($"+{lineNum,4}│ {newLine.Text}");
                }
            }

            return sb.Length > 0 ? sb.ToString() : "Files are identical.";
        }
        catch (Exception ex)
        {
            return $"Error comparing files: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // FILE INFO - Get detailed file metadata
    // ═══════════════════════════════════════════════════════════════════

    [AIFunction]
    [AIDescription("Get detailed information about a file.")]
    public string GetFileInfo(
        [AIDescription("Absolute path to the file.")] string filePath)
    {
        if (!File.Exists(filePath))
            return $"Error: File not found: {filePath}";

        try
        {
            var info = new FileInfo(filePath);
            var ext = info.Extension;
            var mimeType = GetMimeType(ext);
            var isBinary = BinaryExtensions.Contains(ext);
            
            var sb = new StringBuilder();
            sb.AppendLine($"File: {info.Name}");
            sb.AppendLine($"Path: {info.FullName}");
            sb.AppendLine($"Size: {FormatFileSize(info.Length)} ({info.Length:N0} bytes)");
            sb.AppendLine($"Type: {mimeType}");
            sb.AppendLine($"Binary: {isBinary}");
            sb.AppendLine($"Created: {info.CreationTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Modified: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Accessed: {info.LastAccessTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"ReadOnly: {info.IsReadOnly}");

            if (!isBinary)
            {
                var lines = File.ReadAllLines(filePath);
                sb.AppendLine($"Lines: {lines.Length}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error getting file info: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static int CountOccurrences(string text, string substring)
    {
        if (string.IsNullOrEmpty(substring))
            return 0;

        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += substring.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        int pos = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (pos < 0)
            return text;

        return text.Substring(0, pos) + newValue + text.Substring(pos + oldValue.Length);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    private static bool ShouldIgnore(string fileName, List<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.Contains('*'))
            {
                var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                if (Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase))
                    return true;
            }
            else if (fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string GetMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".cs" => "text/x-csharp",
            ".js" => "text/javascript",
            ".ts" => "text/typescript",
            ".tsx" => "text/typescript-jsx",
            ".jsx" => "text/javascript-jsx",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" => "text/html",
            ".css" => "text/css",
            ".md" => "text/markdown",
            ".py" => "text/x-python",
            ".java" => "text/x-java",
            ".go" => "text/x-go",
            ".rs" => "text/x-rust",
            ".cpp" or ".cc" or ".cxx" => "text/x-c++",
            ".c" or ".h" => "text/x-c",
            ".rb" => "text/x-ruby",
            ".php" => "text/x-php",
            ".swift" => "text/x-swift",
            ".kt" => "text/x-kotlin",
            ".scala" => "text/x-scala",
            ".sql" => "text/x-sql",
            ".sh" or ".bash" => "text/x-shellscript",
            ".ps1" => "text/x-powershell",
            ".yaml" or ".yml" => "text/yaml",
            ".toml" => "text/toml",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".log" => "text/plain",
            ".env" => "text/plain",
            ".gitignore" => "text/plain",
            ".dockerfile" or "" when extension == "Dockerfile" => "text/x-dockerfile",
            _ => "application/octet-stream"
        };
    }

    private string GenerateUnifiedDiff(string originalContent, string newContent, string fileName)
    {
        var originalLines = originalContent.Split('\n');
        var newLines = newContent.Split('\n');
        
        var sb = new StringBuilder();
        sb.AppendLine($"--- {fileName}\t(Original)");
        sb.AppendLine($"+++ {fileName}\t(Modified)");
        
        // Simple line-by-line diff
        var maxLines = Math.Max(originalLines.Length, newLines.Length);
        for (int i = 0; i < maxLines; i++)
        {
            var originalLine = i < originalLines.Length ? originalLines[i] : null;
            var newLine = i < newLines.Length ? newLines[i] : null;
            
            if (originalLine != newLine)
            {
                if (originalLine != null)
                    sb.AppendLine($"- {originalLine}");
                if (newLine != null)
                    sb.AppendLine($"+ {newLine}");
            }
            else if (originalLine != null)
            {
                sb.AppendLine($"  {originalLine}");
            }
        }
        
        return sb.ToString();
    }

        private static readonly string DefaultShell = GetDefaultShell();
    private static readonly string ShellArg = GetShellArg();

    // ═══════════════════════════════════════════════════════════════════
    // EXECUTE COMMAND - Cross-platform shell execution
    // ═══════════════════════════════════════════════════════════════════

    [AIFunction]
    [AIDescription("Execute a shell command and return its output. Use for build, test, package management, and running scripts.")]
    public async Task<string> ExecuteCommand(
        [AIDescription("Command to execute (e.g., 'dotnet build', 'npm test', 'git status')")] string command,
        [AIDescription("Working directory for command execution. Default: current directory")] string? workingDirectory = null,
        [AIDescription("Timeout in milliseconds. Default: 120000 (2 minutes)")] int timeout = 120000)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "Error: Command cannot be empty";

        // Use current directory if not specified
        var workDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : workingDirectory;

        if (!Directory.Exists(workDir))
            return $"Error: Working directory not found: {workDir}";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = DefaultShell,
                Arguments = $"{ShellArg} \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var output = new StringBuilder();
            var error = new StringBuilder();

            using var process = new Process { StartInfo = startInfo };

            // Capture output and error streams
            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                    output.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                    error.AppendLine(e.Data);
            };

            var sw = Stopwatch.StartNew();
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for completion with timeout
            var completed = await Task.Run(() => process.WaitForExit(timeout));
            sw.Stop();

            if (!completed)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore kill errors
                }

                return FormatCommandResult(
                    command: command,
                    workingDir: workDir,
                    exitCode: -1,
                    output: output.ToString(),
                    error: $"Command timed out after {timeout}ms",
                    duration: sw.ElapsedMilliseconds,
                    timedOut: true
                );
            }

            var exitCode = process.ExitCode;
            var outputText = output.ToString();
            var errorText = error.ToString();

            return FormatCommandResult(
                command: command,
                workingDir: workDir,
                exitCode: exitCode,
                output: outputText,
                error: errorText,
                duration: sw.ElapsedMilliseconds,
                timedOut: false
            );
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}\n" +
                   $"Command: {command}\n" +
                   $"Working Directory: {workDir}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static string GetDefaultShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "cmd.exe";

        // Unix-like systems (macOS, Linux)
        return Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
    }

    private static string GetShellArg()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "/c";

        return "-c";
    }

    private static string FormatCommandResult(
        string command,
        string workingDir,
        int exitCode,
        string output,
        string error,
        long duration,
        bool timedOut)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine($"Command: {command}");
        sb.AppendLine($"Working Directory: {workingDir}");
        sb.AppendLine($"Duration: {duration}ms");
        sb.AppendLine($"Exit Code: {exitCode}");
        sb.AppendLine("---");

        // Output
        if (!string.IsNullOrWhiteSpace(output))
        {
            sb.AppendLine("OUTPUT:");
            sb.AppendLine(TruncateOutput(output, maxLines: 100));
        }

        // Error
        if (!string.IsNullOrWhiteSpace(error))
        {
            sb.AppendLine();
            sb.AppendLine("ERROR:");
            sb.AppendLine(TruncateOutput(error, maxLines: 50));
        }

        // Status
        sb.AppendLine("---");
        if (timedOut)
        {
            sb.AppendLine("   TIMED OUT");
        }
        else if (exitCode == 0)
        {
            sb.AppendLine("✓ SUCCESS");
        }
        else
        {
            sb.AppendLine($"   FAILED (Exit Code: {exitCode})");
        }

        return sb.ToString();
    }

    private static string TruncateOutput(string text, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lines = text.Split('\n');
        if (lines.Length <= maxLines)
            return text;

        var truncated = string.Join('\n', lines.Take(maxLines));
        return $"{truncated}\n... ({lines.Length - maxLines} more lines truncated)";
    }
}
