/// <summary>
/// Web-specific extensions for HTTP context and endpoint helpers.
/// Keeps the core agent library platform-agnostic while providing
/// convenience methods for ASP.NET Core applications.
/// </summary>
public static class WebEndpointExtensions
{
    /// <summary>
    /// Prepares the HTTP response for Server-Sent Event (SSE) streaming.
    /// Sets the necessary headers for proper SSE communication with clients.
    /// </summary>
    /// <param name="context">The HTTP context to prepare for SSE streaming</param>
    public static void PrepareForSseStreaming(this HttpContext context)
    {
        context.Response.Headers["Content-Type"] = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";
        
        // CORS headers for SSE streaming (useful for development)
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
    }
}
