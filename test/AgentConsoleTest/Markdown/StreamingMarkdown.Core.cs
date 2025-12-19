using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamingMarkdown.Core;

// ============================================================================
// INTERFACES
// ============================================================================

/// <summary>
/// Renders markdown content to a UI-specific renderable type.
/// </summary>
/// <typeparam name="TRenderable">The output type (IRenderable, string, View, etc.)</typeparam>
public interface IMarkdownRenderer<TRenderable>
{
    /// <summary>
    /// Renders a complete markdown string to the target format.
    /// </summary>
    /// <param name="markdown">The markdown content to render</param>
    /// <returns>The rendered output in the target format</returns>
    TRenderable Render(string markdown);
}

/// <summary>
/// Highlights code syntax for a given language.
/// </summary>
public interface ISyntaxHighlighter
{
    /// <summary>
    /// Returns true if the language is supported for highlighting.
    /// </summary>
    /// <param name="language">The language identifier (e.g., "csharp", "python")</param>
    /// <returns>True if syntax highlighting is available for this language</returns>
    bool IsLanguageSupported(string? language);

    /// <summary>
    /// Highlights code and returns formatted output.
    /// </summary>
    /// <param name="code">The source code to highlight</param>
    /// <param name="language">The language identifier</param>
    /// <returns>The highlighted code in the target format</returns>
    string Highlight(string code, string? language);
}

// ============================================================================
// MARKDOWN PARSER - Static utilities for code fence tracking and split points
// ============================================================================

/// <summary>
/// Unified markdown parsing utilities:
/// - Code fence tracking (similar to CodeBlockTracker)
/// - Safe split point detection (from MarkdownSplitter)
/// - Language extraction and code block state tracking
///
/// Consolidates redundant logic from CodeBlockTracker and MarkdownSplitter.
/// </summary>
public static class MarkdownParser
{
    /// <summary>
    /// Find the last position where it's safe to split the markdown.
    /// Content before this point can be formatted; content after streams plain.
    /// </summary>
    public static int FindLastSafeSplitPoint(string content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;

        // 1. If we're inside a code block, split BEFORE the code block starts
        if (IsInsideCodeBlock(content, content.Length))
        {
            var blockStart = FindLastCodeBlockStart(content);
            if (blockStart > 0)
            {
                // Find the last safe point before this code block
                return FindLastSafeSplitPoint(content.Substring(0, blockStart));
            }
            return 0;
        }

        // 2. Look for double newline (paragraph break) - ideal split point
        var lastParagraphBreak = FindLastParagraphBreak(content);
        if (lastParagraphBreak > 0)
            return lastParagraphBreak;

        // 3. Look for single newline at line end (less ideal but workable)
        var lastNewline = FindLastSafeNewline(content);
        if (lastNewline > 0)
            return lastNewline;

        // 4. No safe split found - don't split at all
        return content.Length;
    }

    /// <summary>
    /// Check if a position in the content is inside an unclosed code block.
    /// </summary>
    public static bool IsInsideCodeBlock(string content, int position)
    {
        var searchContent = content.Substring(0, Math.Min(position, content.Length));
        var fenceCount = CountCodeFences(searchContent);
        return fenceCount % 2 == 1; // Odd number = inside a code block
    }

    /// <summary>
    /// Extract the language from a code block (e.g., "python" from ```python).
    /// Searches backward from end to find the current opening fence and extracts language.
    /// </summary>
    public static string? ExtractCodeBlockLanguage(string content)
    {
        if (!IsInsideCodeBlock(content, content.Length))
            return null;

        var lastFence = content.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence == -1)
            return null;

        // Check if this is an opening fence (odd count up to this point)
        var countBefore = CountCodeFences(content.Substring(0, lastFence + 3));
        if (countBefore % 2 == 0)
        {
            // This is a closing fence - look for the previous opening fence
            var searchFrom = lastFence - 1;
            while (searchFrom >= 0)
            {
                var prevFence = content.LastIndexOf("```", searchFrom, StringComparison.Ordinal);
                if (prevFence == -1)
                    return null;

                var prevCount = CountCodeFences(content.Substring(0, prevFence + 3));
                if (prevCount % 2 == 1)
                {
                    lastFence = prevFence;
                    break;
                }

                searchFrom = prevFence - 1;
            }
        }

