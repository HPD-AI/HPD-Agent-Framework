using Microsoft.Extensions.AI;

/// <summary>
/// Immutable workflow context focused purely on workflow execution state.
/// History management is the responsibility of the calling Conversation.
/// </summary>
public record WorkflowContext<TState>(
    TState State,
    string? ConversationId,
    string? CurrentNodeId,
    DateTime LastUpdatedAt
) where TState : class, new();
