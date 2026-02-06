using Microsoft.Extensions.AI;

namespace HPD.Agent.Middleware;

/// <summary>
/// Automatically uploads binary assets (DataContent) to AssetStore before LLM processing.
/// Transforms DataContent(bytes) → UriContent(asset://assetId) for efficient storage.
/// </summary>
/// <remarks>
/// <para>
/// This middleware is automatically registered in AgentBuilder.
/// It checks at runtime if session.Store.GetAssetStore(sessionId) exists - zero cost when not used.
/// </para>
/// <para><b>Behavior:</b></para>
/// <list type="bullet">
/// <item>Scans messages for DataContent with binary data</item>
/// <item>Uploads bytes to session.Store.GetAssetStore(sessionId)</item>
/// <item>Replaces DataContent with UriContent using asset:// URI scheme</item>
/// <item>Emits AssetUploadedEvent or AssetUploadFailedEvent for observability</item>
/// </list>
/// <para><b>Zero-cost when unused:</b></para>
/// <para>
/// If session?.Store?.GetAssetStore(sessionId) is null, middleware returns immediately (no-op).
/// This makes it safe to auto-register without performance impact.
/// </para>
/// </remarks>
public class AssetUploadMiddleware : IAgentMiddleware
{
    // NO instance fields - middleware is stateless

    /// <summary>
    /// Called before processing a user message turn.
    /// Uploads DataContent to AssetStore and replaces with UriContent.
    /// </summary>
    /// <remarks>
    /// Uses BeforeMessageTurnAsync (not BeforeIterationAsync) to upload assets once per user message,
    /// not on every agentic loop iteration. This prevents redundant uploads when the agent
    /// makes multiple LLM calls in a single turn.
    /// </remarks>
    public async Task BeforeMessageTurnAsync(
        BeforeMessageTurnContext context,
        CancellationToken cancellationToken)
    {
        // Get session from context
        var session = context.Session;
        if (session?.Store == null)
            return;  // No store configured - zero cost exit

        // Get session-scoped asset store
        var assetStore = session.Store.GetAssetStore(session.Id);
        if (assetStore == null)
            return;  // No asset store configured - zero cost exit

        var message = context.UserMessage;

        // UserMessage can be null in continuation scenarios
        if (message == null)
            return;

        // Check if message contains DataContent with bytes
        var hasDataBytes = message.Contents.Any(c =>
            c is DataContent data && data.Data.Length > 0);

        if (!hasDataBytes)
            return;

        // Upload assets and build new content list with URIs
        var newContents = new List<AIContent>();

        foreach (var content in message.Contents)
        {
            AIContent transformedContent = content;

            // Handle DataContent (images, audio, files, etc.)
            if (content is DataContent data && data.Data.Length > 0)
            {
                try
                {
                    // Upload to asset store
                    var assetId = await assetStore.UploadAssetAsync(
                        data.Data.ToArray(),
                        data.MediaType ?? "application/octet-stream",
                        cancellationToken);

                    // Replace with URI reference using MEAI's UriContent
                    transformedContent = new UriContent(
                        new Uri($"asset://{assetId}"),
                        data.MediaType);

                    // Emit event for observability
                    context.Emit(new AssetUploadedEvent(
                        AssetId: assetId,
                        MediaType: data.MediaType ?? "application/octet-stream",
                        SizeBytes: data.Data.Length));
                }
                catch (Exception ex)
                {
                    // Log error but continue (could add retry logic here)
                    context.Emit(new AssetUploadFailedEvent(
                        MediaType: data.MediaType ?? "application/octet-stream",
                        Error: ex.Message));

                    // Keep original content if upload fails
                    transformedContent = content;
                }
            }

            newContents.Add(transformedContent);
        }

        // Create new message with transformed contents
        var updatedMessage = new ChatMessage(message.Role, newContents)
        {
            AuthorName = message.AuthorName,
            AdditionalProperties = message.AdditionalProperties
        };

        // Update the user message in the context
        // The agent will handle persisting this to branch.Messages via turnHistory
        context.UserMessage = updatedMessage;
    }
}
