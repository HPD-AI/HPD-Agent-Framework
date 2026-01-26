using Spectre.Console;

/// <summary>
/// Provides escape key cancellation support for long-running agent operations.
/// Monitors for Escape key press in a background thread and triggers cancellation.
/// </summary>
public class EscapeCancellation : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Thread _monitorThread;
    private volatile bool _disposed;
    private volatile bool _cancelled;

    /// <summary>
    /// Gets the cancellation token that will be triggered when Escape is pressed.
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Gets whether the operation was cancelled by the user pressing Escape.
    /// </summary>
    public bool WasCancelled => _cancelled;

    /// <summary>
    /// Creates a new escape cancellation handler and starts monitoring for Escape key.
    /// </summary>
    /// <param name="showHint">Whether to show the "Press Escape to cancel" hint.</param>
    public EscapeCancellation(bool showHint = true)
    {
        _cts = new CancellationTokenSource();

        if (showHint)
        {
            AnsiConsole.MarkupLine("[dim]Press [yellow]Escape[/] to cancel[/]");
        }

        _monitorThread = new Thread(MonitorEscapeKey)
        {
            IsBackground = true,
            Name = "EscapeKeyMonitor"
        };
        _monitorThread.Start();
    }

    private void MonitorEscapeKey()
    {
        try
        {
            while (!_disposed && !_cts.IsCancellationRequested)
            {
                // Check if a key is available (non-blocking)
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Escape)
                    {
                        _cancelled = true;
                        _cts.Cancel();
                        AnsiConsole.MarkupLine("\n[yellow]⊘ Cancelled by user[/]");
                        break;
                    }
                }
                else
                {
                    // Sleep briefly to avoid busy-waiting
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception)
        {
            // Ignore exceptions during monitoring (e.g., if console is redirected)
        }
    }

    /// <summary>
    /// Stops monitoring and cleans up resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Give the monitor thread a moment to exit cleanly
        _monitorThread.Join(100);
        _cts.Dispose();
    }

    /// <summary>
    /// Creates a linked cancellation token that will be cancelled when either
    /// Escape is pressed or the provided token is cancelled.
    /// </summary>
    public static EscapeCancellation CreateLinked(CancellationToken existingToken, bool showHint = true)
    {
        var escape = new EscapeCancellation(showHint);

        // If there's an existing token, link them
        if (existingToken != CancellationToken.None)
        {
            existingToken.Register(() => escape._cts.Cancel());
        }

        return escape;
    }
}
