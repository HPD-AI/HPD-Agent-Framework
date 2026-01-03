using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text.RegularExpressions;

// ============================================================================
// MESSAGE COMPONENTS
// ============================================================================

/// <summary>
/// User message bubble.
/// </summary>
public class UserMessage : UIComponent
{
    public string Content { get; set; } = "";
    
    public override IRenderable Render()
    {
        return new Panel(new Markup(Markup.Escape(Content)))
            .Header("[bold cyan]You[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Padding(1, 0);
    }
}

/// <summary>
/// Error message display.
/// </summary>
public class ErrorMessage : UIComponent
{
    public string Message { get; set; } = "";
    public string? Details { get; set; }
    
    public override IRenderable Render()
    {
        var rows = new List<IRenderable>
        {
            new Markup($"[red bold]Error:[/] [red]{Markup.Escape(Message)}[/]")
        };
        
        if (!string.IsNullOrEmpty(Details))
        {
            rows.Add(new Text(""));
            rows.Add(new Markup($"[dim]{Markup.Escape(Details)}[/]"));
        }
        
        return new Panel(new Rows(rows))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Red)
            .Padding(1, 0);
    }
}

// ============================================================================
// LAYOUT COMPONENTS
// ============================================================================

/// <summary>
/// App header with branding and version.
/// </summary>
public class AppHeader : UIComponent
{
    public string Title { get; set; } = "HPD Agent";
    public string Version { get; set; } = "1.0.0";
    public string? Model { get; set; }
    
    public override IRenderable Render()
    {
        var figlet = new FigletText(Title)
            .LeftJustified()
            .Color(Color.Cyan1);
            
        var info = new Markup(
            $"[dim]v{Version}[/]" + 
            (Model != null ? $" [dim]• {Markup.Escape(Model)}[/]" : "")
        );
        
        return new Rows(figlet, info, new Text(""));
    }
}

/// <summary>
/// Session stats display (tokens, time, etc.).
/// Mirrors Gemini's StatsDisplay.tsx
/// </summary>
public class StatsDisplay : UIComponent
{
    public int TotalTokens { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public TimeSpan TotalTime { get; set; }
    public int ToolCalls { get; set; }
    
    public override IRenderable Render()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[dim]Metric[/]")
            .AddColumn("[dim]Value[/]");
            
        table.AddRow("Total Tokens", TotalTokens.ToString("N0"));
        table.AddRow("Prompt Tokens", PromptTokens.ToString("N0"));
        table.AddRow("Completion Tokens", CompletionTokens.ToString("N0"));
        table.AddRow("Duration", $"{TotalTime.TotalSeconds:F2}s");
        table.AddRow("Tool Calls", ToolCalls.ToString());
        
        return table;
    }
}

/// <summary>
/// Help panel with available commands.
/// Now dynamically populated from CommandRegistry.
/// </summary>
public class HelpPanel : UIComponent
{
    public List<SlashCommand> Commands { get; set; } = new();
    
