using HPD.Agent;
using HPD.Agent.Serialization;
using Microsoft.AspNetCore.Http;
using System.Text;

namespace HPD.Agent.AspNetCore.Streaming;

/// <summary>
/// Handles SSE (Server-Sent Events) streaming for agent events.
/// </summary>
internal static class SseEventHandler
{
    /// <summary>
    /// Stream agent events as SSE to the HTTP response.
    /// </summary>
    public static async Task StreamEventsAsync(
        HttpContext context,
        IAsyncEnumerable<AgentEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(events);

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await context.Response.Body.FlushAsync(cancellationToken);

        await foreach (var evt in events.WithCancellation(cancellationToken))
        {
            var json = AgentEventSerializer.ToJson(evt);
            var data = $"data: {json}\n\n";
            var bytes = Encoding.UTF8.GetBytes(data);

            await context.Response.Body.WriteAsync(bytes, cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
    }
}
