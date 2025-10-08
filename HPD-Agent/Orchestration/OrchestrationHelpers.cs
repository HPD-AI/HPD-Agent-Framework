using Microsoft.Extensions.AI;

namespace HPD_Agent.Orchestration;

/// <summary>
/// Helper utilities for orchestration implementations.
/// Centralizes common patterns like reduction metadata packaging.
/// </summary>
public static class OrchestrationHelpers
{
    /// <summary>
    /// Packages ReductionMetadata from StreamingTurnResult into OrchestrationMetadata.Context dictionary.
    /// This enables Conversation to extract and apply history reduction to storage.
    ///
    /// Use this helper in all IOrchestrator implementations to ensure reduction metadata
    /// flows correctly from Agent -> Orchestrator -> Conversation.
    /// </summary>
    /// <param name="reduction">Reduction metadata from StreamingTurnResult.Reduction, or null if no reduction occurred</param>
    /// <returns>Context dictionary with reduction metadata, or empty dictionary if no reduction</returns>
    /// <example>
    /// <code>
    /// var streamingResult = await agent.ExecuteStreamingTurnAsync(...);
    /// await foreach (var evt in streamingResult.EventStream) { }
    /// var finalHistory = await streamingResult.FinalHistory;
    ///
    /// return new OrchestrationResult
    /// {
    ///     Response = new ChatResponse(finalHistory),
    ///     PrimaryAgent = agent,
    ///     Metadata = new OrchestrationMetadata
    ///     {
    ///         StrategyName = "MyStrategy",
    ///         Context = OrchestrationHelpers.PackageReductionMetadata(streamingResult.Reduction)
    ///     }
    /// };
    /// </code>
    /// </example>
    public static Dictionary<string, object> PackageReductionMetadata(ReductionMetadata? reduction)
    {
        var context = new Dictionary<string, object>();

        if (reduction != null)
        {
            if (reduction.SummaryMessage != null)
            {
                context["SummaryMessage"] = reduction.SummaryMessage;
            }
            context["MessagesRemovedCount"] = reduction.MessagesRemovedCount;
        }

        return context;
    }
}