    public override IRenderable Render()
    {
        var rows = new List<IRenderable>();
        
        // Basics section
        rows.Add(new Markup("[bold cyan]Basics:[/]"));
        rows.Add(new Markup("  Type your message or use commands to interact with the agent"));
        rows.Add(new Markup("  Press [bold]Tab[/] for command completion"));
        rows.Add(new Text(""));
        
        // Commands section
        rows.Add(new Markup("[bold cyan]Available Commands:[/]"));
        
        if (Commands.Count == 0)
        {
            rows.Add(new Markup("[dim]  No commands available[/]"));
        }
        else
        {
            foreach (var cmd in Commands.OrderBy(c => c.Name))
            {
                var aliases = cmd.AltNames.Count > 0 
                    ? $" [{Theme.Text.Muted}](aliases: {string.Join(", ", cmd.AltNames)})[/]" 
                    : "";
                    
                var category = cmd.Category != "Built-in"
                    ? $" [{Theme.Text.Muted}][{cmd.Category}][/]"
                    : "";
                
                rows.Add(new Markup(
                    $"  [bold cyan]/{Markup.Escape(cmd.Name)}[/]{aliases}{category}"
                ));
                
                if (!string.IsNullOrEmpty(cmd.Description))
                {
                    rows.Add(new Markup($"    [{Theme.Text.Secondary}]{Markup.Escape(cmd.Description)}[/]"));
                }
                
                // Show subcommands if any
                if (cmd.SubCommands != null && cmd.SubCommands.Count > 0)
                {
                    foreach (var sub in cmd.SubCommands.Where(s => !s.Hidden))
                    {
                        rows.Add(new Markup(
                            $"    [cyan]• {Markup.Escape(sub.Name)}[/] " +
                            $"[{Theme.Text.Muted}]- {Markup.Escape(sub.Description ?? "")}[/]"
                        ));
                    }
                }
                
                rows.Add(new Text(""));
            }
        }
        
        // Keyboard shortcuts section
        rows.Add(new Markup("[bold cyan]Keyboard Shortcuts:[/]"));
        rows.Add(new Markup("  [bold]Ctrl+C[/] - Cancel current operation or exit"));
        rows.Add(new Markup("  [bold]Ctrl+L[/] - Clear screen"));
        rows.Add(new Markup("  [bold]↑/↓[/] - Navigate command suggestions"));
        rows.Add(new Markup("  [bold]Enter[/] - Execute command or send message"));
        rows.Add(new Markup("  [bold]Tab[/] - Complete command"));
        
        return new Panel(new Rows(rows))
            .Header("[cyan]HPD Agent Help[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Padding(1, 0);
    }
}

/// <summary>
/// Context summary display showing current session information.
/// Inspired by Gemini's ContextSummaryDisplay.
/// </summary>
public class ContextSummaryDisplay : UIComponent
{
    public int FileCount { get; set; }
    public int ToolCount { get; set; }
    public string? WorkingDirectory { get; set; }
    
    public override IRenderable Render()
    {
        var parts = new List<string>();
        
        if (FileCount > 0)
        {
            parts.Add($"[{Theme.Text.Accent}]{FileCount} file{(FileCount > 1 ? "s" : "")}[/]");
        }
        
        if (ToolCount > 0)
        {
            parts.Add($"[{Theme.Text.Accent}]{ToolCount} tool{(ToolCount > 1 ? "s" : "")}[/]");
        }
        
        if (!string.IsNullOrEmpty(WorkingDirectory))
        {
            parts.Add($"[{Theme.Text.Muted}]{Markup.Escape(WorkingDirectory)}[/]");
        }
        
        if (parts.Count == 0)
        {
            return new Markup($"[{Theme.Text.Muted}]Ready[/]");
        }
        
        return new Markup(string.Join(" [dim]•[/] ", parts));
    }
}

/// <summary>
/// Spinner component for indicating active work.
/// </summary>
public class SpinnerComponent : UIComponent
{
    private readonly string _text;
    private readonly Color _color;
    
    public SpinnerComponent(string text = "Thinking...", Color? color = null)
    {
        _text = text;
        _color = color ?? Theme.Status.Executing;
    }
    
    public override IRenderable Render()
    {
        return new Markup($"[{_color}]◐[/] {_text}");
    }
    
    /// <summary>
    /// Run with a live spinner that updates in place.
    /// </summary>
    public static async Task<T> WithSpinnerAsync<T>(string text, Func<Task<T>> action)
    {
        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(text, async ctx => await action());
    }
    
    public static async Task WithSpinnerAsync(string text, Func<Task> action)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(text, async ctx => await action());
    }
}

// ============================================================================
// TOOL COMPONENTS
// ============================================================================

/// <summary>
/// Detected content types for smart UI rendering.
/// Add new types here when extending the UI.
/// </summary>
public enum ResultType
{
    /// <summary>Plain text, no special formatting</summary>
    Plain,

    /// <summary>Unified diff format (file changes)</summary>
    Diff,

    /// <summary>Markdown or ASCII table</summary>
    Table,

    /// <summary>Valid JSON object or array</summary>
    Json,

