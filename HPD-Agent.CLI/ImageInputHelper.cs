// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace AgentConsoleTest;

/// <summary>
/// Helper for handling image input in the console app.
/// Supports file paths, URLs, and auto-detection of drag-dropped files.
/// </summary>
public static class ImageInputHelper
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".heic", ".avif", ".svg", ".tiff", ".tif", ".ico"
    };

    /// <summary>
    /// Check if a file path has an image extension.
    /// </summary>
    public static bool IsImageFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        return ImageExtensions.Contains(extension);
    }

    /// <summary>
    /// Try to parse user input for image content.
    /// Handles: file paths, URLs, /image commands
    /// </summary>
    /// <param name="input">User input string</param>
    /// <param name="imageContent">Parsed image content (ImageContent for files, UriContent for URLs) if successful</param>
    /// <param name="question">Extracted question text (or null if not provided)</param>
    /// <returns>True if input contained valid image reference</returns>
    public static async Task<(bool success, AIContent? image, string? question)> TryParseImageInputAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return (false, null, null);

        // Check for /image command: /image <path> [question]
        if (input.StartsWith("/image ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = input.Substring(7).Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]Usage: /image <path-or-url> [question][/]");
                AnsiConsole.MarkupLine("[dim]Example: /image screenshot.png What's in this?[/]");
                return (false, null, null);
            }

            var pathOrUrl = parts[0];
            var question = parts.Length > 1 ? parts[1] : null;

            // Try URL - use passthrough mode (don't download)
            if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                try
                {
                    var image = await ImageContent.FromUriAsync(uri, download: false);
                    AnsiConsole.MarkupLine($"[green]✓ Image URL (passthrough):[/] {uri}");
                    return (true, image, question);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Failed to create image URL:[/] {ex.Message}");
                    return (false, null, null);
                }
            }

            // Try file path
            if (File.Exists(pathOrUrl))
            {
                if (!IsImageFile(pathOrUrl))
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning:[/] File doesn't have image extension: {Path.GetExtension(pathOrUrl)}");
                }

                try
                {
                    var image = await ImageContent.FromFileAsync(pathOrUrl);
                    AnsiConsole.MarkupLine($"[green]✓ Image loaded:[/] {Path.GetFileName(pathOrUrl)} [dim]({GetFileSize(pathOrUrl)})[/]");
                    return (true, image, question);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Failed to load image:[/] {ex.Message}");
                    return (false, null, null);
                }
            }

            AnsiConsole.MarkupLine($"[red]✗ File not found:[/] {pathOrUrl}");
            return (false, null, null);
        }

        // Auto-detect: Check if input is just a file path (e.g., from drag-and-drop)
        var trimmedInput = input.Trim().Trim('"', '\''); // Remove quotes that some terminals add

        if (File.Exists(trimmedInput) && IsImageFile(trimmedInput))
        {
            try
            {
                var image = await ImageContent.FromFileAsync(trimmedInput);
                AnsiConsole.MarkupLine($"[green]✓ Image detected:[/] {Path.GetFileName(trimmedInput)} [dim]({GetFileSize(trimmedInput)})[/]");

                // Ask for question
                var question = AnsiConsole.Prompt(
                    new TextPrompt<string>("[blue]Question about this image:[/]")
                        .DefaultValue("Describe this image in detail")
                        .AllowEmpty());

                return (true, image, string.IsNullOrWhiteSpace(question) ? "Describe this image in detail" : question);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Failed to load image:[/] {ex.Message}");
                return (false, null, null);
            }
        }

        // Auto-detect: Check if input is a URL - use passthrough mode
        if (Uri.TryCreate(trimmedInput, UriKind.Absolute, out var autoUri) &&
            (autoUri.Scheme == "http" || autoUri.Scheme == "https"))
        {
            try
            {
                var image = await ImageContent.FromUriAsync(autoUri, download: false);
                AnsiConsole.MarkupLine($"[green]✓ Image URL detected (passthrough):[/] {autoUri}");

                // Ask for question
                var question = AnsiConsole.Prompt(
                    new TextPrompt<string>("[blue]Question about this image:[/]")
                        .DefaultValue("Describe this image in detail")
                        .AllowEmpty());

                return (true, image, string.IsNullOrWhiteSpace(question) ? "Describe this image in detail" : question);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Failed to create image URL:[/] {ex.Message}");
                return (false, null, null);
            }
        }

        return (false, null, null);
    }

    /// <summary>
    /// Create a ChatMessage with both text and image content.
    /// </summary>
    /// <param name="text">The text prompt for the image.</param>
    /// <param name="image">The image content (ImageContent or UriContent).</param>
    /// <param name="detail">Image detail level: "low" (~85 tokens), "high" (variable), or "auto" (default).
    /// Use "low" for simple tasks to save tokens and cost. Only applies to ImageContent (DataContent).</param>
    public static ChatMessage CreateImageMessage(string text, AIContent image, string? detail = null)
    {
        // Set detail level if specified (only works on ImageContent/DataContent)
        if (detail != null && image is ImageContent imageContent)
        {
            imageContent.AdditionalProperties ??= new();
            imageContent.AdditionalProperties["detail"] = detail;
        }

        return new ChatMessage(ChatRole.User, new AIContent[]
        {
            new TextContent(text),
            image
        });
    }

    /// <summary>
    /// Show help information for image commands.
    /// </summary>
    public static void ShowImageHelp()
    {
        AnsiConsole.MarkupLine("[bold yellow]Image Input Options:[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[cyan]1. Drag & Drop[/]");
        AnsiConsole.MarkupLine("   [dim]Drag image file into terminal → path is pasted automatically[/]");
        AnsiConsole.MarkupLine("   [dim]Example: Just drag screenshot.png and press Enter[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[cyan]2. /image command[/]");
        AnsiConsole.MarkupLine("   [dim]/image <path-or-url> [question][/]");
        AnsiConsole.MarkupLine("   [dim]Example: /image ~/Desktop/photo.jpg What's in this?[/]");
        AnsiConsole.MarkupLine("   [dim]Example: /image https://example.com/image.png[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[cyan]3. Direct file path[/]");
        AnsiConsole.MarkupLine("   [dim]Type or paste full path to image file[/]");
        AnsiConsole.MarkupLine("   [dim]Example: /Users/name/screenshot.png[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[cyan]Supported formats:[/]");
        AnsiConsole.MarkupLine($"   [dim]{string.Join(", ", ImageExtensions.OrderBy(x => x))}[/]");
        AnsiConsole.WriteLine();
    }

    private static string GetFileSize(string path)
    {
        var bytes = new FileInfo(path).Length;

        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";

        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
