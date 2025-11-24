using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace HPD.Agent.AGUI;

/// <summary>
/// Adapter for streaming event results from protocol-agnostic core to AGUI protocol.
/// Handles error handling and event conversion for AGUI BaseEvent streaming.
/// </summary>
internal static class AGUIStreamAdapter
{
    /// <summary>
    /// Wraps an event stream with AGUI-specific error handling.
    /// Catches exceptions during enumeration and converts them to structured error events.
    /// Works around C#'s limitation of no yield return in try-catch blocks.
    /// </summary>
    public static async IAsyncEnumerable<BaseEvent> WithErrorHandling(
        IAsyncEnumerable<BaseEvent> innerStream,
        TaskCompletionSource<IReadOnlyList<ChatMessage>> historyCompletion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = innerStream.GetAsyncEnumerator(cancellationToken);
        Exception? caughtError = null;
        bool runFinishedEmitted = false;

        // Capture IDs from RunStartedEvent to use in error scenarios
        string? threadId = null;
        string? runId = null;

        try
        {
            while (true)
            {
                BaseEvent? currentEvent = default;
                bool hasNext = false;
                bool hadError = false;

                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (hasNext)
                    {
                        currentEvent = enumerator.Current;

                        // Capture IDs from RunStartedEvent for error correlation
                        if (currentEvent is RunStartedEvent runStarted)
                        {
                            threadId = runStarted.ThreadId;
                            runId = runStarted.RunId;
                        }
                        else if (currentEvent is RunFinishedEvent)
                        {
                            runFinishedEmitted = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    caughtError = ex;
                    hadError = true;
                    historyCompletion.TrySetException(ex);
                }

                // Emit error event AFTER catch block (C# doesn't allow yield in catch)
                if (hadError && caughtError != null)
                {
                    var errorMessage = caughtError is OperationCanceledException
                        ? "Turn was canceled or timed out."
                        : ErrorFormatter.FormatDetailedError(caughtError, null);

                    yield return EventSerialization.CreateRunError(errorMessage);
                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                yield return currentEvent!;
            }

            // If error occurred and RunFinished wasn't emitted, emit it now for lifecycle closure
            if (caughtError != null && !runFinishedEmitted)
            {
                yield return EventSerialization.CreateRunFinished(
                    threadId ?? string.Empty,
                    runId ?? string.Empty);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