        // Extract language after the opening fence
        var afterFence = content.Substring(lastFence + 3);
        var newlineIndex = afterFence.IndexOf('\n');
        if (newlineIndex == -1)
            return afterFence.Trim();

        var language = afterFence.Substring(0, newlineIndex).Trim();
        return string.IsNullOrEmpty(language) ? null : language;
    }

    /// <summary>
    /// Count the number of code fences (```) in the content.
    /// </summary>
    private static int CountCodeFences(string content)
    {
        var count = 0;
        var index = 0;

        while (index < content.Length)
        {
            var fenceIndex = content.IndexOf("```", index, StringComparison.Ordinal);
            if (fenceIndex == -1)
                break;

            count++;
            index = fenceIndex + 3;
        }

        return count;
    }

    /// <summary>
    /// Find the start position of the last (potentially unclosed) code block.
    /// </summary>
    private static int FindLastCodeBlockStart(string content)
    {
        var lastFence = content.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence == -1)
            return -1;

        // Walk backward to find the opening fence of this block
        var fenceCount = CountCodeFences(content.Substring(0, lastFence + 3));

        // If odd, this last fence is an opening fence
        if (fenceCount % 2 == 1)
            return lastFence;

        // Otherwise search for the opening fence
        var searchFrom = lastFence - 1;
        while (searchFrom >= 0)
        {
            var prevFence = content.LastIndexOf("```", searchFrom, StringComparison.Ordinal);
            if (prevFence == -1)
                break;

            var countAtPrev = CountCodeFences(content.Substring(0, prevFence + 3));
            if (countAtPrev % 2 == 1)
                return prevFence;

            searchFrom = prevFence - 1;
        }

        return -1;
    }

    /// <summary>
    /// Find the last double newline (paragraph break) that's not inside a code block.
    /// </summary>
    private static int FindLastParagraphBreak(string content)
    {
        var searchFrom = content.Length - 1;

        while (searchFrom >= 1)
        {
            var breakIndex = content.LastIndexOf("\n\n", searchFrom, StringComparison.Ordinal);
            if (breakIndex == -1)
                break;

            var splitPoint = breakIndex + 2; // After the double newline

            // Make sure this point isn't inside a code block
            if (!IsInsideCodeBlock(content, splitPoint))
                return splitPoint;

            searchFrom = breakIndex - 1;
        }

        return -1;
    }

    /// <summary>
    /// Find the last single newline that's not inside a code block.
    /// Only use this as a fallback - paragraph breaks are preferred.
    /// </summary>
    private static int FindLastSafeNewline(string content)
    {
        // Don't split on very recent newlines (wait for more content)
        if (content.Length < 50)
            return -1;

        var searchFrom = content.Length - 10; // Leave some buffer at the end

        while (searchFrom >= 0)
        {
            var newlineIndex = content.LastIndexOf('\n', searchFrom);
            if (newlineIndex == -1)
                break;

            var splitPoint = newlineIndex + 1;

            // Make sure this point isn't inside a code block
            // and isn't in the middle of a list or other structure
            if (!IsInsideCodeBlock(content, splitPoint) && !IsInsideList(content, splitPoint))
                return splitPoint;

            searchFrom = newlineIndex - 1;
        }

        return -1;
    }

    /// <summary>
    /// Check if a position is inside a list (we don't want to split mid-list).
    /// </summary>
    private static bool IsInsideList(string content, int position)
    {
        if (position >= content.Length)
            return false;

        // Look at lines around this position
        var beforeContent = content.Substring(0, position);
        var afterContent = content.Substring(position);

        var lastLine = GetLastLine(beforeContent);
        var nextLine = GetFirstLine(afterContent);

        // Check if we're between list items
        var lastIsListItem = IsListItem(lastLine);
        var nextIsListItem = IsListItem(nextLine);

        // If both are list items, we're mid-list
        if (lastIsListItem && nextIsListItem)
            return true;

        // If last line is a list item and next line is indented, we're in a nested list
        if (lastIsListItem && nextLine.StartsWith("  "))
            return true;

        return false;
    }

    private static bool IsListItem(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("- ") ||
               trimmed.StartsWith("* ") ||
               trimmed.StartsWith("+ ") ||
               Regex.IsMatch(trimmed, @"^\d+\.\s");
    }

    private static string GetLastLine(string content)
    {
        var lastNewline = content.LastIndexOf('\n');
        if (lastNewline == -1)
            return content;
        return content.Substring(lastNewline + 1);
    }

    private static string GetFirstLine(string content)
    {
        var firstNewline = content.IndexOf('\n');
        if (firstNewline == -1)
            return content;
        return content.Substring(0, firstNewline);
    }
}

