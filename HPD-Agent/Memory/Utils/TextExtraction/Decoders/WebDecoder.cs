using System.Text;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.DataFormats.WebPages; // For IWebScraper

#pragma warning disable KMEXP00 // Possible null reference return.

public sealed class CustomWebDecoder : ITextDecoder
{
    public string Description => "Web Page Decoder (KM-compatible: URL download + HTML processing)";

    private readonly IWebScraper _webScraper;
    private readonly ILogger<CustomWebDecoder>? _logger;

    // HTML processing patterns
    private static readonly System.Text.RegularExpressions.Regex ScriptStyleRegex = new(@"<(script|style)[^>]*>.*?</\1>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex HtmlTagRegex = new(@"<[^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex WhitespaceRegex = new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    public CustomWebDecoder(ILoggerFactory? loggerFactory = null)
    {
        _webScraper = new WebScraper();
        _logger = loggerFactory?.CreateLogger<CustomWebDecoder>();
    }

    public async Task<string> ExtractTextAsync(string urlOrFilePath, CancellationToken cancellationToken = default)
    {
        string htmlContent;

        // STEP 1: Determine if it's a URL or local file and get content
        if (IsUrl(urlOrFilePath))
        {
            htmlContent = await DownloadWebPageAsync(urlOrFilePath, cancellationToken);
        }
        else
        {
            htmlContent = await ReadLocalHtmlFileAsync(urlOrFilePath, cancellationToken);
        }

        // STEP 2: Extract text from HTML content
        return ExtractTextFromHtml(htmlContent);
    }

    /// <summary>
    /// Download web page content using Kernel Memory's WebScraper (official KM pattern)
    /// </summary>
    private async Task<string> DownloadWebPageAsync(string url, CancellationToken cancellationToken)
    {
        _logger?.LogDebug("Downloading web page: {Url}", url);

        var result = await _webScraper.GetContentAsync(url, cancellationToken);

        if (!result.Success)
        {
            var error = $"Failed to download web page '{url}': {result.Error}";
            _logger?.LogWarning("{Error}", error);
            throw new InvalidDataException(error);
        }

        if (result.Content.Length == 0)
        {
            var warning = $"Web page '{url}' returned empty content";
            _logger?.LogWarning("{Warning}", warning);
            return string.Empty;
        }

        _logger?.LogDebug("Successfully downloaded {Size} bytes from {Url}", result.Content.Length, url);
        return result.Content.ToString();
    }

    /// <summary>
    /// Read local HTML file with encoding detection
    /// </summary>
    private async Task<string> ReadLocalHtmlFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException($"HTML file not found: {filePath}");
        }

        _logger?.LogDebug("Reading local HTML file: {FilePath}", filePath);

        // Try to detect encoding from BOM or HTML meta tags
    var encoding = await DetectHtmlEncodingAsync(filePath, cancellationToken);
    // Read file via FileStream to comply with analyzer rules
    await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    using var reader = new StreamReader(fs, encoding);
    var content = await reader.ReadToEndAsync().ConfigureAwait(false);

        _logger?.LogDebug("Read {Size} characters from {FilePath} using {Encoding}",
            content.Length, filePath, encoding.WebName);

        return content;
    }

    /// <summary>
    /// Advanced HTML text extraction with proper handling of scripts, styles, and formatting
    /// </summary>
    private string ExtractTextFromHtml(string htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent))
            return string.Empty;

        var text = htmlContent;

        // Remove script and style blocks (they contain non-readable content)
        text = ScriptStyleRegex.Replace(text, " ");

        // Replace common HTML entities before tag removal
        text = System.Web.HttpUtility.HtmlDecode(text);

        // Replace block-level elements with newlines to preserve document structure
        text = ReplaceBlockElementsWithNewlines(text);

        // Remove all remaining HTML tags
        text = HtmlTagRegex.Replace(text, " ");

        // Normalize whitespace
        text = WhitespaceRegex.Replace(text, " ");

        // Clean up excessive newlines
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n\s*\n\s*\n", "\n\n"); // Max 2 consecutive newlines

        return text.Trim();
    }

    /// <summary>
    /// Replace block-level HTML elements with newlines to preserve document structure
    /// </summary>
    private static string ReplaceBlockElementsWithNewlines(string html)
    {
        var blockElements = new[]
        {
            "div", "p", "br", "h1", "h2", "h3", "h4", "h5", "h6",
            "ul", "ol", "li", "table", "tr", "td", "th",
            "section", "article", "header", "footer", "main", "nav"
        };

        foreach (var element in blockElements)
        {
            // Replace opening and closing tags with newlines
            html = System.Text.RegularExpressions.Regex.Replace(html, $@"<{element}[^>]*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(html, $@"</{element}>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Special handling for <br> tags
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return html;
    }

    /// <summary>
    /// Detect HTML file encoding from BOM or meta tags
    /// </summary>
    private static async Task<Encoding> DetectHtmlEncodingAsync(string filePath, CancellationToken cancellationToken)
    {
        // First, check for BOM
        var bomBuffer = new byte[4];
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytesRead = await fs.ReadAsync(bomBuffer, 0, 4, cancellationToken);

        // Check for UTF-8 BOM
        if (bytesRead >= 3 && bomBuffer[0] == 0xEF && bomBuffer[1] == 0xBB && bomBuffer[2] == 0xBF)
            return Encoding.UTF8;

        // Check for UTF-16 BOMs
        if (bytesRead >= 2)
        {
            if (bomBuffer[0] == 0xFF && bomBuffer[1] == 0xFE)
                return Encoding.Unicode; // UTF-16LE
            if (bomBuffer[0] == 0xFE && bomBuffer[1] == 0xFF)
                return Encoding.BigEndianUnicode; // UTF-16BE
        }

        // No BOM found, try to detect from HTML meta tag
        fs.Position = 0;
        var buffer = new byte[1024]; // Read first 1KB to look for meta charset
        bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
        var htmlStart = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        // Look for charset in meta tag
        var charsetMatch = System.Text.RegularExpressions.Regex.Match(htmlStart, @"<meta[^>]+charset\s*=\s*[""']?([^""'\s>]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (charsetMatch.Success)
        {
            try
            {
                return Encoding.GetEncoding(charsetMatch.Groups[1].Value);
            }
            catch
            {
                // Invalid encoding name, fall back to UTF-8
            }
        }

        // Default to UTF-8
        return Encoding.UTF8;
    }

    /// <summary>
    /// Check if the input string is a URL
    /// </summary>
    public static bool IsUrl(string input)
    {
        return Uri.TryCreate(input, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public void Dispose()
    {
        (_webScraper as IDisposable)?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}