    /// <summary>Error message (starts with "Error:")</summary>
    Error
}

/// <summary>
/// Detects the content type of tool results for smart UI rendering.
///
/// This enables Toolkit-agnostic UI: instead of checking tool names,
/// we detect what the content IS and render accordingly.
///
/// To add a new result type:
/// 1. Add enum value to ResultType
/// 2. Add detection method (IsXxx)
/// 3. Add check in Detect() method
/// 4. Create renderer in Components/
/// 5. Wire in AgentUIRenderer.RenderResultByType()
/// </summary>
public static class ResultDetector
{
    /// <summary>
    /// Detect the content type of a tool result.
    /// </summary>
    public static ResultType Detect(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return ResultType.Plain;

        // Error takes priority
        if (result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            return ResultType.Error;

        // Check specific formats (order matters - most specific first)
        if (IsDiff(result))
            return ResultType.Diff;

        if (IsJson(result))
            return ResultType.Json;

        if (IsTable(result))
            return ResultType.Table;

        return ResultType.Plain;
    }

    /// <summary>
    /// Detect unified diff format.
    /// Matches:
    /// - Standard unified diff (--- / +++ / @@)
    /// - Simple +/- line prefixes
    /// </summary>
    private static bool IsDiff(string text)
    {
        // Standard unified diff markers
        if (text.Contains("---") && text.Contains("+++"))
            return true;

        // Hunk headers
        if (text.Contains("@@ "))
            return true;

        // Multiple +/- prefixed lines (simple diff)
        var lines = text.Split('\n');
        var diffLineCount = lines.Count(l =>
            l.StartsWith("+") || l.StartsWith("-"));

        // At least 3 diff lines to avoid false positives
        return diffLineCount > 2;
    }

    /// <summary>
    /// Detect valid JSON (object or array).
    /// </summary>
    private static bool IsJson(string text)
    {
        var trimmed = text.TrimStart();

        // Quick check for JSON start characters
        if (!trimmed.StartsWith("{") && !trimmed.StartsWith("["))
            return false;

        // Validate by parsing
        try
        {
            System.Text.Json.JsonDocument.Parse(trimmed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Detect markdown or ASCII tables.
    /// Matches:
    /// - Markdown tables (| col1 | col2 |)
    /// - Box-drawing tables (┌─┬─┐)
    /// </summary>
    private static bool IsTable(string text)
    {
        var lines = text.Split('\n');

        // Markdown table: multiple lines starting with |
        var pipeLines = lines.Count(l =>
            l.Contains('|') && l.Trim().StartsWith("|"));
        if (pipeLines >= 2)
            return true;

        // Box-drawing table
        if (lines.Any(l => l.Contains('┌') || l.Contains('├') || l.Contains('└')))
            return true;

        return false;
    }
}

/// <summary>
/// Displays a single tool call with status, args, and result.
/// Mirrors Gemini's ToolMessage.tsx
/// </summary>
public class ToolMessage : UIComponent
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Args { get; set; }
    public string? Result { get; set; }
    public ToolCallStatus Status { get; set; } = ToolCallStatus.Pending;
    
    public override IRenderable Render()
    {
        var (icon, color) = GetStatusIndicator();
        
        var panel = new Panel(RenderContent())
            .Header($"[{color}]{icon}[/] [{Theme.Tool.Header}]{Markup.Escape(Name)}[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(GetBorderColor())
            .Padding(1, 0);
            
        return panel;
    }
    
    private IRenderable RenderContent()
    {
        var rows = new List<IRenderable>();
        
        // Description
        if (!string.IsNullOrEmpty(Description))
        {
            rows.Add(new Markup($"[dim]{Markup.Escape(Description)}[/]"));
            rows.Add(new Text(""));
        }
        
        // Arguments
        if (!string.IsNullOrEmpty(Args))
        {
            rows.Add(new Markup($"[grey]Args:[/]"));
            rows.Add(new Text(UIHelpers.FormatJson(Args)));
            rows.Add(new Text(""));
        }
        
        // Result
        if (!string.IsNullOrEmpty(Result))
        {
            var resultColor = Status == ToolCallStatus.Error ? Theme.Status.Error : Theme.Status.Success;
            var resultIcon = Status == ToolCallStatus.Error ? "✗" : "✓";
            
            rows.Add(new Markup($"[{resultColor}]{resultIcon} Result:[/]"));
            
            // Truncate long results
            var displayResult = Result.Length > 1000 
                ? Result.Substring(0, 1000) + "\n[dim]... (truncated)[/]"
                : Result;
            rows.Add(new Text(displayResult));
        }
        
        // Executing indicator
        if (Status == ToolCallStatus.Executing)
        {
            rows.Add(new Markup("[cyan]◐ Executing...[/]"));
        }
        
        return new Rows(rows);
    }
    
    private (string icon, Color color) GetStatusIndicator() => Status switch
    {
        ToolCallStatus.Pending => ("○", Theme.Status.Pending),
        ToolCallStatus.Executing => ("◐", Theme.Status.Executing),
        ToolCallStatus.Completed => ("●", Theme.Status.Success),
        ToolCallStatus.Error => ("✗", Theme.Status.Error),
        _ => ("○", Theme.Status.Pending)
    };
    
    private Color GetBorderColor() => Status switch
    {
        ToolCallStatus.Pending => Theme.Tool.Border,
        ToolCallStatus.Executing => Theme.Status.Executing,
        ToolCallStatus.Completed => Theme.Status.Success,
        ToolCallStatus.Error => Theme.Status.Error,
        _ => Theme.Tool.Border
    };
}

// ============================================================================
// DIFF RENDERING COMPONENTS
// ============================================================================

/// <summary>
/// Renders unified diff with syntax highlighting.
/// Mirrors Gemini's DiffRenderer.tsx
/// </summary>
public class DiffRenderer : UIComponent
{
    public string DiffContent { get; set; } = "";
    public string? Filename { get; set; }
    public int MaxLines { get; set; } = 50;
    
    public override IRenderable Render()
    {
        var lines = ParseDiff(DiffContent);
        
        if (lines.Count == 0)
            return new Text("[No changes]");
        
        var rows = new List<IRenderable>();
        
        // Header with filename
        if (!string.IsNullOrEmpty(Filename))
        {
            rows.Add(new Markup($"[bold]{Markup.Escape(Filename)}[/]"));
        }
        
        // Stats
        var additions = lines.Count(l => l.Type == DiffLineType.Add);
        var deletions = lines.Count(l => l.Type == DiffLineType.Delete);
        rows.Add(new Markup($"[green]+{additions}[/] [red]-{deletions}[/]"));
        rows.Add(new Text(""));
        
        // Diff lines
        var displayLines = lines.Take(MaxLines).ToList();
        foreach (var line in displayLines)
        {
            rows.Add(RenderLine(line));
        }
        
        // Truncation notice
        if (lines.Count > MaxLines)
        {
            rows.Add(new Markup($"[dim]... {lines.Count - MaxLines} more lines[/]"));
        }
        
        return new Panel(new Rows(rows))
            .Border(BoxBorder.Rounded)
            .BorderColor(Theme.Tool.Border)
            .Padding(1, 0);
    }
    
    private IRenderable RenderLine(DiffLine line)
    {
        var (prefix, color) = line.Type switch
        {
            DiffLineType.Add => ("+", Theme.Diff.Added),
            DiffLineType.Delete => ("-", Theme.Diff.Removed),
            DiffLineType.Context => (" ", Theme.Diff.Context),
            DiffLineType.Hunk => ("@", Theme.Diff.Hunk),
            _ => (" ", Theme.Text.Secondary)
        };
        
        var lineNum = line.Type switch
        {
            DiffLineType.Add => line.NewLine?.ToString().PadLeft(4) ?? "    ",
            DiffLineType.Delete => line.OldLine?.ToString().PadLeft(4) ?? "    ",
            DiffLineType.Context => line.NewLine?.ToString().PadLeft(4) ?? "    ",
            _ => "    "
        };
        
        // Escape content for Spectre
        var content = Markup.Escape(line.Content);
        
        return new Markup($"[dim]{lineNum}[/] [{color}]{prefix}{content}[/]");
    }
    
    private List<DiffLine> ParseDiff(string diff)
    {
        var result = new List<DiffLine>();
        var lines = diff.Split('\n');
        
        int oldLine = 0, newLine = 0;
        var hunkRegex = new Regex(@"^@@ -(\d+),?\d* \+(\d+),?\d* @@");
        bool inHunk = false;
        
        foreach (var line in lines)
        {
            var hunkMatch = hunkRegex.Match(line);
            if (hunkMatch.Success)
            {
                oldLine = int.Parse(hunkMatch.Groups[1].Value) - 1;
                newLine = int.Parse(hunkMatch.Groups[2].Value) - 1;
                inHunk = true;
                result.Add(new DiffLine { Type = DiffLineType.Hunk, Content = line });
                continue;
            }
            
            if (!inHunk) continue;
            if (line.StartsWith("---") || line.StartsWith("+++")) continue;
            
            if (line.StartsWith("+"))
            {
                newLine++;
                result.Add(new DiffLine 
                { 
                    Type = DiffLineType.Add, 
                    NewLine = newLine, 
                    Content = line.Length > 1 ? line.Substring(1) : "" 
                });
            }
            else if (line.StartsWith("-"))
            {
                oldLine++;
                result.Add(new DiffLine 
                { 
                    Type = DiffLineType.Delete, 
                    OldLine = oldLine, 
                    Content = line.Length > 1 ? line.Substring(1) : "" 
                });
            }
            else if (line.StartsWith(" ") || line.Length == 0)
            {
                oldLine++;
                newLine++;
                result.Add(new DiffLine 
                { 
                    Type = DiffLineType.Context, 
                    OldLine = oldLine, 
                    NewLine = newLine, 
                    Content = line.Length > 1 ? line.Substring(1) : line 
                });
            }
        }
        
        return result;
    }
    
    private enum DiffLineType { Add, Delete, Context, Hunk, Other }
    
    private record DiffLine
    {
        public DiffLineType Type { get; init; }
        public int? OldLine { get; init; }
        public int? NewLine { get; init; }
        public string Content { get; init; } = "";
    }
}

/// <summary>
/// Renders inline diff for showing what will change.
/// </summary>
public class InlineDiffRenderer : UIComponent
{
    public string OldContent { get; set; } = "";
    public string NewContent { get; set; } = "";
    public string? Filename { get; set; }
    
    public override IRenderable Render()
    {
        // Use DiffPlex for actual diff generation
        var differ = new DiffPlex.DiffBuilder.InlineDiffBuilder(new DiffPlex.Differ());
        var diff = differ.BuildDiffModel(OldContent, NewContent);
        
        var rows = new List<IRenderable>();
        
        if (!string.IsNullOrEmpty(Filename))
        {
            rows.Add(new Markup($"[bold]{Markup.Escape(Filename)}[/]"));
        }
        
        int lineNum = 0;
        foreach (var line in diff.Lines.Take(50))
        {
            lineNum++;
            var (prefix, color) = line.Type switch
            {
                DiffPlex.DiffBuilder.Model.ChangeType.Inserted => ("+", Theme.Diff.Added),
                DiffPlex.DiffBuilder.Model.ChangeType.Deleted => ("-", Theme.Diff.Removed),
                DiffPlex.DiffBuilder.Model.ChangeType.Modified => ("~", Theme.Status.Warning),
                _ => (" ", Theme.Diff.Context)
            };
            
            rows.Add(new Markup($"[dim]{lineNum,4}[/] [{color}]{prefix} {Markup.Escape(line.Text)}[/]"));
        }
        
        if (diff.Lines.Count > 50)
        {
            rows.Add(new Markup($"[dim]... {diff.Lines.Count - 50} more lines[/]"));
        }
        
        return new Panel(new Rows(rows))
            .Border(BoxBorder.Rounded)
            .BorderColor(Theme.Tool.Border);
    }
}