// ============================================================================
// ANIMATION CONTROLLER - Smooth line-by-line reveals
// ============================================================================

/// <summary>
/// Animation controller for smooth line-by-line reveals.
/// </summary>
/// <typeparam name="TRenderable">The renderable type from the collector</typeparam>
public class AnimationController<TRenderable> : IDisposable
{
    private readonly StreamCollector<TRenderable> _collector;
    private readonly Action<TRenderable> _onLineReady;
    private readonly Action _onAnimationComplete;

    private CancellationTokenSource? _cts;
    private Task? _animationTask;
    private readonly object _lock = new();
    private bool _isRunning = false;

    /// <summary>
    /// Animation tick interval in milliseconds. Codex uses 50ms (20 FPS).
    /// </summary>
    public int TickIntervalMs { get; set; } = 50;

    /// <summary>
    /// Creates a new AnimationController.
    /// </summary>
    /// <param name="collector">The stream collector to animate</param>
    /// <param name="onLineReady">Callback when a line is ready to display</param>
    /// <param name="onAnimationComplete">Callback when animation finishes</param>
    public AnimationController(
        StreamCollector<TRenderable> collector,
        Action<TRenderable> onLineReady,
        Action? onAnimationComplete = null)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _onLineReady = onLineReady ?? throw new ArgumentNullException(nameof(onLineReady));
        _onAnimationComplete = onAnimationComplete ?? (() => { });
    }

    /// <summary>
    /// Start the animation loop if not already running.
    /// Uses atomic CAS like Codex to prevent duplicate threads.
    /// </summary>
    public void StartAnimation()
    {
        lock (_lock)
        {
            if (_isRunning)
                return;

            _isRunning = true;
            _cts = new CancellationTokenSource();

            _animationTask = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        var line = _collector.DequeueNextLine();
                        if (line != null)
                        {
                            _onLineReady(line);
                        }
                        else if (!_collector.HasCompleteLines)
                        {
                            // Queue empty and no more lines coming - stop
                            break;
                        }

                        await Task.Delay(TickIntervalMs, _cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected on cancellation
                }
                finally
                {
                    lock (_lock)
                    {
                        _isRunning = false;
                    }
                    _onAnimationComplete();
                }
            });
        }
    }

    /// <summary>
    /// Stop the animation and drain remaining lines immediately.
    /// </summary>
    public void StopAndDrain()
    {
        lock (_lock)
        {
            _cts?.Cancel();

            // Drain remaining lines immediately
            while (_collector.HasQueuedLines)
            {
                var line = _collector.DequeueNextLine();
                if (line != null)
                {
                    _onLineReady(line);
                }
            }

            _isRunning = false;
        }
    }

    /// <summary>
    /// Check if animation is currently running.
    /// </summary>
    public bool IsAnimating
    {
        get
        {
            lock (_lock)
            {
                return _isRunning;
            }
        }
    }

    /// <summary>
    /// Dispose the animation controller and release resources.
    /// </summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

// ============================================================================
// STREAM COLLECTOR - Unified streaming buffer and collector
// ============================================================================

/// <summary>
/// Unified streaming markdown buffer and collector.
///
/// Consolidates three patterns into one flexible class:
/// 1. **StreamingBuffer pattern**: Split completed vs pending content via Split()
/// 2. **MarkdownStreamBuffer pattern**: Push delta and get yielded chunks
/// 3. **StreamCollector pattern**: Render and queue for animation
///
/// Key features:
/// - Commits on safe split points (paragraph breaks, then newlines)
/// - Never splits inside code blocks
/// - Queues rendered lines for tick-based animation (50ms per line)
/// - Provides both immediate (Push/GetQueuedLines) and animated (DequeueNextLine) output
///
/// Edge cases handled:
/// - UTF-8 multi-byte characters split across deltas
/// - Markdown structures split across chunks
/// - Incomplete code blocks (waits for close)
/// </summary>
/// <typeparam name="TRenderable">The output type from the renderer (optional)</typeparam>
public class StreamCollector<TRenderable>
{
    private readonly IMarkdownRenderer<TRenderable>? _renderer;
    private readonly StringBuilder _buffer = new();
    private readonly ConcurrentQueue<TRenderable> _pendingLines = new();
    private int _lastCommittedPosition = 0;

    /// <summary>
    /// Creates a new StreamCollector with optional renderer and animation support.
    /// </summary>
    /// <param name="renderer">Optional renderer for output. If null, only raw strings are used.</param>
    public StreamCollector(IMarkdownRenderer<TRenderable>? renderer = null)
    {
        _renderer = renderer;
    }

    /// <summary>
    /// Creates a new StreamCollector with a renderer for animation support.
    /// </summary>
    /// <param name="renderer">The markdown renderer to use</param>
    public static StreamCollector<TRenderable> Create(IMarkdownRenderer<TRenderable> renderer)
    {
        return new StreamCollector<TRenderable>(renderer);
    }

    /// <summary>
    /// Push a text delta into the buffer.
    /// </summary>
    public void Push(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;

        _buffer.Append(delta);
    }

    /// <summary>
    /// Check if there are complete lines ready to commit.
    /// Commits at safe split points (paragraph breaks, then newlines).
    /// Never splits inside code blocks.
    /// </summary>
    public bool HasCompleteLines
    {
        get
        {
            var content = _buffer.ToString();

            // Must have content beyond what we've committed
            if (content.Length <= _lastCommittedPosition)
                return false;

            var uncommitted = content.Substring(_lastCommittedPosition);

            // Must have at least one newline in uncommitted content
            if (!uncommitted.Contains('\n'))
                return false;

            // If we're inside a code block at the END of content, don't commit
            // (wait for the code block to close)
            if (MarkdownParser.IsInsideCodeBlock(content, content.Length))
                return false;

            // Find a safe split point that's not inside a code block
            var safePoint = MarkdownParser.FindLastSafeSplitPoint(content);

            // Safe point must be beyond our last committed position
            return safePoint > _lastCommittedPosition;
        }
    }

    /// <summary>
    /// Get completed content (safe to format) and pending content (keep streaming plain).
    /// StreamingBuffer pattern.
    /// </summary>
    /// <returns>Tuple of (completed markdown to format, pending text to stream plain)</returns>
    public (string completed, string pending) Split()
    {
        var content = _buffer.ToString();
        var splitPoint = MarkdownParser.FindLastSafeSplitPoint(content);

        if (splitPoint <= _lastCommittedPosition)
        {
            // No new completed content
            return (string.Empty, content.Substring(_lastCommittedPosition));
        }

        var completed = content.Substring(_lastCommittedPosition, splitPoint - _lastCommittedPosition);
        var pending = content.Substring(splitPoint);

        _lastCommittedPosition = splitPoint;

        return (completed, pending);
    }

    /// <summary>
    /// Get pending content that hasn't been emitted yet.
    /// </summary>
    public string PendingContent => _buffer.ToString(_lastCommittedPosition, _buffer.Length - _lastCommittedPosition);

    /// <summary>
    /// Mark all current content as committed (e.g., on turn complete).
    /// StreamingBuffer pattern.
    /// </summary>
    public string FlushAll()
    {
        var remaining = _buffer.ToString(_lastCommittedPosition, _buffer.Length - _lastCommittedPosition);
        _lastCommittedPosition = _buffer.Length;
        return remaining;
    }

    /// <summary>
    /// Get full buffer content.
    /// </summary>
    public string Content => _buffer.ToString();

    /// <summary>
    /// Commit complete lines and queue them for animation (if renderer provided).
    /// Or render and enqueue with this method.
    /// </summary>
    public void CommitCompleteLines()
    {
        var content = Content;

        // Find the last safe split point (not inside code block)
        var lastSafePoint = MarkdownParser.FindLastSafeSplitPoint(content);
        if (lastSafePoint <= _lastCommittedPosition)
            return;

        // Get NEW content (from last committed position to the safe point)
        var newContent = content.Substring(_lastCommittedPosition, lastSafePoint - _lastCommittedPosition);

        if (string.IsNullOrWhiteSpace(newContent))
            return;

        // Only render if we have a renderer
        if (_renderer != null)
        {
            var rendered = _renderer.Render(newContent);
            _pendingLines.Enqueue(rendered);
        }

        // Update committed position
        _lastCommittedPosition = lastSafePoint;
    }

    /// <summary>
    /// Get queued lines for immediate display (non-animated mode).
    /// Drains the entire queue at once.
    /// </summary>
    public IReadOnlyList<TRenderable> GetQueuedLines()
    {
        var lines = new List<TRenderable>();
        while (_pendingLines.TryDequeue(out var line))
        {
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// Dequeue the next line for tick-based animation.
    /// Call this every TickIntervalMs for smooth Codex-style reveals.
    /// Returns default if queue is empty.
    /// </summary>
    public TRenderable? DequeueNextLine()
    {
        return _pendingLines.TryDequeue(out var line) ? line : default;
    }

    /// <summary>
    /// Check if there are lines waiting in the animation queue.
    /// </summary>
    public bool HasQueuedLines => !_pendingLines.IsEmpty;

    /// <summary>
    /// Get the number of lines in the animation queue.
    /// </summary>
    public int QueuedLineCount => _pendingLines.Count;

    /// <summary>
    /// Finalize and return any remaining content.
    /// Call this at the end of a turn.
    /// </summary>
    public IReadOnlyList<TRenderable> Finalize()
    {
        var content = Content;
        var remaining = new List<TRenderable>();

        // Drain any queued lines first
        while (_pendingLines.TryDequeue(out var queued))
        {
            remaining.Add(queued);
        }

        // Render any uncommitted content (content after last committed position)
        if (_lastCommittedPosition < content.Length && _renderer != null)
        {
            var uncommitted = content.Substring(_lastCommittedPosition);
            if (!string.IsNullOrWhiteSpace(uncommitted))
            {
                var rendered = _renderer.Render(uncommitted);
                remaining.Add(rendered);
            }
        }

        return remaining;
    }

    /// <summary>
    /// Finalize and return all remaining content as raw text (StreamingBuffer pattern).
    /// </summary>
    public string FinalizeAsText()
    {
        var content = Content;
        if (_lastCommittedPosition >= content.Length)
            return string.Empty;

        var remaining = content.Substring(_lastCommittedPosition);
        _lastCommittedPosition = content.Length;
        return remaining;
    }

    /// <summary>
    /// Reset the collector for a new turn.
    /// </summary>
    public void Clear()
    {
        _buffer.Clear();
        _lastCommittedPosition = 0;
        while (_pendingLines.TryDequeue(out _)) { } // Clear queue
    }

    /// <summary>
    /// Check if we're currently inside a code block.
    /// </summary>
    public bool IsInsideCodeBlock => MarkdownParser.IsInsideCodeBlock(Content, _buffer.Length);

    /// <summary>
    /// Get the language of the current code block (if inside one).
    /// </summary>
    public string? CurrentCodeBlockLanguage
    {
        get
        {
            if (!IsInsideCodeBlock) return null;
            return MarkdownParser.ExtractCodeBlockLanguage(Content);
        }
    }

    /// <summary>
    /// Amount of content currently buffered but not yet committed.
    /// </summary>
    public int PendingLength => _buffer.Length - _lastCommittedPosition;
}
